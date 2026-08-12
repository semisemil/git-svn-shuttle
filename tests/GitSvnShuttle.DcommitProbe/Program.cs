using GitSvnShuttle.Core;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: GitSvnShuttle.DcommitProbe <git.exe> <repository> [repository...]");
    return 2;
}

var runner = new ProcessGitCommandRunner(args[0]);
var service = new GitSvnWorkspaceService(runner);
var repositories = args.Skip(1).ToArray();
var before = new Dictionary<string, (string Branch, string Head)>(StringComparer.OrdinalIgnoreCase);
var snapshots = new List<GitSvnPublishSnapshot>();

foreach (var repository in repositories)
{
    var rebase = await service.RebaseAsync(repository, CancellationToken.None);
    Require(rebase.Succeeded, repository + ": rebase failed: " + rebase.Message);

    var inspection = await service.InspectAsync(repository, CancellationToken.None);
    Require(inspection.IsReady, repository + ": repository is not ready after rebase");
    Require(inspection.PendingCommits.Count > 0, repository + ": no pending commit before dcommit");

    var branch = await RunRequiredAsync(repository, "symbolic-ref", "--quiet", "--short", "HEAD");
    var head = await RunRequiredAsync(repository, "rev-parse", "--verify", "HEAD");
    before.Add(repository, (branch, head));

    var preparation = await service.PrepareDcommitAsync(repository, CancellationToken.None);
    Require(preparation.Succeeded, repository + ": dcommit preparation failed: " + preparation.Outcome.Message);
    Require(preparation.Snapshot != null, repository + ": dcommit preparation returned no snapshot");
    snapshots.Add(preparation.Snapshot!);
}

var results = await service.DcommitPreparedAllAsync(snapshots, CancellationToken.None);
Require(results.Count == repositories.Length, "batch dcommit did not return one result per repository");
foreach (var result in results)
{
    Require(result.Succeeded, result.RepositoryPath + ": dcommit failed: " + result.Message);
}

foreach (var repository in repositories)
{
    var branch = await RunRequiredAsync(repository, "symbolic-ref", "--quiet", "--short", "HEAD");
    var head = await RunRequiredAsync(repository, "rev-parse", "--verify", "HEAD");
    var message = await RunRequiredAsync(repository, "log", "-1", "--format=%B");
    var inspection = await service.InspectAsync(repository, CancellationToken.None);

    Require(branch == before[repository].Branch, repository + ": current branch changed during dcommit");
    Require(head != before[repository].Head, repository + ": current branch still points to the pre-dcommit commit");
    Require(message.Contains("git-svn-id:", StringComparison.Ordinal), repository + ": rewritten HEAD has no git-svn-id");
    Require(inspection.IsReady, repository + ": repository is not ready after dcommit");
    Require(inspection.PendingCommits.Count == 0, repository + ": pending commits remain after dcommit");
    Require(inspection.SvnBaseline?.Hash == head, repository + ": SVN baseline does not match current HEAD");

    Console.WriteLine("DCOMMIT_BRANCH_SYNC=PASS");
    Console.WriteLine("REPOSITORY=" + repository);
    Console.WriteLine("BRANCH=" + branch);
    Console.WriteLine("BEFORE_HEAD=" + before[repository].Head);
    Console.WriteLine("AFTER_HEAD=" + head);
    Console.WriteLine("PENDING_COMMITS=0");
}

return 0;

async Task<string> RunRequiredAsync(string repository, params string[] arguments)
{
    var result = await runner.RunAsync(repository, arguments, CancellationToken.None);
    Require(result.Succeeded, repository + ": git " + string.Join(" ", arguments) + " failed: " + result.CombinedOutput);
    return result.StandardOutput.Trim();
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
