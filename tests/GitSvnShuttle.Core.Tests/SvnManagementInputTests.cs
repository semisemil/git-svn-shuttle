using GitSvnShuttle.Vsix;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class SvnManagementInputTests
{
    [Fact]
    public void ExactUrlTrunk_RemainsPresentAfterEditorRoundTrip()
    {
        var formatted = SvnManagementInput.FormatTrunkPaths(new[] { string.Empty, "trunk/other" });

        Assert.Equal(new[] { ".", "trunk/other" }, SvnManagementInput.SplitTrackingPaths(formatted));
    }

    [Theory]
    [InlineData("trunk/project", "https://svn.example.test/displayed/project/trunk/project")]
    [InlineData(".", "https://svn.example.test/displayed/project")]
    [InlineData("branches/my team/", "https://svn.example.test/displayed/project/branches/my%20team")]
    [InlineData("branches/my%20team", "https://svn.example.test/displayed/project/branches/my%20team")]
    [InlineData("branches/100%25", "https://svn.example.test/displayed/project/branches/100%25")]
    [InlineData("branches/%2520", "https://svn.example.test/displayed/project/branches/%2520")]
    public void RelativeBranchPath_UsesDisplayedBaseAndEscapesEachSegment(string path, string expected) =>
        Assert.Equal(expected, SvnManagementInput.ResolveBranchPath("https://svn.example.test/displayed/project", path));

    [Fact]
    public void AbsoluteBranchUrl_DoesNotDependOnDisplayedBase() =>
        Assert.Equal("svn://other.example.test/branches/feature", SvnManagementInput.ResolveBranchPath(
            string.Empty, "svn://other.example.test/branches/feature"));

    [Theory]
    [InlineData("../trunk")]
    [InlineData("branches/%2e%2e/trunk")]
    [InlineData("branches/name%2Fother")]
    [InlineData("branches/./feature")]
    [InlineData("branches\\feature")]
    [InlineData("/trunk")]
    [InlineData("https://svn.example.test/root/../trunk")]
    public void BranchPath_RejectsTraversalAndAmbiguousSeparators(string path) =>
        Assert.Throws<ArgumentException>(() => SvnManagementInput.ResolveBranchPath("https://svn.example.test/root", path));

    [Theory]
    [InlineData("")]
    [InlineData("relative-base")]
    [InlineData("https://svn.example.test/root?query=yes")]
    public void RelativeBranchPath_RequiresValidDisplayedBase(string baseUrl) =>
        Assert.Throws<ArgumentException>(() => SvnManagementInput.ResolveBranchPath(baseUrl, "branches/project"));
}
