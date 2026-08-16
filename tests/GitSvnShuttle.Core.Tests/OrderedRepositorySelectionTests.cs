using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class OrderedRepositorySelectionTests
{
    [Fact]
    public void SetSelected_TracksOnlySelectableRepositoriesInSelectionOrder()
    {
        var selection = new OrderedRepositorySelection();

        Assert.False(selection.GetAllSelectionState(selectableRepositoryCount: 3));
        Assert.True(selection.SetSelected("B", isSelected: true, canSelect: true));
        Assert.Equal(1, selection.Count);
        Assert.Null(selection.GetAllSelectionState(selectableRepositoryCount: 3));
        Assert.False(selection.SetSelected("blocked", isSelected: true, canSelect: false));
        Assert.True(selection.SetSelected("A", isSelected: true, canSelect: true));

        Assert.Equal(2, selection.Count);
        Assert.Equal(new[] { "B", "A" }, selection.SelectedPaths);
        Assert.Null(selection.GetAllSelectionState(selectableRepositoryCount: 3));
    }

    [Fact]
    public void SelectAll_ReplacesIndividualSelectionOrderWithCurrentDisplayOrder()
    {
        var selection = new OrderedRepositorySelection();
        selection.SetSelected("B", isSelected: true, canSelect: true);

        Assert.True(selection.SelectAll(new[] { "A", "B", "C" }));

        Assert.Equal(new[] { "A", "B", "C" }, selection.SelectedPaths);
        Assert.True(selection.GetAllSelectionState(selectableRepositoryCount: 3));
    }

    [Fact]
    public void Clear_ReturnsSelectionToZeroWithoutChangingAvailableRepositories()
    {
        var selection = new OrderedRepositorySelection();
        selection.SelectAll(new[] { "A", "B" });

        Assert.True(selection.Clear());

        Assert.Empty(selection.SelectedPaths);
        Assert.False(selection.GetAllSelectionState(selectableRepositoryCount: 2));
    }
}
