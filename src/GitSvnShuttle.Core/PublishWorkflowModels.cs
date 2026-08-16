using System;
using System.Collections.Generic;
using System.Linq;

namespace GitSvnShuttle.Core;

public enum PublishProgressPhase
{
    Preparing,
    Revalidating,
    Publishing,
}

public enum PublishOutcomeKind
{
    Succeeded,
    Failed,
    Cancelled,
    NotRun,
}

public sealed class PublishProgress
{
    public PublishProgress(
        PublishProgressPhase phase,
        string repositoryName,
        string repositoryPath,
        int repositoryIndex,
        int repositoryCount)
    {
        Phase = phase;
        RepositoryName = repositoryName;
        RepositoryPath = repositoryPath;
        RepositoryIndex = repositoryIndex;
        RepositoryCount = repositoryCount;
    }

    public PublishProgressPhase Phase { get; }
    public string RepositoryName { get; }
    public string RepositoryPath { get; }
    public int RepositoryIndex { get; }
    public int RepositoryCount { get; }
}

public sealed class PublishRepositoryOutcome
{
    public PublishRepositoryOutcome(
        string repositoryName,
        string repositoryPath,
        PublishOutcomeKind kind,
        string message)
    {
        RepositoryName = repositoryName;
        RepositoryPath = repositoryPath;
        Kind = kind;
        Message = SensitiveTextRedactor.Redact(message);
    }

    public string RepositoryName { get; }
    public string RepositoryPath { get; }
    public PublishOutcomeKind Kind { get; }
    public string Message { get; }
}

public sealed class PublishRepositoryTarget
{
    public PublishRepositoryTarget(string repositoryName, string repositoryPath)
    {
        RepositoryName = repositoryName;
        RepositoryPath = repositoryPath;
    }

    public string RepositoryName { get; }
    public string RepositoryPath { get; }
}

public sealed class PublishBatchResult
{
    public PublishBatchResult(IReadOnlyList<PublishRepositoryOutcome> outcomes)
    {
        Outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
    }

    public IReadOnlyList<PublishRepositoryOutcome> Outcomes { get; }
    public bool Succeeded => Outcomes.Count > 0 && Outcomes.All(outcome =>
        outcome.Kind == PublishOutcomeKind.Succeeded);

    public static PublishBatchResult FromPreparationFailure(
        IReadOnlyList<PublishRepositoryTarget> targets,
        string failedRepositoryPath,
        string failureMessage)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        return new PublishBatchResult(targets.Select(target => string.Equals(
                target.RepositoryPath,
                failedRepositoryPath,
                StringComparison.OrdinalIgnoreCase)
            ? new PublishRepositoryOutcome(
                target.RepositoryName,
                target.RepositoryPath,
                PublishOutcomeKind.Failed,
                failureMessage)
            : new PublishRepositoryOutcome(
                target.RepositoryName,
                target.RepositoryPath,
                PublishOutcomeKind.NotRun,
                "게시 준비가 완료되지 않아 실행하지 않았습니다."))
            .ToArray());
    }
}
