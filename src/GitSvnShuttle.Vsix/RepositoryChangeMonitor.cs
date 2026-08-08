using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitSvnShuttle.Core;
using Microsoft.VisualStudio.Shell;

namespace GitSvnShuttle.Vsix;

internal sealed class RepositoryChangeMonitor : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
    private static readonly string[] IgnoredWorkingTreeSegments =
    {
        ".git", ".vs", "bin", "obj", "node_modules", "packages",
    };

    private readonly Func<Task> changed;
    private readonly object gate = new object();
    private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
    private readonly Timer debounceTimer;
    private bool disposed;

    public RepositoryChangeMonitor(Func<Task> changed)
    {
        this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
        debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Configure(string solutionDirectory, IEnumerable<GitSvnRepository> repositories)
    {
        if (disposed)
        {
            return;
        }

        DisposeWatchers();

        if (Directory.Exists(solutionDirectory))
        {
            TryAddWatcher(solutionDirectory, includeSubdirectories: true, IsRelevantWorkingTreeChange);
        }

        foreach (var gitDirectory in repositories
                     .Select(repository => repository.GitDirectory)
                     .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryAddWatcher(gitDirectory!, includeSubdirectories: true, IsRelevantGitMetadataChange);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        DisposeWatchers();
        debounceTimer.Dispose();
    }

    private void TryAddWatcher(
        string directory,
        bool includeSubdirectories,
        Func<string, string, bool> isRelevant)
    {
        try
        {
            AddWatcher(directory, includeSubdirectories, isRelevant);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
        catch (ArgumentException)
        {
        }
    }

    private void AddWatcher(
        string directory,
        bool includeSubdirectories,
        Func<string, string, bool> isRelevant)
    {
        var root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size,
        };

        FileSystemEventHandler onChange = (_, eventArgs) =>
        {
            if (IsRelevantSafely(isRelevant, root, eventArgs.FullPath))
            {
                Schedule();
            }
        };
        RenamedEventHandler onRename = (_, eventArgs) =>
        {
            if (IsRelevantSafely(isRelevant, root, eventArgs.FullPath) ||
                IsRelevantSafely(isRelevant, root, eventArgs.OldFullPath))
            {
                Schedule();
            }
        };

        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += onRename;
        watcher.Error += (_, __) => Schedule();
        watcher.EnableRaisingEvents = true;
        watchers.Add(watcher);
    }

    private static bool IsRelevantSafely(
        Func<string, string, bool> isRelevant,
        string root,
        string fullPath)
    {
        try
        {
            return isRelevant(root, fullPath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private void Schedule()
    {
        lock (gate)
        {
            if (!disposed)
            {
                debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
        }

#pragma warning disable VSSDK007 // Timer callback is a synchronous boundary; FileAndForget observes failures.
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                await changed();
            }
            catch
            {
                // The view model reports operation failures. Watcher callbacks must not crash Visual Studio.
            }
        }).FileAndForget("GitSvnShuttle/RepositoryChange");
#pragma warning restore VSSDK007
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        watchers.Clear();
    }

    private static bool IsRelevantWorkingTreeChange(string root, string fullPath)
    {
        var relative = GetRelativePath(root, fullPath);
        if (relative == null)
        {
            return false;
        }

        var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && !segments.Any(segment =>
            IgnoredWorkingTreeSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsRelevantGitMetadataChange(string root, string fullPath)
    {
        var relative = GetRelativePath(root, fullPath)?.Replace('\\', '/');
        if (relative == null || relative.Length == 0)
        {
            return false;
        }

        return relative.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
               relative.Equals("index", StringComparison.OrdinalIgnoreCase) ||
               relative.Equals("config", StringComparison.OrdinalIgnoreCase) ||
               relative.Equals("packed-refs", StringComparison.OrdinalIgnoreCase) ||
               relative.Equals("MERGE_HEAD", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("refs/", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("svn/", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("rebase-apply/", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("rebase-merge/", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("worktrees/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetRelativePath(string root, string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        var prefix = root + Path.DirectorySeparatorChar;
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized.Substring(prefix.Length)
            : null;
    }
}
