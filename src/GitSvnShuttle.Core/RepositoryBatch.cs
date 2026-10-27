using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

internal static class RepositoryBatch
{
    internal static async Task<IReadOnlyList<T>> RunAsync<T>(
        IReadOnlyList<string> paths,
        Func<int, Task<T>> action,
        Func<T, bool>? succeeded = null)
    {
        var results = new List<T>(paths.Count);
        for (var index = 0; index < paths.Count;)
        {
            var paired = index + 1 < paths.Count && !SamePath(paths[index], paths[index + 1]);
            var first = action(index);
            if (paired)
            {
                var pair = await Task.WhenAll(first, action(index + 1)).ConfigureAwait(false);
                results.AddRange(pair);
                index += 2;
                if (succeeded != null && (!succeeded(pair[0]) || !succeeded(pair[1])))
                {
                    break;
                }
            }
            else
            {
                var result = await first.ConfigureAwait(false);
                results.Add(result);
                index++;
                if (succeeded != null && !succeeded(result))
                {
                    break;
                }
            }
        }

        return results;
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);
}
