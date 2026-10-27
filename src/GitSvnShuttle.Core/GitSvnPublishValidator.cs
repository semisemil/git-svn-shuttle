using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static GitSvnShuttle.Core.GitOperationResults;

namespace GitSvnShuttle.Core;

internal sealed class GitSvnPublishValidator
{
    private readonly IGitCommandRunner runner;
    private readonly GitRepositoryReader reader;

    internal GitSvnPublishValidator(IGitCommandRunner runner, GitRepositoryReader reader)
    {
        this.runner = runner;
        this.reader = reader;
    }

    internal async Task<OperationResult> ValidatePreparedSnapshotAsync(
        GitSvnPublishSnapshot expected,
        bool runDryRun,
        CancellationToken cancellationToken)
    {
        if (runDryRun)
        {
            var inputs = await CheckDryRunInputsAsync(expected, cancellationToken).ConfigureAwait(false);
            if (!inputs.Succeeded)
            {
                return inputs;
            }

            var dryRun = await runner.RunAsync(
                expected.RepositoryPath,
                new[] { "svn", "dcommit", "--dry-run" },
                cancellationToken).ConfigureAwait(false);
            if (!dryRun.Succeeded)
            {
                return Failed(
                    expected.RepositoryPath,
                    "dcommit 사전 검사가 실패했습니다: " + dryRun.CombinedOutput);
            }
        }

        // Full validation after dry-run also checks changes made during the run.
        var preparation = await PrepareSnapshotAsync(expected.RepositoryPath, cancellationToken, expected).ConfigureAwait(false);
        return preparation.Succeeded
            ? new OperationResult(expected.RepositoryPath, true,
                runDryRun ? "사전 검사 통과" : "확인한 게시 상태와 일치합니다.")
            : preparation.Outcome;
    }

    private async Task<OperationResult> CheckDryRunInputsAsync(
        GitSvnPublishSnapshot expected,
        CancellationToken cancellationToken)
    {
        var preflight = await reader.PreflightAsync(expected.RepositoryPath, cancellationToken).ConfigureAwait(false);
        if (!preflight.Succeeded)
        {
            return preflight;
        }

        var headTask = runner.RunAsync(
            expected.RepositoryPath,
            new[] { "rev-parse", "--verify", "HEAD" },
            cancellationToken);
        var configTask = runner.RunAsync(
            expected.RepositoryPath,
            new[] { "config", "--get-regexp", "^(svn\\.|svn-remote\\.)" },
            cancellationToken);
        await Task.WhenAll(headTask, configTask).ConfigureAwait(false);
        var head = GitRepositoryReader.FirstOutputLine(await headTask.ConfigureAwait(false));
        var config = await configTask.ConfigureAwait(false);
        if (!string.Equals(expected.HeadHash, head, StringComparison.OrdinalIgnoreCase) ||
            !config.Succeeded ||
            !string.Equals(expected.SvnConfigurationFingerprint,
                SvnConfiguration.ComputeFingerprint(config.StandardOutput), StringComparison.Ordinal))
        {
            return SnapshotChanged(expected.RepositoryPath);
        }

        return new OperationResult(expected.RepositoryPath, true, "dry-run 입력 확인 완료");
    }

    internal async Task<PublishPreparationResult> PrepareSnapshotAsync(
        string repositoryPath,
        CancellationToken cancellationToken,
        GitSvnPublishSnapshot? expected = null)
    {
        var preflight = await reader.PreflightAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        if (!preflight.Succeeded)
        {
            return new PublishPreparationResult(preflight, null);
        }

        var snapshot = await CapturePublishSnapshotAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (snapshot == null || (expected != null && !PublishSnapshotsMatch(expected, snapshot)))
        {
            return new PublishPreparationResult(
                expected == null
                    ? Failed(repositoryPath, "게시할 저장소 상태를 고정하지 못했습니다.")
                    : SnapshotChanged(repositoryPath),
                null);
        }

        var merges = await runner.RunAsync(
            repositoryPath,
            new[] { "log", "--merges", "--format=%H", snapshot.BaselineHash + "..HEAD" },
            cancellationToken).ConfigureAwait(false);
        if (!merges.Succeeded || !string.IsNullOrWhiteSpace(merges.StandardOutput))
        {
            return new PublishPreparationResult(Failed(repositoryPath, "게시 범위에 merge commit이 있습니다."), null);
        }

        return new PublishPreparationResult(new OperationResult(repositoryPath, true, "게시 전 확인 완료"), snapshot);
    }

    private async Task<GitSvnPublishSnapshot?> CapturePublishSnapshotAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var headTask = runner.RunAsync(
            repositoryPath,
            new[] { "rev-parse", "--verify", "HEAD" },
            cancellationToken);
        var baselineTask = reader.FindSvnBaselineAsync(repositoryPath, cancellationToken);
        var configTask = runner.RunAsync(
            repositoryPath,
            new[] { "config", "--get-regexp", "^(svn\\.|svn-remote\\.)" },
            cancellationToken);
        var gitDirectoryTask = reader.GetGitDirectoryAsync(repositoryPath, cancellationToken);
        await Task.WhenAll(headTask, baselineTask, configTask, gitDirectoryTask).ConfigureAwait(false);

        var head = GitRepositoryReader.FirstOutputLine(await headTask.ConfigureAwait(false));
        var baseline = await baselineTask.ConfigureAwait(false);
        var configResult = await configTask.ConfigureAwait(false);
        var gitDirectory = await gitDirectoryTask.ConfigureAwait(false);
        if (head == null || baseline == null || gitDirectory == null ||
            !configResult.Succeeded || string.IsNullOrWhiteSpace(configResult.StandardOutput))
        {
            return null;
        }

        var commitsTask = reader.GetPendingCommitsAsync(repositoryPath, baseline, cancellationToken);
        var targetTask = new GitSvnTargetResolver(runner)
            .ResolveUrlAsync(repositoryPath, configResult.StandardOutput, cancellationToken);
        await Task.WhenAll(commitsTask, targetTask).ConfigureAwait(false);
        var commits = await commitsTask.ConfigureAwait(false);
        var target = await targetTask.ConfigureAwait(false);
        if (commits.Count == 0 || !target.IsResolved)
        {
            return null;
        }

        return new GitSvnPublishSnapshot(
            GitRepositoryReader.GetRepositoryName(repositoryPath),
            repositoryPath,
            gitDirectory,
            head,
            baseline,
            commits,
            SvnConfiguration.ComputeFingerprint(configResult.StandardOutput),
            new[] { target.Url! });
    }

    private static bool PublishSnapshotsMatch(GitSvnPublishSnapshot expected, GitSvnPublishSnapshot current) =>
        string.Equals(expected.RepositoryPath, current.RepositoryPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.HeadHash, current.HeadHash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.BaselineHash, current.BaselineHash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            expected.SvnConfigurationFingerprint,
            current.SvnConfigurationFingerprint,
            StringComparison.Ordinal) &&
        expected.SvnTargets.SequenceEqual(current.SvnTargets, StringComparer.Ordinal) &&
        expected.PendingCommits.Select(commit => commit.Hash).SequenceEqual(
            current.PendingCommits.Select(commit => commit.Hash),
            StringComparer.OrdinalIgnoreCase);

    private static OperationResult SnapshotChanged(string path) =>
        Failed(path, "확인 후 저장소 상태 또는 SVN 설정이 변경되었습니다. 새로 고친 뒤 다시 확인하세요.");
}
