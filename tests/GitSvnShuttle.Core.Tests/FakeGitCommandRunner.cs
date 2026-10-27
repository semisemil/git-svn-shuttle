using GitSvnShuttle.Core;

namespace GitSvnShuttle.Core.Tests;

internal sealed class FakeGitCommandRunner : IGitCommandRunner
{
    private readonly Queue<GitCommandResult> results = new();

    public List<(string WorkingDirectory, string Arguments)> Calls { get; } = new();
    public Func<string, string, GitCommandResult>? Responder { get; set; }
    public Func<string, string, CancellationToken, Task<GitCommandResult>>? AsyncResponder { get; set; }
    public Func<string, string, Exception?>? ExceptionFactory { get; set; }

    public void Enqueue(int exitCode = 0, string output = "", string error = "") =>
        results.Enqueue(new GitCommandResult(exitCode, output, error));

    public Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var joinedArguments = string.Join(" ", arguments);
        lock (Calls)
        {
            Calls.Add((workingDirectory, joinedArguments));
        }
        var exception = ExceptionFactory?.Invoke(workingDirectory, joinedArguments);
        if (exception != null)
        {
            throw exception;
        }

        if (AsyncResponder != null)
        {
            return AsyncResponder(workingDirectory, joinedArguments, cancellationToken);
        }

        if (Responder != null)
        {
            return Task.FromResult(Responder(workingDirectory, joinedArguments));
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException("No fake result was queued for: " + joinedArguments);
        }

        return Task.FromResult(results.Dequeue());
    }
}
