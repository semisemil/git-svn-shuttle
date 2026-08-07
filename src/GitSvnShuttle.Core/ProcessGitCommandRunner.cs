using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

public sealed class ProcessGitCommandRunner : IGitCommandRunner
{
    private readonly string gitExecutablePath;

    public ProcessGitCommandRunner(string gitExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gitExecutablePath))
        {
            throw new ArgumentException("Git executable path is required.", nameof(gitExecutablePath));
        }

        this.gitExecutablePath = gitExecutablePath;
    }

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = gitExecutablePath,
            Arguments = JoinArguments(arguments),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            var completion = new TaskCompletionSource<int>();

            process.OutputDataReceived += (_, eventArgs) => AppendLine(output, eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => AppendLine(error, eventArgs.Data);
            process.Exited += (_, __) => completion.TrySetResult(process.ExitCode);

            if (!process.Start())
            {
                throw new InvalidOperationException("Git could not be started.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                }

                completion.TrySetCanceled();
            }))
            {
                var exitCode = await completion.Task.ConfigureAwait(false);
                process.WaitForExit();
                return new GitCommandResult(exitCode, output.ToString().TrimEnd(), error.ToString().TrimEnd());
            }
        }
    }

    private static void AppendLine(StringBuilder builder, string? value)
    {
        if (value != null)
        {
            builder.AppendLine(value);
        }
    }

    internal static string JoinArguments(IReadOnlyList<string> arguments)
    {
        var result = new StringBuilder();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (index > 0)
            {
                result.Append(' ');
            }

            result.Append(Quote(arguments[index]));
        }

        return result.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return value;
        }

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append(character);
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            result.Append(character);
            backslashes = 0;
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }
}
