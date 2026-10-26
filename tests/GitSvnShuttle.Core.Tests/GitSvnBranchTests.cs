using GitSvnShuttle.Core;
using GitSvnShuttle.Vsix;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class GitSvnBranchTests
{
    private const string Root = "https://svn.example.test/repo";
    private const string Repository = "/work/project";

    [Fact]
    public async Task CreateBranch_WithOnlyANameUsesTheAutomaticallyConfiguredSourceAndLocalBranch()
    {
        var (service, git, svn) = Create(BranchConfiguration, Root + "/branches/project/fix");
        var form = new SvnBranchForm();
        form.ApplyTracking(new GitSvnTrackingPaths(Root, new[] { "trunk/project" },
            new[] { "branches/project/*" }, Array.Empty<string>()), null);
        form.Name = "fix";

        var result = await service.CreateSvnBranchAsync(Repository, form.CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(new[] { "copy", "--non-interactive", "--parents", "--revision", "HEAD", "--message", "브랜치 생성: fix", "--",
            Root + "/trunk/project@HEAD", Root + "/branches/project/fix" }, Assert.Single(svn.Calls));
        Assert.Contains(git.Calls, call => call.Arguments == "branch --no-track fix refs/remotes/selected/fix");
        Assert.Contains(git.Calls, call => call.Arguments == "checkout fix");
    }

    [Theory]
    [InlineData("project/trunk", "project/branches", "project/branches/*:refs/remotes/old/*", "refs/remotes/old/fix")]
    [InlineData("trunk/project", "branches", "branches/*:refs/remotes/new/*", "refs/remotes/new/fix")]
    [InlineData("trunk/project", "branches/project", "branches/project/*:refs/remotes/new/*", "refs/remotes/new/fix")]
    public async Task CreateBranch_CopiesExactRevisionFetchesAndConnectsLocalHistory(
        string source, string parent, string branches, string reference)
    {
        var (service, git, svn) = Create("svn-remote.selected.url " + Root + "\nsvn-remote.selected.branches " + branches,
            Root + "/" + parent + "/fix");

        var result = await service.CreateSvnBranchAsync(Repository,
            new GitSvnBranchRequest(source, "42", parent, "fix", "Create bug fix", "looks-like-trunk"), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(new[] { "copy", "--non-interactive", "--parents", "--revision", "42", "--message", "Create bug fix", "--",
            Root + "/" + source + "@42", Root + "/" + parent + "/fix" }, Assert.Single(svn.Calls));
        Assert.Contains(git.Calls, call => call.Arguments == "svn fetch --no-checkout selected");
        Assert.Contains(git.Calls, call => call.Arguments == "branch --no-track looks-like-trunk " + reference);
        Assert.Contains(git.Calls, call => call.Arguments == "checkout looks-like-trunk");
        Assert.Contains(git.Calls, call => call.Arguments == "svn info");
        Assert.DoesNotContain(git.Calls, call => call.Arguments.StartsWith("reset ") || call.Arguments.StartsWith("rebase ") || call.Arguments.Contains("checkout -"));
    }

    [Fact]
    public async Task CreateBranch_CreatesIndependentExactMappingForUntrackedDestination()
    {
        var (service, git, _) = Create("svn-remote.old.url " + Root + "\nsvn-remote.old.fetch project/trunk:refs/remotes/old/trunk",
            Root + "/branches/project/fix");

        var result = await service.CreateSvnBranchAsync(Repository,
            new GitSvnBranchRequest(Root + "/trunk/project", "HEAD", Root + "/branches/project", "fix", "Create fix", "fix", false), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(git.Calls, call => call.Arguments.StartsWith("config --local svn-remote.shuttle-branch-") && call.Arguments.EndsWith(".url " + Root + "/branches/project/fix"));
        Assert.Contains(git.Calls, call => call.Arguments.StartsWith("config --local svn-remote.shuttle-branch-") && call.Arguments.Contains(".fetch :refs/remotes/shuttle/branch-"));
        Assert.DoesNotContain(git.Calls, call => call.Arguments.StartsWith("checkout ") || call.Arguments.Contains("status --porcelain") || call.Arguments.Contains("--unset"));
    }

    [Theory]
    [InlineData("fix space", "fix%20space", "fix%20space")]
    [InlineData("fix%20literal", "fix%2520literal", "fix%2520literal")]
    public async Task CreateBranch_UsesEscapedUrlAndGitSvnRefName(string name, string escapedName, string referenceName)
    {
        var destination = Root + "/branches/project/" + escapedName;
        var (service, git, svn) = Create(BranchConfiguration, destination);

        var result = await service.CreateSvnBranchAsync(Repository,
            new GitSvnBranchRequest("trunk/project", "42", "branches/project", name, "Create", "fix", false), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(destination, Assert.Single(svn.Calls).Last());
        Assert.Contains(git.Calls, call => call.Arguments == "branch --no-track fix refs/remotes/selected/" + referenceName);
    }

    [Fact]
    public async Task CreateBranch_DirtyWorkingTreeBlocksSwitchBeforeServerCopy()
    {
        var (service, git, svn) = Create(BranchConfiguration, Root + "/branches/project/fix");
        var responder = git.Responder!;
        git.Responder = (path, command) => command.Contains("status --porcelain") ? Success("MM file.cs") : responder(path, command);

        var result = await service.CreateSvnBranchAsync(Repository, Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(svn.Calls);
        Assert.DoesNotContain(git.Calls, call => call.Arguments.StartsWith("svn fetch") || call.Arguments.StartsWith("checkout "));
    }

    [Fact]
    public async Task CreateBranch_NoSwitchPreservesDirtyIndexAndWorktree()
    {
        var (service, git, svn) = Create(BranchConfiguration, Root + "/branches/project/fix");
        var responder = git.Responder!;
        git.Responder = (path, command) => command.Contains("status --porcelain") ? Success("MM file.cs") : responder(path, command);

        var result = await service.CreateSvnBranchAsync(Repository, Request(switchToBranch: false), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Single(svn.Calls);
        Assert.DoesNotContain(git.Calls, call => call.Arguments.StartsWith("checkout ") || call.Arguments.StartsWith("stash ") || call.Arguments.StartsWith("reset "));
    }

    [Fact]
    public async Task CreateBranch_RejectsExistingUnrelatedLocalCommitsBeforeServerCopy()
    {
        var (service, git, svn) = Create(BranchConfiguration, Root + "/branches/project/fix");
        var responder = git.Responder!;
        git.Responder = (path, command) => command.StartsWith("show-ref ") ? Success("") : command.EndsWith("refs/heads/fix --")
            ? Success("git-svn-id: " + Root + "/trunk/project@40 1234-abcd") : responder(path, command);

        var result = await service.CreateSvnBranchAsync(Repository, Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(svn.Calls);
        Assert.Contains("기존 커밋 보존", result.Message);
    }

    [Fact]
    public async Task CreateBranch_FetchFailureReportsCreatedServerBranchWithoutRollback()
    {
        var (service, git, svn) = Create(BranchConfiguration, Root + "/branches/project/fix");
        var responder = git.Responder!;
        git.Responder = (path, command) => command.StartsWith("svn fetch ") ? new GitCommandResult(1, "", "network offline") : responder(path, command);

        var result = await service.CreateSvnBranchAsync(Repository, Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Single(svn.Calls);
        Assert.Contains("이미 생성되었습니다", result.Message);
        Assert.Contains(Root + "/branches/project/fix", result.Message);
        Assert.DoesNotContain(git.Calls, call => call.Arguments.StartsWith("branch ") || call.Arguments.StartsWith("checkout "));
    }

    [Fact]
    public async Task CreateBranch_RejectsGlobalCommitOverrideBeforeServerCopy()
    {
        var (service, _, svn) = Create(BranchConfiguration + "\nsvn.commiturl " + Root + "/trunk/project", Root + "/branches/project/fix");

        var result = await service.CreateSvnBranchAsync(Repository, Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("svn.commiturl", result.Message);
        Assert.Empty(svn.Calls);
    }

    [Theory]
    [InlineData("r42", "fix")]
    [InlineData("40:42", "fix")]
    [InlineData("HEAD", "../fix")]
    public async Task CreateBranch_RejectsInvalidRevisionOrDestinationName(string revision, string name)
    {
        var (service, _, svn) = Create(BranchConfiguration, Root + "/branches/project/fix");

        var result = await service.CreateSvnBranchAsync(Repository,
            new GitSvnBranchRequest("trunk/project", revision, "branches/project", name, "Create", "fix"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(svn.Calls);
    }

    private const string BranchConfiguration = "svn-remote.selected.url " + Root + "\nsvn-remote.selected.branches branches/project/*:refs/remotes/selected/*";
    private static GitSvnBranchRequest Request(bool switchToBranch = true) => new("trunk/project", "42", "branches/project", "fix", "Create fix", "fix", switchToBranch);

    private static (GitSvnWorkspaceService, FakeGitCommandRunner, FakeSvnCommandRunner) Create(string initialConfiguration, string destination)
    {
        var config = initialConfiguration;
        var git = new FakeGitCommandRunner();
        git.Responder = (_, command) =>
        {
            if (command == "config --get-regexp ^(svn\\.|svn-remote\\.)") return Success(config);
            if (command.StartsWith("config --local "))
            {
                config += "\n" + command.Substring("config --local ".Length);
                return Success("");
            }
            if (command.StartsWith("check-ref-format ")) return Success("");
            if (command.StartsWith("show-ref ")) return new GitCommandResult(1, "", "");
            if (command == "--no-optional-locks status --porcelain=v1 --untracked-files=no") return Success("");
            if (command == "rev-parse --git-dir") return Success("/work/project/.git");
            if (command == "rev-parse --verify HEAD") return Success("local-head-preserved");
            if (command == "symbolic-ref --quiet --short HEAD") return Success("old-local");
            if (command.StartsWith("svn fetch ")) return Success("Fetched r43");
            if (command.StartsWith("log --first-parent ")) return Success("Branch copy\n\ngit-svn-id: " + destination + "@43 1234-abcd");
            if (command.StartsWith("branch --no-track ") || command.StartsWith("checkout ")) return Success("");
            if (command == "svn info") return Success("URL: " + destination + "\nRevision: 43");
            throw new InvalidOperationException("Unexpected command: " + command);
        };
        var svn = new FakeSvnCommandRunner();
        return (new GitSvnWorkspaceService(git, svn), git, svn);
    }

    private sealed class FakeSvnCommandRunner : ISvnCommandRunner
    {
        internal List<IReadOnlyList<string>> Calls { get; } = new();
        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        { Calls.Add(arguments.ToArray()); return Task.FromResult(Success("Committed revision 43.")); }
    }

    private static GitCommandResult Success(string output) => new(0, output, "");
}
