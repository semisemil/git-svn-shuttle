using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class GitSvnLocalTargetTests : IDisposable
{
    private const string RootUrl = "https://svn.example.test/project";
    private const string Uuid = "01234567-89ab-cdef-0123-456789abcdef";
    private readonly string root = Path.Combine(Path.GetTempPath(), "ShuttleLocalTarget-" + Guid.NewGuid().ToString("N"));
    private readonly string baseline;
    private readonly string head;
    private readonly string mapPath;
    private string configuration = "svn-remote.svn.url " + RootUrl + "\n" +
        "svn-remote.svn.branches branches/*:refs/remotes/svn/*";
    private GitCommandResult nativeTarget = Success(RootUrl + "/branches/first");
    private string references = "refs/remotes/svn/first";
    private string metadataUrl = RootUrl + "/branches/first";
    private string historySuffix = "";
    private readonly FakeGitCommandRunner runner;
    private readonly GitSvnWorkspaceService service;

    public GitSvnLocalTargetTests() : this(40) { }

    private GitSvnLocalTargetTests(int hashLength)
    {
        baseline = new string('a', hashLength);
        head = new string('b', hashLength);
        mapPath = Path.Combine(root, ".git", "svn", "refs", "remotes", "svn", "first", ".rev_map." + Uuid);
        Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!);
        File.WriteAllBytes(mapPath, new byte[] { 0, 0, 0, 42 }.Concat(Convert.FromHexString(baseline)).ToArray());
        runner = new FakeGitCommandRunner
        {
            Responder = (_, command) => command switch
            {
                "--no-optional-locks status --porcelain=v1 --untracked-files=no" => Success(""),
                "rev-parse --git-dir" => Success(Path.Combine(root, ".git")),
                "symbolic-ref --quiet --short HEAD" => Success("local"),
                "rev-parse --verify HEAD" => Success(head),
                "log --first-parent --grep=git-svn-id: --format=%H -1" => Success(baseline),
                "rev-parse --show-prefix --git-path svn" => Success("\n.git/svn\n"),
                "log --first-parent --grep=git-svn-id: --format=%H%x1f%B -1 HEAD --" =>
                    Success(baseline + "\u001fImported\ngit-svn-id: " + metadataUrl + "@42 " + Uuid + "\n" + historySuffix),
                "for-each-ref --format=%(refname) refs/" => Success(references),
                "config --get-regexp ^(svn\\.|svn-remote\\.)" => Success(configuration),
                "svn info --url" => nativeTarget,
                _ when command.StartsWith("show -s ", StringComparison.Ordinal) =>
                    Success(baseline + "\u001fbase\u001fSVN\u001f2026-10-27\u001fImported"),
                _ when command.StartsWith("log --date=short ", StringComparison.Ordinal) =>
                    Success(head + "\u001fhead\u001fUser\u001f2026-10-27\u001fPending"),
                _ when command.StartsWith("log --merges ", StringComparison.Ordinal) => Success(""),
                _ => throw new InvalidOperationException(command),
            },
        };
        service = new GitSvnWorkspaceService(runner);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public async Task Prepare_UsesVerifiedRevisionMappingForTheConfirmedDestination(int hashLength)
    {
        using var fixture = new GitSvnLocalTargetTests(hashLength);
        var prepared = await fixture.service.PrepareDcommitAsync(fixture.root, CancellationToken.None);

        Assert.True(prepared.Succeeded);
        Assert.Equal(new[] { RootUrl + "/branches/first" }, prepared.Snapshot!.SvnTargets);
        Assert.Equal(new[] { fixture.head }, prepared.Snapshot.PendingCommits.Select(commit => commit.Hash));
        Assert.DoesNotContain(fixture.runner.Calls, call => call.Arguments.StartsWith("svn ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepare_ReadsTheRawMetadataDirectoryForAnEscapedGitRef()
    {
        metadataUrl = RootUrl + "/branches/fix%25one";
        references = "refs/remotes/svn/fix%25one";
        var escapedMap = Path.Combine(root, ".git", "svn", "refs", "remotes", "svn", "fix%one", ".rev_map." + Uuid);
        Directory.CreateDirectory(Path.GetDirectoryName(escapedMap)!);
        File.Copy(mapPath, escapedMap);

        var prepared = await service.PrepareDcommitAsync(root, CancellationToken.None);

        Assert.True(prepared.Succeeded);
        Assert.Equal(new[] { metadataUrl }, prepared.Snapshot!.SvnTargets);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments == "svn info --url");
    }

    [Fact]
    public async Task Inspect_RechecksTheRevisionMapAndUsesNativeResolutionWhenItNoLongerMatches()
    {
        var first = await service.InspectAsync(root, CancellationToken.None);
        var bytes = File.ReadAllBytes(mapPath);
        bytes[4] = 0;
        File.WriteAllBytes(mapPath, bytes);
        nativeTarget = Success(RootUrl + "/branches/second");

        var second = await service.InspectAsync(root, CancellationToken.None);

        Assert.Equal(new[] { RootUrl + "/branches/first" }, first.SvnTargets);
        Assert.Equal(new[] { RootUrl + "/branches/second" }, second.SvnTargets);
        Assert.Single(runner.Calls, call => call.Arguments == "svn info --url");
    }

    [Fact]
    public async Task Inspect_UsesNativeMetadataWhenCommitOutputIsTruncated()
    {
        historySuffix = "[output truncated by Git-SVN Shuttle]";
        nativeTarget = Success(RootUrl + "/branches/second");

        var inspected = await service.InspectAsync(root, CancellationToken.None);

        Assert.Equal(new[] { RootUrl + "/branches/second" }, inspected.SvnTargets);
        Assert.Single(runner.Calls, call => call.Arguments == "svn info --url");
    }

    [Fact]
    public async Task Prepare_PreservesNativeFailureForAnInvalidRevisionMap()
    {
        File.WriteAllBytes(mapPath, File.ReadAllBytes(mapPath).Concat(new byte[] { 0 }).ToArray());
        nativeTarget = new GitCommandResult(1, "", "inconsistent revision map");

        var prepared = await service.PrepareDcommitAsync(root, CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Null(prepared.Snapshot);
        Assert.Single(runner.Calls, call => call.Arguments == "svn info --url");
    }

    [Fact]
    public async Task Prepare_LeavesDuplicateRefDiagnosticsToGitSvn()
    {
        configuration += "\nsvn-remote.svn.fetch branches/first:refs/remotes/svn/first\n" +
            "svn-remote.other.url " + RootUrl + "\n" +
            "svn-remote.other.fetch trunk:refs/remotes/svn/first";
        nativeTarget = new GitCommandResult(1, "", "Remote ref is tracked twice");

        var prepared = await service.PrepareDcommitAsync(root, CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Single(runner.Calls, call => call.Arguments == "svn info --url");
    }

    [Fact]
    public async Task Prepare_LeavesMissingTrackingRefResolutionToGitSvn()
    {
        references = "";
        nativeTarget = new GitCommandResult(1, "", "No matching SVN history");

        var prepared = await service.PrepareDcommitAsync(root, CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Single(runner.Calls, call => call.Arguments == "svn info --url");
    }

    private static GitCommandResult Success(string text) => new(0, text, "");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
