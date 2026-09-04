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
        var rules = await ValidateDcommitRulesAsync(expected.RepositoryPath, cancellationToken).ConfigureAwait(false);
        if (!rules.Succeeded)
        {
            return rules;
        }

        var current = await CapturePublishSnapshotAsync(expected.RepositoryPath, cancellationToken).ConfigureAwait(false);
        if (current == null || !PublishSnapshotsMatch(expected, current))
        {
            return SnapshotChanged(expected.RepositoryPath);
        }

        if (!runDryRun)
        {
            return new OperationResult(expected.RepositoryPath, true, "확인한 게시 상태와 일치합니다.");
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

        var afterDryRun = await CapturePublishSnapshotAsync(expected.RepositoryPath, cancellationToken).ConfigureAwait(false);
        return afterDryRun != null && PublishSnapshotsMatch(expected, afterDryRun)
            ? new OperationResult(expected.RepositoryPath, true, "사전 검사 통과")
            : SnapshotChanged(expected.RepositoryPath);
    }

    private async Task<OperationResult> ValidateDcommitRulesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var preflight = await reader.PreflightAsync(repositoryPath, requirePendingCommits: true, cancellationToken)
            .ConfigureAwait(false);
        if (!preflight.Succeeded)
        {
            return preflight;
        }

        var baseline = await reader.FindSvnBaselineAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
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

        return new OperationResult(repositoryPath, true, "게시 규칙 검사 통과");
    }

    internal async Task<GitSvnPublishSnapshot?> CapturePublishSnapshotAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var headResult = await runner.RunAsync(
            repositoryPath,
            new[] { "rev-parse", "--verify", "HEAD" },
            cancellationToken).ConfigureAwait(false);
        var head = GitRepositoryReader.FirstOutputLine(headResult);
        if (head == null)
        {
            return null;
        }

        var baseline = await reader.FindSvnBaselineAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (baseline == null)
        {
            return null;
        }

        var commits = await reader.GetPendingCommitsAsync(repositoryPath, baseline, cancellationToken).ConfigureAwait(false);
        if (commits.Count == 0)
        {
            return null;
        }

        var configResult = await runner.RunAsync(
            repositoryPath,
            new[] { "config", "--get-regexp", "^(svn\\.|svn-remote\\.)" },
            cancellationToken).ConfigureAwait(false);
        if (!configResult.Succeeded || string.IsNullOrWhiteSpace(configResult.StandardOutput))
        {
            return null;
        }

        var gitDirectory = await reader.GetGitDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (gitDirectory == null)
        {
            return null;
        }

        var svnTargets = SvnConfiguration.ExtractSvnTargets(configResult.StandardOutput);
        if (svnTargets.Count == 0)
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
            svnTargets);
    }

    private static bool PublishSnapshotsMatch(GitSvnPublishSnapshot expected, GitSvnPublishSnapshot current) =>
        string.Equals(expected.RepositoryPath, current.RepositoryPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.HeadHash, current.HeadHash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.BaselineHash, current.BaselineHash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            expected.SvnConfigurationFingerprint,
            current.SvnConfigurationFingerprint,
            StringComparison.Ordinal) &&
        expected.PendingCommits.Select(commit => commit.Hash).SequenceEqual(
            current.PendingCommits.Select(commit => commit.Hash),
            StringComparer.OrdinalIgnoreCase);

    private static OperationResult SnapshotChanged(string path) =>
        Failed(path, "확인 후 저장소 상태 또는 SVN 설정이 변경되었습니다. 새로 고친 뒤 다시 확인하세요.");
}
