namespace GitSvnShuttle.Core;

public sealed class GitCommandResult
{
    public GitCommandResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public bool Succeeded => ExitCode == 0;

    public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : string.IsNullOrWhiteSpace(StandardOutput)
            ? StandardError
            : StandardOutput + System.Environment.NewLine + StandardError;
}
