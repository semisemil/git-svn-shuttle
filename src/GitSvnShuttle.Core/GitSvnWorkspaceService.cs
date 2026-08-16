using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

public sealed class GitSvnWorkspaceService
{
    private const int MaxDirectoriesToScan = 25_000;
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
        return await DiscoverAsync(
            solutionDirectory,
            Array.Empty<string>(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitSvnRepository>> DiscoverAsync(
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

            var inspected = await InspectAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
            repositories.Add(WithDiscoveryContext(
                inspected,
                candidate,
                ExtractSvnTargets(config.StandardOutput)));
        }

        return repositories;
    }

    public async Task<GitSvnRepository> InspectAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var status = await runner.RunAsync(
            repositoryPath,
            new[] { "--no-optional-locks", "status", "--porcelain=v1" },
            cancellationToken).ConfigureAwait(false);

        if (!status.Succeeded)
        {
            return RepositoryWithProblem(repositoryPath, null, "Git 상태를 읽지 못했습니다: " + status.CombinedOutput);
        }

        var gitDirectoryResult = await runner.RunAsync(
            repositoryPath,
            new[] { "rev-parse", "--git-dir" },
            cancellationToken).ConfigureAwait(false);
        var gitDirectory = gitDirectoryResult.Succeeded
            ? ResolveGitDirectory(repositoryPath, gitDirectoryResult.StandardOutput)
            : null;

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

    public async Task<OperationResult> ContinueRebaseAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var gitDirectory = await GetGitDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (gitDirectory == null || !IsRebaseInProgress(gitDirectory))
        {
            return Failed(repositoryPath, "계속할 rebase가 없습니다.");
        }

        var conflicts = await GetConflictedFilesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (conflicts == null)
        {
            return Failed(repositoryPath, "rebase 충돌 파일을 확인하지 못했습니다.");
        }

        if (conflicts.Count > 0)
        {
            return Failed(repositoryPath, "미해결 충돌이 남아 있어 rebase를 계속할 수 없습니다.");
        }

        if (await HasStagedChangesAsync(repositoryPath, cancellationToken).ConfigureAwait(false) != true)
        {
            return Failed(repositoryPath, "해결한 변경을 스테이징한 뒤 rebase를 계속하세요.");
        }

        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "rebase", "--continue" },
            cancellationToken).ConfigureAwait(false);
        return ToOperationResult(repositoryPath, "rebase 계속", result);
    }

    public async Task<OperationResult> AbortRebaseAsync(
        string repositoryPath,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
        {
            return Failed(repositoryPath, "사용자 확인 없이 rebase를 중단하지 않았습니다.");
        }

        var gitDirectory = await GetGitDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (gitDirectory == null || !IsRebaseInProgress(gitDirectory))
        {
            return Failed(repositoryPath, "중단할 rebase가 없습니다.");
        }

        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "rebase", "--abort" },
            cancellationToken).ConfigureAwait(false);
        return ToOperationResult(repositoryPath, "rebase 중단", result);
    }

    public async Task<PublishPreparationResult> PrepareDcommitAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var snapshot = await CapturePublishSnapshotAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (snapshot == null)
        {
            return PreparationFailed(repositoryPath, "게시할 저장소 상태를 고정하지 못했습니다.");
        }

        var validation = await ValidatePreparedSnapshotAsync(snapshot, runDryRun: true, cancellationToken)
            .ConfigureAwait(false);
        return validation.Succeeded
            ? new PublishPreparationResult(
                new OperationResult(repositoryPath, true, "게시 전 확인 완료"),
                snapshot)
            : new PublishPreparationResult(validation, null);
    }

    public async Task<PublishBatchPreparationResult> PrepareDcommitAllAsync(
        IReadOnlyList<string> repositoryPaths,
        CancellationToken cancellationToken,
        IProgress<PublishProgress>? progress = null)
    {
        if (repositoryPaths == null)
        {
            throw new ArgumentNullException(nameof(repositoryPaths));
        }

        var snapshots = new List<GitSvnPublishSnapshot>();
        for (var index = 0; index < repositoryPaths.Count; index++)
        {
            var repositoryPath = repositoryPaths[index];
            progress?.Report(new PublishProgress(
                PublishProgressPhase.Preparing,
                repositoryPath,
                repositoryPath,
                index,
                repositoryPaths.Count));
            var preparation = await PrepareDcommitAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            if (!preparation.Succeeded)
            {
                return new PublishBatchPreparationResult(
                    preparation.Outcome,
                    Array.Empty<GitSvnPublishSnapshot>());
            }

            snapshots.Add(preparation.Snapshot!);
        }

        var outcomePath = snapshots.Count == 0 ? string.Empty : snapshots[0].RepositoryPath;
        return new PublishBatchPreparationResult(
            new OperationResult(outcomePath, true, "선택한 저장소의 게시 전 확인 완료"),
            snapshots);
    }

    public async Task<OperationResult> DcommitPreparedAsync(
        GitSvnPublishSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var validation = await ValidatePreparedSnapshotAsync(snapshot, runDryRun: true, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var finalValidation = await ValidatePreparedSnapshotAsync(snapshot, runDryRun: false, cancellationToken)
            .ConfigureAwait(false);
        if (!finalValidation.Succeeded)
        {
            return finalValidation;
        }

        return await ExecutePreparedDcommitAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OperationResult>> DcommitPreparedAllAsync(
        IReadOnlyList<GitSvnPublishSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        if (snapshots == null)
        {
            throw new ArgumentNullException(nameof(snapshots));
        }

        var results = new List<OperationResult>();

        // Nothing is published until every confirmed snapshot still passes its dry run.
        foreach (var snapshot in snapshots)
        {
            var validation = await ValidatePreparedSnapshotAsync(snapshot, runDryRun: true, cancellationToken)
                .ConfigureAwait(false);
            if (!validation.Succeeded)
            {
                results.Add(validation);
                return results;
            }
        }

        foreach (var snapshot in snapshots)
        {
            var finalValidation = await ValidatePreparedSnapshotAsync(snapshot, runDryRun: false, cancellationToken)
                .ConfigureAwait(false);
            if (!finalValidation.Succeeded)
            {
                results.Add(finalValidation);
                return results;
            }

            var result = await ExecutePreparedDcommitAsync(snapshot, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (!result.Succeeded)
            {
                break;
            }
        }

        return results;
    }

    public async Task<PublishBatchResult> DcommitPreparedBatchAsync(
        IReadOnlyList<GitSvnPublishSnapshot> snapshots,
        IProgress<PublishProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (snapshots == null)
        {
            throw new ArgumentNullException(nameof(snapshots));
        }

        var outcomes = new PublishRepositoryOutcome?[snapshots.Count];
        var activeIndex = -1;
        try
        {
            // Preserve the all-dry-runs-before-any-publish protection from DcommitPreparedAllAsync.
            for (var index = 0; index < snapshots.Count; index++)
            {
                activeIndex = index;
                var snapshot = snapshots[index];
                ReportPublishProgress(progress, PublishProgressPhase.Revalidating, snapshot, index, snapshots.Count);
                var validation = await ValidatePreparedSnapshotAsync(
                        snapshot,
                        runDryRun: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!validation.Succeeded)
                {
                    outcomes[index] = ToPublishOutcome(snapshot, PublishOutcomeKind.Failed, validation.Message);
                    return CompletePublishOutcomes(snapshots, outcomes);
                }
            }

            for (var index = 0; index < snapshots.Count; index++)
            {
                activeIndex = index;
                var snapshot = snapshots[index];
                ReportPublishProgress(progress, PublishProgressPhase.Revalidating, snapshot, index, snapshots.Count);
                var validation = await ValidatePreparedSnapshotAsync(
                        snapshot,
                        runDryRun: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!validation.Succeeded)
                {
                    outcomes[index] = ToPublishOutcome(snapshot, PublishOutcomeKind.Failed, validation.Message);
                    return CompletePublishOutcomes(snapshots, outcomes);
                }

                ReportPublishProgress(progress, PublishProgressPhase.Publishing, snapshot, index, snapshots.Count);
                var result = await ExecutePreparedDcommitAsync(snapshot, cancellationToken).ConfigureAwait(false);
                outcomes[index] = ToPublishOutcome(
                    snapshot,
                    result.Succeeded ? PublishOutcomeKind.Succeeded : PublishOutcomeKind.Failed,
                    result.Message);
                if (!result.Succeeded)
                {
                    return CompletePublishOutcomes(snapshots, outcomes);
                }
            }

            return CompletePublishOutcomes(snapshots, outcomes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (activeIndex >= 0 && activeIndex < snapshots.Count && outcomes[activeIndex] == null)
            {
                outcomes[activeIndex] = ToPublishOutcome(
                    snapshots[activeIndex],
                    PublishOutcomeKind.Cancelled,
                    "사용자가 현재 게시 작업을 취소했습니다.");
            }

            return CompletePublishOutcomes(snapshots, outcomes);
        }
    }

    private static void ReportPublishProgress(
        IProgress<PublishProgress>? progress,
        PublishProgressPhase phase,
        GitSvnPublishSnapshot snapshot,
        int index,
        int count) =>
        progress?.Report(new PublishProgress(
            phase,
            snapshot.RepositoryName,
            snapshot.RepositoryPath,
            index,
            count));

    private static PublishRepositoryOutcome ToPublishOutcome(
        GitSvnPublishSnapshot snapshot,
        PublishOutcomeKind kind,
        string message) =>
        new PublishRepositoryOutcome(snapshot.RepositoryName, snapshot.RepositoryPath, kind, message);

    private static PublishBatchResult CompletePublishOutcomes(
        IReadOnlyList<GitSvnPublishSnapshot> snapshots,
        IReadOnlyList<PublishRepositoryOutcome?> outcomes)
    {
        var completed = new PublishRepositoryOutcome[snapshots.Count];
        for (var index = 0; index < snapshots.Count; index++)
        {
            completed[index] = outcomes[index] ?? ToPublishOutcome(
                snapshots[index],
                PublishOutcomeKind.NotRun,
                "앞선 저장소 작업이 완료되지 않아 실행하지 않았습니다.");
        }

        return new PublishBatchResult(completed);
    }

    public async Task<OperationResult> DcommitAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var preparation = await PrepareDcommitAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        return preparation.Succeeded
            ? await DcommitPreparedAsync(preparation.Snapshot!, cancellationToken).ConfigureAwait(false)
            : preparation.Outcome;
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
        var preparation = await PrepareDcommitAllAsync(repositoryPaths, cancellationToken).ConfigureAwait(false);
        if (!preparation.Succeeded)
        {
            return new[] { preparation.Outcome };
        }

        return await DcommitPreparedAllAsync(preparation.Snapshots, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult> ExecutePreparedDcommitAsync(
        GitSvnPublishSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            snapshot.RepositoryPath,
            new[] { "svn", "dcommit" },
            cancellationToken).ConfigureAwait(false);
        return ToOperationResult(snapshot.RepositoryPath, "SVN에 게시", result);
    }

    private async Task<OperationResult> ValidatePreparedSnapshotAsync(
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

        return new OperationResult(repositoryPath, true, "게시 규칙 검사 통과");
    }

    private async Task<OperationResult> PreflightAsync(
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

    private async Task<GitSvnPublishSnapshot?> CapturePublishSnapshotAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var headResult = await runner.RunAsync(
            repositoryPath,
            new[] { "rev-parse", "--verify", "HEAD" },
            cancellationToken).ConfigureAwait(false);
        var head = FirstOutputLine(headResult);
        if (head == null)
        {
            return null;
        }

        var baseline = await FindSvnBaselineAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (baseline == null)
        {
            return null;
        }

        var commits = await GetPendingCommitsAsync(repositoryPath, baseline, cancellationToken).ConfigureAwait(false);
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

        var gitDirectoryResult = await runner.RunAsync(
            repositoryPath,
            new[] { "rev-parse", "--git-dir" },
            cancellationToken).ConfigureAwait(false);
        var gitDirectory = gitDirectoryResult.Succeeded
            ? ResolveGitDirectory(repositoryPath, gitDirectoryResult.StandardOutput)
            : null;
        if (gitDirectory == null)
        {
            return null;
        }

        var svnTargets = ExtractSvnTargets(configResult.StandardOutput);
        if (svnTargets.Count == 0)
        {
            return null;
        }

        return new GitSvnPublishSnapshot(
            GetRepositoryName(repositoryPath),
            repositoryPath,
            gitDirectory,
            head,
            baseline,
            commits,
            ComputeFingerprint(configResult.StandardOutput),
            svnTargets);
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

    private async Task<string?> FindSvnBaselineAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "log", "--grep=git-svn-id:", "--format=%H", "-1" },
            cancellationToken).ConfigureAwait(false);
        return FirstOutputLine(result);
    }

    private async Task<string?> GetGitDirectoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "rev-parse", "--git-dir" },
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? ResolveGitDirectory(repositoryPath, result.StandardOutput) : null;
    }

    private async Task<IReadOnlyList<string>?> GetConflictedFilesAsync(
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

    private async Task<bool?> HasStagedChangesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            repositoryPath,
            new[] { "diff", "--cached", "--quiet", "--exit-code" },
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? false : result.ExitCode == 1 ? true : null;
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

    private static IReadOnlyList<string> ExtractSvnTargets(string configOutput)
    {
        var entries = configOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var separator = line.IndexOfAny(new[] { ' ', '\t' });
                return separator > 0
                    ? new { Key = line.Substring(0, separator), Value = line.Substring(separator + 1).Trim() }
                    : null;
            })
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Value))
            .ToArray();

        var commitTargets = entries
            .Where(entry => entry!.Key.Equals("svn.commiturl", StringComparison.OrdinalIgnoreCase) ||
                            entry.Key.EndsWith(".commiturl", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry!.Value)
            .ToArray();
        var targets = commitTargets.Length > 0
            ? commitTargets
            : entries
                .Where(entry => entry!.Key.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry!.Value)
                .ToArray();

        return targets
            .Select(SensitiveTextRedactor.Redact)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ComputeFingerprint(string value)
    {
        using (var sha256 = SHA256.Create())
        {
            return Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
    }

    private static string? FirstOutputLine(GitCommandResult result) =>
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

        if (Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")) ||
            Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")))
        {
            return "rebase가 진행 중입니다.";
        }

        return null;
    }

    private static bool IsRebaseInProgress(string gitDirectory) =>
        Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")) ||
        Directory.Exists(Path.Combine(gitDirectory, "rebase-apply"));

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

    private static string GetRepositoryName(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static PublishPreparationResult PreparationFailed(string path, string message) =>
        new PublishPreparationResult(Failed(path, message), null);

    private static OperationResult SnapshotChanged(string path) =>
        Failed(path, "확인 후 저장소 상태 또는 SVN 설정이 변경되었습니다. 새로 고친 뒤 다시 확인하세요.");

    private static OperationResult Failed(string path, string message) =>
        new OperationResult(path, false, message);

    private static OperationResult ToOperationResult(string path, string action, GitCommandResult result) =>
        new OperationResult(
            path,
            result.Succeeded,
            result.Succeeded
                ? action + " 완료" + (string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? string.Empty
                    : Environment.NewLine + result.CombinedOutput)
                : action + " 실패" + Environment.NewLine + result.CombinedOutput);

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
