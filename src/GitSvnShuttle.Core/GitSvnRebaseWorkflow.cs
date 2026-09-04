using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static GitSvnShuttle.Core.GitOperationResults;

namespace GitSvnShuttle.Core;

internal sealed class GitSvnRebaseWorkflow
{
    private readonly IGitCommandRunner runner;
    private readonly GitRepositoryReader reader;

    internal GitSvnRebaseWorkflow(IGitCommandRunner runner, GitRepositoryReader reader)
    {
        this.runner = runner;
        this.reader = reader;
    }

    internal async Task<OperationResult> RebaseAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        try
        {
            var preflight = await reader.PreflightAsync(repositoryPath, requirePendingCommits: false, cancellationToken)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(repositoryPath, "SVN 변경 가져오기 실패: " + exception.Message);
        }
    }

    internal async Task<OperationResult> ContinueRebaseAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var gitDirectory = await reader.GetGitDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (gitDirectory == null || !GitRepositoryReader.IsRebaseInProgress(gitDirectory))
        {
            return Failed(repositoryPath, "계속할 rebase가 없습니다.");
        }

        var conflicts = await reader.GetConflictedFilesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (conflicts == null)
        {
            return Failed(repositoryPath, "rebase 충돌 파일을 확인하지 못했습니다.");
        }

        if (conflicts.Count > 0)
        {
            return Failed(repositoryPath, "미해결 충돌이 남아 있어 rebase를 계속할 수 없습니다.");
        }

        if (await reader.HasStagedChangesAsync(repositoryPath, cancellationToken).ConfigureAwait(false) != true)
        {
            return Failed(repositoryPath, "해결한 변경을 스테이징한 뒤 rebase를 계속하세요.");
        }

        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "rebase", "--continue" },
            cancellationToken).ConfigureAwait(false);
        return ToOperationResult(repositoryPath, "rebase 계속", result);
    }

    internal async Task<OperationResult> AbortRebaseAsync(
        string repositoryPath,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
        {
            return Failed(repositoryPath, "사용자 확인 없이 rebase를 중단하지 않았습니다.");
        }

        var gitDirectory = await reader.GetGitDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (gitDirectory == null || !GitRepositoryReader.IsRebaseInProgress(gitDirectory))
        {
            return Failed(repositoryPath, "중단할 rebase가 없습니다.");
        }

        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "rebase", "--abort" },
            cancellationToken).ConfigureAwait(false);
        return ToOperationResult(repositoryPath, "rebase 중단", result);
    }

    internal async Task<IReadOnlyList<OperationResult>> RebaseAllAsync(
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
}
