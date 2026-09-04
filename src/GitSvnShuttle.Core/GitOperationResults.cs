using System;

namespace GitSvnShuttle.Core;

internal static class GitOperationResults
{
    internal static PublishPreparationResult PreparationFailed(string path, string message) =>
        new PublishPreparationResult(Failed(path, message), null);

    internal static OperationResult SnapshotChanged(string path) =>
        Failed(path, "확인 후 저장소 상태 또는 SVN 설정이 변경되었습니다. 새로 고친 뒤 다시 확인하세요.");

    internal static OperationResult Failed(string path, string message) =>
        new OperationResult(path, false, message);

    internal static OperationResult ToOperationResult(string path, string action, GitCommandResult result) =>
        new OperationResult(
            path,
            result.Succeeded,
            result.Succeeded
                ? action + " 완료" + (string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? string.Empty
                    : Environment.NewLine + result.CombinedOutput)
                : action + " 실패" + Environment.NewLine + result.CombinedOutput);
}
