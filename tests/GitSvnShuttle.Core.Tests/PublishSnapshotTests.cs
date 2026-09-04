using GitSvnShuttle.Core;
using Xunit;

namespace GitSvnShuttle.Core.Tests;

public sealed class PublishSnapshotTests
{
    [Fact]
    public void Snapshot_DoesNotChangeWhenSourceCollectionsChange()
    {
        var commit = new GitSvnCommit("confirmed", "confirm", "Kim", "2026-09-05", "Confirmed commit");
        var commits = new List<GitSvnCommit> { commit };
        var targets = new[] { "https://svn.test/confirmed" };
        var snapshot = new GitSvnPublishSnapshot("a", "a", "a/.git", "confirmed", "baseline",
            commits, "fingerprint", targets);

        commits.Clear();
        targets[0] = "https://svn.test/changed";

        Assert.Same(commit, Assert.Single(snapshot.PendingCommits));
        Assert.Equal("https://svn.test/confirmed", Assert.Single(snapshot.SvnTargets));
    }

    [Fact]
    public void Snapshot_DoesNotExposeMutableCollections()
    {
        var commit = new GitSvnCommit("confirmed", "confirm", "Kim", "2026-09-05", "Confirmed commit");
        var snapshot = new GitSvnPublishSnapshot("a", "a", "a/.git", "confirmed", "baseline",
            new[] { commit }, "fingerprint", new[] { "https://svn.test/confirmed" });

        Assert.Throws<NotSupportedException>(() => ((IList<GitSvnCommit>)snapshot.PendingCommits)[0] =
            new GitSvnCommit("changed", "changed", "Kim", "2026-09-05", "Changed commit"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.SvnTargets)[0] =
            "https://svn.test/changed");
    }
}
