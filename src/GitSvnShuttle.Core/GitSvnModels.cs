using System.Collections.Generic;

namespace GitSvnShuttle.Core;

public sealed class GitSvnCommit
{
    public GitSvnCommit(string hash, string shortHash, string author, string date, string subject)
    {
        Hash = hash;
        ShortHash = shortHash;
        Author = author;
        Date = date;
        Subject = subject;
    }

    public string Hash { get; }
    public string ShortHash { get; }
    public string Author { get; }
    public string Date { get; }
    public string Subject { get; }
}

public sealed class GitSvnRepository
{
    public GitSvnRepository(
        string name,
        string path,
        string? gitDirectory,
        GitSvnCommit? svnBaseline,
        IReadOnlyList<GitSvnCommit> pendingCommits,
        string? problem)
    {
        Name = name;
        Path = path;
        GitDirectory = gitDirectory;
        SvnBaseline = svnBaseline;
        PendingCommits = pendingCommits;
        Problem = problem;
    }

    public string Name { get; }
    public string Path { get; }
    public string? GitDirectory { get; }
    public GitSvnCommit? SvnBaseline { get; }
    public IReadOnlyList<GitSvnCommit> PendingCommits { get; }
    public string? Problem { get; }
    public bool IsReady => string.IsNullOrWhiteSpace(Problem);
}

public sealed class GitSvnPublishSnapshot
{
    public GitSvnPublishSnapshot(
        string repositoryName,
        string repositoryPath,
        string gitDirectory,
        string headHash,
        string baselineHash,
        IReadOnlyList<GitSvnCommit> pendingCommits,
        string svnConfigurationFingerprint,
        IReadOnlyList<string> svnTargets)
    {
        RepositoryName = repositoryName;
        RepositoryPath = repositoryPath;
        GitDirectory = gitDirectory;
        HeadHash = headHash;
        BaselineHash = baselineHash;
        PendingCommits = pendingCommits;
        SvnConfigurationFingerprint = svnConfigurationFingerprint;
        SvnTargets = svnTargets;
    }

    public string RepositoryName { get; }
    public string RepositoryPath { get; }
    public string GitDirectory { get; }
    public string HeadHash { get; }
    public string BaselineHash { get; }
    public IReadOnlyList<GitSvnCommit> PendingCommits { get; }
    public string SvnConfigurationFingerprint { get; }
    public IReadOnlyList<string> SvnTargets { get; }
}

public sealed class PublishPreparationResult
{
    public PublishPreparationResult(OperationResult outcome, GitSvnPublishSnapshot? snapshot)
    {
        Outcome = outcome;
        Snapshot = snapshot;
    }

    public OperationResult Outcome { get; }
    public GitSvnPublishSnapshot? Snapshot { get; }
    public bool Succeeded => Outcome.Succeeded && Snapshot != null;
}

public sealed class OperationResult
{
    public OperationResult(string repositoryPath, bool succeeded, string message)
    {
        RepositoryPath = repositoryPath;
        Succeeded = succeeded;
        Message = message;
    }

    public string RepositoryPath { get; }
    public bool Succeeded { get; }
    public string Message { get; }
}
