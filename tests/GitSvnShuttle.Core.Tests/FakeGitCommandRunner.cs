using GitSvnShuttle.Core;

namespace GitSvnShuttle.Core.Tests;

internal sealed class FakeGitCommandRunner : IGitCommandRunner
{
    private readonly Queue<GitCommandResult> results = new();

    public List<(string WorkingDirectory, string Arguments)> Calls { get; } = new();

    public void Enqueue(int exitCode = 0, string output = "", string error = "") =>
        results.Enqueue(new GitCommandResult(exitCode, output, error));

    public Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((workingDirectory, string.Join(" ", arguments)));
        if (results.Count == 0)
        {
            throw new InvalidOperationException("No fake result was queued for: " + string.Join(" ", arguments));
        }

        return Task.FromResult(results.Dequeue());
    }
}
