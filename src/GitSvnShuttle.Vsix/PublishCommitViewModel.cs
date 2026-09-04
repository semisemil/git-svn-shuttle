namespace GitSvnShuttle.Vsix;

internal sealed class PublishCommitViewModel
{
    public PublishCommitViewModel(string repositoryName, string subject, string shortHash)
    {
        RepositoryName = repositoryName;
        Subject = subject;
        ShortHash = shortHash;
    }

    public string RepositoryName { get; }
    public string Subject { get; }
    public string ShortHash { get; }
}
