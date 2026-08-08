using System.Diagnostics;
using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class SecurityBoundaryTests
{
    [Fact]
    public async Task DiscoverAsync_DoesNotFollowDirectoryJunctions()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "GitSvnShuttleTests", Guid.NewGuid().ToString("N"));
        var solutionRoot = Path.Combine(testRoot, "solution");
        var outsideRoot = Path.Combine(testRoot, "outside");
        var junctionPath = Path.Combine(solutionRoot, "linked-outside");
        Directory.CreateDirectory(solutionRoot);
        Directory.CreateDirectory(Path.Combine(outsideRoot, ".git"));

        try
        {
            using (var process = Process.Start(new ProcessStartInfo
                   {
                       FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                       Arguments = "/d /c mklink /J \"" + junctionPath + "\" \"" + outsideRoot + "\"",
                       UseShellExecute = false,
                       RedirectStandardOutput = true,
                       RedirectStandardError = true,
                       CreateNoWindow = true,
                   }))
            {
                Assert.NotNull(process);
                process!.WaitForExit();
                Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
            }

            var runner = new FakeGitCommandRunner
            {
                Responder = (_, __) => new GitCommandResult(1, string.Empty, "not expected"),
            };
            var service = new GitSvnWorkspaceService(runner);

            var repositories = await service.DiscoverAsync(solutionRoot, CancellationToken.None);

            Assert.Empty(repositories);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ProcessRunner_RejectsConfiguredRelativeExecutablePath()
    {
        Assert.Throws<ArgumentException>(() => new ProcessGitCommandRunner("tools\\git.exe"));
    }
}
