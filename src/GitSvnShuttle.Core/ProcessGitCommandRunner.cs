using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

public sealed class ProcessGitCommandRunner : IGitCommandRunner
{
    private const int MaxCapturedCharactersPerStream = 1_000_000;
    private static readonly TimeSpan ReadCommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SvnCommandTimeout = TimeSpan.FromMinutes(30);
    private readonly string gitExecutablePath;
    private static readonly Encoding GitOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public ProcessGitCommandRunner(string gitExecutablePath)
    {
        this.gitExecutablePath = ResolveExecutablePath(gitExecutablePath);
    }

    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("Git working directory does not exist: " + workingDirectory);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = gitExecutablePath,
            Arguments = JoinArguments(arguments),
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = GitOutputEncoding,
            StandardErrorEncoding = GitOutputEncoding,
            CreateNoWindow = true,
        };
        startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "Never";
        if (arguments.Count >= 2 &&
            string.Equals(arguments[0], "rebase", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(arguments[1], "--continue", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.EnvironmentVariables["GIT_EDITOR"] = "true";
        }

        using (var timeoutSource = new CancellationTokenSource(GetTimeout(arguments)))
        using (var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   timeoutSource.Token))
        using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
        {
            var output = new BoundedTextBuffer(MaxCapturedCharactersPerStream);
            var error = new BoundedTextBuffer(MaxCapturedCharactersPerStream);
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, eventArgs) => output.AppendLine(eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => error.AppendLine(eventArgs.Data);
            process.Exited += (_, __) => completion.TrySetResult(process.ExitCode);

            if (!process.Start())
            {
                throw new InvalidOperationException("Git could not be started.");
            }

            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (linkedSource.Token.Register(() =>
            {
                TryKill(process);
                completion.TrySetCanceled();
            }))
            {
                try
                {
                    var exitCode = await completion.Task.ConfigureAwait(false);
                    process.WaitForExit();
                    return new GitCommandResult(exitCode, output.ToString(), error.ToString());
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Git command exceeded its time limit and was stopped: " +
                        string.Join(" ", arguments));
                }
            }
        }
    }

    internal static string ResolveExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Git executable path is required.", nameof(executablePath));
        }

        var value = Environment.ExpandEnvironmentVariables(executablePath.Trim().Trim('"'));
        if (Path.IsPathRooted(value))
        {
            var absolute = Path.GetFullPath(value);
            if (!File.Exists(absolute))
            {
                throw new FileNotFoundException("Git executable was not found.", absolute);
            }

            return absolute;
        }

        if (value.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            value.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
        {
            throw new ArgumentException(
                "A configured Git executable must use an absolute path.",
                nameof(executablePath));
        }

        var fileName = Path.HasExtension(value) ? value : value + ".exe";
        foreach (var candidate in FindCandidateExecutablePaths(fileName))
        {
            return candidate;
        }

        throw new FileNotFoundException(
            "Git executable was not found on PATH. Configure GIT_SVN_SHUTTLE_GIT with an absolute path.",
            fileName);
    }

    internal static IReadOnlyList<string> FindCandidateExecutablePaths(string fileName = "git.exe")
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in pathValue.Split(Path.PathSeparator))
        {
            var directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
            {
                continue;
            }

            AddExistingCandidate(candidates, seen, Path.Combine(directory, fileName));
        }

        foreach (var candidate in GetWellKnownGitPaths(fileName))
        {
            AddExistingCandidate(candidates, seen, candidate);
        }

        return candidates;
    }

    private static void AddExistingCandidate(
        ICollection<string> candidates,
        ISet<string> seen,
        string candidate)
    {
        var absolute = Path.GetFullPath(candidate);
        if (File.Exists(absolute) && seen.Add(absolute))
        {
            candidates.Add(absolute);
        }
    }

    private static IEnumerable<string> GetWellKnownGitPaths(string fileName)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "Git", "cmd", fileName);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Git", "cmd", fileName);
        }

        var applicationDirectory = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(applicationDirectory))
        {
            yield return Path.Combine(
                applicationDirectory,
                "CommonExtensions",
                "Microsoft",
                "TeamFoundation",
                "Team Explorer",
                "Git",
                "cmd",
                fileName);
        }

        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (!string.IsNullOrWhiteSpace(systemDrive))
        {
            yield return Path.Combine(systemDrive + Path.DirectorySeparatorChar, "msys64", "ucrt64", "bin", fileName);
            yield return Path.Combine(systemDrive + Path.DirectorySeparatorChar, "msys64", "mingw64", "bin", fileName);
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

    private static TimeSpan GetTimeout(IReadOnlyList<string> arguments) =>
        arguments.Count >= 2 && string.Equals(arguments[0], "svn", StringComparison.OrdinalIgnoreCase)
            ? SvnCommandTimeout
            : ReadCommandTimeout;

    private static void TryKill(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            var taskKillPath = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
            if (File.Exists(taskKillPath))
            {
                using (var taskKill = Process.Start(new ProcessStartInfo
                       {
                           FileName = taskKillPath,
                           Arguments = "/PID " + process.Id + " /T /F",
                           UseShellExecute = false,
                           CreateNoWindow = true,
                       }))
                {
                    taskKill?.WaitForExit(5_000);
                }
            }

            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
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

    private sealed class BoundedTextBuffer
    {
        private const string TruncationMarker = "[output truncated by Git-SVN Shuttle]";
        private readonly StringBuilder value = new StringBuilder();
        private readonly int limit;
        private bool truncated;

        public BoundedTextBuffer(int limit)
        {
            this.limit = limit;
        }

        public void AppendLine(string? line)
        {
            if (line == null || truncated)
            {
                return;
            }

            var required = line.Length + Environment.NewLine.Length;
            if (value.Length + required <= limit)
            {
                value.AppendLine(line);
                return;
            }

            var remaining = Math.Max(0, limit - value.Length - TruncationMarker.Length - Environment.NewLine.Length);
            if (remaining > 0)
            {
                value.Append(line, 0, Math.Min(line.Length, remaining));
                value.AppendLine();
            }

            value.Append(TruncationMarker);
            truncated = true;
        }

        public override string ToString() => value.ToString().TrimEnd();
    }
}
