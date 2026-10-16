using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class GitSvnTargetTests
{
    private const string RepositoryPath = "/work/project";
    private const string Root = "https://svn.example.test/repository";

    [Theory]
    [InlineData("project/trunk", "fetch", "project/trunk:refs/remotes/old/trunk", "refs/remotes/old/trunk")]
    [InlineData("project/branches/bugfix", "branches", "project/branches/*:refs/remotes/old/*", "refs/remotes/old/bugfix")]
    [InlineData("project/tags/release-1", "tags", "project/tags/*:refs/remotes/old/tags/*", "refs/remotes/old/tags/release-1")]
    [InlineData("trunk/project", "fetch", "trunk/project:refs/remotes/new/trunk", "refs/remotes/new/trunk")]
    [InlineData("branches/project-bugfix", "branches", "branches/*:refs/remotes/new/*", "refs/remotes/new/project-bugfix")]
    [InlineData("branches/project/bugfix", "branches", "branches/project/*:refs/remotes/new/*", "refs/remotes/new/bugfix")]
    [InlineData("tags/project/release-1", "tags", "tags/project/*:refs/remotes/new/tags/*", "refs/remotes/new/tags/release-1")]
    public async Task ResolveTarget_UsesSvnHistoryAndTrackedPathAcrossLayouts(
        string path, string kind, string mapping, string expectedRef)
    {
        var (service, runner) = Create(Root + "/" + path,
            "svn-remote.selected.url " + Root + "\n" +
            "svn-remote.selected." + kind + " " + mapping);

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.Equal(Root + "/" + path, target.Url);
        Assert.Equal(Root + "/" + path, target.SvnUrl);
        Assert.Equal("selected", target.RemoteName);
        Assert.Equal(expectedRef, target.RemoteRef);
        Assert.Equal("42", target.Revision);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.StartsWith("symbolic-ref "));
    }

    [Theory]
    [InlineData("project/branches/old-fix", "old", "refs/remotes/old/old-fix")]
    [InlineData("branches/project/new-fix", "new", "refs/remotes/new/new-fix")]
    public async Task ResolveTarget_SelectsCorrectRemoteWhenLayoutsCoexist(
        string path, string remote, string expectedRef)
    {
        var config =
            "svn-remote.old.url " + Root + "\n" +
            "svn-remote.old.fetch project/trunk:refs/remotes/old/trunk\n" +
            "svn-remote.old.branches project/branches/*:refs/remotes/old/*\n" +
            "svn-remote.new.url " + Root + "\n" +
            "svn-remote.new.fetch trunk/project:refs/remotes/new/trunk\n" +
            "svn-remote.new.branches branches/project/*:refs/remotes/new/*";
        var (service, _) = Create(Root + "/" + path, config);

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.Equal(Root + "/" + path, target.Url);
        Assert.Equal(remote, target.RemoteName);
        Assert.Equal(expectedRef, target.RemoteRef);
    }

    [Fact]
    public async Task ResolveTarget_MatchesMetadataWithoutConfiguredTransportUserInfo()
    {
        var (service, _) = Create("svn+ssh://svn.example.test/repository/branches/project/fix",
            "svn-remote.selected.url svn+ssh://developer@svn.example.test/repository\n" +
            "svn-remote.selected.branches branches/project/*:refs/remotes/selected/*");

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.Equal("svn+ssh://***@svn.example.test/repository/branches/project/fix", target.Url);
        Assert.DoesNotContain("developer", target.Url);
    }

    [Fact]
    public async Task ResolveTarget_IgnoresUnrelatedRemoteCommitUrl()
    {
        var (service, _) = Create(Root + "/branches/project/fix", BranchConfiguration + "\n" +
            "svn-remote.unrelated.url https://svn.example.test/other\n" +
            "svn-remote.unrelated.fetch trunk:refs/remotes/other/trunk\n" +
            "svn-remote.unrelated.commiturl https://write.example.test/wrong-trunk");

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.Equal(Root + "/branches/project/fix", target.Url);
    }

    [Fact]
    public async Task ResolveTarget_RemoteCommitUrlOverridesPushUrlWithoutAppendingTrackedPath()
    {
        const string commitUrl = "https://write.example.test/exact-branch";
        var (service, _) = Create(Root + "/branches/project/fix", BranchConfiguration + "\n" +
            "svn-remote.selected.pushurl https://push.example.test/repository\n" +
            "svn-remote.selected.commiturl " + commitUrl);

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.Equal(commitUrl, target.Url);
        Assert.Equal(Root + "/branches/project/fix", target.SvnUrl);
    }

    [Fact]
    public async Task ResolveTarget_GlobalCommitUrlOverridesRemoteCommitUrl()
    {
        const string commitUrl = "https://write.example.test/global-branch";
        var (service, _) = Create(Root + "/branches/project/fix", BranchConfiguration + "\n" +
            "svn-remote.selected.commiturl https://write.example.test/remote-branch\n" +
            "svn.commiturl " + commitUrl);

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.Equal(commitUrl, target.Url);
    }

    [Fact]
    public async Task ResolveTarget_PushUrlKeepsTrackedNestedPath()
    {
        var (service, _) = Create(Root + "/branches/project/fix", BranchConfiguration + "\n" +
            "svn-remote.selected.pushurl https://write.example.test/repository");

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.Equal("https://write.example.test/repository/branches/project/fix", target.Url);
    }

    [Fact]
    public async Task ResolveTarget_DecodesMetadataUrlWhenMatchingUnescapedSvnPaths()
    {
        var (service, _) = Create(Root + "/branches/project%20name/fix",
            "svn-remote.selected.url " + Root + "\n" +
            "svn-remote.selected.branches branches/project name/*:refs/remotes/selected/*\n" +
            "svn-remote.selected.pushurl https://write.example.test/repository");

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.Equal("https://write.example.test/repository/branches/project%20name/fix", target.Url);
        Assert.Equal("refs/remotes/selected/fix", target.RemoteRef);
    }

    [Fact]
    public async Task ResolveTarget_PrefixWildcardCapturesWholeSvnPathComponent()
    {
        var (service, _) = Create(Root + "/branches/Project-foo",
            "svn-remote.new.url " + Root + "\n" +
            "svn-remote.new.branches branches/Project-*:refs/remotes/new/*");

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.Equal(Root + "/branches/Project-foo", target.Url);
        Assert.Equal("refs/remotes/new/Project-foo", target.RemoteRef);
    }

    [Theory]
    [InlineData("fix%20one", "fix%20one")]
    [InlineData("fix%25one", "fix%25one")]
    [InlineData("fix%2520literal", "fix%2520literal")]
    [InlineData("fix%40%7Bone", "fix%40{one")]
    public async Task ResolveTarget_UsesGitSvnRefEscapingForSvnBranchNames(string encodedBranch, string expectedGitName)
    {
        var svnUrl = Root + "/branches/project/" + encodedBranch;
        var (service, _) = Create(svnUrl, BranchConfiguration);

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.Equal(svnUrl, target.Url);
        Assert.Equal("refs/remotes/selected/" + expectedGitName, target.RemoteRef);
    }

    [Fact]
    public async Task ResolveTarget_SingleWildcardDoesNotCrossProjectDirectory()
    {
        var (service, _) = Create(Root + "/branches/project/fix",
            "svn-remote.selected.url " + Root + "\n" +
            "svn-remote.selected.branches branches/*:refs/remotes/selected/*");

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.False(target.IsResolved);
        Assert.False(string.IsNullOrWhiteSpace(target.Problem));
    }

    [Fact]
    public async Task ResolveTarget_RejectsAmbiguousTrackingMappings()
    {
        var (service, _) = Create(Root + "/branches/project/fix", BranchConfiguration + "\n" +
            "svn-remote.overlap.url " + Root + "\n" +
            "svn-remote.overlap.fetch branches/project/fix:refs/remotes/overlap/fix");

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.False(target.IsResolved);
        Assert.False(string.IsNullOrWhiteSpace(target.Problem));
    }

    [Fact]
    public async Task ResolveTarget_RejectsUrlWithoutMatchingTrackingMapping()
    {
        var (service, _) = Create(Root + "/branches/project/fix",
            "svn-remote.selected.url " + Root + "\n" +
            "svn-remote.selected.fetch trunk/project:refs/remotes/selected/trunk");

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.False(target.IsResolved);
        Assert.False(string.IsNullOrWhiteSpace(target.Problem));
    }

    [Theory]
    [InlineData("rewriteRoot", "https://metadata.example.test/repository")]
    [InlineData("useSvmProps", "true")]
    [InlineData("useSvnsyncProps", "true")]
    public async Task ResolveTarget_ReportsUnsupportedMetadataRewriting(string key, string value)
    {
        var (service, _) = Create(Root + "/branches/project/fix", BranchConfiguration + "\n" +
            "svn-remote.selected." + key + " " + value);

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.False(target.IsResolved);
        Assert.False(string.IsNullOrWhiteSpace(target.Problem));
    }

    [Theory]
    [InlineData("useSvmProps")]
    [InlineData("useSvnsyncProps")]
    public async Task ResolveTarget_RejectsGlobalMetadataRewriting(string key)
    {
        var (service, _) = Create(Root + "/branches/project/fix", BranchConfiguration + "\n" +
            "svn." + key + " true");

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.False(target.IsResolved);
        Assert.False(string.IsNullOrWhiteSpace(target.Problem));
    }

    [Fact]
    public async Task ResolveTarget_ReportsGitSvnInfoFailureWithoutGuessingFromConfiguration()
    {
        var (service, _) = Create(Root + "/branches/project/fix", BranchConfiguration, infoFailure: true);

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.False(target.IsResolved);
        Assert.False(string.IsNullOrWhiteSpace(target.Problem));
    }

    [Fact]
    public async Task ResolveTarget_UsesOnlyReadCommandsWithStagedAndUncommittedChanges()
    {
        var (service, runner) = Create(Root + "/branches/project/fix", BranchConfiguration);

        var target = await service.ResolveDcommitTargetAsync(RepositoryPath, CancellationToken.None);

        Assert.True(target.IsResolved, target.Problem);
        Assert.All(runner.Calls, call => Assert.True(
            call.Arguments == "svn info" ||
            call.Arguments == "config --get-regexp ^(svn\\.|svn-remote\\.)",
            "Target lookup must preserve index and worktree: " + call.Arguments));
    }

    private static string BranchConfiguration =>
        "svn-remote.selected.url " + Root + "\n" +
        "svn-remote.selected.branches branches/project/*:refs/remotes/selected/*";

    private static (GitSvnWorkspaceService Service, FakeGitCommandRunner Runner) Create(
        string svnUrl, string configuration, bool infoFailure = false)
    {
        var runner = new FakeGitCommandRunner
        {
            Responder = (_, arguments) => arguments switch
            {
                "svn info" => infoFailure
                    ? new GitCommandResult(1, "", "Unable to determine upstream SVN information from working tree history")
                    : new GitCommandResult(0, "Path: .\nURL: " + svnUrl + "\nRevision: 42\nNode Kind: directory\n", ""),
                "config --get-regexp ^(svn\\.|svn-remote\\.)" => new GitCommandResult(0, configuration, ""),
                "symbolic-ref --quiet --short HEAD" => new GitCommandResult(0, "misleading-trunk-name", ""),
                "--no-optional-locks status --porcelain=v1 --untracked-files=no" => new GitCommandResult(0, "MM staged-and-dirty.txt", ""),
                _ => throw new InvalidOperationException("Unexpected command during target lookup: " + arguments),
            },
        };
        return (new GitSvnWorkspaceService(runner), runner);
    }
}
