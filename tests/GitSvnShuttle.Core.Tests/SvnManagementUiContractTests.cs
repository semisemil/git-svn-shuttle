using System.Xml.Linq;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class SvnManagementUiContractTests
{
    [Fact]
    public void Management_ExposesExistingAndNewConnectionsAndIndependentMultilinePathLists()
    {
        var document = XDocument.Load(RepositoryPath("src", "GitSvnShuttle.Vsix", "GitSvnShuttleControl.xaml"));
        var buttons = document.Descendants().Where(element => element.Name.LocalName == "Button").ToArray();
        Assert.Contains(buttons, button => HasBinding(button, "Command", "NewSvnConnectionCommand"));
        Assert.Contains(buttons, button => HasBinding(button, "Command", "ManageSvnCommand"));
        Assert.Contains(buttons, button => HasBinding(button, "Command", "LoadTrackingPathsCommand"));
        Assert.Contains(buttons, button => HasBinding(button, "Command", "SaveTrackingPathsCommand"));

        foreach (var binding in new[] { "TrunkPathsText", "BranchesPathsText", "TagsPathsText" })
        {
            var input = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "TextBox" && HasBinding(element, "Text", binding));
            Assert.Equal("True", input.Attribute("AcceptsReturn")?.Value);
            Assert.Contains("UpdateSourceTrigger=PropertyChanged", input.Attribute("Text")!.Value, StringComparison.Ordinal);
        }

        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock" && HasBinding(element, "Text", "RetainedTrackingPathsText"));
    }

    [Fact]
    public void BranchCreation_OnlyShowsSourceNameAndDestinationUntilAdvancedSettingsAreOpened()
    {
        var document = XDocument.Load(RepositoryPath("src", "GitSvnShuttle.Vsix", "GitSvnShuttleControl.xaml"));
        foreach (var binding in new[] { "BranchForm.SourcePath", "BranchForm.Name", "BranchForm.DestinationPath" })
        {
            var input = Assert.Single(document.Descendants(), element =>
                (element.Name.LocalName == "TextBox" || element.Name.LocalName == "ComboBox") && HasBinding(element, "Text", binding));
            Assert.DoesNotContain(input.Ancestors(), element => element.Name.LocalName == "Expander");
        }

        foreach (var binding in new[] { "BranchForm.Revision", "BranchForm.Message", "BranchForm.LocalName" })
        {
            var input = Assert.Single(document.Descendants(), element => element.Name.LocalName == "TextBox" && HasBinding(element, "Text", binding));
            var advanced = Assert.Single(input.Ancestors(), element => element.Name.LocalName == "Expander");
            Assert.NotEqual("True", advanced.Attribute("IsExpanded")?.Value);
        }
        Assert.Contains(document.Descendants(), element => HasBinding(element, "IsChecked", "BranchForm.SwitchAfterCreate"));
        Assert.Contains(document.Descendants(), element => HasBinding(element, "Text", "BranchForm.DestinationUrl"));
        Assert.Contains(document.Descendants(), element => HasBinding(element, "Text", "BranchForm.ValidationMessage"));
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Button" && HasBinding(element, "Command", "CreateSvnBranchCommand"));
    }

    [Fact]
    public void Management_ResolvesActualTargetAndKeepsCancellationAvailableDuringOperations()
    {
        var xaml = File.ReadAllText(RepositoryPath("src", "GitSvnShuttle.Vsix", "GitSvnShuttleControl.xaml"));
        var viewModel = File.ReadAllText(RepositoryPath("src", "GitSvnShuttle.Vsix", "GitSvnShuttleViewModel.SvnManagement.cs"));

        Assert.Contains("Text=\"{Binding ActualDcommitTarget}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WorkspaceService.ResolveDcommitTargetAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("target.IsResolved", viewModel, StringComparison.Ordinal);
        Assert.Contains("SensitiveTextRedactor.Redact(target.Url)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("SvnTargets", viewModel, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ManagementProgressText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding NextAction}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"작업 취소\" Command=\"{Binding CancelOperationCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"닫기\" Command=\"{Binding CloseSvnManagementCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ClosePublishConfirmation();", viewModel, StringComparison.Ordinal);
        Assert.Contains("RefreshRepositoriesAsync(RepositoryRefreshReason.Internal)", viewModel, StringComparison.Ordinal);
    }

    private static bool HasBinding(XElement element, string attribute, string property) =>
        element.Attribute(attribute)?.Value.StartsWith("{Binding " + property, StringComparison.Ordinal) == true;

    private static string RepositoryPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "git-svn-shuttle.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(new[] { directory!.FullName }.Concat(segments).ToArray());
    }
}
