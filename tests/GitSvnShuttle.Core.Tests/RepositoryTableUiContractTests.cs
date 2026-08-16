using System.Xml.Linq;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class RepositoryTableUiContractTests
{
    [Fact]
    public void RepositoryTable_DeclaresWideAndCompactLayoutsWithoutHorizontalScrolling()
    {
        var xaml = File.ReadAllText(RepositoryPath(
            "src", "GitSvnShuttle.Vsix", "GitSvnShuttleControl.xaml"));

        Assert.Contains("MinWidth=\"420\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsCompactLayout", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"저장소\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"현재 상태\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"게시 대기\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"SVN 대상\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"개별 작업\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CompactPathText", xaml, StringComparison.Ordinal);
        Assert.Contains("LinkedProjectPathText", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryDetails_DeclareIndependentRowToggleAndAllPublishOrderFields()
    {
        var xaml = File.ReadAllText(RepositoryPath(
            "src", "GitSvnShuttle.Vsix", "GitSvnShuttleControl.xaml"));
        var codeBehind = File.ReadAllText(RepositoryPath(
            "src", "GitSvnShuttle.Vsix", "GitSvnShuttleControl.xaml.cs"));
        var viewModel = File.ReadAllText(RepositoryPath(
            "src", "GitSvnShuttle.Vsix", "GitSvnShuttleViewModel.cs"));

        Assert.Equal(2, CountOccurrences(xaml, "MouseLeftButtonUp=\"OnRepositorySummaryMouseLeftButtonUp\""));
        Assert.Contains("current is ButtonBase", codeBehind, StringComparison.Ordinal);
        Assert.Contains("repository.IsExpanded = !repository.IsExpanded", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private bool isExpanded;", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("private bool isExpanded = true", viewModel, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ShortHash}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Subject}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Author}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Date}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void IconOnlyActions_HaveKoreanTooltipsAccessibilityNamesAndLocalVectorGeometry()
    {
        var path = RepositoryPath("src", "GitSvnShuttle.Vsix", "GitSvnShuttleControl.xaml");
        var xaml = File.ReadAllText(path);
        var document = XDocument.Load(path);
        var iconButtons = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => element.Attribute("Style")?.Value.Contains("IconButton", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.True(iconButtons.Length >= 12);
        foreach (var button in iconButtons)
        {
            Assert.NotNull(button.Attribute("ToolTip"));
            Assert.Contains(
                button.Attributes(),
                attribute => attribute.Name.LocalName == "AutomationProperties.Name" &&
                             !string.IsNullOrWhiteSpace(attribute.Value));
        }

        foreach (var key in new[]
                 {
                     "SettingsGeometry", "RefreshGeometry", "DownloadGeometry", "UploadGeometry",
                     "CloseGeometry", "StopGeometry", "EyeGeometry",
                 })
        {
            Assert.Contains("x:Key=\"" + key + "\"", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("DynamicResource {x:Static shell:VsBrushes", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("pack://", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Source=\"http", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishOutcomes_AreSeparateFromCurrentStatusAndRefreshLifecycleIsExplicit()
    {
        var xaml = File.ReadAllText(RepositoryPath(
            "src", "GitSvnShuttle.Vsix", "GitSvnShuttleControl.xaml"));
        var viewModel = File.ReadAllText(RepositoryPath(
            "src", "GitSvnShuttle.Vsix", "GitSvnShuttleViewModel.cs"));
        var package = File.ReadAllText(RepositoryPath(
            "src", "GitSvnShuttle.Vsix", "GitSvnShuttlePackage.cs"));

        Assert.Equal(2, CountOccurrences(xaml, "Visibility=\"{Binding PublishOutcomeVisibility}\""));
        Assert.Contains("Text=\"{Binding StatusText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding PublishOutcomeText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding PublishOutcomeMessage}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding PublishOutcomeAutomationName}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("repositoryState.ApplyPublishResult(batchResult)", viewModel, StringComparison.Ordinal);
        Assert.Contains("RepositoryRefreshReason.Automatic", viewModel, StringComparison.Ordinal);
        Assert.Contains("저장소 변경을 감지해 게시 확인을 닫고 준비 상태를 폐기했습니다.", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (disposed || IsBusy)", viewModel, StringComparison.Ordinal);
        Assert.Contains("IVsSolutionEvents.OnBeforeCloseSolution", package, StringComparison.Ordinal);
        Assert.Contains("IVsSolutionEvents.OnAfterOpenSolution", package, StringComparison.Ordinal);
        Assert.Contains("ResetRepositorySession();", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("publishPreparationProblem", viewModel, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string target)
    {
        var count = 0;
        var position = 0;
        while ((position = value.IndexOf(target, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += target.Length;
        }

        return count;
    }

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
