using GitSvnShuttle.Vsix;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class SvnManagementFeedbackTests
{
    [Fact]
    public void MissingHistory_ExplainsBranchSelectionAndOffersAReadOnlyRetry()
    {
        var feedback = SvnManagementFeedback.ConnectionFailure(
            "현재 HEAD의 git-svn 추적 이력을 확인하지 못했습니다: Unable to determine upstream SVN information from working tree history");
        Assert.Contains("SVN 연결", feedback.Title);
        Assert.Contains("브랜치로 전환", feedback.NextAction);
        Assert.True(feedback.CanRetryQuery);
    }

    [Fact]
    public void RealTimeoutMessage_ExplainsConnectionAndAuthenticationChecks()
    {
        var feedback = SvnManagementFeedback.ConnectionFailure("Git command exceeded its time limit and was stopped: svn info");
        Assert.Contains("시간이 초과", feedback.Title);
        Assert.Contains("네트워크", feedback.NextAction);
        Assert.Contains("인증", feedback.NextAction);
    }

    [Fact]
    public void CreatedServerBranch_RequiresRecoveryBeforeRetryAndRedactsCredentials()
    {
        var feedback = SvnManagementFeedback.Failure(
            "SVN 브랜치는 이미 생성되었습니다: https://name:secret@svn.example.test/branches/fix\nfetch 실패: authentication failed", true);
        Assert.Contains("서버 브랜치는 생성", feedback.Title);
        Assert.Contains("다시 만들지 마세요", feedback.NextAction);
        Assert.False(feedback.CanRetryQuery);
        Assert.True(feedback.ServerCopyMayHaveCompleted);
        Assert.DoesNotContain("secret", feedback.Details);
        Assert.Contains("/branches/fix", feedback.Details);
    }

    [Fact]
    public void UnknownCopyOutcome_RequiresCheckingTheServerBeforeRetry()
    {
        var feedback = SvnManagementFeedback.Failure("SVN copy가 취소되었습니다. 서버에서 생성 여부를 확인한 뒤 재시도하세요.", true);
        Assert.Contains("생성됐는지 확인", feedback.Title);
        Assert.Contains("서버", feedback.NextAction);
        Assert.False(feedback.CanRetryQuery);
        Assert.True(feedback.ServerCopyMayHaveCompleted);
    }
}
