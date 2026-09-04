using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security;

namespace GitSvnShuttle.Core;

internal sealed class GitSvnRepositoryDiscovery
{
    private const int MaxDirectoriesToScan = 25_000;

    private static readonly string[] IgnoredDirectoryNames =
    {
        ".git", ".vs", "bin", "obj", "node_modules", "packages",
    };

    private readonly IGitCommandRunner runner;
    private readonly GitRepositoryReader reader;

    internal GitSvnRepositoryDiscovery(IGitCommandRunner runner, GitRepositoryReader reader)
    {
        this.runner = runner;
        this.reader = reader;
    }

    internal async Task<IReadOnlyList<GitSvnRepository>> DiscoverAsync(
        string solutionDirectory,
        CancellationToken cancellationToken)
    {
        return await DiscoverAsync(
            solutionDirectory,
            Array.Empty<string>(),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<GitSvnRepository>> DiscoverAsync(
        string solutionDirectory,
        IReadOnlyList<string> loadedProjectPaths,
        CancellationToken cancellationToken)
    {
        if (loadedProjectPaths == null)
        {
            throw new ArgumentNullException(nameof(loadedProjectPaths));
        }

        var candidates = FindRepositoryDirectories(solutionDirectory)
            .Select(path => new RepositoryCandidate(path, isExternalLink: false, linkedProjectPath: null))
            .ToList();
        var knownPaths = new HashSet<string>(
            candidates.Select(candidate => candidate.Path),
            StringComparer.OrdinalIgnoreCase);
        foreach (var projectPath in loadedProjectPaths)
        {
            var external = FindExternalLinkedRepository(solutionDirectory, projectPath);
            if (external != null && knownPaths.Add(external.Path))
            {
                candidates.Add(external);
            }
        }

        var repositories = new List<GitSvnRepository>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = await runner.RunAsync(
                candidate.Path,
                new[] { "config", "--get-regexp", "^svn-remote\\." },
                cancellationToken).ConfigureAwait(false);

            if (!config.Succeeded || string.IsNullOrWhiteSpace(config.StandardOutput))
            {
                continue;
            }

            var inspected = await reader.InspectAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
            repositories.Add(WithDiscoveryContext(
                inspected,
                candidate,
                SvnConfiguration.ExtractSvnTargets(config.StandardOutput)));
        }

        return repositories;
    }

    private static RepositoryCandidate? FindExternalLinkedRepository(
        string solutionDirectory,
        string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        try
        {
            var absoluteProjectPath = Path.GetFullPath(projectPath);
            var projectDirectory = Directory.Exists(absoluteProjectPath)
                ? absoluteProjectPath
                : Path.GetDirectoryName(absoluteProjectPath);
            if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
            {
                return null;
            }

            var lexicalProjectDirectory = NormalizeDirectory(projectDirectory);
            var physicalProjectDirectory = NormalizeDirectory(
                WindowsPhysicalPathResolver.ResolveDirectory(projectDirectory));
            var physicalSolutionDirectory = NormalizeDirectory(
                WindowsPhysicalPathResolver.ResolveDirectory(solutionDirectory));
            if (string.Equals(
                    lexicalProjectDirectory,
                    physicalProjectDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                IsWithinRoot(physicalSolutionDirectory, physicalProjectDirectory))
            {
                return null;
            }

            var repository = FindContainingRepositoryDirectory(physicalProjectDirectory);
            return repository == null
                ? null
                : new RepositoryCandidate(repository, isExternalLink: true, absoluteProjectPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? FindContainingRepositoryDirectory(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return NormalizeDirectory(current.FullName);
            }

            current = current.Parent;
        }

        return null;
    }

    private static IReadOnlyList<string> FindRepositoryDirectories(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Solution directory is required.", nameof(rootPath));
        }

        var root = NormalizeDirectory(rootPath);
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        var repositories = new List<string>();
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(root);

        var scanned = 0;
        while (pending.Count > 0)
        {
            if (++scanned > MaxDirectoriesToScan)
            {
                throw new InvalidOperationException(
                    "저장소 탐색 범위가 너무 큽니다. 솔루션 경로와 junction 구성을 확인하세요.");
            }

            var directory = NormalizeDirectory(pending.Dequeue());
            if (!visited.Add(directory))
            {
                continue;
            }

            if (Directory.Exists(Path.Combine(directory, ".git")) ||
                File.Exists(Path.Combine(directory, ".git")))
            {
                repositories.Add(directory);
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            catch (SecurityException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (IgnoredDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                    IsReparsePoint(child))
                {
                    continue;
                }

                var normalizedChild = NormalizeDirectory(child);
                if (IsWithinRoot(root, normalizedChild))
                {
                    pending.Enqueue(normalizedChild);
                }
            }
        }

        return repositories
            .OrderBy(path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (SecurityException)
        {
            return true;
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsWithinRoot(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static GitSvnRepository WithDiscoveryContext(
        GitSvnRepository repository,
        RepositoryCandidate candidate,
        IReadOnlyList<string> svnTargets) =>
        new GitSvnRepository(
            repository.Name,
            repository.Path,
            repository.GitDirectory,
            repository.SvnBaseline,
            repository.PendingCommits,
            repository.Problem,
            repository.IsRebaseInProgress,
            repository.ConflictedFiles,
            repository.CanContinueRebase,
            candidate.IsExternalLink,
            candidate.LinkedProjectPath,
            svnTargets);

    private sealed class RepositoryCandidate
    {
        public RepositoryCandidate(string path, bool isExternalLink, string? linkedProjectPath)
        {
            Path = NormalizeDirectory(path);
            IsExternalLink = isExternalLink;
            LinkedProjectPath = linkedProjectPath;
        }

        public string Path { get; }
        public bool IsExternalLink { get; }
        public string? LinkedProjectPath { get; }
    }
}
