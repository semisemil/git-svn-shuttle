using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class GitSvnWorkspaceServiceTests
{
    [Fact]
    public async Task InspectAsync_ShowsPendingCommitsNewestFirstAfterLastGitSvnCommit()
    {
        var runner = new FakeGitCommandRunner();
        runner.Enqueue(output: string.Empty); // status
        runner.Enqueue(output: ".git"); // git dir
        runner.Enqueue(output: "abc123"); // baseline
        runner.Enqueue(output: "abc123\u001fabc123\u001fSvn User\u001f2026-08-06\u001fImport from SVN");
        runner.Enqueue(output:
            "new789\u001fnew789\u001fKim\u001f2026-08-08\u001fPolish shuttle" + Environment.NewLine +
            "def456\u001fdef456\u001fKim\u001f2026-08-07\u001fFix shuttle");
        var service = new GitSvnWorkspaceService(runner);

        var result = await service.InspectAsync("C:\\work\\main", CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.NotNull(result.SvnBaseline);
        Assert.Equal("Import from SVN", result.SvnBaseline!.Subject);
        Assert.Equal(2, result.PendingCommits.Count);
        Assert.Equal("Polish shuttle", result.PendingCommits[0].Subject);
        Assert.Equal("Fix shuttle", result.PendingCommits[1].Subject);
        Assert.Contains("abc123..HEAD", runner.Calls.Last().Arguments);
        Assert.DoesNotContain("--reverse", runner.Calls.Last().Arguments);
    }

    [Fact]
    public async Task DcommitAllAsync_PreflightsEveryRepositoryBeforePublishing()
    {
        var runner = new FakeGitCommandRunner();
        QueueSuccessfulDcommitPreflight(runner, "base-a", "commit-a");
        runner.Enqueue(output: string.Empty); // repo B status
        runner.Enqueue(output: "main"); // repo B branch
        runner.Enqueue(output: "base-b"); // repo B pending baseline
        runner.Enqueue(output: string.Empty); // repo B has no pending commits
        var service = new GitSvnWorkspaceService(runner);

        var results = await service.DcommitAllAsync(
            new[] { "C:\\work\\a", "C:\\work\\b" },
            CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Succeeded);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments == "svn dcommit");
    }

    [Fact]
    public async Task DcommitAllAsync_PublishesSequentiallyAfterAllPreflights()
    {
        var runner = new FakeGitCommandRunner();
        QueueSuccessfulDcommitPreflight(runner, "base-a", "commit-a");
        QueueSuccessfulDcommitPreflight(runner, "base-b", "commit-b");
        runner.Enqueue(output: "Committed r10");
        runner.Enqueue(output: "Committed r11");
        var service = new GitSvnWorkspaceService(runner);

        var results = await service.DcommitAllAsync(
            new[] { "C:\\work\\a", "C:\\work\\b" },
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Succeeded));
        var firstPublish = runner.Calls.FindIndex(call => call.Arguments == "svn dcommit");
        var lastDryRun = runner.Calls.FindLastIndex(call => call.Arguments == "svn dcommit --dry-run");
        Assert.True(firstPublish > lastDryRun);
    }

    [Fact]
    public async Task RebaseAsync_RejectsDirtyWorkingTree()
    {
        var runner = new FakeGitCommandRunner();
        runner.Enqueue(output: " M source.cs");
        var service = new GitSvnWorkspaceService(runner);

        var result = await service.RebaseAsync("C:\\work\\main", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments == "svn rebase");
    }

    private static void QueueSuccessfulDcommitPreflight(
        FakeGitCommandRunner runner,
        string baseline,
        string pendingHash)
    {
        runner.Enqueue(output: string.Empty); // status
        runner.Enqueue(output: "main"); // branch
        runner.Enqueue(output: baseline); // pending baseline
        runner.Enqueue(output: pendingHash + "\u001f" + pendingHash + "\u001fKim\u001f2026-08-07\u001fCommit");
        runner.Enqueue(output: baseline); // merge-check baseline
        runner.Enqueue(output: string.Empty); // no merges
        runner.Enqueue(output: "diff-tree " + pendingHash); // dry run
    }
}
