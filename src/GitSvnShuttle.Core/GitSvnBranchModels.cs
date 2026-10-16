namespace GitSvnShuttle.Core;

public sealed class GitSvnDcommitTarget
{
    public GitSvnDcommitTarget(string? url, string? svnUrl, string? remoteName, string? remoteRef, string? revision, string? problem)
    {
        Url = url;
        SvnUrl = svnUrl;
        RemoteName = remoteName;
        RemoteRef = remoteRef;
        Revision = revision;
        Problem = problem;
    }

    public string? Url { get; }
    public string? SvnUrl { get; }
    public string? RemoteName { get; }
    public string? RemoteRef { get; }
    public string? Revision { get; }
    public string? Problem { get; }
    public bool IsResolved => Url != null && Problem == null;
}

public sealed class GitSvnBranchRequest
{
    public GitSvnBranchRequest(
        string sourcePath,
        string revision,
        string destinationPath,
        string branchName,
        string message,
        string localBranchName,
        bool switchToBranch = true)
    {
        SourcePath = sourcePath;
        Revision = revision;
        DestinationPath = destinationPath;
        BranchName = branchName;
        Message = message;
        LocalBranchName = localBranchName;
        SwitchToBranch = switchToBranch;
    }

    public string SourcePath { get; }
    public string Revision { get; }
    public string DestinationPath { get; }
    public string BranchName { get; }
    public string Message { get; }
    public string LocalBranchName { get; }
    public bool SwitchToBranch { get; }
}
