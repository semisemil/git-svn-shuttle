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
        GitSvnCommit? svnBaseline,
        IReadOnlyList<GitSvnCommit> pendingCommits,
        string? problem)
    {
        Name = name;
        Path = path;
        SvnBaseline = svnBaseline;
        PendingCommits = pendingCommits;
        Problem = problem;
    }

    public string Name { get; }
    public string Path { get; }
    public GitSvnCommit? SvnBaseline { get; }
    public IReadOnlyList<GitSvnCommit> PendingCommits { get; }
    public string? Problem { get; }
    public bool IsReady => string.IsNullOrWhiteSpace(Problem);
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
