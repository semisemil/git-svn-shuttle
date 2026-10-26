using GitSvnShuttle.Core;
using GitSvnShuttle.Vsix;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class SvnBranchFormTests
{
    private const string Root = "https://svn.example.test/repo";

    [Fact]
    public void KnownLayout_OnlyNeedsANewNameAndProducesTheDisplayedDestination()
    {
        var form = new SvnBranchForm();
        form.ApplyTracking(Paths(), null);
        Assert.False(form.IsReady);
        Assert.Contains("이름", form.ValidationMessage);

        form.Name = "fix-login";
        var request = form.CreateRequest();

        Assert.Equal(Root + "/trunk/ProjectA", request.SourcePath);
        Assert.Equal(Root + "/branches/ProjectA", request.DestinationPath);
        Assert.Equal("HEAD", request.Revision);
        Assert.Equal("브랜치 생성: fix-login", request.Message);
        Assert.Equal("fix-login", request.LocalBranchName);
        Assert.True(request.SwitchToBranch);
        Assert.Equal(Root + "/branches/ProjectA/fix-login", form.DestinationUrl);
    }

    [Fact]
    public void CurrentSvnHistory_IsPreferredAfterSettingsLoadAndManualSourceIsKept()
    {
        var form = new SvnBranchForm();
        form.ApplyTracking(Paths(), null);
        form.ApplyTracking(Paths(), Root + "/branches/ProjectA/current-work");
        Assert.Equal(Root + "/branches/ProjectA/current-work", form.SourceUrl);
        Assert.Contains(Root + "/branches/ProjectA/current-work", form.SourceChoices);

        form.SourcePath = "tags/ProjectA/release";
        form.ApplyTracking(Paths(), Root + "/trunk/ProjectA");
        Assert.Equal(Root + "/tags/ProjectA/release", form.SourceUrl);
    }

    [Fact]
    public void MixedLayout_RequiresAnExplicitSourceAndDestinationInsteadOfChoosingTheFirst()
    {
        var paths = new GitSvnTrackingPaths(Root, new[] { "trunk/ProjectA", "ProjectA/trunk" },
            new[] { "branches/ProjectA/*", "ProjectA/branches/*" }, Array.Empty<string>());
        var form = new SvnBranchForm { Name = "fix-login" };
        form.ApplyTracking(paths, null);

        Assert.Empty(form.SourcePath);
        Assert.Empty(form.DestinationPath);
        Assert.False(form.IsReady);
        Assert.Throws<ArgumentException>(() => form.CreateRequest());

        form.SourcePath = form.SourceChoices[1];
        form.DestinationPath = form.DestinationChoices[0];
        var request = form.CreateRequest();
        Assert.Equal(Root + "/ProjectA/trunk", request.SourcePath);
        Assert.Equal(Root + "/branches/ProjectA", request.DestinationPath);
    }

    [Fact]
    public void AdvancedOverrides_SurviveNameChangesAndRefreshAndCanReturnToAutomaticValues()
    {
        var form = new SvnBranchForm { Name = "first" };
        form.ApplyTracking(Paths(), null);
        form.LocalName = "work/custom";
        form.Message = "Create release branch from r42";
        form.Revision = "42";
        form.SwitchAfterCreate = false;
        form.DestinationPath = "release/ProjectA";

        form.Name = "release";
        form.ApplyTracking(Paths(), null);
        var request = form.CreateRequest();
        Assert.Equal("work/custom", request.LocalBranchName);
        Assert.Equal("Create release branch from r42", request.Message);
        Assert.Equal("42", request.Revision);
        Assert.False(request.SwitchToBranch);
        Assert.Equal(Root + "/release/ProjectA", request.DestinationPath);

        form.LocalName = "";
        form.Message = "";
        form.Name = "next";
        Assert.Equal("next", form.CreateRequest().LocalBranchName);
        Assert.Equal("브랜치 생성: next", form.CreateRequest().Message);
    }

    [Fact]
    public void DiscoveredPaths_PreserveLiteralPercentAndSpacesInTheActualRequest()
    {
        var form = new SvnBranchForm { Name = "수정 100%" };
        form.ApplyTracking(new GitSvnTrackingPaths(Root, new[] { "trunk/100%20 team" },
            new[] { "branches/100%20 team/*" }, Array.Empty<string>()), null);

        var request = form.CreateRequest();
        Assert.Equal(Root + "/trunk/100%2520%20team", request.SourcePath);
        Assert.Equal(Root + "/branches/100%2520%20team", request.DestinationPath);
        Assert.Equal(Root + "/branches/100%2520%20team/%EC%88%98%EC%A0%95%20100%25", form.DestinationUrl);
        Assert.Equal("수정-100", request.LocalBranchName);
    }

    [Fact]
    public void ExactConnection_DoesNotInventADestinationAndKeepsTheExplicitSourceOnBaseChange()
    {
        var form = new SvnBranchForm { Name = "fix-login" };
        form.ApplyTracking(new GitSvnTrackingPaths(Root + "/trunk/ProjectA", new[] { "" }, Array.Empty<string>(), Array.Empty<string>()), null);
        Assert.Equal(Root + "/trunk/ProjectA", form.SourceUrl);
        Assert.Empty(form.DestinationChoices);
        Assert.Contains("저장 위치", form.ValidationMessage);

        form.SourcePath = "old";
        form.DestinationPath = Root + "/branches/ProjectA";
        form.ApplyTracking(Paths(), null);
        Assert.Equal(Root + "/trunk/ProjectA/old", form.CreateRequest().SourcePath);
        Assert.Equal(Root + "/branches/ProjectA", form.CreateRequest().DestinationPath);
    }

    private static GitSvnTrackingPaths Paths() => new(Root, new[] { "trunk/ProjectA" },
        new[] { "branches/ProjectA/*" }, new[] { "tags/ProjectA/*" });

    [Fact]
    public void ServerCopyThatMayHaveCompleted_CannotBeRepeatedForTheSameDestination()
    {
        var form = new SvnBranchForm { Name = "fix-login" };
        form.ApplyTracking(Paths(), null);
        form.BlockDuplicateCreation();

        Assert.False(form.IsReady);
        Assert.Contains("다시 생성하지 마세요", form.ValidationMessage);
        Assert.Throws<ArgumentException>(() => form.CreateRequest());

        form.Name = "another-fix";
        Assert.True(form.IsReady);
        Assert.Equal("another-fix", form.CreateRequest().BranchName);
    }

    [Fact]
    public void RelativeInputs_FollowAnExplicitChangeToTheDisplayedBaseUrl()
    {
        var form = new SvnBranchForm { Name = "fix-login" };
        form.ApplyTracking(Paths(), null);
        form.ChangeBaseUrl("https://svn.example.test/new-root");

        var request = form.CreateRequest();
        Assert.Equal("https://svn.example.test/new-root/trunk/ProjectA", request.SourcePath);
        Assert.Equal("https://svn.example.test/new-root/branches/ProjectA", request.DestinationPath);
        Assert.Equal("https://svn.example.test/new-root/branches/ProjectA/fix-login", form.DestinationUrl);
    }
}
