using System;
using System.Collections.Generic;
using System.IO;

namespace GitSvnShuttle.Core;

internal static class GitExecutableResolver
{
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
}
