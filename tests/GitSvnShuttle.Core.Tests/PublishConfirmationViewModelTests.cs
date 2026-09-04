using GitSvnShuttle.Core;
using GitSvnShuttle.Vsix;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class PublishConfirmationViewModelTests
{
    [Fact]
    public void Prepare_PreservesRepositoryAndCommitOrderAndDistinctDestinations()
    {
        var model = new PublishConfirmationViewModel();
        var snapshots = new List<GitSvnPublishSnapshot>
        {
            Snapshot("b", "https://svn.test/shared", "b1", "b2"),
            Snapshot("a", "https://SVN.test/shared", "a1"),
        };
        model.Prepare(snapshots);
        snapshots.Clear();

        Assert.True(model.IsPublishConfirmationOpen);
        Assert.Equal(new[] { "b1", "b2", "a1" }, model.PendingPublishItems.Select(item => item.ShortHash));
        Assert.Equal(new[] { "b", "b", "a" }, model.PendingPublishItems.Select(item => item.RepositoryName));
        Assert.Equal("커밋 3개를 아래 순서대로 게시합니다.", model.PublishConfirmationSubtitle);
        Assert.Equal("https://svn.test/shared", model.PublishTargetSummary);
        Assert.Equal(new[] { "b", "a" }, model.TakeSnapshots().Select(snapshot => snapshot.RepositoryName));
    }

    [Fact]
    public void TakeSnapshots_ConsumesConfirmationOnceAndNotifiesBindings()
    {
        var model = new PublishConfirmationViewModel();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        model.Prepare(new[] { Snapshot("a", "https://svn.test/a", "a1") });
        notifications.Clear();

        Assert.Single(model.TakeSnapshots());

        Assert.False(model.IsPublishConfirmationOpen);
        Assert.Empty(model.PendingPublishItems);
        Assert.Empty(model.TakeSnapshots());
        Assert.Equal("SVN 대상 확인 불가", model.PublishTargetSummary);
        Assert.Equal("커밋 0개를 아래 순서대로 게시합니다.", model.PublishConfirmationSubtitle);
        Assert.Contains(nameof(model.IsPublishConfirmationOpen), notifications);
        Assert.Contains(nameof(model.PublishConfirmationSubtitle), notifications);
        Assert.Contains(nameof(model.PublishTargetSummary), notifications);
    }

    [Fact]
    public void Close_DiscardsPreviousPreparationAndAllowsFreshConfirmation()
    {
        var model = new PublishConfirmationViewModel();
        model.Prepare(new[] { Snapshot("old", "https://svn.test/old", "old") });
        model.Close();
        Assert.Empty(model.TakeSnapshots());

        model.Prepare(new[] { Snapshot("new", "https://svn.test/new", "new") });

        Assert.True(model.IsPublishConfirmationOpen);
        Assert.Equal("new", Assert.Single(model.PendingPublishItems).Subject);
        Assert.Equal("https://svn.test/new", model.PublishTargetSummary);
        Assert.Equal("new", Assert.Single(model.TakeSnapshots()).RepositoryName);
    }

    [Fact]
    public void EmptyPreparation_DoesNotOpenConfirmation()
    {
        var model = new PublishConfirmationViewModel();
        model.Prepare(Array.Empty<GitSvnPublishSnapshot>());
        Assert.False(model.IsPublishConfirmationOpen);
        Assert.Empty(model.TakeSnapshots());
    }

    [Fact]
    public async Task IsCurrent_ValidatesEverySelectedRepositoryWithoutChangingConfirmation()
    {
        var model = new PublishConfirmationViewModel();
        model.Prepare(new[] { Snapshot("b", "https://svn.test/b", "b1"), Snapshot("a", "https://svn.test/a", "a1") });
        var calls = new List<string>();

        var current = await model.IsCurrentAsync((snapshot, _) =>
        {
            calls.Add(snapshot.RepositoryName);
            return Task.FromResult(new OperationResult(snapshot.RepositoryPath, snapshot.RepositoryName != "a", "check"));
        }, CancellationToken.None);

        Assert.False(current);
        Assert.Equal(new[] { "b", "a" }, calls);
        Assert.Equal(new[] { "b1", "a1" }, model.PendingPublishItems.Select(item => item.ShortHash));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsCurrent_DoesNotAcceptValidationForClosedOrReplacedConfirmation(bool prepareAgain)
    {
        var model = new PublishConfirmationViewModel();
        model.Prepare(new[] { Snapshot("old", "https://svn.test/old", "old") });
        var pending = new TaskCompletionSource<OperationResult>();
        var validation = model.IsCurrentAsync((_, _) => pending.Task, CancellationToken.None);
        model.Close();
        if (prepareAgain)
        {
            model.Prepare(new[] { Snapshot("new", "https://svn.test/new", "new") });
        }
        pending.SetResult(new OperationResult("old", true, "matched"));

        Assert.False(await validation);
        Assert.Equal(prepareAgain, model.IsPublishConfirmationOpen);
        Assert.Equal(prepareAgain ? new[] { "new" } : Array.Empty<string>(),
            model.PendingPublishItems.Select(item => item.ShortHash));
    }

    [Fact]
    public async Task IsCurrent_ClosedConfirmationDoesNotStartValidation()
    {
        var model = new PublishConfirmationViewModel();
        Assert.False(await model.IsCurrentAsync((_, _) => throw new InvalidOperationException("must not run"),
            CancellationToken.None));
    }

    [Fact]
    public async Task IsCurrent_CancellationDuringValidationIsNotReportedAsAMatch()
    {
        var model = new PublishConfirmationViewModel();
        model.Prepare(new[] { Snapshot("a", "https://svn.test/a", "a") });
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.IsCurrentAsync((snapshot, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new OperationResult(snapshot.RepositoryPath, true, "matched"));
        }, cancellation.Token));
    }

    private static GitSvnPublishSnapshot Snapshot(string name, string target, params string[] hashes) =>
        new(name, @"C:\work\" + name, @"C:\work\" + name + @"\.git", hashes.Last(), "baseline",
            hashes.Select(hash => new GitSvnCommit(hash, hash, "Kim", "2026-09-05", hash)).ToArray(),
            "configuration", new[] { target });
}
