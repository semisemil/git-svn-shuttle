using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static GitSvnShuttle.Core.GitOperationResults;

namespace GitSvnShuttle.Core;

internal sealed class GitRepositoryReader
{
    private readonly IGitCommandRunner runner;

    internal GitRepositoryReader(IGitCommandRunner runner)
    {
        this.runner = runner;
    }

    internal async Task<GitSvnRepository> InspectAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var status = await runner.RunAsync(
            repositoryPath,
            new[] { "--no-optional-locks", "status", "--porcelain=v1" },
            cancellationToken).ConfigureAwait(false);

        if (!status.Succeeded)
        {
            return RepositoryWithProblem(repositoryPath, null, "Git 상태를 읽지 못했습니다: " + status.CombinedOutput);
        }

        var gitDirectory = await GetGitDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);

        if (gitDirectory == null)
        {
            return RepositoryWithProblem(repositoryPath, null, "Git 저장소 경로를 확인하지 못했습니다.");
        }

        if (IsRebaseInProgress(gitDirectory))
        {
            var conflicts = await GetConflictedFilesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            if (conflicts == null)
            {
                return RepositoryWithProblem(
                    repositoryPath,
                    gitDirectory,
                    "rebase 충돌 파일을 확인하지 못했습니다.",
                    isRebaseInProgress: true);
            }

            var canContinue = conflicts.Count == 0 &&
                              await HasStagedChangesAsync(repositoryPath, cancellationToken).ConfigureAwait(false) == true;
            var rebaseProblem = conflicts.Count > 0
                ? "rebase 충돌을 해결하고 변경을 스테이징한 뒤 계속하세요."
                : canContinue
                    ? "충돌 해결이 스테이징되었습니다. rebase를 계속할 수 있습니다."
                    : "rebase를 계속하려면 해결한 변경을 스테이징하세요.";
            return RepositoryWithProblem(
                repositoryPath,
                gitDirectory,
                rebaseProblem,
                isRebaseInProgress: true,
                conflictedFiles: conflicts,
                canContinueRebase: canContinue);
        }

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")))
        {
            return RepositoryWithProblem(repositoryPath, gitDirectory, "merge가 진행 중입니다.");
        }

        var baselineHash = await FindSvnBaselineAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (baselineHash == null)
        {
            return RepositoryWithProblem(repositoryPath, gitDirectory, "SVN과 동기화된 기준 커밋을 찾지 못했습니다.");
        }

        var baseline = await GetCommitAsync(repositoryPath, baselineHash, cancellationToken).ConfigureAwait(false);
        if (baseline == null)
        {
            return RepositoryWithProblem(repositoryPath, gitDirectory, "SVN 기준 커밋 정보를 읽지 못했습니다.");
        }

        var commits = await GetPendingCommitsAsync(repositoryPath, baselineHash, cancellationToken).ConfigureAwait(false);
        var problem = string.IsNullOrWhiteSpace(status.StandardOutput)
            ? null
            : "커밋되지 않은 변경이 있습니다.";

        return new GitSvnRepository(
            GetRepositoryName(repositoryPath),
            repositoryPath,
            gitDirectory,
            baseline,
            commits,
            problem);
    }

    internal async Task<OperationResult> PreflightAsync(
        string repositoryPath,
        bool requirePendingCommits,
        CancellationToken cancellationToken)
    {
        var status = await runner.RunAsync(
            repositoryPath,
            new[] { "--no-optional-locks", "status", "--porcelain=v1" },
            cancellationToken).ConfigureAwait(false);
        if (!status.Succeeded)
        {
            return Failed(repositoryPath, "Git 상태를 읽지 못했습니다: " + status.CombinedOutput);
        }

        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return Failed(repositoryPath, "커밋되지 않은 변경이 있어 실행하지 않았습니다.");
        }

        var gitDirectory = await GetGitDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (gitDirectory == null)
        {
            return Failed(repositoryPath, "Git 저장소 경로를 확인하지 못했습니다.");
        }

        var operationProblem = FindOperationProblem(gitDirectory);
        if (operationProblem != null)
        {
            return Failed(repositoryPath, operationProblem);
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
        return baseline == null
            ? Array.Empty<GitSvnCommit>()
            : await GetPendingCommitsAsync(repositoryPath, baseline, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<GitSvnCommit>> GetPendingCommitsAsync(
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
                "--encoding=UTF-8",
                "--reverse",
                "--format=%H%x1f%h%x1f%an%x1f%ad%x1f%s",
                baseline + "..HEAD",
            },
            cancellationToken).ConfigureAwait(false);

        return !log.Succeeded || string.IsNullOrWhiteSpace(log.StandardOutput)
            ? Array.Empty<GitSvnCommit>()
            : ParseCommits(log.StandardOutput);
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
                "--encoding=UTF-8",
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

    internal async Task<string?> FindSvnBaselineAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "log", "--grep=git-svn-id:", "--format=%H", "-1" },
            cancellationToken).ConfigureAwait(false);
        return FirstOutputLine(result);
    }

    internal async Task<string?> GetGitDirectoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "rev-parse", "--git-dir" },
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? ResolveGitDirectory(repositoryPath, result.StandardOutput) : null;
    }

    internal async Task<IReadOnlyList<string>?> GetConflictedFilesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "diff", "--name-only", "--diff-filter=U", "-z" },
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        return result.StandardOutput
            .Split(new[] { '\0', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim())
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal async Task<bool?> HasStagedChangesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "diff", "--cached", "--quiet", "--exit-code" },
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? false : result.ExitCode == 1 ? true : null;
    }

    internal static string? FirstOutputLine(GitCommandResult result) =>
        result.Succeeded
            ? result.StandardOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0)
            : null;

    private static string? ResolveGitDirectory(string repositoryPath, string gitDirectoryValue)
    {
        var value = gitDirectoryValue
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(value)
                ? value
                : Path.Combine(repositoryPath, value));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }

    private static string? FindOperationProblem(string gitDirectory)
    {
        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")))
        {
            return "merge가 진행 중입니다.";
        }

        if (IsRebaseInProgress(gitDirectory))
        {
            return "rebase가 진행 중입니다.";
        }

        return null;
    }

    internal static bool IsRebaseInProgress(string gitDirectory) =>
        Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")) ||
        Directory.Exists(Path.Combine(gitDirectory, "rebase-apply"));

    private static GitSvnRepository RepositoryWithProblem(
        string path,
        string? gitDirectory,
        string problem,
        bool isRebaseInProgress = false,
        IReadOnlyList<string>? conflictedFiles = null,
        bool canContinueRebase = false) =>
        new GitSvnRepository(
            GetRepositoryName(path),
            path,
            gitDirectory,
            null,
            Array.Empty<GitSvnCommit>(),
            problem,
            isRebaseInProgress,
            conflictedFiles,
            canContinueRebase);

    internal static string GetRepositoryName(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
