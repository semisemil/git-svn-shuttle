# Git-SVN Shuttle

Git-SVN Shuttle is a Visual Studio extension for teams that use Git locally while an SVN server remains the source of truth.

It fills the gap left after Visual Studio's built-in Git UI has staged and committed changes:

- **Get from SVN** runs `git svn rebase`.
- **Publish to SVN** previews local commits and runs `git svn dcommit`.
- The solution root and nested Git-SVN repositories are discovered and processed sequentially.

## Current MVP

Open **Tools > Git-SVN Shuttle** in Visual Studio. The tool window shows every Git repository under the solution that contains `svn-remote.*` configuration. Each repository history is displayed newest first:

- Blue rows with an upload icon are local commits that will be sent by `git svn dcommit`.
- The server marker separates them from the last commit already represented in SVN.

Uncommitted file changes are shown separately in the repository header. They are not part of the dcommit list, and that repository's action buttons remain disabled until the changes are committed or discarded.

Before a rebase, the extension requires a clean working tree and an attached branch. Before dcommit, it additionally rejects merge commits and runs `git svn dcommit --dry-run`. **Publish all** preflights every repository before publishing the first one, then stops at the first failure. SVN cannot make publishing across multiple repositories atomic, so earlier successful dcommits cannot be rolled back automatically.

The root repository is processed first, followed by nested repositories in path order. User-defined ordering is planned but is not part of this MVP.

## Use in Visual Studio

1. Install the VSIX from `src\GitSvnShuttle.Vsix\bin\Release\net472\GitSvnShuttle.Vsix.vsix`.
2. If `git svn` is not on the normal Git path, set `GIT_SVN_SHUTTLE_GIT` before starting Visual Studio.
3. Open a solution located inside the main Git-SVN working copy.
4. Open **Tools > Git-SVN Shuttle**.
5. Review the blue upload rows above the server marker. Only those commits will be dcommitted.
6. Use the download icon for one repository or **모두 받기** before publishing.
7. Use an upload icon or **SVN에 게시**. Confirm the exact repositories and commits in the confirmation window before starting. The extension then checks for a clean working tree, an attached branch, merge commits, and a successful dcommit dry-run.
8. Read detailed command output under **View > Output > Git-SVN Shuttle**.

For a reproducible local SVN and nested Git-SVN workspace, see [`test-env/README.md`](test-env/README.md).

## Requirements

- Visual Studio 2022 or 2026 on x64 Windows
- .NET Framework 4.7.2 or newer
- A working `git svn` runtime
- Existing Git-SVN checkouts (clone/init is intentionally out of scope)

The extension invokes `git` by default. To use MSYS2 or another Git-SVN runtime, set `GIT_SVN_SHUTTLE_GIT` before starting Visual Studio:

```powershell
$env:GIT_SVN_SHUTTLE_GIT = 'C:\msys64\ucrt64\bin\git.exe'
```

## Build

```powershell
dotnet restore git-svn-shuttle.sln
dotnet test tests\GitSvnShuttle.Core.Tests\GitSvnShuttle.Core.Tests.csproj
dotnet build src\GitSvnShuttle.Vsix\GitSvnShuttle.Vsix.csproj -c Release
```

The VSIX is written below `src\GitSvnShuttle.Vsix\bin\Release`.

## Deliberately out of scope

- `git svn clone`, `init`, or migration
- Git commit/staging UI already provided by Visual Studio
- Installing or bundling a Git-SVN runtime
- Parallel dcommit
- Automatic rollback across SVN repositories
