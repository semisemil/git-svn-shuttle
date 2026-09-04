using GitSvnShuttle.Core;
using GitSvnShuttle.Vsix;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class OperationOutcomeBuilderTests
{
    [Fact]
    public void RebaseFailure_PreservesCompletedResultsAndMarksRemainderNotRun()
    {
        var outcomes = OperationOutcomeBuilder.BuildRebaseOutcomes(
            new[] { "b", "a", "c" },
            new[] { new OperationResult("b", true, "done"), new OperationResult("a", false, "conflict") },
            BusyExecutionResult.Completed());

        Assert.Equal(new[] { "b", "a", "c" }, outcomes.Select(outcome => outcome.RepositoryPath));
        Assert.Equal(new[] { RepositoryOperationOutcomeKind.Succeeded, RepositoryOperationOutcomeKind.Failed,
            RepositoryOperationOutcomeKind.NotRun }, outcomes.Select(outcome => outcome.Kind));
        Assert.Equal("conflict", outcomes[1].Message);
    }

    [Fact]
    public void RebaseCancellation_PreservesCompletedResultsAndIdentifiesInterruptedRepository()
    {
        var outcomes = OperationOutcomeBuilder.BuildRebaseOutcomes(
            new[] { "a", "b", "c" }, new[] { new OperationResult("a", true, "done") },
            BusyExecutionResult.Cancelled());

        Assert.Equal(new[] { RepositoryOperationOutcomeKind.Succeeded, RepositoryOperationOutcomeKind.Cancelled,
            RepositoryOperationOutcomeKind.NotRun }, outcomes.Select(outcome => outcome.Kind));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 1)]
    [InlineData(8, 2)]
    public void InterruptedPublish_AssignsFailureOnlyToActiveRepository(int activeIndex, int expectedIndex)
    {
        var outcomes = OperationOutcomeBuilder.BuildInterruptedOutcomes(
            new[] { "a", "b", "c" }, RepositoryOperationKind.Dcommit,
            BusyExecutionResult.Failed("failed token=secret"), activeIndex);

        Assert.Equal(RepositoryOperationOutcomeKind.Failed, outcomes[expectedIndex].Kind);
        Assert.All(outcomes.Where((_, index) => index != expectedIndex), outcome =>
            Assert.Equal(RepositoryOperationOutcomeKind.NotRun, outcome.Kind));
        Assert.DoesNotContain("secret", outcomes[expectedIndex].Message);
    }

    [Fact]
    public void EmptyInterruptedBatch_HasNoInventedOutcome()
    {
        Assert.Empty(OperationOutcomeBuilder.BuildInterruptedOutcomes(
            Array.Empty<string>(), RepositoryOperationKind.Dcommit, BusyExecutionResult.Cancelled(), 0));
    }

    [Fact]
    public void SkippedExecution_IsNotReportedAsFailureOrSuccess()
    {
        var outcome = OperationOutcomeBuilder.OutcomeFromBusyExecution(
            "a", RepositoryOperationKind.Rebase, BusyExecutionResult.Skipped());
        Assert.Equal(RepositoryOperationOutcomeKind.NotRun, outcome.Kind);
    }

    [Fact]
    public void MissingResult_IsNotReportedAsSuccess()
    {
        var outcome = OperationOutcomeBuilder.OutcomeFromBusyExecution(
            "a", RepositoryOperationKind.Rebase, BusyExecutionResult.Completed());
        Assert.Equal(RepositoryOperationOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("작업 결과를 받지 못했습니다", outcome.Message);
    }
}
