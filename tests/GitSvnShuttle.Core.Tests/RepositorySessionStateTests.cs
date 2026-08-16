using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class RepositorySessionStateTests
{
    [Fact]
    public void PreparationFailure_MapsFailedRepositoryAndMarksEveryOtherTargetNotRun()
    {
        var result = PublishBatchResult.FromPreparationFailure(
            new[]
            {
                new PublishRepositoryTarget("A", "A"),
                new PublishRepositoryTarget("B", "B"),
                new PublishRepositoryTarget("C", "C"),
            },
            "B",
            "request?token=abc123&mode=1");

        Assert.Equal(
            new[]
            {
                PublishOutcomeKind.NotRun,
                PublishOutcomeKind.Failed,
                PublishOutcomeKind.NotRun,
            },
            result.Outcomes.Select(outcome => outcome.Kind));
        Assert.Contains("token=***", result.Outcomes[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", result.Outcomes[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_PreservesSelectionOrderAndExpansionWhileRemovingInvalidRepositories()
    {
        var state = new RepositorySessionState();
        state.SetSelected("B", isSelected: true, canSelect: true);
        state.SetSelected("A", isSelected: true, canSelect: true);
        state.SetSelected("removed", isSelected: true, canSelect: true);
        state.SetExpanded("A", isExpanded: true, canExpand: true);
        state.SetExpanded("blocked", isExpanded: true, canExpand: true);

        state.Reconcile(new[]
        {
            new RepositoryAvailability("A", canSelect: true, canExpand: true),
            new RepositoryAvailability("B", canSelect: true, canExpand: true),
            new RepositoryAvailability("blocked", canSelect: false, canExpand: false),
        });

        Assert.Equal(new[] { "B", "A" }, state.SelectedPaths);
        Assert.True(state.IsExpanded("A"));
        Assert.False(state.IsExpanded("blocked"));
        Assert.False(state.IsSelected("removed"));
    }

    [Fact]
    public void ApplyPublishResult_RemovesOnlySuccessfulSelectionsAndKeepsIncompleteOrder()
    {
        var state = new RepositorySessionState();
        state.SetSelected("A", isSelected: true, canSelect: true);
        state.SetSelected("B", isSelected: true, canSelect: true);
        state.SetSelected("C", isSelected: true, canSelect: true);

        state.ApplyPublishResult(Result(
            Outcome("A", PublishOutcomeKind.Succeeded),
            Outcome("B", PublishOutcomeKind.Failed),
            Outcome("C", PublishOutcomeKind.NotRun)));

        Assert.Equal(new[] { "B", "C" }, state.SelectedPaths);
        Assert.Equal(PublishOutcomeKind.Succeeded, state.GetOutcome("A")!.Kind);
        Assert.Equal(PublishOutcomeKind.Failed, state.GetOutcome("B")!.Kind);
        Assert.Equal(PublishOutcomeKind.NotRun, state.GetOutcome("C")!.Kind);
    }

    [Fact]
    public void ApplySingleRepositorySuccess_PreservesOtherSelectionsAndOrder()
    {
        var state = new RepositorySessionState();
        state.SetSelected("A", isSelected: true, canSelect: true);
        state.SetSelected("B", isSelected: true, canSelect: true);

        state.ApplyPublishResult(Result(Outcome("C", PublishOutcomeKind.Succeeded)));

        Assert.Equal(new[] { "A", "B" }, state.SelectedPaths);
        Assert.Equal(PublishOutcomeKind.Succeeded, state.GetOutcome("C")!.Kind);
    }

    [Fact]
    public void OutcomesSurviveReconcileUntilManualClearAndResetClearsAllState()
    {
        var state = new RepositorySessionState();
        state.SetSelected("A", isSelected: true, canSelect: true);
        state.SetExpanded("A", isExpanded: true, canExpand: true);
        state.SetOutcomes(new[] { Outcome("A", PublishOutcomeKind.Cancelled) });

        state.Reconcile(new[] { new RepositoryAvailability("A", canSelect: true, canExpand: true) });

        Assert.Equal(PublishOutcomeKind.Cancelled, state.GetOutcome("A")!.Kind);
        Assert.True(state.IsSelected("A"));
        Assert.True(state.IsExpanded("A"));

        state.ClearOutcomes();
        Assert.Null(state.GetOutcome("A"));
        Assert.True(state.IsSelected("A"));
        Assert.True(state.IsExpanded("A"));

        state.SetOutcomes(new[] { Outcome("A", PublishOutcomeKind.Cancelled) });
        state.Reset();
        Assert.Empty(state.SelectedPaths);
        Assert.False(state.IsExpanded("A"));
        Assert.Null(state.GetOutcome("A"));
    }

    private static PublishBatchResult Result(params PublishRepositoryOutcome[] outcomes) => new(outcomes);

    private static PublishRepositoryOutcome Outcome(string path, PublishOutcomeKind kind) =>
        new(path, path, kind, kind.ToString());
}
