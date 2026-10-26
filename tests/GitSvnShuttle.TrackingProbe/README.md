This executable creates disposable local SVN repositories and runs the production workspace service against real `git svn` and `svn` commands. It requires .NET 10, Git with git-svn, and matching SVN client/admin tools. It never connects to an existing SVN server.

```powershell
dotnet run --project tests/GitSvnShuttle.TrackingProbe/GitSvnShuttle.TrackingProbe.csproj -- <absolute-git-path> <absolute-svn-path> <absolute-svnadmin-path> <new-fixture-directory> [loopback-fixture-url]
```

The fixture directory must not exist. It remains on disk for inspecting SVN history, Git configuration, refs, and preserved dirty-state stashes. Omitting arguments uses commands from PATH and a unique temporary directory; on systems whose executable resolver expects `git.exe`, pass an absolute Git path.

Without the fifth argument, the probe accesses its repositories through `file://` URLs. Windows CI passes a loopback `svn://` URL and places the fixture directory beneath the server root already created by `test-env/setup.ps1`. The URL must map to that new directory; for example, a server rooted at `ShuttleTest` maps `ShuttleTest/tracking` to `svn://127.0.0.1:<port>/tracking`. The probe enables anonymous writes only in the disposable repositories it creates. This uses the same network transport as the existing smoke test, avoiding direct filesystem access between the Windows SVN tools and MSYS Git-SVN runtime.

The probe checks:

- Old (`Project/trunk`) and new (`trunk/Project`) layouts: tracking path lookup, first local connection from an unborn HEAD, server branch creation from an explicit older numeric revision, Korean/space/percent branch names, messages, prefix patterns such as `branches/Project-*`, local branch connection, and real dcommit verified with `svn cat` on the destination and absence on trunk.
- Mixed layouts: simultaneous imports of old/new trunks, branch containers and deployment tags, including a nested new branch container.
- Existing repositories initialized with conventional `git svn init`: adding and retiring paths preserve HEAD, local commits, staged changes, unstaged changes, and untracked content. Preparing a remote/local branch without switching also preserves that state. The original trunk binding and imported wildcard branch binding remain usable, with real dcommit verified on each old SVN path after retirement.
- A new nested branch during migration: real dcommit reaches its SVN path even when the local branch name contains `trunk`.
- A destination outside all configured branch globs: new parent directories, an exact history binding, and real dcommit to that path.
- The production management form: name-only input, automatic paths and message, the displayed destination, a real branch-only dcommit, and duplicate-submission protection.
- Partial creation and recovery: create an SVN branch, fail the real Git-SVN fetch with a fixture-owned ref lock, verify the current local branch is preserved, then fetch and connect the existing server branch and publish to it without another SVN copy.

Successful output ends with `REAL_SVN_TRACKING_AND_BRANCHES=PASS`. CI runs this probe after the existing rebase/dcommit smoke test, using the same installed Git/SVN tools.
