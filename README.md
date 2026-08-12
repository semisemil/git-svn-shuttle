# Git-SVN Shuttle

Git-SVN Shuttle is a Visual Studio extension for teams that use Git locally while an SVN server remains the source of truth.

![Git-SVN Shuttle](src/GitSvnShuttle.Vsix/Assets/GitSvnShuttle.Preview.png)

![Git-SVN Shuttle tool window](marketplace/images/git-svn-shuttle-overview.png)

It fills the gap left after Visual Studio's built-in Git UI has staged and committed changes:

- **Get from SVN** runs `git svn rebase`.
- **Publish to SVN** previews local commits and runs `git svn dcommit`.
- The solution root and nested Git-SVN repositories are discovered and processed sequentially.
- Loaded projects linked through directory junctions can resolve their external Git-SVN repository root without recursively scanning arbitrary junction targets.

## Current MVP

Open **Tools > Git-SVN Shuttle** in Visual Studio. The tool window shows every Git repository under the solution that contains `svn-remote.*` configuration. Each repository history is displayed newest first:

- Blue rows with an upload icon are local commits that will be sent by `git svn dcommit`.
- The server marker separates them from the last commit already represented in SVN.

Uncommitted file changes are shown separately in the repository header. They are not part of the dcommit list, and that repository's action buttons remain disabled until the changes are committed or discarded.

If `git svn rebase` stops on conflicts, the repository shows the unresolved files separately. Resolve and stage them with Visual Studio's Git tools, then continue the rebase from Git-SVN Shuttle, or explicitly confirm that you want to abort it.

Before a rebase, the extension requires a clean working tree and an attached branch. Before dcommit, it additionally rejects merge commits and runs `git svn dcommit --dry-run`. The confirmation records the exact HEAD, pending commit hashes, SVN baseline, and SVN configuration. Confirming revalidates that snapshot and publishes the pinned HEAD; any mismatch aborts the operation. **Publish all** preflights every repository before publishing the first one, then stops at the first failure. SVN cannot make publishing across multiple repositories atomic, so earlier successful dcommits cannot be rolled back automatically.

Repository and Git metadata changes are monitored through debounced filesystem notifications. This keeps the UI current without periodic Git polling. The snapshot validation remains the final safety boundary even if a filesystem notification is delayed or lost.

The root repository is processed first, followed by nested repositories in path order. User-defined ordering is planned but is not part of this MVP.

## Use in Visual Studio

1. Install the VSIX from `src\GitSvnShuttle.Vsix\bin\Release\net472\GitSvnShuttle.Vsix.vsix`.
2. Open a solution located inside the main Git-SVN working copy.
3. Open **Tools > Git-SVN Shuttle**.
4. The extension checks `git --version` and `git svn --version` once. If setup is required, use the folder button to select `git.exe` or the search button to scan common locations.
5. Review the blue upload rows above the server marker. Only those commits will be dcommitted.
6. Use the download icon for one repository or **모두 받기** before publishing.
7. Use an upload icon or **SVN에 게시**. Confirm the SVN destination and exact commits in the confirmation window before starting. If HEAD, pending commits, the SVN baseline, or SVN configuration changes, publishing stops and requires a new confirmation.
8. Read detailed command output under **View > Output > Git-SVN Shuttle**.

When a loaded project is linked through a directory junction, the repository card identifies it as an external link and shows the physical Git working path used for Git-SVN operations.

While a Git-SVN command is running, use **취소** in the command bar to request cancellation.

For a reproducible local SVN and nested Git-SVN workspace, see [`test-env/README.md`](test-env/README.md).

## Requirements

- Visual Studio 2022 or 2026 on x64 Windows
- .NET Framework 4.7.2 or newer
- A working `git svn` runtime
- Existing Git-SVN checkouts (clone/init is intentionally out of scope)

The extension scans `PATH`, common Git for Windows locations, and common MSYS2 UCRT64/MINGW64 locations. The runtime panel provides four actions:

- Select a `git.exe` file.
- Automatically search again.
- Recheck the currently displayed path.
- Clear the user-selected path and return to automatic discovery.

A selected path is validated with both `git --version` and `git svn --version` before it is saved. The runtime panel can be reopened from the gear button in the command bar.

For scripted or managed setup, `GIT_SVN_SHUTTLE_GIT` remains available as an advanced override:

```powershell
$env:GIT_SVN_SHUTTLE_GIT = 'C:\msys64\ucrt64\bin\git.exe'
```

The configured runtime path must be absolute. UI changes apply immediately; an externally changed environment variable requires restarting Visual Studio. Git commands are non-interactive, have bounded output and time limits, and never accept credentials through this extension. Configure SVN credentials in the selected Git-SVN runtime before using the extension.

## Security boundaries

- Discovery skips directory junctions and other reparse-point children and stops at a finite directory limit.
- External repository resolution starts only from paths of projects loaded by Visual Studio, resolves one physical path, and does not recursively traverse arbitrary junction targets.
- Publish authorization uses an immutable snapshot and a pinned commit hash rather than only a repository path.
- SVN destinations are shown with URL credentials removed.
- Credential-shaped values are removed from Visual Studio Output logs.
- The Git executable is resolved to an absolute file path before any repository command runs.

## Build

```powershell
dotnet restore git-svn-shuttle.sln
dotnet test tests\GitSvnShuttle.Core.Tests\GitSvnShuttle.Core.Tests.csproj
dotnet build src\GitSvnShuttle.Vsix\GitSvnShuttle.Vsix.csproj -c Release
```

The VSIX is written below `src\GitSvnShuttle.Vsix\bin\Release`.

## Privacy and license

Git-SVN Shuttle does not send telemetry or repository data to the publisher. See [PRIVACY.md](PRIVACY.md) for the exact boundary.

Git-SVN Shuttle is available under the [MIT License](LICENSE.txt).

## Deliberately out of scope

- `git svn clone`, `init`, or migration
- Git commit/staging UI already provided by Visual Studio
- Installing or bundling a Git-SVN runtime
- Parallel dcommit
- Automatic rollback across SVN repositories
