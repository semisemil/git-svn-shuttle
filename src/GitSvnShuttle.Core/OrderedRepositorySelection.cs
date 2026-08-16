using System;
using System.Collections.Generic;
using System.Linq;

namespace GitSvnShuttle.Core;

public sealed class OrderedRepositorySelection
{
    private readonly List<string> selectedPaths = new List<string>();

    public int Count => selectedPaths.Count;

    public IReadOnlyList<string> SelectedPaths => selectedPaths.ToArray();

    public bool SetSelected(string repositoryPath, bool isSelected, bool canSelect)
    {
        var existingIndex = selectedPaths.FindIndex(path =>
            string.Equals(path, repositoryPath, StringComparison.OrdinalIgnoreCase));

        if (isSelected)
        {
            if (!canSelect || existingIndex >= 0)
            {
                return false;
            }

            selectedPaths.Add(repositoryPath);
            return true;
        }

        if (existingIndex < 0)
        {
            return false;
        }

        selectedPaths.RemoveAt(existingIndex);
        return true;
    }

    public bool SelectAll(IEnumerable<string> repositoryPathsInDisplayOrder)
    {
        if (repositoryPathsInDisplayOrder == null)
        {
            throw new ArgumentNullException(nameof(repositoryPathsInDisplayOrder));
        }

        var orderedPaths = repositoryPathsInDisplayOrder
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedPaths.SequenceEqual(orderedPaths, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        selectedPaths.Clear();
        selectedPaths.AddRange(orderedPaths);
        return true;
    }

    public bool Clear()
    {
        if (selectedPaths.Count == 0)
        {
            return false;
        }

        selectedPaths.Clear();
        return true;
    }

    public bool Retain(IEnumerable<string> selectableRepositoryPaths)
    {
        if (selectableRepositoryPaths == null)
        {
            throw new ArgumentNullException(nameof(selectableRepositoryPaths));
        }

        var selectable = new HashSet<string>(
            selectableRepositoryPaths,
            StringComparer.OrdinalIgnoreCase);
        var removed = selectedPaths.RemoveAll(path => !selectable.Contains(path));
        return removed > 0;
    }

    public bool? GetAllSelectionState(int selectableRepositoryCount)
    {
        if (selectedPaths.Count == 0 || selectableRepositoryCount <= 0)
        {
            return false;
        }

        return selectedPaths.Count == selectableRepositoryCount ? true : null;
    }
}
