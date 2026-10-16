using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static GitSvnShuttle.Core.GitOperationResults;

namespace GitSvnShuttle.Core;

// Existing remote URLs/refs are immutable: they identify both imported history and dcommit ancestry.
internal sealed class GitSvnTrackingWorkflow
{
    private const string RetiredKey = "shuttle.retiredmapping";
    private readonly IGitCommandRunner runner;

    internal GitSvnTrackingWorkflow(IGitCommandRunner runner) => this.runner = runner;

    internal async Task<GitSvnTrackingPaths> GetAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var entries = await ReadAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var mappings = Mappings(entries);
        var active = mappings.Where(m => !m.Retired && !m.BranchBinding).ToArray();
        var urls = mappings.Select(m => m.Url).Distinct(StringComparer.Ordinal).ToArray();
        var baseUrl = CommonBase(urls);
        string Relative(Mapping m) => RelativeTo(baseUrl, FullPath(m));
        return new GitSvnTrackingPaths(baseUrl,
            active.Where(m => m.Kind == "fetch").Select(Relative).Distinct().ToArray(),
            active.Where(m => m.Kind == "branches").Select(Relative).Distinct().ToArray(),
            active.Where(m => m.Kind == "tags").Select(Relative).Distinct().ToArray(),
            mappings.Where(m => m.Retired || m.BranchBinding).Select(FullPath).Select(SensitiveTextRedactor.Redact).Distinct().ToArray());
    }

    internal async Task<OperationResult> ConfigureAsync(string repositoryPath, GitSvnTrackingPaths paths,
        CancellationToken cancellationToken)
    {
        string? lockPath = null;
        var ownsLock = false;
        try
        {
            var desired = Desired(paths);
            if (!Directory.Exists(repositoryPath))
                return Failed(repositoryPath, "로컬 저장소 폴더가 없습니다.");

            var topLevel = await Run(repositoryPath, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (topLevel.Succeeded && !string.Equals(Path.GetFullPath(topLevel.StandardOutput.Trim()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar), comparison))
                return Failed(repositoryPath, "선택한 폴더는 다른 Git 저장소의 하위 폴더입니다. 저장소 루트를 선택하거나 별도 Git 저장소를 초기화한 뒤 연결하세요.");

            var gitDirResult = await Run(repositoryPath, cancellationToken, "rev-parse", "--git-path", "config").ConfigureAwait(false);
            if (!gitDirResult.Succeeded)
            {
                var init = await Run(repositoryPath, cancellationToken, "init").ConfigureAwait(false);
                if (!init.Succeeded) return ToOperationResult(repositoryPath, "Git 초기화", init);
                gitDirResult = await Run(repositoryPath, cancellationToken, "rev-parse", "--git-path", "config").ConfigureAwait(false);
            }
            if (!gitDirResult.Succeeded) return Failed(repositoryPath, "Git 설정 경로를 확인하지 못했습니다.");
            var configPath = Path.GetFullPath(Path.Combine(repositoryPath, gitDirResult.StandardOutput.Trim()));
            var originalConfig = File.ReadAllBytes(configPath);
            var entries = await ReadAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            var effective = await Run(repositoryPath, cancellationToken, "config", "--get-regexp", @"^(svn\.|svn-remote\.)").ConfigureAwait(false);
            if (effective.ExitCode != 0 && effective.ExitCode != 1)
                return Failed(repositoryPath, "SVN 설정을 읽지 못했습니다: " + effective.CombinedOutput);
            if (Regex.IsMatch(effective.StandardOutput, @"^(?:svn|svn-remote\..*)\.(noMetadata|rewriteRoot|rewriteUUID|useSvmProps|useSvnsyncProps)\s", RegexOptions.IgnoreCase | RegexOptions.Multiline))
                return Failed(repositoryPath, "noMetadata, rewriteRoot/UUID, SVM/SVNSync 설정 저장소의 경로 전환은 지원하지 않습니다.");

            var mappings = Mappings(entries);
            if (mappings.Any(m => m.Path.Count(c => c == '*') > 1 || m.Path.IndexOfAny(new[] { '{', '}' }) >= 0))
                return Failed(repositoryPath, "여러 별표 또는 중괄호 패턴의 기존 매핑은 경로 전환을 지원하지 않습니다.");
            var wanted = new HashSet<string>(desired.Select(Identity), StringComparer.Ordinal);
            var retiring = mappings.Where(m => !m.Retired && !m.BranchBinding && !wanted.Contains(Identity(m))).ToArray();
            var allRefs = retiring.Any(m => m.Kind != "fetch")
                ? await Run(repositoryPath, cancellationToken, "for-each-ref", "--format=%(refname)", "refs/remotes/").ConfigureAwait(false)
                : new GitCommandResult(0, string.Empty, string.Empty);
            if (!allRefs.Succeeded) return Failed(repositoryPath, "기존 SVN 추적 ref를 읽지 못했습니다.");

            // Build the full edit before acquiring config.lock; failure cannot leave half a configuration.
            var edits = new List<string[]>();
            foreach (var old in retiring)
            {
                if (old.Kind != "fetch")
                {
                    foreach (var reference in Lines(allRefs.StandardOutput).Where(r => GlobMatches(SanitizePattern(old.Reference), r)))
                    {
                        var log = await Run(repositoryPath, cancellationToken, "log", "--first-parent", "--format=%B", "--grep=^git-svn-id:", "-1", reference).ConfigureAwait(false);
                        var metadata = Regex.Match(log.StandardOutput, @"^git-svn-id: (.+)@\d+ [^\s]+\s*$", RegexOptions.Multiline);
                        if (!log.Succeeded || !metadata.Success)
                            return Failed(repositoryPath, "기존 SVN ref의 이력을 확인하지 못했습니다: " + reference);
                        var url = NormalizeUrl(metadata.Groups[1].Value);
                        if (!GlobMatches(FullPath(old), url))
                        {
                            // Conventional --prefix=svn/ shares svn/* with the
                            // explicit svn/trunk ref. Other configured mappings
                            // own such refs; this branch glob must not retarget them.
                            if (mappings.Any(m => !ReferenceEquals(m, old) &&
                                GlobMatches(SanitizePattern(m.Reference), reference) && GlobMatches(FullPath(m), url))) continue;
                            // --follow-parent creates name@revision refs carrying
                            // the copy source's metadata. They are ancestry refs,
                            // not SVN paths under this glob; retain their objects
                            // and revision maps without inventing a destination.
                            if (Regex.IsMatch(reference, @"@\d+-*$")) continue;
                            return Failed(repositoryPath, "기존 SVN ref와 경로가 일치하지 않아 설정을 보존했습니다: " + reference);
                        }
                        // git-svn config stores the unsanitized ref ID; git-svn
                        // itself escapes '%' and spaces when resolving the ref.
                        var exact = RelativeTo(old.Url, url) + ":" + DesanitizeRef(reference);
                        if (!entries.Any(e => e.Key == "svn-remote." + old.Remote + ".fetch" && e.Value == exact))
                            edits.Add(new[] { "--add", "svn-remote." + old.Remote + ".fetch", exact });
                    }
                    edits.Add(new[] { "--fixed-value", "--unset-all", old.Key, old.Value });
                }
                edits.Add(new[] { "--add", RetiredKey, Encode(old) });
            }

            foreach (var target in desired)
            {
                var existing = mappings.FirstOrDefault(m => Identity(m) == Identity(target));
                if (existing != null)
                {
                    if (existing.Retired)
                    {
                        if (existing.Kind != "fetch") edits.Add(new[] { "--add", existing.Key, existing.Value });
                        edits.Add(new[] { "--fixed-value", "--unset-all", RetiredKey, Encode(existing) });
                    }
                    continue;
                }
                // Overlapping globs can give git-svn two ref identities for the same SVN URL.
                if (mappings.Concat(desired.Where(d => !ReferenceEquals(d, target))).Any(m => Overlaps(m, target)))
                    return Failed(repositoryPath, "기존 추적 경로와 겹치는 경로입니다. 같은 SVN URL은 하나의 매핑으로 추적하세요: " + SensitiveTextRedactor.Redact(FullPath(target)));
                if (entries.Any(e => e.Key.StartsWith("svn-remote." + target.Remote + ".", StringComparison.OrdinalIgnoreCase)))
                    return Failed(repositoryPath, "새 추적 remote 이름이 기존 설정과 충돌합니다. 기존 SVN 설정을 확인하세요.");
                edits.Add(new[] { "--add", "svn-remote." + target.Remote + ".url", target.Url });
                edits.Add(new[] { "--add", target.Key, target.Value });
            }

            lockPath = configPath + ".lock";
            using (var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsLock = true;
                var original = File.ReadAllBytes(configPath);
                if (!original.SequenceEqual(originalConfig))
                    return Failed(repositoryPath, "다른 작업에서 Git 설정을 변경했습니다. 경로를 다시 조회한 뒤 적용하세요.");
                stream.Write(original, 0, original.Length);
            }
            foreach (var edit in edits)
            {
                var command = new[] { "config", "--file", lockPath }.Concat(edit).ToArray();
                var changed = await runner.RunAsync(repositoryPath, command, cancellationToken).ConfigureAwait(false);
                if (!changed.Succeeded) return Failed(repositoryPath, "SVN 추적 설정을 저장하지 못했습니다: " + changed.CombinedOutput);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(lockPath, configPath, null);
            lockPath = null;

            // New remotes have their own revision cursors. No checkout/rebase/reset touches local work.
            var fetch = await Run(repositoryPath, cancellationToken, "svn", "fetch", "--all", "--no-checkout").ConfigureAwait(false);
            return fetch.Succeeded
                ? new OperationResult(repositoryPath, true, "SVN 추적 경로와 이력을 갱신했습니다. 기존 로컬 브랜치의 전송 대상은 유지됩니다.")
                : Failed(repositoryPath, "추적 설정은 저장했지만 fetch가 완료되지 않았습니다. 같은 설정으로 다시 적용하세요. " + fetch.CombinedOutput);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Failed(repositoryPath, "SVN 추적 경로 설정 실패: " + exception.Message); }
        finally { if (ownsLock && lockPath != null && File.Exists(lockPath)) File.Delete(lockPath); }
    }

    private async Task<List<Entry>> ReadAsync(string path, CancellationToken token)
    {
        var result = await Run(path, token, "config", "--local", "--null", "--get-regexp", @"^(svn-remote\..*|shuttle\.retiredmapping)$").ConfigureAwait(false);
        if (result.ExitCode != 0 && result.ExitCode != 1) throw new InvalidOperationException(result.CombinedOutput);
        return result.StandardOutput.Split('\0').Where(s => s.Contains("\n")).Select(s =>
        {
            var split = s.IndexOf('\n');
            return new Entry(s.Substring(0, split), s.Substring(split + 1));
        }).ToList();
    }

    private static List<Mapping> Mappings(List<Entry> entries)
    {
        var result = new List<Mapping>();
        foreach (var entry in entries.Where(e => e.Key.Equals(RetiredKey, StringComparison.OrdinalIgnoreCase)))
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(entry.Value)).Split('\n');
            if (parts.Length != 4) throw new InvalidOperationException("보존된 SVN 경로 설정 형식이 잘못되었습니다.");
            result.Add(new Mapping(parts[0], parts[1], parts[2], parts[3], true));
        }
        foreach (var entry in entries)
        {
            var match = Regex.Match(entry.Key, @"^svn-remote\.(.+)\.(fetch|branches|tags)$", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var url = entries.FirstOrDefault(e => e.Key.Equals("svn-remote." + match.Groups[1].Value + ".url", StringComparison.OrdinalIgnoreCase))?.Value;
            if (url == null) throw new InvalidOperationException("SVN remote URL이 없습니다.");
            var binding = entries.Any(e => e.Key.Equals("svn-remote." + match.Groups[1].Value + ".shuttlebranch", StringComparison.OrdinalIgnoreCase) &&
                e.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
            var mapping = new Mapping(match.Groups[1].Value, match.Groups[2].Value.ToLowerInvariant(), url, entry.Value, branchBinding: binding);
            if (result.Any(m => m.Key == mapping.Key && m.Value == mapping.Value)) continue;
            result.Add(mapping);
        }
        // git-svn may also write explicit bindings for descendants of branch/tag globs.
        return result.Where(m => m.Kind != "fetch" || !result.Any(g => g.Kind != "fetch" && g.Remote == m.Remote &&
            GlobMatches(g.Reference, m.Reference) && GlobMatches(FullPath(g), FullPath(m)))).ToList();
    }

    private static List<Mapping> Desired(GitSvnTrackingPaths paths)
    {
        if (paths == null) throw new ArgumentNullException(nameof(paths));
        var root = NormalizeUrl(paths.BaseUrl);
        var result = new List<Mapping>();
        void Add(string kind, IEnumerable<string> values)
        {
            foreach (var value in values)
            {
                var path = value.Trim().TrimEnd('/');
                if (path == ".") path = string.Empty;
                if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains("\\") || path.Contains(":") ||
                    path.Any(char.IsControl) || path.Split('/').Any(p => Uri.UnescapeDataString(p) == ".." || Uri.UnescapeDataString(p) == ".") ||
                    path.IndexOfAny(new[] { '?', '#', '{', '}' }) >= 0)
                    throw new ArgumentException("SVN 경로는 Base URL 기준 상대 경로여야 합니다: " + path);
                if (kind != "fetch" && !path.Contains("*")) path += (path.Length == 0 ? "" : "/") + "*";
                if ((kind == "fetch" && path.Contains("*")) || path.Count(c => c == '*') > 1)
                    throw new ArgumentException("trunk는 정확한 경로, branches/tags는 별표 하나를 지원합니다.");
                var full = Join(root, path);
                if (kind != "fetch" && path == "*")
                    throw new ArgumentException("branches/tags는 SVN 루트 아래의 컨테이너 경로를 지정하세요.");
                // A bare '*' at a remote root also matches the container itself
                // in git-svn, producing an invalid ref with a trailing slash.
                var remoteUrl = kind == "fetch" ? full : root;
                var relative = kind == "fetch" ? string.Empty : path;
                var hash = SvnConfiguration.ComputeFingerprint(kind + "\n" + full);
                var id = BitConverter.ToString(Convert.FromBase64String(hash)).Replace("-", "").ToLowerInvariant().Substring(0, 20);
                var remote = "shuttle-" + id;
                var reference = "refs/remotes/" + remote + "/" + (kind == "fetch" ? "trunk" : kind + "/*");
                var mapping = new Mapping(remote, kind, remoteUrl, relative + ":" + reference);
                if (!result.Any(m => Identity(m) == Identity(mapping))) result.Add(mapping);
            }
        }
        Add("fetch", paths.TrunkPaths);
        Add("branches", paths.BranchesPaths);
        Add("tags", paths.TagsPaths);
        if (result.Count == 0) throw new ArgumentException("하나 이상의 SVN 추적 경로가 필요합니다.");
        return result;
    }

    private static bool Overlaps(Mapping left, Mapping right)
    {
        var a = FullPath(left); var b = FullPath(right);
        var leftParts = a.Split('/'); var rightParts = b.Split('/');
        if (leftParts.Length != rightParts.Length) return false;
        for (var index = 0; index < leftParts.Length; index++)
        {
            var x = leftParts[index]; var y = rightParts[index];
            if (!x.Contains("*") || !y.Contains("*"))
            {
                if (!GlobMatches(x, y) && !GlobMatches(y, x)) return false;
                continue;
            }
            var xp = x.Split('*'); var yp = y.Split('*');
            if (!(xp[0].StartsWith(yp[0], StringComparison.Ordinal) || yp[0].StartsWith(xp[0], StringComparison.Ordinal)) ||
                !(xp[1].EndsWith(yp[1], StringComparison.Ordinal) || yp[1].EndsWith(xp[1], StringComparison.Ordinal))) return false;
        }
        return true;
    }

    private Task<GitCommandResult> Run(string path, CancellationToken token, params string[] args) => runner.RunAsync(path, args, token);
    private static IEnumerable<string> Lines(string value) => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    private static string Encode(Mapping m) => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", m.Remote, m.Kind, m.Url, m.Value)));
    private static string Identity(Mapping m) => m.Kind + "\n" + FullPath(m);
    private static string DesanitizeRef(string value) => Regex.Replace(value, "%([0-9A-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    private static string SanitizePattern(string value) => string.Join("*", value.Split('*').Select(GitSvnTargetResolver.SanitizeRef));
    private static string FullPath(Mapping m) => Join(m.Url, m.Path);
    private static bool GlobMatches(string pattern, string value) => Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", "[^/]*") + "$");
    private static string Join(string root, string path) => path.Length == 0 ? NormalizeUrl(root) :
        NormalizeUrl(root.TrimEnd('/') + "/" + string.Join("/", path.Split('/').Select(p => Uri.EscapeDataString(p).Replace("%2A", "*"))));
    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "file" || uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "svn" || uri.Scheme == "svn+ssh"))
            throw new ArgumentException("유효한 SVN Base URL이 필요합니다.");
        return uri.AbsoluteUri.TrimEnd('/');
    }
    private static string RelativeTo(string root, string value)
    {
        if (value == root) return string.Empty;
        if (!value.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal)) throw new InvalidOperationException("서로 다른 SVN 저장소 URL입니다.");
        return Uri.UnescapeDataString(value.Substring(root.TrimEnd('/').Length + 1));
    }
    private static string CommonBase(string[] urls)
    {
        if (urls.Length == 0) return string.Empty;
        var root = NormalizeUrl(urls[0]);
        foreach (var url in urls.Skip(1))
        {
            while (NormalizeUrl(url) != root && !NormalizeUrl(url).StartsWith(root + "/", StringComparison.Ordinal))
            {
                var slash = root.LastIndexOf('/');
                if (slash <= root.IndexOf("://", StringComparison.Ordinal) + 2) throw new InvalidOperationException("서로 다른 SVN 서버의 경로 설정은 별도 저장소로 관리하세요.");
                root = root.Substring(0, slash);
            }
        }
        return root;
    }

    private sealed class Entry
    {
        internal Entry(string key, string value) { Key = key; Value = value; }
        internal string Key { get; }
        internal string Value { get; }
    }
    private sealed class Mapping
    {
        internal Mapping(string remote, string kind, string url, string value, bool retired = false, bool branchBinding = false)
        {
            Remote = remote; Kind = kind; Url = NormalizeUrl(url); Value = value; Retired = retired;
            BranchBinding = branchBinding;
            var separator = value.LastIndexOf(':');
            if (separator < 0) throw new InvalidOperationException("SVN ref 매핑 형식이 잘못되었습니다.");
            Path = value.Substring(0, separator); Reference = value.Substring(separator + 1);
        }
        internal string Remote { get; }
        internal string Kind { get; }
        internal string Url { get; }
        internal string Value { get; }
        internal string Path { get; }
        internal string Reference { get; }
        internal bool Retired { get; }
        internal bool BranchBinding { get; }
        internal string Key => "svn-remote." + Remote + "." + Kind;
    }
}
