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
        Assert.NotNull(result.GitDirectory);
        Assert.NotNull(result.SvnBaseline);
        Assert.Equal("Import from SVN", result.SvnBaseline!.Subject);
        Assert.Equal(2, result.PendingCommits.Count);
        Assert.Equal("Polish shuttle", result.PendingCommits[0].Subject);
        Assert.Equal("Fix shuttle", result.PendingCommits[1].Subject);
        Assert.Contains("abc123..HEAD", runner.Calls.Last().Arguments);
        Assert.DoesNotContain("--reverse", runner.Calls.Last().Arguments);
    }

    [Fact]
    public async Task DcommitPreparedAsync_RejectsChangedHeadAfterConfirmation()
    {
        var repository = "C:\\work\\main";
        var heads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [repository] = "commit-a",
        };
        var runner = CreateRepositoryRunner(heads);
        var service = new GitSvnWorkspaceService(runner);

        var preparation = await service.PrepareDcommitAsync(repository, CancellationToken.None);
        Assert.True(preparation.Succeeded);

        heads[repository] = "commit-b";
        var result = await service.DcommitPreparedAsync(preparation.Snapshot!, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("변경되었습니다", result.Message);
        Assert.DoesNotContain(runner.Calls, IsActualDcommit);
    }

    [Fact]
    public async Task DcommitPreparedAsync_RejectsChangedSvnConfigurationAfterConfirmation()
    {
        var repository = "C:\\work\\main";
        var heads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [repository] = "commit-a",
        };
        var svnTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [repository] = "https://svn.example.test/project/trunk",
        };
        var runner = CreateRepositoryRunner(heads, svnTargets);
        var service = new GitSvnWorkspaceService(runner);

        var preparation = await service.PrepareDcommitAsync(repository, CancellationToken.None);
        Assert.True(preparation.Succeeded);

        svnTargets[repository] = "https://evil.example.test/other/trunk";
        var result = await service.DcommitPreparedAsync(preparation.Snapshot!, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("SVN 설정이 변경", result.Message);
        Assert.DoesNotContain(runner.Calls, IsActualDcommit);
    }

    [Fact]
    public async Task DcommitPreparedAllAsync_PreflightsEverySnapshotBeforePublishingPinnedHeads()
    {
        var repositoryA = "C:\\work\\a";
        var repositoryB = "C:\\work\\b";
        var heads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [repositoryA] = "commit-a",
            [repositoryB] = "commit-b",
        };
        var runner = CreateRepositoryRunner(heads);
        var service = new GitSvnWorkspaceService(runner);
        var preparedA = await service.PrepareDcommitAsync(repositoryA, CancellationToken.None);
        var preparedB = await service.PrepareDcommitAsync(repositoryB, CancellationToken.None);
        runner.Calls.Clear();

        var results = await service.DcommitPreparedAllAsync(
            new[] { preparedA.Snapshot!, preparedB.Snapshot! },
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Succeeded));
        var firstPublish = runner.Calls.FindIndex(IsActualDcommit);
        var lastDryRun = runner.Calls.FindLastIndex(call => call.Arguments.Contains("svn dcommit --dry-run"));
        Assert.True(firstPublish > lastDryRun);
        Assert.Contains(runner.Calls, call => call.Arguments == "svn dcommit commit-a");
        Assert.Contains(runner.Calls, call => call.Arguments == "svn dcommit commit-b");
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

    private static FakeGitCommandRunner CreateRepositoryRunner(
        IReadOnlyDictionary<string, string> heads,
        IReadOnlyDictionary<string, string>? svnTargets = null)
    {
        var runner = new FakeGitCommandRunner();
        runner.Responder = (repository, arguments) =>
        {
            var head = heads[repository];
            var target = svnTargets != null && svnTargets.TryGetValue(repository, out var configuredTarget)
                ? configuredTarget
                : "https://svn.example.test/" + Path.GetFileName(repository) + "/trunk";

            if (arguments == "rev-parse --verify HEAD")
            {
                return Success(head);
            }

            if (arguments == "log --grep=git-svn-id: --format=%H -1")
            {
                return Success("base");
            }

            if (arguments.StartsWith("log --date=short --format=", StringComparison.Ordinal))
            {
                return Success(head + "\u001f" + head + "\u001fKim\u001f2026-08-07\u001fCommit " + head);
            }

            if (arguments == "config --get-regexp ^(svn\\.|svn-remote\\.)")
            {
                return Success("svn-remote.svn.url " + target);
            }

            if (arguments == "rev-parse --git-dir")
            {
                return Success(Path.Combine(repository, ".git"));
            }

            if (arguments == "--no-optional-locks status --porcelain=v1" ||
                arguments.StartsWith("log --merges --format=%H", StringComparison.Ordinal))
            {
                return Success(string.Empty);
            }

            if (arguments == "symbolic-ref --quiet --short HEAD")
            {
                return Success("main");
            }

            if (arguments.StartsWith("svn dcommit --dry-run ", StringComparison.Ordinal))
            {
                return Success("diff-tree " + head);
            }

            if (arguments.StartsWith("svn dcommit ", StringComparison.Ordinal))
            {
                return Success("Committed " + head);
            }

            throw new InvalidOperationException("Unexpected fake Git call: " + arguments);
        };
        return runner;
    }

    private static bool IsActualDcommit((string WorkingDirectory, string Arguments) call) =>
        call.Arguments.StartsWith("svn dcommit ", StringComparison.Ordinal) &&
        !call.Arguments.Contains("--dry-run", StringComparison.Ordinal);

    private static GitCommandResult Success(string output) => new GitCommandResult(0, output, string.Empty);
}
