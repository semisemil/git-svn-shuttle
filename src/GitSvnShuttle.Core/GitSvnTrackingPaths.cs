using System;
using System.Collections.Generic;
using System.Linq;

namespace GitSvnShuttle.Core;

/// <summary>Paths relative to BaseUrl. Branch/tag containers accept one '*'; otherwise '/*' is added.</summary>
public sealed class GitSvnTrackingPaths
{
    public GitSvnTrackingPaths(string baseUrl, IReadOnlyList<string> trunkPaths,
        IReadOnlyList<string> branchesPaths, IReadOnlyList<string> tagsPaths,
        IReadOnlyList<string>? retainedPaths = null)
    {
        BaseUrl = baseUrl;
        TrunkPaths = Array.AsReadOnly(trunkPaths.ToArray());
        BranchesPaths = Array.AsReadOnly(branchesPaths.ToArray());
        TagsPaths = Array.AsReadOnly(tagsPaths.ToArray());
        RetainedPaths = Array.AsReadOnly((retainedPaths ?? Array.Empty<string>()).ToArray());
    }

    public string BaseUrl { get; }
    public IReadOnlyList<string> TrunkPaths { get; }
    public IReadOnlyList<string> BranchesPaths { get; }
    public IReadOnlyList<string> TagsPaths { get; }
    public IReadOnlyList<string> RetainedPaths { get; }
}
