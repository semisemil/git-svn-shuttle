using GitSvnShuttle.Core;
using GitSvnShuttle.Vsix;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class OperationPresentationTests
{
    [Fact]
    public void PublishSummary_CountsEachOutcomeAndUsesFirstProblemInRepositoryOrder()
    {
        var result = new PublishBatchResult(new[]
        {
            new PublishRepositoryOutcome("a", "a", PublishOutcomeKind.Succeeded, "done"),
            new PublishRepositoryOutcome("b", "b", PublishOutcomeKind.Cancelled, "cancelled"),
            new PublishRepositoryOutcome("c", "c", PublishOutcomeKind.Failed, "later failure"),
            new PublishRepositoryOutcome("d", "d", PublishOutcomeKind.NotRun, "not run"),
        });

        Assert.Equal("게시 결과 · 성공 1개 · 실패 1개 · 취소됨 1개 · 실행 안 함 1개 · cancelled",
            OperationPresentation.BuildPublishSummary(result));
    }

    [Fact]
    public void RebaseSummaryAndStatus_UseConsistentLabelsAndRedactedMessages()
    {
        var outcome = new RepositoryOperationOutcome("a", RepositoryOperationKind.Rebase,
            RepositoryOperationOutcomeKind.Failed, "token=secret");
        var summary = OperationPresentation.BuildRebaseSummary(new[] { outcome });
        var status = OperationPresentation.BuildOperationStatus(outcome);

        Assert.StartsWith("SVN 변경 받기 결과 · 실패 1개 · ", summary);
        Assert.StartsWith("SVN 변경 받기 실패 · ", status);
        Assert.DoesNotContain("secret", summary);
        Assert.DoesNotContain("secret", status);
        Assert.EndsWith(outcome.Message, summary);
        Assert.EndsWith(outcome.Message, status);
    }
}
