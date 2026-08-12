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
    public async Task DiscoverAsync_UsesOnlyLoadedJunctionProjectToFindExternalGitSvnRootOnce()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "GitSvnShuttleTests", Guid.NewGuid().ToString("N"));
        var solutionRoot = Path.Combine(testRoot, "solution");
        var externalRoot = Path.Combine(testRoot, "external-repository");
        var externalProject = Path.Combine(externalRoot, "src", "Demo");
        var junctionPath = Path.Combine(solutionRoot, "linked-demo");
        var linkedProjectPath = Path.Combine(junctionPath, "Demo.csproj");
        Directory.CreateDirectory(solutionRoot);
        Directory.CreateDirectory(Path.Combine(externalRoot, ".git"));
        Directory.CreateDirectory(externalProject);
        File.WriteAllText(Path.Combine(externalProject, "Demo.csproj"), "<Project />");

        try
        {
            CreateJunction(junctionPath, externalProject);
            var runner = new FakeGitCommandRunner
            {
                Responder = (repository, arguments) =>
                {
                    Assert.Equal(Path.GetFullPath(externalRoot), Path.GetFullPath(repository));
                    if (arguments == "config --get-regexp ^svn-remote\\.")
                    {
                        return Success("svn-remote.svn.url https://svn.example.test/demo/trunk");
                    }

                    if (arguments == "--no-optional-locks status --porcelain=v1")
                    {
                        return Success(string.Empty);
                    }

                    if (arguments == "rev-parse --git-dir")
                    {
                        return Success(Path.Combine(externalRoot, ".git"));
                    }

                    if (arguments == "log --grep=git-svn-id: --format=%H -1")
                    {
                        return Success("baseline");
                    }

                    if (arguments.StartsWith("show -s --date=short --encoding=UTF-8 --format=", StringComparison.Ordinal))
                    {
                        return Success("baseline\u001fbase\u001fSVN\u001f2026-08-11\u001f기준");
                    }

                    if (arguments.StartsWith("log --date=short --encoding=UTF-8 --format=", StringComparison.Ordinal))
                    {
                        return Success(string.Empty);
                    }

                    throw new InvalidOperationException("Unexpected fake Git call: " + arguments);
                },
            };
            var service = new GitSvnWorkspaceService(runner);

            var repositories = await service.DiscoverAsync(
                solutionRoot,
                new[] { linkedProjectPath, linkedProjectPath },
                CancellationToken.None);

            var repository = Assert.Single(repositories);
            Assert.True(repository.IsExternalLink);
            Assert.Equal(Path.GetFullPath(externalRoot), repository.Path);
            Assert.Equal(Path.GetFullPath(linkedProjectPath), repository.LinkedProjectPath);
            Assert.Equal(1, runner.Calls.Count(call => call.Arguments == "config --get-regexp ^svn-remote\\."));
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

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /c mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        process!.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private static GitCommandResult Success(string output) => new GitCommandResult(0, output, string.Empty);
}
