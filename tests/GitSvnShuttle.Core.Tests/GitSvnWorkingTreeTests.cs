using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class GitSvnWorkingTreeTests : IDisposable
{
    private readonly string repository = Path.Combine(
        Path.GetTempPath(), "GitSvnShuttleTests", "status-" + Guid.NewGuid().ToString("N"));
    private readonly ProcessGitCommandRunner runner = new("git");

    [Theory]
    [InlineData(null, true)]
    [InlineData("modified", false)]
    [InlineData("staged", false)]
    [InlineData("deleted", false)]
    [InlineData("added", false)]
    [InlineData("renamed", false)]
    public async Task InspectAsync_IgnoresUntrackedFilesButRejectsTrackedChanges(
        string? trackedChange, bool expectedReady)
    {
        Directory.CreateDirectory(repository);
        await GitAsync("init");
        await GitAsync("config", "user.name", "Shuttle Tester");
        await GitAsync("config", "user.email", "shuttle@example.invalid");
        var trackedFile = Path.Combine(repository, "tracked.txt");
        File.WriteAllText(trackedFile, "Baseline content\n");
        await GitAsync("add", "tracked.txt");
        await GitAsync("commit", "-m", "SVN baseline", "-m",
            "git-svn-id: https://svn.example.test/trunk@1 00000000-0000-0000-0000-000000000000");
        await GitAsync("commit", "--allow-empty", "-m", "Pending commit");

        File.WriteAllText(Path.Combine(repository, "local notes.txt"), "Keep local\n");
        var untrackedDirectory = Path.Combine(repository, "local-directory");
        Directory.CreateDirectory(untrackedDirectory);
        File.WriteAllText(Path.Combine(untrackedDirectory, "notes.txt"), "Keep local too\n");

        switch (trackedChange)
        {
            case "modified":
                File.AppendAllText(trackedFile, "Modified\n");
                break;
            case "staged":
                File.AppendAllText(trackedFile, "Staged\n");
                await GitAsync("add", "tracked.txt");
                break;
            case "deleted":
                File.Delete(trackedFile);
                break;
            case "added":
                File.WriteAllText(Path.Combine(repository, "added.txt"), "Staged addition\n");
                await GitAsync("add", "added.txt");
                break;
            case "renamed":
                await GitAsync("mv", "tracked.txt", "renamed.txt");
                break;
        }

        var status = await runner.RunAsync(repository,
            new[] { "status", "--porcelain=v1" }, CancellationToken.None);
        Assert.True(status.Succeeded, status.CombinedOutput);
        Assert.Contains("?? ", status.StandardOutput, StringComparison.Ordinal);

        var inspected = await new GitSvnWorkspaceService(runner)
            .InspectAsync(repository, CancellationToken.None);

        Assert.Equal(expectedReady, inspected.IsReady);
        Assert.NotNull(inspected.SvnBaseline);
        Assert.Equal("Pending commit", Assert.Single(inspected.PendingCommits).Subject);
        if (expectedReady)
        {
            Assert.Null(inspected.Problem);
        }
        else
        {
            Assert.Contains("커밋되지 않은 변경", inspected.Problem);
        }
    }

    private async Task GitAsync(params string[] arguments)
    {
        var result = await runner.RunAsync(repository, arguments, CancellationToken.None);
        Assert.True(result.Succeeded, result.CombinedOutput);
    }

    public void Dispose()
    {
        if (!Directory.Exists(repository))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(repository, recursive: true);
    }
}
