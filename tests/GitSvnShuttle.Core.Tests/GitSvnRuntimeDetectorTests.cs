using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class GitSvnRuntimeDetectorTests
{
    [Fact]
    public async Task DiagnoseAsync_DistinguishesGitWithoutSvn()
    {
        var runner = new FakeGitCommandRunner
        {
            Responder = (_, arguments) => arguments == "--version"
                ? new GitCommandResult(0, "git version 2.50.0", string.Empty)
                : new GitCommandResult(1, string.Empty, "git: 'svn' is not a git command"),
        };
        var detector = CreateDetector(_ => runner, new[] { "C:\\Git\\git.exe" });

        var result = await detector.DiagnoseAsync("C:\\Git\\git.exe", CancellationToken.None);

        Assert.Equal(GitSvnRuntimeStatus.GitSvnNotAvailable, result.Status);
        Assert.Equal("git version 2.50.0", result.GitVersion);
        Assert.Contains("git svn", result.Message);
    }

    [Fact]
    public async Task AutoDetectAsync_SkipsGitOnlyCandidateAndSelectsGitSvnRuntime()
    {
        var plainGit = CreateVersionRunner(hasGitSvn: false);
        var gitSvn = CreateVersionRunner(hasGitSvn: true);
        var runners = new Dictionary<string, IGitCommandRunner>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:\\Program Files\\Git\\cmd\\git.exe"] = plainGit,
            ["C:\\msys64\\ucrt64\\bin\\git.exe"] = gitSvn,
        };
        var detector = CreateDetector(
            path => runners[path],
            runners.Keys.ToArray());

        var result = await detector.AutoDetectAsync(CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal("C:\\msys64\\ucrt64\\bin\\git.exe", result.ExecutablePath);
        Assert.Equal("git-svn version 2.50.0", result.GitSvnVersion);
    }

    [Fact]
    public async Task DiagnoseAsync_MissingConfiguredPathReturnsActionableState()
    {
        var detector = new GitSvnRuntimeDetector(
            _ => throw new FileNotFoundException(),
            _ => throw new InvalidOperationException("Runner must not be created."),
            () => Array.Empty<string>(),
            Path.GetTempPath());

        var result = await detector.DiagnoseAsync("C:\\missing\\git.exe", CancellationToken.None);

        Assert.Equal(GitSvnRuntimeStatus.GitNotFound, result.Status);
        Assert.Contains("찾을 수 없습니다", result.Message);
    }

    private static GitSvnRuntimeDetector CreateDetector(
        Func<string, IGitCommandRunner> runnerFactory,
        IReadOnlyList<string> candidates) =>
        new(
            path => path,
            runnerFactory,
            () => candidates,
            Path.GetTempPath());

    private static FakeGitCommandRunner CreateVersionRunner(bool hasGitSvn) =>
        new()
        {
            Responder = (_, arguments) => arguments switch
            {
                "--version" => new GitCommandResult(0, "git version 2.50.0", string.Empty),
                "svn --version" when hasGitSvn =>
                    new GitCommandResult(0, "git-svn version 2.50.0", string.Empty),
                "svn --version" =>
                    new GitCommandResult(1, string.Empty, "git: 'svn' is not a git command"),
                _ => throw new InvalidOperationException("Unexpected command: " + arguments),
            },
        };
}
