using GitSvnShuttle.Core;

if (args.Length < 2 || (args[1] == "--startup-options-only" && args.Length != 3))
{
    Console.Error.WriteLine("Usage: GitSvnShuttle.DcommitProbe <git.exe> <repository> [repository...]");
    Console.Error.WriteLine("   or: GitSvnShuttle.DcommitProbe <git.exe> --startup-options-only <repository-with-pending-commit>");
    return 2;
}

var runner = new ProcessGitCommandRunner(args[0]);
var service = new GitSvnWorkspaceService(runner);
if (args.Length == 3 && args[1] == "--startup-options-only")
{
    await VerifyStartupOptionsAsync(args[2]);
    return 0;
}
var repositories = args.Skip(1).ToArray();
var before = new Dictionary<string, (string Branch, string Head)>(StringComparer.OrdinalIgnoreCase);
var snapshots = new List<GitSvnPublishSnapshot>();
var untrackedFiles = new Dictionary<string, string>();

foreach (var repository in repositories)
{
    await AddUntrackedFileAsync(repository);
}

var discovered = await service.DiscoverAsync(repositories[0], CancellationToken.None);
Require(discovered.Count == repositories.Length, "nested repository discovery returned an unexpected repository count");
Require(
    discovered.Select(repository => Path.GetFullPath(repository.Path)).SequenceEqual(
        repositories.Select(Path.GetFullPath),
        StringComparer.OrdinalIgnoreCase),
    "nested repositories were not discovered in root-first path order");
Console.WriteLine("NESTED_DISCOVERY=PASS");
Require(discovered.All(repository => repository.IsReady), "untracked files made discovered repositories unready");

foreach (var repository in repositories)
{
    var rebase = await service.RebaseAsync(repository, CancellationToken.None);
    Require(rebase.Succeeded, repository + ": rebase failed: " + rebase.Message);

    var inspection = await service.InspectAsync(repository, CancellationToken.None);
    Require(inspection.IsReady, repository + ": repository is not ready after rebase");
    Require(inspection.PendingCommits.Count > 0, repository + ": no pending commit before dcommit");
    if (repository == repositories[0]) await VerifyStartupOptionsAsync(repository);

    var branch = await RunRequiredAsync(repository, "symbolic-ref", "--quiet", "--short", "HEAD");
    var head = await RunRequiredAsync(repository, "rev-parse", "--verify", "HEAD");
    before.Add(repository, (branch, head));

    var preparation = await service.PrepareDcommitAsync(repository, CancellationToken.None);
    Require(preparation.Succeeded, repository + ": dcommit preparation failed: " + preparation.Outcome.Message);
    Require(preparation.Snapshot != null, repository + ": dcommit preparation returned no snapshot");
    snapshots.Add(preparation.Snapshot!);

    await AddUntrackedFileAsync(repository);
    var validation = await service.ValidatePublishSnapshotAsync(preparation.Snapshot!, CancellationToken.None);
    Require(validation.Succeeded, repository + ": an untracked file invalidated publish confirmation");
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

foreach (var file in untrackedFiles)
{
    Require(File.Exists(file.Key), "untracked file was removed: " + file.Key);
    Require(File.ReadAllText(file.Key) == file.Value, "untracked file was changed: " + file.Key);
    var status = await RunRequiredAsync(Path.GetDirectoryName(file.Key)!,
        "status", "--porcelain=v1", "--untracked-files=all", "--", Path.GetFileName(file.Key));
    Require(status.StartsWith("?? ", StringComparison.Ordinal), "local file is no longer untracked: " + file.Key);
}
Console.WriteLine("UNTRACKED_FILES_PRESERVED=PASS");

return 0;

async Task VerifyStartupOptionsAsync(string repository)
{
    var head = await RunRequiredAsync(repository, "rev-parse", "HEAD");
    Require(head != await RunRequiredAsync(repository, "log", "--first-parent", "--grep=git-svn-id:", "--format=%H", "-1"),
        "startup option probe requires a pending Git-SVN commit");
    var status = await RunRequiredAsync(repository, "status", "--porcelain=v1");
    var configPath = Path.GetFullPath(Path.Combine(repository,
        await RunRequiredAsync(repository, "rev-parse", "--git-path", "shuttle-options-" + Guid.NewGuid().ToString("N") + ".config")));
    File.WriteAllText(configPath, "[SVN]\nDryRun\n");
    try
    {
        var included = await RunRequiredAsync(repository, "-c", "include.path=" + configPath, "svn", "dcommit");
        Require(included.Contains("diff-tree ", StringComparison.Ordinal), "included valueless boolean was not applied");
        var overridden = await RunRequiredAsync(repository, "-c", "svn.dryrun=false", "svn", "dcommit", "--dry-run");
        Require(overridden.Contains("diff-tree ", StringComparison.Ordinal), "command-line dry-run did not override configuration");
        var conversion = await runner.RunAsync(repository,
            ["-c", "svn.logwindowsize=not-a-number", "config", "--int", "--get", "svn.logwindowsize"], CancellationToken.None);
        Require(!conversion.Succeeded && conversion.StandardError.Length > 0, "invalid integer did not produce a native Git diagnostic");
        var invalid = await runner.RunAsync(repository,
            ["-c", "svn.logwindowsize=not-a-number", "svn", "dcommit", "--dry-run"], CancellationToken.None);
        Require(invalid.Succeeded && invalid.StandardOutput.Contains("diff-tree ", StringComparison.Ordinal),
            "invalid optional integer changed Git-SVN's existing fallback");
        Require(invalid.StandardError == conversion.StandardError, "Git-SVN did not retain Git's integer diagnostic");
        Require(head == await RunRequiredAsync(repository, "rev-parse", "HEAD"), "startup option checks changed HEAD");
        Require(status == await RunRequiredAsync(repository, "status", "--porcelain=v1"), "startup option checks changed worktree state");
    }
    finally { File.Delete(configPath); }
    Console.WriteLine("GIT_SVN_STARTUP_OPTIONS=PASS");
}

async Task AddUntrackedFileAsync(string repository)
{
    var path = Path.Combine(repository, "shuttle-untracked-" + Guid.NewGuid().ToString("N") + ".txt");
    const string content = "Local untracked content must survive rebase and dcommit.\n";
    File.WriteAllText(path, content);
    untrackedFiles.Add(path, content);
    var status = await RunRequiredAsync(repository,
        "status", "--porcelain=v1", "--untracked-files=all", "--", Path.GetFileName(path));
    Require(status.StartsWith("?? ", StringComparison.Ordinal), "fixture file is not untracked: " + path);
}

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
