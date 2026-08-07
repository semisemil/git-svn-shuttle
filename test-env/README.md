# Local Git-SVN test environment

This fixture creates a real local SVN repository and two real Git-SVN working copies:

```text
%LOCALAPPDATA%/GitSvnShuttle/TestWorkspace/
├─ svn-repository/                 local SVN repository
└─ ShuttleDemo/                    main Git-SVN repository
   ├─ ShuttleDemo.sln
   └─ Externals/Common/            nested Git-SVN repository
```

The setup leaves each Git repository with one local commit waiting for `dcommit` and then creates a newer SVN revision. This gives the extension something real to show and lets you test the intended order: first rebase, then dcommit.

The SVN repository is served only on `127.0.0.1` by the bundled `svnserve.exe`, using an available ephemeral port. It is intentionally anonymous and writable because it contains disposable test data only.

## Create or reset

From the repository root:

```powershell
.\test-env\setup.ps1
```

To recreate it later:

```powershell
.\test-env\setup.ps1 -Reset
```

The setup downloads VisualSVN's standalone Apache Subversion command-line package into the ignored `.test-tools` directory. It uses the existing Git for Windows `git svn` runtime and does not install anything globally. The generated workspace uses an ASCII-only `%LOCALAPPDATA%` path because the Windows Subversion command-line package can misread Korean path segments.

## Exercise the Visual Studio extension

1. Install `src\GitSvnShuttle.Vsix\bin\Release\net472\GitSvnShuttle.Vsix.vsix`.
2. Open `%LOCALAPPDATA%\GitSvnShuttle\TestWorkspace\ShuttleDemo\ShuttleDemo.sln` in Visual Studio.
3. Open **Tools > Git-SVN Shuttle**.
4. Confirm that `ShuttleDemo` and `Common` each show one blue upload row above the server marker and the SVN baseline below it.
5. Select **모두 받기**. Both repositories should receive their newer SVN revisions without losing the local Git commits.
6. Confirm the pending commits are still visible.
7. Select **SVN에 게시**, verify the two listed commits, and select **게시 시작**. The extension should preflight both repositories and dcommit them sequentially.
8. Select **새로 고침**. Both repositories should show no commits waiting to publish.

`dcommit` across two SVN locations is not atomic. If the second repository fails after the first succeeds, the first SVN commit remains published. The extension stops immediately and reports the partial result.

## Command-line end-to-end smoke test

To exercise the same real Git-SVN round trip without Visual Studio:

```powershell
.\test-env\smoke.ps1
```

The smoke test mutates the fixture by publishing both local commits. Run setup again with `-Reset` to restore the initial UI test state.

To stop the local test server without removing its files:

```powershell
.\test-env\stop.ps1
```
