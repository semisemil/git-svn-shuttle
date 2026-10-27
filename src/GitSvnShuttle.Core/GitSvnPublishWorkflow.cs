using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static GitSvnShuttle.Core.GitOperationResults;

namespace GitSvnShuttle.Core;

internal sealed class GitSvnPublishWorkflow
{
    private readonly IGitCommandRunner runner;
    private readonly GitSvnPublishValidator validator;

    internal GitSvnPublishWorkflow(IGitCommandRunner runner, GitSvnPublishValidator validator)
    {
        this.runner = runner;
        this.validator = validator;
    }

    internal Task<PublishPreparationResult> PrepareDcommitAsync(
        string repositoryPath,
        CancellationToken cancellationToken) => validator.PrepareSnapshotAsync(repositoryPath, cancellationToken);

    internal async Task<PublishBatchPreparationResult> PrepareDcommitAllAsync(
        IReadOnlyList<string> repositoryPaths,
        CancellationToken cancellationToken,
        IProgress<PublishProgress>? progress = null)
    {
        if (repositoryPaths == null)
        {
            throw new ArgumentNullException(nameof(repositoryPaths));
        }

        var preparations = await RepositoryBatch.RunAsync(repositoryPaths, async index =>
        {
            var repositoryPath = repositoryPaths[index];
            progress?.Report(new PublishProgress(
                PublishProgressPhase.Preparing,
                repositoryPath,
                repositoryPath,
                index,
                repositoryPaths.Count));
            return await PrepareDcommitAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        }, preparation => preparation.Succeeded).ConfigureAwait(false);

        var snapshots = new List<GitSvnPublishSnapshot>();
        foreach (var preparation in preparations)
        {
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

    internal async Task<OperationResult> ValidatePublishSnapshotAsync(
        GitSvnPublishSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        // Watcher notifications can come from preparation itself. Read current state
        // without another git-svn dry run, which could produce more notifications.
        return await validator.ValidatePreparedSnapshotAsync(snapshot, runDryRun: false, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<bool> ValidatePublishSnapshotsAsync(
        IReadOnlyList<GitSvnPublishSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        if (snapshots == null) throw new ArgumentNullException(nameof(snapshots));
        var results = await RepositoryBatch.RunAsync(
            snapshots.Select(snapshot => snapshot.RepositoryPath).ToArray(),
            index => ValidatePublishSnapshotAsync(snapshots[index], cancellationToken),
            result => result.Succeeded).ConfigureAwait(false);
        return results.Count == snapshots.Count && results.All(result => result.Succeeded);
    }

    internal async Task<OperationResult> DcommitPreparedAsync(
        GitSvnPublishSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var validation = await validator.ValidatePreparedSnapshotAsync(snapshot, runDryRun: true, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var finalValidation = await validator.ValidatePreparedSnapshotAsync(snapshot, runDryRun: false, cancellationToken)
            .ConfigureAwait(false);
        if (!finalValidation.Succeeded)
        {
            return finalValidation;
        }

        return await ExecutePreparedDcommitAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<OperationResult>> DcommitPreparedAllAsync(
        IReadOnlyList<GitSvnPublishSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        if (snapshots == null)
        {
            throw new ArgumentNullException(nameof(snapshots));
        }

        var results = new List<OperationResult>();

        var validations = await RepositoryBatch.RunAsync(
            snapshots.Select(snapshot => snapshot.RepositoryPath).ToArray(),
            index => validator.ValidatePreparedSnapshotAsync(snapshots[index], runDryRun: true, cancellationToken),
            result => result.Succeeded).ConfigureAwait(false);
        foreach (var validation in validations)
        {
            if (!validation.Succeeded)
            {
                results.Add(validation);
                return results;
            }
        }

        foreach (var snapshot in snapshots)
        {
            var finalValidation = await validator.ValidatePreparedSnapshotAsync(snapshot, runDryRun: false, cancellationToken)
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

    internal async Task<PublishBatchResult> DcommitPreparedBatchAsync(
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
            var validations = await RepositoryBatch.RunAsync(
                snapshots.Select(snapshot => snapshot.RepositoryPath).ToArray(),
                async index =>
                {
                    var snapshot = snapshots[index];
                    try
                    {
                        ReportPublishProgress(progress, PublishProgressPhase.Revalidating, snapshot, index, snapshots.Count);
                        var validation = await validator.ValidatePreparedSnapshotAsync(
                            snapshot, runDryRun: true, cancellationToken).ConfigureAwait(false);
                        return ToPublishOutcome(snapshot,
                            validation.Succeeded ? PublishOutcomeKind.Succeeded : PublishOutcomeKind.Failed,
                            validation.Message);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return ToPublishOutcome(snapshot, PublishOutcomeKind.Cancelled, "사용자가 현재 게시 작업을 취소했습니다.");
                    }
                    catch (Exception exception)
                    {
                        return ToPublishOutcome(snapshot, PublishOutcomeKind.Failed,
                            "게시 작업을 완료하지 못했습니다: " + exception.Message);
                    }
                },
                result => result.Kind == PublishOutcomeKind.Succeeded).ConfigureAwait(false);
            for (var index = 0; index < validations.Count; index++)
            {
                if (validations[index].Kind != PublishOutcomeKind.Succeeded)
                {
                    outcomes[index] = validations[index];
                    return CompletePublishOutcomes(snapshots, outcomes);
                }
            }

            for (var index = 0; index < snapshots.Count; index++)
            {
                activeIndex = index;
                var snapshot = snapshots[index];
                ReportPublishProgress(progress, PublishProgressPhase.Revalidating, snapshot, index, snapshots.Count);
                var validation = await validator.ValidatePreparedSnapshotAsync(
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
        catch (Exception exception)
        {
            if (activeIndex >= 0 && activeIndex < snapshots.Count && outcomes[activeIndex] == null)
            {
                outcomes[activeIndex] = ToPublishOutcome(
                    snapshots[activeIndex],
                    PublishOutcomeKind.Failed,
                    "게시 작업을 완료하지 못했습니다: " + exception.Message);
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

    internal async Task<OperationResult> DcommitAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var preparation = await PrepareDcommitAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        return preparation.Succeeded
            ? await DcommitPreparedAsync(preparation.Snapshot!, cancellationToken).ConfigureAwait(false)
            : preparation.Outcome;
    }

    internal async Task<IReadOnlyList<OperationResult>> DcommitAllAsync(
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

}
