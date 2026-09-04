using System;
using System.Collections.Generic;
using System.Linq;
using GitSvnShuttle.Core;

namespace GitSvnShuttle.Vsix;

internal static class OperationPresentation
{
    internal static string BuildPublishSummary(PublishBatchResult result)
    {
        var succeeded = result.Outcomes.Count(outcome => outcome.Kind == PublishOutcomeKind.Succeeded);
        var failed = result.Outcomes.Count(outcome => outcome.Kind == PublishOutcomeKind.Failed);
        var cancelled = result.Outcomes.Count(outcome => outcome.Kind == PublishOutcomeKind.Cancelled);
        var notRun = result.Outcomes.Count(outcome => outcome.Kind == PublishOutcomeKind.NotRun);
        var parts = new List<string>();
        if (succeeded > 0) parts.Add("성공 " + succeeded + "개");
        if (failed > 0) parts.Add("실패 " + failed + "개");
        if (cancelled > 0) parts.Add("취소됨 " + cancelled + "개");
        if (notRun > 0) parts.Add("실행 안 함 " + notRun + "개");

        var problem = result.Outcomes.FirstOrDefault(outcome =>
            outcome.Kind == PublishOutcomeKind.Failed || outcome.Kind == PublishOutcomeKind.Cancelled);
        var summary = "게시 결과 · " + string.Join(" · ", parts);
        return problem == null ? summary : summary + " · " + SensitiveTextRedactor.Redact(problem.Message);
    }

    internal static string BuildOperationStatus(RepositoryOperationOutcome outcome) =>
        OperationName(outcome.Operation) + " " + OutcomeName(outcome.Kind) + " · " + outcome.Message;

    internal static string BuildRebaseSummary(IReadOnlyList<RepositoryOperationOutcome> outcomes) =>
        BuildOperationSummary("SVN 변경 받기 결과", outcomes);

    internal static string BuildOperationSummary(
        string title,
        IReadOnlyList<RepositoryOperationOutcome> outcomes)
    {
        var succeeded = outcomes.Count(outcome => outcome.Kind == RepositoryOperationOutcomeKind.Succeeded);
        var failed = outcomes.Count(outcome => outcome.Kind == RepositoryOperationOutcomeKind.Failed);
        var cancelled = outcomes.Count(outcome => outcome.Kind == RepositoryOperationOutcomeKind.Cancelled);
        var notRun = outcomes.Count(outcome => outcome.Kind == RepositoryOperationOutcomeKind.NotRun);
        var parts = new List<string>();
        if (succeeded > 0) parts.Add("성공 " + succeeded + "개");
        if (failed > 0) parts.Add("실패 " + failed + "개");
        if (cancelled > 0) parts.Add("취소됨 " + cancelled + "개");
        if (notRun > 0) parts.Add("실행 안 함 " + notRun + "개");
        var problem = outcomes.FirstOrDefault(outcome =>
            outcome.Kind == RepositoryOperationOutcomeKind.Failed ||
            outcome.Kind == RepositoryOperationOutcomeKind.Cancelled);
        var summary = title + " · " + string.Join(" · ", parts);
        return problem == null ? summary : summary + " · " + problem.Message;
    }

    internal static string OperationName(RepositoryOperationKind operation) => operation switch
    {
        RepositoryOperationKind.Rebase => "SVN 변경 받기",
        RepositoryOperationKind.Dcommit => "SVN 게시",
        _ => "Git-SVN 작업",
    };

    internal static string OutcomeName(RepositoryOperationOutcomeKind kind) => kind switch
    {
        RepositoryOperationOutcomeKind.Succeeded => "성공",
        RepositoryOperationOutcomeKind.Failed => "실패",
        RepositoryOperationOutcomeKind.Cancelled => "취소됨",
        RepositoryOperationOutcomeKind.NotRun => "실행 안 함",
        _ => "결과 없음",
    };
}
