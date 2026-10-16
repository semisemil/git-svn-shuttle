using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

internal sealed class GitSvnTargetResolver
{
    private readonly IGitCommandRunner runner;

    internal GitSvnTargetResolver(IGitCommandRunner runner) => this.runner = runner;

    internal async Task<GitSvnDcommitTarget> ResolveAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        // git-svn validates first-parent metadata against its revision map. A local
        // branch name, an upstream Git branch, or a trailer alone cannot do this.
        var info = await runner.RunAsync(repositoryPath, new[] { "svn", "info" }, cancellationToken)
            .ConfigureAwait(false);
        var svnUrl = InfoValue(info.StandardOutput, "URL");
        var revision = InfoValue(info.StandardOutput, "Revision");
        if (!info.Succeeded || svnUrl == null || !IsSvnUrl(svnUrl))
        {
            return Unresolved(null, "현재 HEAD의 git-svn 추적 이력을 확인하지 못했습니다: " + info.CombinedOutput);
        }

        var config = await runner.RunAsync(repositoryPath,
            new[] { "config", "--get-regexp", "^(svn\\.|svn-remote\\.)" }, cancellationToken).ConfigureAwait(false);
        if (!config.Succeeded)
        {
            return Unresolved(svnUrl, "SVN 전송 설정을 읽지 못했습니다.");
        }

        var resolved = ResolveConfiguration(svnUrl, config.StandardOutput);
        return new GitSvnDcommitTarget(resolved.Url, resolved.SvnUrl, resolved.RemoteName, resolved.RemoteRef, revision, resolved.Problem);
    }

    internal static GitSvnDcommitTarget ResolveConfiguration(string svnUrl, string configOutput)
    {
        var entries = ParseConfiguration(configOutput);
        if (entries.Any(entry => new[] { "svn.useSvmProps", "svn.useSvnsyncProps" }.Contains(entry.Key, StringComparer.OrdinalIgnoreCase) && IsEnabled(entry.Value)))
            return Unresolved(svnUrl, "SVM/SVNSync 전역 설정의 전송 대상 조회는 지원하지 않습니다.");
        var matches = FindMappings(entries, svnUrl);
        if (matches.Count != 1)
        {
            return Unresolved(svnUrl, matches.Count == 0
                ? "현재 SVN URL과 연결된 추적 경로를 찾지 못했습니다. rewriteRoot/SVM 등 특수 설정은 지원하지 않습니다."
                : "현재 SVN URL이 여러 추적 경로에 연결되어 실제 전송 대상을 확정할 수 없습니다.");
        }

        var match = matches[0];
        var prefix = "svn-remote." + match.RemoteName + ".";
        var target = LastValue(entries, "svn.commiturl") ?? LastValue(entries, prefix + "commiturl");
        if (string.IsNullOrWhiteSpace(target))
        {
            target = AppendUrlPath(LastValue(entries, prefix + "pushurl") ?? match.BaseUrl, match.Path);
        }

        if (!IsSvnUrl(target!))
        {
            return Unresolved(svnUrl, "SVN 전송 URL 설정이 올바르지 않습니다.");
        }

        return new GitSvnDcommitTarget(SensitiveTextRedactor.Redact(target!), SensitiveTextRedactor.Redact(svnUrl),
            match.RemoteName, match.RemoteRef, null, null);
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> ParseConfiguration(string output) => output
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            var index = line.IndexOfAny(new[] { ' ', '\t' });
            return index < 0
                ? new KeyValuePair<string, string>(line, string.Empty)
                : new KeyValuePair<string, string>(line.Substring(0, index), line.Substring(index + 1).Trim());
        }).ToArray();

    internal static string? LastValue(IReadOnlyList<KeyValuePair<string, string>> entries, string key) =>
        entries.LastOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? InfoValue(string output, string key) => output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith(key + ": ", StringComparison.Ordinal)).Select(line => line.Substring(key.Length + 2)).FirstOrDefault();

    internal static IReadOnlyList<SvnMapping> FindMappings(IReadOnlyList<KeyValuePair<string, string>> entries, string url)
    {
        var mappings = new List<SvnMapping>();
        foreach (var entry in entries)
        {
            var keyMatch = Regex.Match(entry.Key, @"^svn-remote\.(.+)\.(fetch|branches|tags)$", RegexOptions.IgnoreCase);
            if (!keyMatch.Success) continue;
            var remote = keyMatch.Groups[1].Value;
            var baseUrl = LastValue(entries, "svn-remote." + remote + ".url");
            if (string.IsNullOrWhiteSpace(baseUrl)) continue;
            // These modes rewrite the metadata URL. Resolve them with git-svn's
            // own dry-run instead of guessing which transport URL it represents.
            if (entries.Any(item => item.Key.StartsWith("svn-remote." + remote + ".", StringComparison.OrdinalIgnoreCase) &&
                (item.Key.EndsWith(".rewriteRoot", StringComparison.OrdinalIgnoreCase) ||
                 item.Key.EndsWith(".useSvmProps", StringComparison.OrdinalIgnoreCase) ||
                 item.Key.EndsWith(".useSvnsyncProps", StringComparison.OrdinalIgnoreCase)))) continue;
            var colon = entry.Value.LastIndexOf(":refs/", StringComparison.Ordinal);
            if (colon < 0) continue;
            var path = entry.Value.Substring(0, colon).Trim('/');
            var reference = entry.Value.Substring(colon + 1);
            var patternUrl = AppendUrlPath(baseUrl!, path);
            if (path.IndexOfAny(new[] { '{', '}', '?' }) >= 0 || path.Count(character => character == '*') > 1) continue;
            var decodedPattern = Uri.UnescapeDataString(WithoutUserInfo(patternUrl));
            var decodedUrl = Uri.UnescapeDataString(WithoutUserInfo(url.TrimEnd('/')));
            var wildcard = decodedPattern.IndexOf('*');
            if (wildcard < 0)
            {
                if (UrlEquals(decodedPattern, decodedUrl)) mappings.Add(new SvnMapping(remote, baseUrl!, path, SanitizeRef(reference)));
                continue;
            }

            // git-svn substitutes the entire wildcard component in the ref,
            // including a component's fixed prefix/suffix (Project-*).
            var componentStart = decodedPattern.LastIndexOf('/', wildcard) + 1;
            var componentEnd = decodedPattern.IndexOf('/', wildcard);
            if (componentEnd < 0) componentEnd = decodedPattern.Length;
            var component = decodedPattern.Substring(componentStart, componentEnd - componentStart);
            var pattern = "^" + Regex.Escape(decodedPattern.Substring(0, componentStart)) + "(" +
                Regex.Escape(component).Replace("\\*", "[^/]*") + ")" + Regex.Escape(decodedPattern.Substring(componentEnd)) + "$";
            var match = Regex.Match(decodedUrl, pattern, RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var value = match.Groups[1].Value;
            var actualPath = string.Join("/", path.Split('/').Select(part => part.Contains("*") ? value : part));
            mappings.Add(new SvnMapping(remote, baseUrl!, actualPath, SanitizeRef(reference.Replace("*", value))));
        }

        return mappings.GroupBy(item => item.RemoteName + "\n" + item.RemoteRef, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    }

    internal static string AppendUrlPath(string url, string path) => url.TrimEnd('/') +
        (string.IsNullOrEmpty(path.Trim('/')) ? string.Empty : "/" + string.Join("/", path.Trim('/').Split('/').Select(segment =>
            segment == "*" ? segment : Uri.EscapeDataString(segment))));

    internal static bool IsSvnUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        new[] { "http", "https", "svn", "svn+ssh", "file" }.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    internal static bool UrlEquals(string left, string right) => string.Equals(WithoutUserInfo(left.TrimEnd('/')), WithoutUserInfo(right.TrimEnd('/')), StringComparison.Ordinal);

    private static string WithoutUserInfo(string value) => Regex.Replace(value, @"^([a-z][a-z0-9+.-]*://)[^/@\s]+@", "$1", RegexOptions.IgnoreCase);

    private static bool IsEnabled(string value) => new[] { "true", "yes", "on", "1" }.Contains(value, StringComparer.OrdinalIgnoreCase);

    // Mirrors Git::SVN::refname, including reversible escaping of percent.
    internal static string SanitizeRef(string reference)
    {
        var result = Regex.Replace(reference, @"[ %~^:?*\[\t\\]", match => "%" + ((int)match.Value[0]).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        result = result.Replace("/.", "/%2E").Replace("..", "%2E%2E");
        result = Regex.Replace(result, @"\.(?=$|lock$)", "%2E");
        return result.Replace("@{", "%40{");
    }

    private static GitSvnDcommitTarget Unresolved(string? svnUrl, string problem) => new GitSvnDcommitTarget(
        null, svnUrl == null ? null : SensitiveTextRedactor.Redact(svnUrl), null, null, null, SensitiveTextRedactor.Redact(problem));

    internal sealed class SvnMapping
    {
        internal SvnMapping(string remoteName, string baseUrl, string path, string remoteRef)
        { RemoteName = remoteName; BaseUrl = baseUrl; Path = path; RemoteRef = remoteRef; }
        internal string RemoteName { get; }
        internal string BaseUrl { get; }
        internal string Path { get; }
        internal string RemoteRef { get; }
    }
}
