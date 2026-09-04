using System;
using System.Collections.Generic;
using System.Linq;
using GitSvnShuttle.Core;

namespace GitSvnShuttle.Vsix;

internal static class OperationOutcomeBuilder
{
    internal static IReadOnlyList<RepositoryOperationOutcome> BuildRebaseOutcomes(
        IReadOnlyList<string> repositoryPaths,
        IReadOnlyList<OperationResult> results,
        BusyExecutionResult execution)
    {
        var outcomes = new List<RepositoryOperationOutcome>(repositoryPaths.Count);
        for (var index = 0; index < repositoryPaths.Count; index++)
        {
            if (index < results.Count)
            {
                var result = results[index];
                outcomes.Add(new RepositoryOperationOutcome(
                    repositoryPaths[index],
                    RepositoryOperationKind.Rebase,
                    result.Succeeded
                        ? RepositoryOperationOutcomeKind.Succeeded
                        : RepositoryOperationOutcomeKind.Failed,
                    result.Message));
                continue;
            }

            if (index == results.Count && execution.Kind != BusyExecutionKind.Completed)
            {
                outcomes.Add(OutcomeFromBusyExecution(
                    repositoryPaths[index],
                    RepositoryOperationKind.Rebase,
                    execution));
                continue;
            }

            outcomes.Add(new RepositoryOperationOutcome(
                repositoryPaths[index],
                RepositoryOperationKind.Rebase,
                RepositoryOperationOutcomeKind.NotRun,
                "앞선 저장소 작업이 완료되지 않아 실행하지 않았습니다."));
        }

        return outcomes;
    }

    internal static IReadOnlyList<RepositoryOperationOutcome> BuildInterruptedOutcomes(
        IReadOnlyList<string> repositoryPaths,
        RepositoryOperationKind operation,
        BusyExecutionResult execution,
        int activeIndex)
    {
        var boundedActiveIndex = repositoryPaths.Count == 0
            ? -1
            : Math.Max(0, Math.Min(activeIndex, repositoryPaths.Count - 1));
        return repositoryPaths.Select((path, index) => index == boundedActiveIndex
                ? OutcomeFromBusyExecution(path, operation, execution)
                : new RepositoryOperationOutcome(
                    path,
                    operation,
                    RepositoryOperationOutcomeKind.NotRun,
                    "작업 결과를 확정하지 못해 실행 완료로 표시하지 않았습니다."))
            .ToArray();
    }

    internal static RepositoryOperationOutcome OutcomeFromBusyExecution(
        string repositoryPath,
        RepositoryOperationKind operation,
        BusyExecutionResult execution)
    {
        var kind = execution.Kind switch
        {
            BusyExecutionKind.Cancelled => RepositoryOperationOutcomeKind.Cancelled,
            BusyExecutionKind.Skipped => RepositoryOperationOutcomeKind.NotRun,
            _ => RepositoryOperationOutcomeKind.Failed,
        };
        var message = execution.Kind == BusyExecutionKind.Completed
            ? "작업 결과를 받지 못했습니다. 로그를 확인하세요."
            : execution.Message;
        return new RepositoryOperationOutcome(repositoryPath, operation, kind, message);
    }
}
