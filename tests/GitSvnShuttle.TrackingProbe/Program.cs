using System.Diagnostics;
using GitSvnShuttle.Core;

// This probe writes only to its disposable fixture root. It needs real git-svn,
// svn and svnadmin; it never connects to an existing SVN repository.
if (args.Length > 5)
{
    Console.Error.WriteLine("Usage: GitSvnShuttle.TrackingProbe [git] [svn] [svnadmin] [fixture-root] [loopback-fixture-url]");
    return 2;
}

var git = args.ElementAtOrDefault(0) ?? "git";
var svn = args.ElementAtOrDefault(1) ?? "svn";
var svnadmin = args.ElementAtOrDefault(2) ?? "svnadmin";
var fixtureRoot = Path.GetFullPath(args.ElementAtOrDefault(3) ?? Path.Combine(Path.GetTempPath(), "shuttle-tracking-" + Guid.NewGuid().ToString("N")));
var fixtureUrl = args.ElementAtOrDefault(4)?.TrimEnd('/');
if (fixtureUrl != null)
{
    Require(Uri.TryCreate(fixtureUrl, UriKind.Absolute, out var endpoint) && endpoint.Scheme == "svn" && endpoint.IsLoopback &&
        endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0,
        "The fixture URL must use svn:// on a loopback host and map to the new fixture root.");
}
Require(!Directory.Exists(fixtureRoot), "The fixture root must not exist: " + fixtureRoot);
Directory.CreateDirectory(fixtureRoot);
var runner = new ProcessGitCommandRunner(git);
var service = new GitSvnWorkspaceService(runner, new ProcessSvnCommandRunner(args.ElementAtOrDefault(1)));
var cancellation = CancellationToken.None;
Console.WriteLine("FIXTURE_ROOT=" + fixtureRoot);

await VerifyLayoutAsync("old", "Project/trunk", "Project/branches", "Project/tags");
await VerifyLayoutAsync("new", "trunk/Project", "branches", "tags/Project", "branches/Project-*");
await VerifyMigrationAsync();
Console.WriteLine("REAL_SVN_TRACKING_AND_BRANCHES=PASS");
return 0;

async Task VerifyLayoutAsync(string name, string trunk, string branches, string tags, string? branchPattern = null)
{
    var fixture = await SeedAsync(name, trunk, branches, tags);
    var trackingBranches = branchPattern ?? branches;
    await ConfigureAsync(fixture.Local, new GitSvnTrackingPaths(fixture.Url, [trunk], [trackingBranches], [tags]));
    var imported = await FindImportedRefAsync(fixture.Local, fixture.Url + "/" + trunk);
    var bootstrapName = name == "new" ? "Project-bootstrap" : "bootstrap";
    var bootstrap = await service.CreateSvnBranchAsync(fixture.Local,
        new GitSvnBranchRequest(trunk, "1", branches, bootstrapName, "Connect the first local branch from an unborn HEAD", "bootstrap"), cancellation);
    Require(bootstrap.Succeeded, name + ": first branch connection from unborn HEAD failed: " + bootstrap.Message);
    await RequireTargetAsync(fixture.Local, fixture.Url + "/" + branches + "/" + bootstrapName);
    await GitAsync(fixture.Local, "checkout", "-b", "initial", imported);
    var paths = await service.GetTrackingPathsAsync(fixture.Local, cancellation);
    Require(paths.TrunkPaths.Contains(trunk), name + ": trunk path was not returned");
    Require(paths.BranchesPaths.Any(path => path.TrimEnd('*', '/') == trackingBranches.TrimEnd('*', '/')), name + ": branches path was not returned");
    Require(paths.TagsPaths.Any(path => path.TrimEnd('*', '/') == tags), name + ": tags path was not returned");
    await RequireTargetAsync(fixture.Local, fixture.Url + "/" + trunk);

    // HEAD now differs from r1. An explicit old SVN revision must still copy r1.
    await RunAsync(svn, fixture.Root, "copy", fixture.Url + "/" + trunk, fixture.Url + "/" + tags + "/release-1", "-m", "배포 태그 보관");
    await RunAsync(svn, fixture.Root, "checkout", fixture.Url + "/" + trunk, fixture.WorkingCopy);
    File.WriteAllText(Path.Combine(fixture.WorkingCopy, "revision.txt"), "newer trunk revision\n");
    await RunAsync(svn, fixture.WorkingCopy, "commit", "-m", "Advance trunk after the selected source revision");

    const string localName = "trunk-looking-local-name";
    var branchName = name == "new" ? "Project-fix %" : "원격 fix%";
    var branch = await service.CreateSvnBranchAsync(fixture.Local,
        new GitSvnBranchRequest(trunk, "1", branches, branchName, "지정한 리비전에서 원격 브랜치 생성", localName), cancellation);
    Require(branch.Succeeded, name + ": branch creation failed: " + branch.Message);
    var branchUrl = fixture.Url + "/" + branches + "/" + Uri.EscapeDataString(branchName);
    var sourceContent = await RunAsync(svn, fixture.Root, "cat", branchUrl + "/revision.txt");
    Require(sourceContent == "selected revision 1\n", name + ": branch did not copy the requested revision");
    Require(await GitAsync(fixture.Local, "symbolic-ref", "--short", "HEAD") == localName, name + ": local branch was not switched");
    await RequireTargetAsync(fixture.Local, branchUrl);

    var content = name + " branch-only dcommit\n";
    File.WriteAllText(Path.Combine(fixture.Local, "branch-only.txt"), content);
    await GitAsync(fixture.Local, "add", "branch-only.txt");
    await GitAsync(fixture.Local, "commit", "-m", "Branch-only local change");
    var publish = await service.DcommitAsync(fixture.Local, cancellation);
    Require(publish.Succeeded, name + ": dcommit failed: " + publish.Message);
    Require(await RunAsync(svn, fixture.Root, "cat", branchUrl + "/branch-only.txt") == content, name + ": branch did not receive dcommit");
    Require(!(await RunResultAsync(svn, fixture.Root, "info", fixture.Url + "/" + trunk + "/branch-only.txt")).Succeeded, name + ": trunk received a branch-only dcommit");
    await RequireTargetAsync(fixture.Local, branchUrl);
    Console.WriteLine(name.ToUpperInvariant() + "_LAYOUT_BRANCH_REVISION_DCOMMIT=PASS");
}

async Task VerifyMigrationAsync()
{
    const string oldTrunk = "Project/trunk";
    const string oldBranches = "Project/branches";
    const string oldTags = "Project/tags";
    var fixture = await SeedAsync("mixed", oldTrunk, oldBranches, oldTags);
    // Model a repository that predates this feature, using git-svn's own
    // conventional single-remote configuration rather than managed namespaces.
    await GitAsync(fixture.Local, "svn", "init", "--trunk=" + oldTrunk, "--branches=" + oldBranches,
        "--tags=" + oldTags, "--prefix=svn/", fixture.Url);
    await GitAsync(fixture.Local, "svn", "fetch", "--all", "--no-checkout");
    await GitAsync(fixture.Local, "checkout", "-b", "local-work", await FindImportedRefAsync(fixture.Local, fixture.Url + "/" + oldTrunk));
    await RunAsync(svn, fixture.Root, "mkdir", fixture.Url + "/trunk", fixture.Url + "/branches", fixture.Url + "/branches/Project", fixture.Url + "/branches/Project/team", fixture.Url + "/tags", fixture.Url + "/tags/Project", "-m", "Create the project-first migration destinations", "--parents");
    await RunAsync(svn, fixture.Root, "copy", fixture.Url + "/" + oldTrunk, fixture.Url + "/trunk/Project", "-m", "Copy trunk into the new layout");
    await RunAsync(svn, fixture.Root, "copy", fixture.Url + "/" + oldTrunk, fixture.Url + "/" + oldBranches + "/existing", "-m", "Keep an old-layout feature branch");
    await RunAsync(svn, fixture.Root, "copy", fixture.Url + "/trunk/Project", fixture.Url + "/branches/Project/team/existing", "-m", "Create a nested new-layout feature branch");
    await RunAsync(svn, fixture.Root, "copy", fixture.Url + "/" + oldTrunk, fixture.Url + "/" + oldTags + "/release-old", "-m", "Retain a deployment tag");
    await RunAsync(svn, fixture.Root, "copy", fixture.Url + "/trunk/Project", fixture.Url + "/tags/Project/release-new", "-m", "Create a new-layout deployment tag");

    File.WriteAllText(Path.Combine(fixture.Local, "local-commit.txt"), "preserved local commit\n");
    await GitAsync(fixture.Local, "add", "local-commit.txt");
    await GitAsync(fixture.Local, "commit", "-m", "Preserve this unpublished local commit");
    File.AppendAllText(Path.Combine(fixture.Local, "staged.txt"), "staged content\n");
    await GitAsync(fixture.Local, "add", "staged.txt");
    File.AppendAllText(Path.Combine(fixture.Local, "unstaged.txt"), "unstaged content\n");
    File.WriteAllText(Path.Combine(fixture.Local, "untracked.txt"), "untracked content\n");
    var before = await SnapshotAsync(fixture.Local);

    await ConfigureAsync(fixture.Local, new GitSvnTrackingPaths(fixture.Url,
        [oldTrunk, "trunk/Project"], [oldBranches, "branches/Project/team"], [oldTags, "tags/Project"]));
    Require(await SnapshotAsync(fixture.Local) == before, "mixed: adding migration paths changed HEAD/index/worktree");
    var simultaneous = await service.GetTrackingPathsAsync(fixture.Local, cancellation);
    Require(simultaneous.TrunkPaths.Count == 2 && simultaneous.BranchesPaths.Count == 2 && simultaneous.TagsPaths.Count == 2,
        "mixed: old and new tracking paths were not returned together");
    await FindImportedRefAsync(fixture.Local, fixture.Url + "/" + oldBranches + "/existing");
    await FindImportedRefAsync(fixture.Local, fixture.Url + "/branches/Project/team/existing");
    await FindImportedRefAsync(fixture.Local, fixture.Url + "/" + oldTags + "/release-old");
    await FindImportedRefAsync(fixture.Local, fixture.Url + "/tags/Project/release-new");
    await RequireTargetAsync(fixture.Local, fixture.Url + "/" + oldTrunk);

    var preparedOnly = await service.CreateSvnBranchAsync(fixture.Local,
        new GitSvnBranchRequest("trunk/Project", "HEAD", "branches/Project/team", "prepared-dirty",
            "Prepare a branch while local changes are preserved", "prepared-with-dirty", switchToBranch: false), cancellation);
    Require(preparedOnly.Succeeded, "mixed: branch preparation with dirty state failed: " + preparedOnly.Message);
    Require(await SnapshotAsync(fixture.Local) == before, "mixed: preparing a branch changed current HEAD/index/worktree");
    await GitAsync(fixture.Local, "rev-parse", "--verify", "refs/heads/prepared-with-dirty");
    Console.WriteLine("DIRTY_STATE_BRANCH_PREPARATION=PASS");

    await ConfigureAsync(fixture.Local, new GitSvnTrackingPaths(fixture.Url,
        ["trunk/Project"], ["branches/Project/team"], ["tags/Project"]));
    Require(await SnapshotAsync(fixture.Local) == before, "mixed: retiring old paths changed HEAD/index/worktree");
    var migrated = await service.GetTrackingPathsAsync(fixture.Local, cancellation);
    Require(migrated.RetainedPaths.Any(path => UrlEquals(path, fixture.Url + "/" + oldTrunk)), "mixed: old trunk history binding was not retained");
    await RequireTargetAsync(fixture.Local, fixture.Url + "/" + oldTrunk);
    Console.WriteLine("MIXED_PATHS_AND_DIRTY_STATE_PRESERVATION=PASS");

    await GitAsync(fixture.Local, "stash", "push", "-u", "-m", "probe dirty-state snapshot");
    var publish = await service.DcommitAsync(fixture.Local, cancellation);
    Require(publish.Succeeded, "mixed: old history dcommit after retirement failed: " + publish.Message);
    Require(await RunAsync(svn, fixture.Root, "cat", fixture.Url + "/" + oldTrunk + "/local-commit.txt") == "preserved local commit\n", "mixed: preserved old-history commit was sent elsewhere");
    Require(!(await RunResultAsync(svn, fixture.Root, "info", fixture.Url + "/trunk/Project/local-commit.txt")).Succeeded, "mixed: migrating paths redirected the current local branch");
    await GitAsync(fixture.Local, "stash", "pop", "--index");
    Require((await GitAsync(fixture.Local, "diff", "--cached", "--", "staged.txt")).Contains("staged content", StringComparison.Ordinal), "mixed: staging was lost after publish/stash restore");
    Require(File.ReadAllText(Path.Combine(fixture.Local, "untracked.txt")) == "untracked content\n", "mixed: untracked content was lost");
    await GitAsync(fixture.Local, "stash", "push", "-u", "-m", "leave preserved user state in fixture stash");
    Console.WriteLine("RETIRED_HISTORY_DCOMMIT_TARGET=PASS");

    var retiredFeatureUrl = fixture.Url + "/" + oldBranches + "/existing";
    await GitAsync(fixture.Local, "checkout", "-b", "retired-feature", await FindImportedRefAsync(fixture.Local, retiredFeatureUrl));
    await RequireTargetAsync(fixture.Local, retiredFeatureUrl);
    File.WriteAllText(Path.Combine(fixture.Local, "retired-feature.txt"), "retired branch binding\n");
    await GitAsync(fixture.Local, "add", "retired-feature.txt");
    await GitAsync(fixture.Local, "commit", "-m", "Publish through a retired wildcard branch binding");
    publish = await service.DcommitAsync(fixture.Local, cancellation);
    Require(publish.Succeeded, "mixed: retired wildcard branch dcommit failed: " + publish.Message);
    Require(await RunAsync(svn, fixture.Root, "cat", retiredFeatureUrl + "/retired-feature.txt") == "retired branch binding\n", "mixed: retired branch did not receive dcommit");
    Require(!(await RunResultAsync(svn, fixture.Root, "info", fixture.Url + "/branches/Project/team/existing/retired-feature.txt")).Succeeded, "mixed: retired branch commit was sent to the new branch layout");
    Console.WriteLine("RETIRED_WILDCARD_BRANCH_DCOMMIT=PASS");

    var branch = await service.CreateSvnBranchAsync(fixture.Local,
        new GitSvnBranchRequest("trunk/Project", "HEAD", "branches/Project/team", "migrated-fix", "Create a nested branch during migration", "misleading-trunk"), cancellation);
    Require(branch.Succeeded, "mixed: nested branch creation failed: " + branch.Message);
    var expected = fixture.Url + "/branches/Project/team/migrated-fix";
    await RequireTargetAsync(fixture.Local, expected);
    File.WriteAllText(Path.Combine(fixture.Local, "nested-only.txt"), "nested migration branch\n");
    await GitAsync(fixture.Local, "add", "nested-only.txt");
    await GitAsync(fixture.Local, "commit", "-m", "Commit to the nested branch");
    publish = await service.DcommitAsync(fixture.Local, cancellation);
    Require(publish.Succeeded, "mixed: nested branch dcommit failed: " + publish.Message);
    Require(await RunAsync(svn, fixture.Root, "cat", expected + "/nested-only.txt") == "nested migration branch\n", "mixed: nested branch did not receive dcommit");
    await GitAsync(fixture.Local, "checkout", "prepared-with-dirty");
    await RequireTargetAsync(fixture.Local, fixture.Url + "/branches/Project/team/prepared-dirty");
    Console.WriteLine("MIXED_NESTED_BRANCH_DCOMMIT=PASS");

    // A user-selected destination can lie outside every configured glob.
    // The production workflow must add an exact history binding for it.
    var untrackedDestination = await service.CreateSvnBranchAsync(fixture.Local,
        new GitSvnBranchRequest(fixture.Url + "/trunk/Project", "HEAD", fixture.Url + "/branches/Project/other/subteam",
            "exact-fix", "Create parents and an exact tracking binding", "outside-the-glob"), cancellation);
    Require(untrackedDestination.Succeeded, "mixed: exact branch binding failed: " + untrackedDestination.Message);
    var exactUrl = fixture.Url + "/branches/Project/other/subteam/exact-fix";
    await RequireTargetAsync(fixture.Local, exactUrl);
    File.WriteAllText(Path.Combine(fixture.Local, "exact-only.txt"), "exact branch binding\n");
    await GitAsync(fixture.Local, "add", "exact-only.txt");
    await GitAsync(fixture.Local, "commit", "-m", "Commit through the exact branch binding");
    publish = await service.DcommitAsync(fixture.Local, cancellation);
    Require(publish.Succeeded, "mixed: exact branch dcommit failed: " + publish.Message);
    Require(await RunAsync(svn, fixture.Root, "cat", exactUrl + "/exact-only.txt") == "exact branch binding\n", "mixed: exact binding sent the change elsewhere");
    Console.WriteLine("UNTRACKED_DESTINATION_EXACT_BINDING_DCOMMIT=PASS");
}

async Task<Fixture> SeedAsync(string name, string trunk, string branches, string tags)
{
    var root = Path.Combine(fixtureRoot, name);
    Directory.CreateDirectory(root);
    var server = Path.Combine(root, "svn-repository");
    var url = fixtureUrl == null
        ? new Uri(server + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/')
        : fixtureUrl + "/" + name + "/svn-repository";
    await RunAsync(svnadmin, root, "create", server);
    if (fixtureUrl != null)
    {
        File.WriteAllText(Path.Combine(server, "conf", "svnserve.conf"), "[general]\nanon-access = write\nauth-access = write\n");
    }
    var seed = Path.Combine(root, "seed");
    foreach (var path in new[] { trunk, branches, tags }) Directory.CreateDirectory(Path.Combine(seed, path));
    foreach (var file in new[] { "staged.txt", "unstaged.txt" }) File.WriteAllText(Path.Combine(seed, trunk, file), "original\n");
    File.WriteAllText(Path.Combine(seed, trunk, "revision.txt"), "selected revision 1\n");
    await RunAsync(svn, root, "import", seed, url, "-m", "Seed a disposable repository");
    var local = Path.Combine(root, "local");
    Directory.CreateDirectory(local);
    // Set identity only inside this disposable Git repository.
    await GitAsync(local, "init");
    // Assertions compare exact file bytes across stash/checkout, independently
    // of the host Git installation's global newline conversion defaults.
    await GitAsync(local, "config", "core.autocrlf", "false");
    await GitAsync(local, "config", "user.name", "Tracking Probe");
    await GitAsync(local, "config", "user.email", "tracking-probe@example.invalid");
    return new Fixture(root, local, url, Path.Combine(root, "svn-working-copy"));
}

async Task ConfigureAsync(string repository, GitSvnTrackingPaths paths)
{
    var result = await service.ConfigureTrackingPathsAsync(repository, paths, cancellation);
    Require(result.Succeeded, "Configuring tracking paths failed: " + result.Message);
}

async Task RequireTargetAsync(string repository, string expected)
{
    var target = await service.ResolveDcommitTargetAsync(repository, cancellation);
    Require(target.IsResolved, "Dcommit target was unresolved: " + target.Problem);
    Require(UrlEquals(target.Url!, expected), "Expected dcommit target " + expected + "; actual " + target.Url);
    Require(long.TryParse(target.Revision, out _), "The resolved SVN revision is missing or invalid");
    var actual = await GitAsync(repository, "svn", "info", "--url");
    Require(UrlEquals(actual, expected), "git-svn's actual history URL differs: " + actual);
}

async Task<string> FindImportedRefAsync(string repository, string url)
{
    var refs = (await GitAsync(repository, "for-each-ref", "--format=%(refname)", "refs/remotes"))
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    foreach (var reference in refs)
    {
        var message = await GitAsync(repository, "show", "-s", "--format=%B", reference);
        var marker = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("git-svn-id: ", StringComparison.Ordinal));
        if (marker != null && UrlEquals(marker.Substring("git-svn-id: ".Length).Split('@')[0], url)) return reference;
    }
    throw new InvalidOperationException("No imported SVN ref for " + url);
}

async Task<string> SnapshotAsync(string repository) => string.Join("\u001e", new[]
{
    await GitAsync(repository, "rev-parse", "HEAD"),
    await GitAsync(repository, "symbolic-ref", "HEAD"),
    await GitAsync(repository, "status", "--porcelain=v1", "--untracked-files=all"),
    await GitAsync(repository, "diff", "--binary"),
    await GitAsync(repository, "diff", "--cached", "--binary"),
    File.ReadAllText(Path.Combine(repository, "untracked.txt")),
});

async Task<string> GitAsync(string directory, params string[] arguments)
{
    var result = await runner.RunAsync(directory, arguments, cancellation);
    Require(result.Succeeded, "git " + string.Join(" ", arguments) + " failed: " + result.CombinedOutput);
    return result.StandardOutput.TrimEnd('\r', '\n');
}

async Task<string> RunAsync(string executable, string directory, params string[] arguments)
{
    var result = await RunResultAsync(executable, directory, arguments);
    Require(result.Succeeded, executable + " " + string.Join(" ", arguments) + " failed: " + result.Error);
    return result.Output;
}

async Task<(bool Succeeded, string Output, string Error)> RunResultAsync(string executable, string directory, params string[] arguments)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + executable);
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode == 0, await output, await error);
}

static bool UrlEquals(string left, string right) => Uri.UnescapeDataString(left.TrimEnd('/')) == Uri.UnescapeDataString(right.TrimEnd('/'));
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
internal sealed record Fixture(string Root, string Local, string Url, string WorkingCopy);
