using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class ProcessGitCommandRunnerUtf8Tests
{
    [Fact]
    public async Task InspectAsync_ReadsKoreanAuthorAndSubjectFromActualGitAsUtf8()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "GitSvnShuttleTests",
            "utf8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            var runner = new ProcessGitCommandRunner("git");
            Assert.True((await RunAsync(runner, repository, "init")).Succeeded);
            Assert.True((await RunAsync(runner, repository, "config", "user.name", "홍길동")).Succeeded);
            Assert.True((await RunAsync(runner, repository, "config", "user.email", "hong@example.test")).Succeeded);
            Assert.True((await RunAsync(
                runner,
                repository,
                "commit",
                "--allow-empty",
                "-m",
                "SVN 기준",
                "-m",
                "git-svn-id: https://svn.example.test/demo/trunk@1 00000000-0000-0000-0000-000000000000")).Succeeded);
            Assert.True((await RunAsync(
                runner,
                repository,
                "commit",
                "--allow-empty",
                "-m",
                "한글 커밋 제목")).Succeeded);

            var log = await RunAsync(
                runner,
                repository,
                "log",
                "-1",
                "--encoding=UTF-8",
                "--format=%an%x1f%s");

            Assert.True(log.Succeeded, log.CombinedOutput);
            Assert.Equal("홍길동\u001f한글 커밋 제목", log.StandardOutput.Trim());

            var service = new GitSvnWorkspaceService(runner);
            var inspected = await service.InspectAsync(repository, CancellationToken.None);
            var pending = Assert.Single(inspected.PendingCommits);
            Assert.Equal("홍길동", pending.Author);
            Assert.Equal("한글 커밋 제목", pending.Subject);
        }
        finally
        {
            if (Directory.Exists(repository))
            {
                ClearReadOnlyAttributes(repository);
                Directory.Delete(repository, recursive: true);
            }
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private static Task<GitCommandResult> RunAsync(
        ProcessGitCommandRunner runner,
        string repository,
        params string[] arguments) =>
        runner.RunAsync(repository, arguments, CancellationToken.None);
}
