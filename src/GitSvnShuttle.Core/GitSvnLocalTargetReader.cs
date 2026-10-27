using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

internal sealed class GitSvnLocalTargetReader
{
    private readonly IGitCommandRunner runner;

    internal GitSvnLocalTargetReader(IGitCommandRunner runner) => this.runner = runner;

    internal async Task<GitSvnDcommitTarget?> TryResolveAsync(
        string repositoryPath, string? configuration, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(repositoryPath, ".git", "svn")) &&
            !File.Exists(Path.Combine(repositoryPath, ".git"))) return null;

        var location = await runner.RunAsync(repositoryPath,
            new[] { "rev-parse", "--show-prefix", "--git-path", "svn" }, cancellationToken).ConfigureAwait(false);
        var newline = location.StandardOutput.IndexOfAny(new[] { '\r', '\n' });
        // info run below the repository root includes that relative path in its URL.
        if (!location.Succeeded || newline != 0) return null;

        try
        {
            var svnDirectory = Path.GetFullPath(Path.Combine(repositoryPath, location.StandardOutput.Trim()));
            if (!Directory.Exists(svnDirectory) || Directory.Exists(Path.Combine(svnDirectory, "info")) ||
                File.Exists(Path.Combine(svnDirectory, "config"))) return null;

            var historyTask = runner.RunAsync(repositoryPath,
                new[] { "log", "--first-parent", "--grep=git-svn-id:", "--format=%H%x1f%B", "-1", "HEAD", "--" },
                cancellationToken);
            var refsTask = runner.RunAsync(repositoryPath,
                new[] { "for-each-ref", "--format=%(refname)", "refs/" }, cancellationToken);
            var configTask = configuration == null
                ? runner.RunAsync(repositoryPath, new[] { "config", "--get-regexp", "^(svn\\.|svn-remote\\.)" }, cancellationToken)
                : Task.FromResult(new GitCommandResult(0, configuration, string.Empty));
            await Task.WhenAll(historyTask, refsTask, configTask).ConfigureAwait(false);
            var history = await historyTask.ConfigureAwait(false);
            var references = await refsTask.ConfigureAwait(false);
            var config = await configTask.ConfigureAwait(false);
            if (!history.Succeeded || !references.Succeeded || !config.Succeeded ||
                history.StandardOutput.EndsWith("[output truncated by Git-SVN Shuttle]", StringComparison.Ordinal) ||
                !SupportsConfiguration(config.StandardOutput))
                return null;
            var refNames = references.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (refNames.Any(reference => !reference.StartsWith("refs/", StringComparison.Ordinal))) return null;
            if (refNames.Any(reference => reference.StartsWith("refs/heads/", StringComparison.Ordinal) &&
                    reference.EndsWith("-HEAD", StringComparison.Ordinal)))
                return null; // Ancient layouts can require git-svn's ref migration.

            var separator = history.StandardOutput.IndexOf('\x1f');
            if (separator < 0) return null;
            var hash = history.StandardOutput.Substring(0, separator).Trim();
            if (!Regex.IsMatch(hash, "^(?:[0-9a-f]{40}|[0-9a-f]{64})$")) return null;
            var body = history.StandardOutput.Substring(separator + 1);
            var lines = Regex.Matches(body, @"^[ \t]*git-svn-id:.*$", RegexOptions.Multiline);
            if (lines.Count != 1) return null;
            var metadata = Regex.Match(lines[0].Value.Trim(), @"^git-svn-id: (.+)@(\d+) ([0-9a-fA-F-]{36})$");
            if (!metadata.Success || !uint.TryParse(metadata.Groups[2].Value, out var revision) ||
                !Guid.TryParse(metadata.Groups[3].Value, out _)) return null;
            var url = metadata.Groups[1].Value;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
                uri.Query.Length > 0 || uri.Fragment.Length > 0 || !string.Equals(uri.AbsoluteUri, url, StringComparison.Ordinal))
                return null;

            var target = GitSvnTargetResolver.ResolveConfiguration(url, config.StandardOutput);
            if (!target.IsResolved || target.RemoteRef == null ||
                !refNames.Contains(target.RemoteRef, StringComparer.Ordinal)) return null;

            // Git::SVN stores maps under its raw ref ID; Git ref names escape it.
            var metadataRef = Regex.Replace(target.RemoteRef, "%([0-9a-fA-F]{2})",
                match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
            var mapPath = Path.GetFullPath(Path.Combine(svnDirectory,
                metadataRef.Replace('/', Path.DirectorySeparatorChar), ".rev_map." + metadata.Groups[3].Value));
            if (!mapPath.StartsWith(svnDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) return null;
            cancellationToken.ThrowIfCancellationRequested();
            return MatchesRevision(mapPath, revision, hash) ? target : null;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
            exception is SecurityException || exception is ArgumentException || exception is NotSupportedException)
        {
            return null;
        }
    }

    private static bool SupportsConfiguration(string configuration)
    {
        var paths = new List<(string Remote, string Path, bool Wildcard)>();
        var fetchRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in GitSvnTargetResolver.ParseConfiguration(configuration))
        {
            if (!entry.Key.StartsWith("svn.", StringComparison.OrdinalIgnoreCase) &&
                !entry.Key.StartsWith("svn-remote.", StringComparison.OrdinalIgnoreCase)) return false;
            if (Regex.IsMatch(entry.Key,
                    @"^(svn|svn-remote\..+)\.(rewriteRoot|rewriteUUID|noMetadata|useSvmProps|useSvnsyncProps|ignore-refs)$",
                    RegexOptions.IgnoreCase)) return false;
            var key = Regex.Match(entry.Key, @"^svn-remote\.(.+)\.(fetch|branches|tags)$", RegexOptions.IgnoreCase);
            if (!key.Success) continue;
            var colon = entry.Value.LastIndexOf(":refs/remotes/", StringComparison.Ordinal);
            if (colon < 0) return false;
            var path = entry.Value.Substring(0, colon);
            var reference = entry.Value.Substring(colon + 1);
            var wildcard = !key.Groups[2].Value.Equals("fetch", StringComparison.OrdinalIgnoreCase);
            if (path != path.Trim('/') || path.IndexOfAny(new[] { '{', '}', '?', '%' }) >= 0 ||
                reference.Any(char.IsWhiteSpace)) return false;
            if (wildcard)
            {
                if (path.Count(c => c == '*') != 1 || path.Substring(path.IndexOf('*')).Contains("/") ||
                    !reference.EndsWith("/*", StringComparison.Ordinal) || reference.Count(c => c == '*') != 1) return false;
            }
            else if (path.Contains("*") || reference.Contains("*") || !fetchRefs.Add(reference)) return false;

            var remote = key.Groups[1].Value;
            foreach (var previous in paths.Where(item => item.Remote == remote))
            {
                if (OverlappingPaths(previous.Path, previous.Wildcard, path, wildcard)) return false;
            }
            paths.Add((remote, path, wildcard));
        }
        return paths.Count > 0;
    }

    private static bool OverlappingPaths(string first, bool firstWildcard, string second, bool secondWildcard)
    {
        if (!firstWildcard && !secondWildcard) return first == second;
        if (!firstWildcard) return Regex.IsMatch(first, "^" + Regex.Escape(second).Replace("\\*", ".*") + "$");
        if (!secondWildcard) return Regex.IsMatch(second, "^" + Regex.Escape(first).Replace("\\*", ".*") + "$");
        var left = first.Split('*');
        var right = second.Split('*');
        return (left[0].StartsWith(right[0], StringComparison.Ordinal) || right[0].StartsWith(left[0], StringComparison.Ordinal)) &&
            (left[1].EndsWith(right[1], StringComparison.Ordinal) || right[1].EndsWith(left[1], StringComparison.Ordinal));
    }

    private static bool MatchesRevision(string path, uint revision, string hash)
    {
        if (!File.Exists(path)) return false;
        // Git::SVN uses a big-endian revision followed by a 20- or 32-byte object ID.
        var record = new byte[4 + hash.Length / 2];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length == 0 || stream.Length % record.Length != 0) return false;
            var low = 0L;
            var high = stream.Length / record.Length - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                stream.Position = middle * record.Length;
                var count = 0;
                while (count < record.Length)
                {
                    var read = stream.Read(record, count, record.Length - count);
                    if (read == 0) return false;
                    count += read;
                }
                var found = ((uint)record[0] << 24) | ((uint)record[1] << 16) | ((uint)record[2] << 8) | record[3];
                if (found < revision) low = middle + 1;
                else if (found > revision) high = middle - 1;
                else return string.Equals(BitConverter.ToString(record, 4).Replace("-", string.Empty), hash,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }
}
