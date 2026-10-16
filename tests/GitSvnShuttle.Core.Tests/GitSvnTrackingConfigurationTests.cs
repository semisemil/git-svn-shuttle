using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

// Exercise the real Git configuration/ref implementation; SVN import is covered by the server probe.
public sealed class GitSvnTrackingConfigurationTests : IDisposable
{
    private const string BaseUrl = "https://svn.example.test/repository";
    private readonly string repository = Path.Combine(Path.GetTempPath(), "GitSvnShuttleTests", "tracking-" + Guid.NewGuid().ToString("N"));
    private readonly FetchInterceptRunner runner = new();

    [Fact]
    public async Task Configure_PreservesProjectNameWildcardWithinBranchComponent()
    {
        await InitializeAsync();
        var service = new GitSvnWorkspaceService(runner);

        var result = await service.ConfigureTrackingPathsAsync(repository,
            Paths(branches: new[] { "branches/Project-*" }), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var paths = await service.GetTrackingPathsAsync(repository, CancellationToken.None);
        Assert.Equal(BaseUrl + "/branches/Project-*", FullPath(paths.BaseUrl, Assert.Single(paths.BranchesPaths)));
        Assert.Contains(runner.FetchCalls, args => args.SequenceEqual(new[] { "svn", "fetch", "--all", "--no-checkout" }));
    }

    [Fact]
    public async Task Configure_DoesNotRemoveAnotherGitWritersLock()
    {
        await InitializeAsync();
        var configPath = Path.Combine(repository, ".git", "config");
        var originalConfig = File.ReadAllBytes(configPath);
        var lockPath = configPath + ".lock";
        File.WriteAllText(lockPath, "Another Git writer owns this file.\n");

        var result = await new GitSvnWorkspaceService(runner).ConfigureTrackingPathsAsync(repository,
            Paths(trunk: new[] { "trunk/Project" }), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Another Git writer owns this file.\n", File.ReadAllText(lockPath));
        Assert.Equal(originalConfig, File.ReadAllBytes(configPath));
        Assert.Empty(runner.FetchCalls);
    }

    [Fact]
    public async Task Configure_ChildFolderDoesNotModifyItsParentsGitConfiguration()
    {
        await InitializeAsync();
        var configPath = Path.Combine(repository, ".git", "config");
        var originalConfig = File.ReadAllBytes(configPath);
        var child = Directory.CreateDirectory(Path.Combine(repository, "child-folder")).FullName;
        File.WriteAllText(Path.Combine(child, "local.txt"), "Keep child content\n");

        var result = await new GitSvnWorkspaceService(runner).ConfigureTrackingPathsAsync(child,
            Paths(trunk: new[] { "trunk/Project" }), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("하위 폴더", result.Message);
        Assert.Equal(originalConfig, File.ReadAllBytes(configPath));
        Assert.False(File.Exists(configPath + ".lock"));
        Assert.False(Directory.Exists(Path.Combine(child, ".git")));
        Assert.Equal("Keep child content\n", File.ReadAllText(Path.Combine(child, "local.txt")));
        Assert.Empty(runner.FetchCalls);
    }

    [Fact]
    public async Task Configure_RetiredBranchKeepsExactBindingAndCanBeReactivated()
    {
        await InitializeAsync();
        await GitAsync("config", "svn-remote.svn.url", BaseUrl + "/Project");
        await GitAsync("config", "svn-remote.svn.fetch", "trunk:refs/remotes/svn/trunk");
        await GitAsync("config", "svn-remote.svn.branches", "branches/*:refs/remotes/svn/branches/*");
        await GitAsync("commit", "--allow-empty", "-m", "Branch import", "-m",
            "git-svn-id: " + BaseUrl + "/Project/branches/feature@5 00000000-0000-0000-0000-000000000000");
        await GitAsync("update-ref", "refs/remotes/svn/branches/feature", "HEAD");
        var service = new GitSvnWorkspaceService(runner);

        var retire = await service.ConfigureTrackingPathsAsync(repository,
            Paths(trunk: new[] { "Project/trunk", "trunk/Project" }), CancellationToken.None);

        Assert.True(retire.Succeeded, retire.Message);
        var retired = await service.GetTrackingPathsAsync(repository, CancellationToken.None);
        Assert.Empty(retired.BranchesPaths);
        Assert.DoesNotContain(retired.TrunkPaths, path => path.Contains("feature", StringComparison.Ordinal));
        Assert.Contains(BaseUrl + "/Project/branches/*", retired.RetainedPaths);
        var bindings = await GitAsync("config", "--get-all", "svn-remote.svn.fetch");
        Assert.Contains("branches/feature:refs/remotes/svn/branches/feature", bindings);

        var reactivate = await service.ConfigureTrackingPathsAsync(repository,
            Paths(trunk: new[] { "Project/trunk", "trunk/Project" }, branches: new[] { "Project/branches" }), CancellationToken.None);

        Assert.True(reactivate.Succeeded, reactivate.Message);
        var active = await service.GetTrackingPathsAsync(repository, CancellationToken.None);
        Assert.Equal(BaseUrl + "/Project/branches/*", FullPath(active.BaseUrl, Assert.Single(active.BranchesPaths)));
        Assert.DoesNotContain(active.RetainedPaths, path => path == BaseUrl + "/Project/branches/*");
        var activeConfig = await GitAsync("config", "--get-all", "svn-remote.svn.branches");
        Assert.Equal("branches/*:refs/remotes/svn/branches/*", activeConfig.Trim());
    }

    [Fact]
    public async Task Configure_RetiresConventionalWildcardWithoutMisreadingTrunkAndCopySourceRefs()
    {
        await InitializeAsync();
        await GitAsync("config", "svn-remote.svn.url", BaseUrl + "/Project");
        await GitAsync("config", "svn-remote.svn.fetch", "trunk:refs/remotes/svn/trunk");
        await GitAsync("config", "svn-remote.svn.branches", "branches/*:refs/remotes/svn/*");
        await GitAsync("commit", "--allow-empty", "-m", "Trunk import", "-m",
            "git-svn-id: " + BaseUrl + "/Project/trunk@1 00000000-0000-0000-0000-000000000000");
        await GitAsync("update-ref", "refs/remotes/svn/trunk", "HEAD");
        await GitAsync("update-ref", "refs/remotes/svn/feature@1", "HEAD");
        await GitAsync("commit", "--allow-empty", "-m", "Branch import", "-m",
            "git-svn-id: " + BaseUrl + "/Project/branches/feature@2 00000000-0000-0000-0000-000000000000");
        await GitAsync("update-ref", "refs/remotes/svn/feature", "HEAD");

        var result = await new GitSvnWorkspaceService(runner).ConfigureTrackingPathsAsync(repository,
            Paths(trunk: new[] { "Project/trunk", "trunk/Project" }), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var bindings = await GitAsync("config", "--get-all", "svn-remote.svn.fetch");
        Assert.Contains("trunk:refs/remotes/svn/trunk", bindings);
        Assert.Contains("branches/feature:refs/remotes/svn/feature", bindings);
        Assert.DoesNotContain("branches/trunk:", bindings);
        Assert.DoesNotContain("branches/feature@1:", bindings);
        Assert.Equal((await GitAsync("rev-parse", "refs/remotes/svn/trunk")).Trim(),
            (await GitAsync("rev-parse", "refs/remotes/svn/feature@1")).Trim());
    }

    [Theory]
    [InlineData("Project A")]
    [InlineData("Project%20A")]
    [InlineData("프로젝트 %20")]
    public async Task Configure_RoundTripsRawSpaceAndLiteralPercentNames(string projectName)
    {
        await InitializeAsync();
        var service = new GitSvnWorkspaceService(runner);
        var desired = new GitSvnTrackingPaths(BaseUrl, new[] { "trunk/" + projectName },
            new[] { "branches/" + projectName + "/*" }, new[] { "tags/" + projectName + "/*" });

        var configured = await service.ConfigureTrackingPathsAsync(repository, desired, CancellationToken.None);

        Assert.True(configured.Succeeded, configured.Message);
        var queried = await service.GetTrackingPathsAsync(repository, CancellationToken.None);
        Assert.Equal(BaseUrl, queried.BaseUrl);
        Assert.Equal(desired.TrunkPaths, queried.TrunkPaths);
        Assert.Equal(desired.BranchesPaths, queried.BranchesPaths);
        Assert.Equal(desired.TagsPaths, queried.TagsPaths);
        var configPath = Path.Combine(repository, ".git", "config");
        var configBeforeReapply = File.ReadAllBytes(configPath);
        var remotes = await GitAsync("config", "--get-regexp", @"^svn-remote\..*\.url$");
        Assert.Contains(BaseUrl + "/trunk/" + Uri.EscapeDataString(projectName), remotes);

        var reapplied = await service.ConfigureTrackingPathsAsync(repository, queried, CancellationToken.None);

        Assert.True(reapplied.Succeeded, reapplied.Message);
        Assert.Equal(configBeforeReapply, File.ReadAllBytes(configPath));
    }

    [Fact]
    public async Task Configure_ReusesExistingFullUrlAcrossDifferentBaseUrls()
    {
        await InitializeAsync();
        await GitAsync("config", "svn-remote.svn.url", BaseUrl + "/Project");
        await GitAsync("config", "svn-remote.svn.fetch", "trunk:refs/remotes/svn/trunk");

        var result = await new GitSvnWorkspaceService(runner).ConfigureTrackingPathsAsync(repository,
            Paths(trunk: new[] { "Project/trunk" }), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var remotes = await GitAsync("config", "--get-regexp", @"^svn-remote\..*\.url$");
        Assert.Equal("svn-remote.svn.url " + BaseUrl + "/Project", remotes.Trim());
        Assert.Equal("trunk:refs/remotes/svn/trunk", (await GitAsync("config", "--get", "svn-remote.svn.fetch")).Trim());
    }

    [Fact]
    public async Task Configure_PreservesLocalCommitIndexAndUncommittedContents()
    {
        await InitializeAsync();
        await GitAsync("config", "svn-remote.svn.url", BaseUrl + "/Project/trunk");
        await GitAsync("config", "svn-remote.svn.fetch", ":refs/remotes/git-svn");
        File.WriteAllText(Path.Combine(repository, "tracked.txt"), "Committed content\n");
        await GitAsync("add", "tracked.txt");
        await GitAsync("commit", "-m", "Local pending commit");
        File.AppendAllText(Path.Combine(repository, "tracked.txt"), "Staged content\n");
        await GitAsync("add", "tracked.txt");
        File.AppendAllText(Path.Combine(repository, "tracked.txt"), "Unstaged content\n");
        File.WriteAllText(Path.Combine(repository, "untracked.txt"), "Untracked content\n");
        var head = await GitAsync("rev-parse", "HEAD");
        var indexPath = Path.Combine(repository, ".git", "index");
        var index = File.ReadAllBytes(indexPath);
        var trackedContents = File.ReadAllText(Path.Combine(repository, "tracked.txt"));

        var result = await new GitSvnWorkspaceService(runner).ConfigureTrackingPathsAsync(repository,
            Paths(trunk: new[] { "trunk/Project" }, branches: new[] { "branches/Project" }), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(index, File.ReadAllBytes(indexPath));
        Assert.Equal(head, await GitAsync("rev-parse", "HEAD"));
        Assert.Equal(trackedContents, File.ReadAllText(Path.Combine(repository, "tracked.txt")));
        Assert.Equal("Untracked content\n", File.ReadAllText(Path.Combine(repository, "untracked.txt")));
        Assert.Equal(":refs/remotes/git-svn", (await GitAsync("config", "--get", "svn-remote.svn.fetch")).Trim());
        var paths = await new GitSvnWorkspaceService(runner).GetTrackingPathsAsync(repository, CancellationToken.None);
        Assert.Contains(BaseUrl + "/Project/trunk", paths.RetainedPaths);
        Assert.DoesNotContain(paths.TrunkPaths, path => FullPath(paths.BaseUrl, path) == BaseUrl + "/Project/trunk");
    }

    [Fact]
    public async Task Configure_RejectsConfigurationChangedWhilePlanningAndPreservesOtherWriter()
    {
        await InitializeAsync();
        var configPath = Path.Combine(repository, ".git", "config");
        var changed = false;
        runner.AfterGitCommand = (_, arguments, _) =>
        {
            if (!changed && arguments.SequenceEqual(new[] { "config", "--local", "--null", "--get-regexp", @"^(svn-remote\..*|shuttle\.retiredmapping)$" }))
            {
                File.AppendAllText(configPath, "\n[concurrent-writer]\n\tvalue = keep-me\n");
                changed = true;
            }
        };

        var result = await new GitSvnWorkspaceService(runner).ConfigureTrackingPathsAsync(repository,
            Paths(trunk: new[] { "trunk/Project" }), CancellationToken.None);

        Assert.True(changed);
        Assert.False(result.Succeeded);
        Assert.Contains("Git 설정을 변경", result.Message);
        Assert.Contains("value = keep-me", File.ReadAllText(configPath));
        Assert.DoesNotContain("svn-remote", File.ReadAllText(configPath));
        Assert.False(File.Exists(configPath + ".lock"));
        Assert.Empty(runner.FetchCalls);
    }

    [Fact]
    public async Task Configure_FailedFetchRetainsSavedMappingsAndCanRetryWithoutDuplicates()
    {
        await InitializeAsync();
        runner.FetchResult = new GitCommandResult(1, string.Empty, "SVN server is temporarily unavailable.");
        var service = new GitSvnWorkspaceService(runner);
        var desired = Paths(trunk: new[] { "trunk/Project" }, branches: new[] { "branches/Project" });

        var failed = await service.ConfigureTrackingPathsAsync(repository, desired, CancellationToken.None);

        Assert.False(failed.Succeeded);
        Assert.Contains("설정은 저장", failed.Message);
        var retained = await service.GetTrackingPathsAsync(repository, CancellationToken.None);
        Assert.Equal(BaseUrl + "/trunk/Project", FullPath(retained.BaseUrl, Assert.Single(retained.TrunkPaths)));
        Assert.Equal(BaseUrl + "/branches/Project/*", FullPath(retained.BaseUrl, Assert.Single(retained.BranchesPaths)));
        Assert.False(File.Exists(Path.Combine(repository, ".git", "config.lock")));

        runner.FetchResult = new GitCommandResult(0, string.Empty, string.Empty);
        var retried = await service.ConfigureTrackingPathsAsync(repository, desired, CancellationToken.None);

        Assert.True(retried.Succeeded, retried.Message);
        var remotes = await GitAsync("config", "--get-regexp", @"^svn-remote\..*\.url$");
        Assert.Equal(2, remotes.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(2, runner.FetchCalls.Count);
    }

    [Theory]
    [InlineData("../trunk", false)]
    [InlineData("/trunk", false)]
    [InlineData("branches/*/*", true)]
    public async Task Configure_InvalidPathsLeaveExistingConfigurationUntouched(string path, bool isBranches)
    {
        await InitializeAsync();
        var configPath = Path.Combine(repository, ".git", "config");
        var before = File.ReadAllBytes(configPath);

        var result = await new GitSvnWorkspaceService(runner).ConfigureTrackingPathsAsync(repository,
            isBranches ? Paths(branches: new[] { path }) : Paths(trunk: new[] { path }), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(before, File.ReadAllBytes(configPath));
        Assert.False(File.Exists(configPath + ".lock"));
        Assert.Empty(runner.FetchCalls);
    }

    private static GitSvnTrackingPaths Paths(string[]? trunk = null, string[]? branches = null) =>
        new(BaseUrl, trunk ?? Array.Empty<string>(), branches ?? Array.Empty<string>(), Array.Empty<string>());

    private static string FullPath(string root, string path) => root.TrimEnd('/') + (path.Length == 0 ? "" : "/" + path);

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(repository);
        await GitAsync("init");
        await GitAsync("config", "user.name", "Shuttle Tester");
        await GitAsync("config", "user.email", "shuttle@example.invalid");
    }

    private async Task<string> GitAsync(params string[] arguments)
    {
        var result = await runner.RunAsync(repository, arguments, CancellationToken.None);
        Assert.True(result.Succeeded, result.CombinedOutput);
        return result.StandardOutput;
    }

    public void Dispose()
    {
        if (!Directory.Exists(repository)) return;
        foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(repository, recursive: true);
    }

    private sealed class FetchInterceptRunner : IGitCommandRunner
    {
        private readonly ProcessGitCommandRunner git = new("git");
        internal List<string[]> FetchCalls { get; } = new();
        internal GitCommandResult FetchResult { get; set; } = new(0, string.Empty, string.Empty);
        internal Action<string, IReadOnlyList<string>, GitCommandResult>? AfterGitCommand { get; set; }

        public async Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            if (arguments.Count >= 2 && arguments[0] == "svn" && arguments[1] == "fetch")
            {
                FetchCalls.Add(arguments.ToArray());
                return FetchResult;
            }
            var result = await git.RunAsync(workingDirectory, arguments, cancellationToken);
            AfterGitCommand?.Invoke(workingDirectory, arguments, result);
            return result;
        }
    }
}
