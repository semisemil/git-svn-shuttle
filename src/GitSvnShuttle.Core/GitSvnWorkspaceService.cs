using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

public sealed class GitSvnWorkspaceService
{
    private readonly GitRepositoryReader reader;
    private readonly GitSvnRepositoryDiscovery discovery;
    private readonly GitSvnRebaseWorkflow rebase;
    private readonly GitSvnPublishWorkflow publish;
    private readonly GitSvnTrackingWorkflow tracking;
    private readonly GitSvnTargetResolver targetResolver;
    private readonly GitSvnBranchWorkflow branchWorkflow;

    public GitSvnWorkspaceService(IGitCommandRunner runner, ISvnCommandRunner? svnRunner = null)
    {
        if (runner == null)
        {
            throw new ArgumentNullException(nameof(runner));
        }

        reader = new GitRepositoryReader(runner);
        discovery = new GitSvnRepositoryDiscovery(runner, reader);
        rebase = new GitSvnRebaseWorkflow(runner, reader);
        publish = new GitSvnPublishWorkflow(runner, new GitSvnPublishValidator(runner, reader));
        tracking = new GitSvnTrackingWorkflow(runner);
        targetResolver = new GitSvnTargetResolver(runner);
        branchWorkflow = new GitSvnBranchWorkflow(runner, svnRunner ?? new ProcessSvnCommandRunner(), reader);
    }

    public Task<GitSvnTrackingPaths> GetTrackingPathsAsync(string repositoryPath, CancellationToken cancellationToken) =>
        tracking.GetAsync(repositoryPath, cancellationToken);

    public Task<OperationResult> ConfigureTrackingPathsAsync(string repositoryPath, GitSvnTrackingPaths paths,
        CancellationToken cancellationToken) => tracking.ConfigureAsync(repositoryPath, paths, cancellationToken);

    public Task<GitSvnDcommitTarget> ResolveDcommitTargetAsync(string repositoryPath, CancellationToken cancellationToken) =>
        targetResolver.ResolveAsync(repositoryPath, cancellationToken);

    public Task<OperationResult> CreateSvnBranchAsync(string repositoryPath, GitSvnBranchRequest request,
        CancellationToken cancellationToken) => branchWorkflow.CreateAsync(repositoryPath, request, cancellationToken);

    public Task<IReadOnlyList<GitSvnRepository>> DiscoverAsync(
        string solutionDirectory,
        CancellationToken cancellationToken) =>
        discovery.DiscoverAsync(solutionDirectory, cancellationToken);

    public Task<IReadOnlyList<GitSvnRepository>> DiscoverAsync(
        string solutionDirectory,
        IReadOnlyList<string> loadedProjectPaths,
        CancellationToken cancellationToken) =>
        discovery.DiscoverAsync(solutionDirectory, loadedProjectPaths, cancellationToken);

    public Task<GitSvnRepository> InspectAsync(string repositoryPath, CancellationToken cancellationToken) =>
        reader.InspectAsync(repositoryPath, cancellationToken);

    public Task<OperationResult> RebaseAsync(string repositoryPath, CancellationToken cancellationToken) =>
        rebase.RebaseAsync(repositoryPath, cancellationToken);

    public Task<OperationResult> ContinueRebaseAsync(
        string repositoryPath,
        CancellationToken cancellationToken) =>
        rebase.ContinueRebaseAsync(repositoryPath, cancellationToken);

    public Task<OperationResult> AbortRebaseAsync(
        string repositoryPath,
        bool confirmed,
        CancellationToken cancellationToken) =>
        rebase.AbortRebaseAsync(repositoryPath, confirmed, cancellationToken);

    public Task<PublishPreparationResult> PrepareDcommitAsync(
        string repositoryPath,
        CancellationToken cancellationToken) =>
        publish.PrepareDcommitAsync(repositoryPath, cancellationToken);

    public Task<PublishBatchPreparationResult> PrepareDcommitAllAsync(
        IReadOnlyList<string> repositoryPaths,
        CancellationToken cancellationToken,
        IProgress<PublishProgress>? progress = null) =>
        publish.PrepareDcommitAllAsync(repositoryPaths, cancellationToken, progress);

    public Task<OperationResult> DcommitPreparedAsync(
        GitSvnPublishSnapshot snapshot,
        CancellationToken cancellationToken) =>
        publish.DcommitPreparedAsync(snapshot, cancellationToken);

    public Task<OperationResult> ValidatePublishSnapshotAsync(
        GitSvnPublishSnapshot snapshot,
        CancellationToken cancellationToken) =>
        publish.ValidatePublishSnapshotAsync(snapshot, cancellationToken);

    public Task<IReadOnlyList<OperationResult>> DcommitPreparedAllAsync(
        IReadOnlyList<GitSvnPublishSnapshot> snapshots,
        CancellationToken cancellationToken) =>
        publish.DcommitPreparedAllAsync(snapshots, cancellationToken);

    public Task<PublishBatchResult> DcommitPreparedBatchAsync(
        IReadOnlyList<GitSvnPublishSnapshot> snapshots,
        IProgress<PublishProgress>? progress,
        CancellationToken cancellationToken) =>
        publish.DcommitPreparedBatchAsync(snapshots, progress, cancellationToken);

    public Task<OperationResult> DcommitAsync(string repositoryPath, CancellationToken cancellationToken) =>
        publish.DcommitAsync(repositoryPath, cancellationToken);

    public Task<IReadOnlyList<OperationResult>> RebaseAllAsync(
        IReadOnlyList<string> repositoryPaths,
        CancellationToken cancellationToken) =>
        rebase.RebaseAllAsync(repositoryPaths, cancellationToken);

    public Task<IReadOnlyList<OperationResult>> DcommitAllAsync(
        IReadOnlyList<string> repositoryPaths,
        CancellationToken cancellationToken) =>
        publish.DcommitAllAsync(repositoryPaths, cancellationToken);
}
