using System.Diagnostics;
using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class ProcessGitCommandRunnerCancellationTests
{
    [Fact]
    public async Task RunAsync_CancellationStopsCurrentProcessPromptly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        Assert.True(File.Exists(ping));
        var runner = new ProcessGitCommandRunner(ping);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            Path.GetTempPath(),
            new[] { "127.0.0.1", "-n", "30" },
            cancellation.Token));

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            "취소된 프로세스 트리가 10초 안에 종료되지 않았습니다: " + stopwatch.Elapsed);
    }
}
