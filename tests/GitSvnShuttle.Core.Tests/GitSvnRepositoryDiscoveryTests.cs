using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class GitSvnRepositoryDiscoveryTests : IDisposable
{
    private const string ConfigCommand = "config --get-regexp ^svn-remote\\.";
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(), "GitSvnShuttleTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task DiscoverAsync_FindsContainingRepositoryOnce(int depth, bool gitFile)
    {
        var root = CreateRepository("repository", gitFile);
        var solution = root;
        for (var i = 0; i < depth; i++)
        {
            solution = Path.Combine(solution, "project");
        }

        Directory.CreateDirectory(solution);
        File.WriteAllText(Path.Combine(solution, "Demo.sln"), string.Empty);
        var runner = CreateRunner();

        var repositories = await new GitSvnWorkspaceService(runner)
            .DiscoverAsync(solution, CancellationToken.None);

        var repository = Assert.Single(repositories);
        Assert.Equal(root, repository.Path);
        Assert.False(repository.IsExternalLink);
        Assert.Null(repository.LinkedProjectPath);
        Assert.Equal(new[] { "https://svn.example.test/trunk" }, repository.SvnTargets);
        Assert.Null(repository.Problem);
        Assert.All(runner.Calls, call => Assert.Equal(root, call.WorkingDirectory));
        Assert.Single(runner.Calls, call => call.Arguments == ConfigCommand);
    }

    [Fact]
    public async Task DiscoverAsync_KeepsDescendantsWithoutScanningAncestorSiblings()
    {
        var root = CreateRepository("repository");
        var solution = Path.Combine(root, "project");
        var child = CreateRepository(Path.Combine("repository", "project", "child"));
        CreateRepository(Path.Combine("repository", "sibling"));
        var runner = CreateRunner();

        var repositories = await new GitSvnWorkspaceService(runner)
            .DiscoverAsync(solution, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(new[] { root, child }, repositories.Select(repository => repository.Path));
        Assert.Equal(new[] { root, child }, runner.Calls
            .Where(call => call.Arguments == ConfigCommand).Select(call => call.WorkingDirectory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoverAsync_StopsAtNearestRepositoryEvenWithoutSvn(bool hasSvn)
    {
        CreateRepository("outer");
        var nearest = CreateRepository(Path.Combine("outer", "inner"));
        var solution = Path.Combine(nearest, "project");
        Directory.CreateDirectory(solution);
        var runner = CreateRunner(hasSvn);

        var repositories = await new GitSvnWorkspaceService(runner)
            .DiscoverAsync(solution, CancellationToken.None);

        if (hasSvn)
        {
            Assert.Equal(nearest, Assert.Single(repositories).Path);
        }
        else
        {
            Assert.Empty(repositories);
        }

        Assert.Equal(nearest, Assert.Single(runner.Calls,
            call => call.Arguments == ConfigCommand).WorkingDirectory);
    }

    private string CreateRepository(string relativePath, bool gitFile = false)
    {
        var path = Path.Combine(testRoot, relativePath);
        Directory.CreateDirectory(path);
        if (gitFile)
        {
            File.WriteAllText(Path.Combine(path, ".git"), "gitdir: ../metadata");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(path, ".git"));
        }

        return path;
    }

    private static FakeGitCommandRunner CreateRunner(bool hasSvn = true) => new()
    {
        Responder = (repository, arguments) => arguments switch
        {
            ConfigCommand => hasSvn
                ? Success("svn-remote.svn.url https://svn.example.test\nsvn-remote.svn.fetch trunk:refs/remotes/git-svn")
                : new GitCommandResult(1, string.Empty, string.Empty),
            "--no-optional-locks status --porcelain=v1 --untracked-files=no" => Success(string.Empty),
            "rev-parse --git-dir" => Success(Path.Combine(repository, ".git")),
            "rev-parse --show-prefix --git-path svn" => new GitCommandResult(1, string.Empty, "No local SVN metadata"),
            "log --first-parent --grep=git-svn-id: --format=%H -1" => Success("baseline"),
            "svn info --url" => Success("https://svn.example.test/trunk"),
            "config --get-regexp ^(svn\\.|svn-remote\\.)" =>
                Success("svn-remote.svn.url https://svn.example.test\nsvn-remote.svn.fetch trunk:refs/remotes/git-svn"),
            _ when arguments.StartsWith("show -s ", StringComparison.Ordinal) =>
                Success("baseline\u001fbase\u001fSVN\u001f2026-09-17\u001fBaseline"),
            _ when arguments.StartsWith("log --date=short ", StringComparison.Ordinal) => Success(string.Empty),
            _ => throw new InvalidOperationException("Unexpected Git call: " + arguments),
        },
    };

    private static GitCommandResult Success(string output) => new(0, output, string.Empty);

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
