using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

public sealed class GitSvnWorkspaceService
{
    private static readonly string[] IgnoredDirectoryNames =
    {
        ".git", ".vs", "bin", "obj", "node_modules", "packages",
    };

    private readonly IGitCommandRunner runner;

    public GitSvnWorkspaceService(IGitCommandRunner runner)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<IReadOnlyList<GitSvnRepository>> DiscoverAsync(
        string solutionDirectory,
        CancellationToken cancellationToken)
    {
        var candidates = FindRepositoryDirectories(solutionDirectory);
        var repositories = new List<GitSvnRepository>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = await runner.RunAsync(
                candidate,
                new[] { "config", "--get-regexp", "^svn-remote\\." },
                cancellationToken).ConfigureAwait(false);

            if (!config.Succeeded || string.IsNullOrWhiteSpace(config.StandardOutput))
            {
                continue;
            }

            repositories.Add(await InspectAsync(candidate, cancellationToken).ConfigureAwait(false));
        }

        return repositories;
    }

    public async Task<GitSvnRepository> InspectAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var status = await runner.RunAsync(
            repositoryPath,
            new[] { "status", "--porcelain=v1" },
            cancellationToken).ConfigureAwait(false);

        if (!status.Succeeded)
        {
            return RepositoryWithProblem(repositoryPath, "Git 상태를 읽지 못했습니다: " + status.CombinedOutput);
        }

        var gitDirectory = await runner.RunAsync(
            repositoryPath,
            new[] { "rev-parse", "--git-dir" },
            cancellationToken).ConfigureAwait(false);

        if (!gitDirectory.Succeeded)
        {
            return RepositoryWithProblem(repositoryPath, "Git 저장소가 아닙니다.");
        }

        var operationProblem = FindOperationProblem(repositoryPath, gitDirectory.StandardOutput);
        if (operationProblem != null)
        {
            return RepositoryWithProblem(repositoryPath, operationProblem);
        }

        var baselineHash = await FindSvnBaselineAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (baselineHash == null)
        {
            return RepositoryWithProblem(repositoryPath, "SVN과 동기화된 기준 커밋을 찾지 못했습니다.");
        }

        var baseline = await GetCommitAsync(repositoryPath, baselineHash, cancellationToken).ConfigureAwait(false);
        if (baseline == null)
        {
            return RepositoryWithProblem(repositoryPath, "SVN 기준 커밋 정보를 읽지 못했습니다.");
        }

        var commits = await GetPendingCommitsAsync(repositoryPath, baselineHash, cancellationToken).ConfigureAwait(false);
        var problem = string.IsNullOrWhiteSpace(status.StandardOutput)
            ? null
            : "커밋되지 않은 변경이 있습니다.";

        return new GitSvnRepository(GetRepositoryName(repositoryPath), repositoryPath, baseline, commits, problem);
    }

    public async Task<OperationResult> RebaseAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var preflight = await PreflightAsync(repositoryPath, requirePendingCommits: false, cancellationToken)
            .ConfigureAwait(false);
        if (!preflight.Succeeded)
        {
            return preflight;
        }

        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "svn", "rebase" },
            cancellationToken).ConfigureAwait(false);
        return ToOperationResult(repositoryPath, "SVN 변경 가져오기", result);
    }

    public async Task<OperationResult> DcommitAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var preflight = await PreflightDcommitAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!preflight.Succeeded)
        {
            return preflight;
        }

        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "svn", "dcommit" },
            cancellationToken).ConfigureAwait(false);
        return ToOperationResult(repositoryPath, "SVN에 게시", result);
    }

    public async Task<IReadOnlyList<OperationResult>> RebaseAllAsync(
        IReadOnlyList<string> repositoryPaths,
        CancellationToken cancellationToken)
    {
        var results = new List<OperationResult>();
        foreach (var repositoryPath in repositoryPaths)
        {
            var result = await RebaseAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (!result.Succeeded)
            {
                break;
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<OperationResult>> DcommitAllAsync(
        IReadOnlyList<string> repositoryPaths,
        CancellationToken cancellationToken)
    {
        var results = new List<OperationResult>();

        // Nothing is published until every repository has passed its preflight.
        foreach (var repositoryPath in repositoryPaths)
        {
            var preflight = await PreflightDcommitAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            if (!preflight.Succeeded)
            {
                results.Add(preflight);
                return results;
            }
        }

        foreach (var repositoryPath in repositoryPaths)
        {
            var command = await runner.RunAsync(
                repositoryPath,
                new[] { "svn", "dcommit" },
                cancellationToken).ConfigureAwait(false);
            var result = ToOperationResult(repositoryPath, "SVN에 게시", command);
            results.Add(result);
            if (!result.Succeeded)
            {
                break;
            }
        }

        return results;
    }

    private async Task<OperationResult> PreflightDcommitAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var preflight = await PreflightAsync(repositoryPath, requirePendingCommits: true, cancellationToken)
            .ConfigureAwait(false);
        if (!preflight.Succeeded)
        {
            return preflight;
        }

        var baseline = await FindSvnBaselineAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (baseline == null)
        {
            return Failed(repositoryPath, "마지막 git-svn-id 커밋을 찾지 못했습니다.");
        }

        var merges = await runner.RunAsync(
            repositoryPath,
            new[] { "log", "--merges", "--format=%H", baseline + "..HEAD" },
            cancellationToken).ConfigureAwait(false);
        if (!merges.Succeeded || !string.IsNullOrWhiteSpace(merges.StandardOutput))
        {
            return Failed(repositoryPath, "게시 범위에 merge commit이 있습니다.");
        }

        var dryRun = await runner.RunAsync(
            repositoryPath,
            new[] { "svn", "dcommit", "--dry-run" },
            cancellationToken).ConfigureAwait(false);
        return dryRun.Succeeded
            ? new OperationResult(repositoryPath, true, "사전 검사 통과")
            : Failed(repositoryPath, "dcommit 사전 검사가 실패했습니다: " + dryRun.CombinedOutput);
    }

    private async Task<OperationResult> PreflightAsync(
        string repositoryPath,
        bool requirePendingCommits,
        CancellationToken cancellationToken)
    {
        var status = await runner.RunAsync(
            repositoryPath,
            new[] { "status", "--porcelain=v1" },
            cancellationToken).ConfigureAwait(false);
        if (!status.Succeeded)
        {
            return Failed(repositoryPath, "Git 상태를 읽지 못했습니다: " + status.CombinedOutput);
        }

        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return Failed(repositoryPath, "커밋되지 않은 변경이 있어 실행하지 않았습니다.");
        }

        var branch = await runner.RunAsync(
            repositoryPath,
            new[] { "symbolic-ref", "--quiet", "--short", "HEAD" },
            cancellationToken).ConfigureAwait(false);
        if (!branch.Succeeded || string.IsNullOrWhiteSpace(branch.StandardOutput))
        {
            return Failed(repositoryPath, "detached HEAD 상태에서는 실행할 수 없습니다.");
        }

        if (requirePendingCommits)
        {
            var commits = await GetPendingCommitsAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            if (commits.Count == 0)
            {
                return Failed(repositoryPath, "SVN에 게시할 로컬 커밋이 없습니다.");
            }
        }

        return new OperationResult(repositoryPath, true, "사전 검사 통과");
    }

    private async Task<IReadOnlyList<GitSvnCommit>> GetPendingCommitsAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var baseline = await FindSvnBaselineAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (baseline == null)
        {
            return Array.Empty<GitSvnCommit>();
        }

        return await GetPendingCommitsAsync(repositoryPath, baseline, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GitSvnCommit>> GetPendingCommitsAsync(
        string repositoryPath,
        string baseline,
        CancellationToken cancellationToken)
    {
        var log = await runner.RunAsync(
            repositoryPath,
            new[]
            {
                "log",
                "--date=short",
                "--format=%H%x1f%h%x1f%an%x1f%ad%x1f%s",
                baseline + "..HEAD",
            },
            cancellationToken).ConfigureAwait(false);

        if (!log.Succeeded || string.IsNullOrWhiteSpace(log.StandardOutput))
        {
            return Array.Empty<GitSvnCommit>();
        }

        return ParseCommits(log.StandardOutput);
    }

    private async Task<GitSvnCommit?> GetCommitAsync(
        string repositoryPath,
        string revision,
        CancellationToken cancellationToken)
    {
        var log = await runner.RunAsync(
            repositoryPath,
            new[]
            {
                "show",
                "-s",
                "--date=short",
                "--format=%H%x1f%h%x1f%an%x1f%ad%x1f%s",
                revision,
            },
            cancellationToken).ConfigureAwait(false);

        return log.Succeeded ? ParseCommits(log.StandardOutput).SingleOrDefault() : null;
    }

    private static IReadOnlyList<GitSvnCommit> ParseCommits(string output)
    {
        const string separator = "\u001f";
        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(new[] { separator }, StringSplitOptions.None))
            .Where(parts => parts.Length == 5)
            .Select(parts => new GitSvnCommit(parts[0], parts[1], parts[2], parts[3], parts[4]))
            .ToArray();
    }

    private async Task<string?> FindSvnBaselineAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "log", "--grep=git-svn-id:", "--format=%H", "-1" },
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput.Trim()
            : null;
    }

    private static IReadOnlyList<string> FindRepositoryDirectories(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var repositories = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            if (Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git")))
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

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (!IgnoredDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return repositories
            .OrderBy(path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? FindOperationProblem(string repositoryPath, string gitDirectoryValue)
    {
        var gitDirectory = gitDirectoryValue.Trim();
        if (!Path.IsPathRooted(gitDirectory))
        {
            gitDirectory = Path.GetFullPath(Path.Combine(repositoryPath, gitDirectory));
        }

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")))
        {
            return "merge가 진행 중입니다.";
        }

        if (Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")) ||
            Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")))
        {
            return "rebase가 진행 중입니다.";
        }

        return null;
    }

    private static GitSvnRepository RepositoryWithProblem(string path, string problem) =>
        new GitSvnRepository(GetRepositoryName(path), path, null, Array.Empty<GitSvnCommit>(), problem);

    private static string GetRepositoryName(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static OperationResult Failed(string path, string message) =>
        new OperationResult(path, false, message);

    private static OperationResult ToOperationResult(string path, string action, GitCommandResult result) =>
        new OperationResult(
            path,
            result.Succeeded,
            result.Succeeded
                ? action + " 완료" + (string.IsNullOrWhiteSpace(result.CombinedOutput) ? string.Empty : Environment.NewLine + result.CombinedOutput)
                : action + " 실패" + Environment.NewLine + result.CombinedOutput);
}
