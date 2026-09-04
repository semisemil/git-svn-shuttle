using System;
using System.Collections.Generic;
using System.Linq;

namespace GitSvnShuttle.Core;

public sealed class RepositoryAvailability
{
    public RepositoryAvailability(string path, bool canSelect, bool canExpand)
    {
        Path = path;
        CanSelect = canSelect;
        CanExpand = canExpand;
    }

    public string Path { get; }
    public bool CanSelect { get; }
    public bool CanExpand { get; }
}

public sealed class RepositorySessionState
{
    private readonly OrderedRepositorySelection selection = new OrderedRepositorySelection();
    private readonly HashSet<string> expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RepositoryOperationOutcome> outcomes =
        new Dictionary<string, RepositoryOperationOutcome>(StringComparer.OrdinalIgnoreCase);

    public int SelectedCount => selection.Count;
    public IReadOnlyList<string> SelectedPaths => selection.SelectedPaths;

    public bool SetSelected(string path, bool isSelected, bool canSelect) =>
        selection.SetSelected(path, isSelected, canSelect);

    public bool SelectAll(IEnumerable<string> repositoryPathsInDisplayOrder) =>
        selection.SelectAll(repositoryPathsInDisplayOrder);

    public bool ClearSelection() => selection.Clear();

    public bool? GetAllSelectionState(int selectableRepositoryCount) =>
        selection.GetAllSelectionState(selectableRepositoryCount);

    public bool IsSelected(string path) => selection.SelectedPaths.Any(selectedPath =>
        string.Equals(selectedPath, path, StringComparison.OrdinalIgnoreCase));

    public void SetExpanded(string path, bool isExpanded, bool canExpand)
    {
        if (isExpanded && canExpand)
        {
            expandedPaths.Add(path);
        }
        else
        {
            expandedPaths.Remove(path);
        }
    }

    public bool IsExpanded(string path) => expandedPaths.Contains(path);

    public RepositoryOperationOutcome? GetOutcome(string path) =>
        outcomes.TryGetValue(path, out var outcome) ? outcome : null;

    public void SetOutcome(RepositoryOperationOutcome outcome)
    {
        if (outcome == null)
        {
            throw new ArgumentNullException(nameof(outcome));
        }

        outcomes[outcome.RepositoryPath] = outcome;
    }

    public void SetOutcomes(IEnumerable<RepositoryOperationOutcome> operationOutcomes)
    {
        if (operationOutcomes == null)
        {
            throw new ArgumentNullException(nameof(operationOutcomes));
        }

        outcomes.Clear();
        foreach (var outcome in operationOutcomes)
        {
            outcomes[outcome.RepositoryPath] = outcome;
        }
    }

    public void ApplyPublishResult(PublishBatchResult result)
    {
        if (result == null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        SetOutcomes(result.Outcomes.Select(outcome => new RepositoryOperationOutcome(
            outcome.RepositoryPath,
            RepositoryOperationKind.Dcommit,
            outcome.Kind switch
            {
                PublishOutcomeKind.Succeeded => RepositoryOperationOutcomeKind.Succeeded,
                PublishOutcomeKind.Failed => RepositoryOperationOutcomeKind.Failed,
                PublishOutcomeKind.Cancelled => RepositoryOperationOutcomeKind.Cancelled,
                PublishOutcomeKind.NotRun => RepositoryOperationOutcomeKind.NotRun,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome.Kind)),
            },
            outcome.Message)));
        foreach (var outcome in result.Outcomes.Where(outcome =>
                     outcome.Kind == PublishOutcomeKind.Succeeded))
        {
            selection.SetSelected(outcome.RepositoryPath, isSelected: false, canSelect: true);
        }
    }

    public void ClearOutcomes() => outcomes.Clear();

    public void Reconcile(IEnumerable<RepositoryAvailability> repositories)
    {
        if (repositories == null)
        {
            throw new ArgumentNullException(nameof(repositories));
        }

        var availability = repositories.ToDictionary(
            repository => repository.Path,
            StringComparer.OrdinalIgnoreCase);
        selection.Retain(availability.Values
            .Where(repository => repository.CanSelect)
            .Select(repository => repository.Path));
        expandedPaths.RemoveWhere(path =>
            !availability.TryGetValue(path, out var repository) || !repository.CanExpand);

        foreach (var path in outcomes.Keys.Where(path => !availability.ContainsKey(path)).ToArray())
        {
            outcomes.Remove(path);
        }
    }

    public void Reset()
    {
        selection.Clear();
        expandedPaths.Clear();
        outcomes.Clear();
    }
}
