# SVN paths, branches, and layout migration

Git-SVN Shuttle manages tracking paths for each local Git repository. It can keep an old project-first layout alongside a new repository-wide layout, then create an SVN feature branch and connect a local work branch to its imported history.

In Visual Studio, open **Tools > Git-SVN Shuttle** and select **SVN 연결 관리** on the repository row.
The panel reads the configuration and opens **새 브랜치**.
Use **연결 설정** to edit tracking paths, **설정 다시 조회** to reload configuration, and **연결 다시 확인** to resolve the current local branch's actual SVN destination.
The command bar action **SVN 저장소 새 연결** opens connection settings for an existing local folder, including a folder without Git metadata.

## Tracking paths

In **연결 설정**, enter the **SVN 기준 주소** and select a path layout.
Choose **주소 아래의 trunk, branches, tags** for a conventional project root, or **입력한 주소 하나만** for an exact trunk or branch URL.
Use **경로 직접 지정** or expand **고급 설정: 가져올 경로 편집** for custom layouts.
Enter paths relative to the base URL, one per line in the separate trunk, branches, and tags fields.
**SVN 이력 가져오기** saves the local Git configuration and fetches history.

For this repository URL:

```text
https://svn.example.com/repos/team
```

the following settings describe three layouts for project `ProjectA`:

| Setting | Existing layout | New layout with a project folder | Mixed migration period |
| --- | --- | --- | --- |
| trunk | `ProjectA/trunk` | `trunk/ProjectA` | Both paths, one per line |
| branches | `ProjectA/branches/*` | `branches/ProjectA/*` | Both patterns, one per line |
| tags | `ProjectA/tags/*` | `tags/ProjectA/*` | Both patterns, one per line |

The branch and tag fields also accept a container path without `/*`; the application adds it. Trunk entries are exact paths. Use `.` to represent the base URL itself, as with a checkout whose SVN URL already ends in `/ProjectA/trunk`.

Project folders are optional. A shared branch directory with project-prefixed names can use `branches/ProjectA-*`. A shared tag directory can similarly use `tags/ProjectA-*`. Intermediate folders must be explicit: `branches/products/ProjectA/*` tracks the branches below that folder. A single `*` matches within one path component; it does not search arbitrary directory depths. List each required container separately.

**SVN 이력 가져오기** saves repository-local settings and fetches all configured SVN remotes with initial checkout disabled.
New mappings use separate remote refs and revision cursors, so a newly added path can import history older than the latest revision already fetched for an existing path.
Distinct old and new branches with the same leaf name receive separate ref namespaces.

An existing Git-SVN repository may use a project URL as its remote base rather than the repository root. The form can display a common base URL across those mappings. Tracking identity uses the completed SVN URL, so changing the displayed base from `/repos/team/ProjectA` to `/repos/team` and entering `ProjectA/trunk` reuses the existing trunk binding.

## What changes when a path is removed

Removing an entry stops discovering new branches or tags under that pattern. Existing imported branch/tag refs become exact fetch bindings and remain available to Git-SVN. Exact trunk bindings also remain available to existing ancestry. The form lists retained paths separately from active tracking paths.

Those retained bindings preserve revision maps and the association between existing local commits and their SVN destination. Reapplying a previously retained path reactivates its original mapping. Configuration changes do not reset a branch, rebase local commits, clear the index, stash changes, or check out files. Local commits, staged content, unstaged content, and untracked files remain in place.

The application's `shuttle.retiredMapping` entries record why old bindings are retained. A branch created outside the configured branch patterns has a protected exact binding marked `svn-remote.<name>.shuttleBranch`; the form also shows it among retained paths, rather than treating it as an active trunk entry. Keep these entries and their corresponding `svn-remote` settings together. Deleting old refs, revision maps, or those settings manually can break subsequent Git-SVN operations on local branches that still depend on them.

Changing the list of tracking paths does **not** change the actual dcommit destination of an existing local branch. Git-SVN derives that destination from imported SVN ancestry and `commiturl`/`pushurl` settings. A local branch named `ProjectA-new` can still publish to an old SVN path if its history comes from that path. Always inspect the actual destination before publishing.

## A new connection and its first checkout

For a new connection, create or select the local folder, enter tracking settings, and apply them. The application initializes Git when necessary and imports SVN refs. It leaves existing files, an existing Git branch, and an unborn HEAD untouched.

Select the Git repository root when connecting an existing repository. A folder inside another Git repository is rejected to avoid changing its parent's tracking settings. To use that child folder as an independent repository, explicitly initialize a separate Git repository there first, then connect it.

To start work after the first import, either create an SVN branch in the form and switch to its connected local branch, or select an already fetched ref in a terminal:

```bash
git for-each-ref --format='%(refname)' refs/remotes/
```

Select the desired trunk or branch ref, then create a local branch explicitly:

```bash
git switch --no-track -c work refs/remotes/<selected-ref>
```

The selected ref must be the SVN path intended for this work. If the folder already contains local work, prepare it through your usual Git commit or stash workflow before switching. Initial checkout and moving unrelated Git history onto SVN history are explicit user operations.

## Create an SVN server branch

Open **새 브랜치** and enter a new branch name.
The form prefers the current branch's confirmed SVN source, or uses the configured trunk when there is exactly one candidate.
It fills the destination folder when there is exactly one configured branch container.
If several sources or destinations are possible, choose them from the lists.
Both fields also accept a full SVN URL or a path relative to the loaded base URL.

The preview shows the completed destination URL, revision, local Git name, and whether the new branch will be checked out.
The following values are filled automatically and can be changed under **고급 설정**:

| Setting | Default | Example override |
| --- | --- | --- |
| Revision | Latest server revision (`HEAD`) | `1250` |
| Creation commit message | `브랜치 생성: fix-login` | `Create login fix from r1250` |
| Local Git branch | SVN branch name, with characters unsuitable for Git replaced | `work/fix-login` |
| Switch after creation | Enabled | Disable to preserve the current worktree |

Clearing the message or local name restores its automatic value.

For a source of `trunk/ProjectA`, destination folder `branches/ProjectA`, and name `fix-login`, **브랜치 만들기** creates `/branches/ProjectA/fix-login` by server-side SVN copy.
Use a nonnegative numeric revision or `HEAD`.
Numeric revisions set both the source peg revision and operative revision, which allows copying the requested historical path even if it was subsequently moved.
The destination parent can contain intermediate folders; missing destination parents are created by SVN's `--parents` behavior.
The branch name itself must be one path component.

For the old layout, the source can be `/ProjectA/trunk` and the destination parent `/ProjectA/branches`. For a shared directory without a project folder, use `/branches` and branch name `ProjectA-fix-login`. During migration, a source in the old layout and destination in the new layout are supported within the same SVN repository.

Full URLs are the clearest choice when several remote bases coexist. The Visual Studio form resolves relative paths against its displayed base URL. Callers using the core service directly can use relative paths when the configured remote URLs have a common URL ancestor on the same server; paths are resolved against that common ancestor.

After SVN copy succeeds, the application reuses a compatible existing tracking mapping or creates a separate exact mapping, fetches the branch, and creates a local branch from that imported ref.
With **생성 후 이 브랜치로 전환** enabled under advanced settings, it also checks out the local branch and validates its actual dcommit destination.
Subsequent commits on that branch publish to the new SVN branch through the existing publish workflow.

Switching requires an attached symbolic Git branch and no uncommitted tracked changes or in-progress Git operation. A clean unborn branch immediately after the first import is supported; it does not need an existing commit or SVN baseline. Clear the checkbox to create the local branch while preserving current local work, including an unborn HEAD. An unrelated local branch with the requested name is rejected without moving its commits. Choose a different local name in that case.

Branch creation requires an SVN command-line client because `git svn branch` does not provide an arbitrary source URL and exact source revision. Configure `svn.exe` on `PATH` or set an absolute executable path before starting Visual Studio:

```powershell
$env:GIT_SVN_SHUTTLE_SVN = 'C:\Tools\Subversion\bin\svn.exe'
```

The SVN client and Git-SVN runtime may use different credential stores. Prepare non-interactive authentication for both. The application accepts no passwords and does not install either runtime.

## Move existing local work to a new SVN path

Use an explicit Git replay when existing local commits must publish to a new SVN branch. Applying tracking settings alone preserves their original destination.

1. Preserve the original work branch under a backup name. Record the original SVN baseline commit and the local commits to move. Commit or stash any working changes through the usual Git workflow before changing branches; keep the backup and any stash until verification is complete.
2. Create the destination SVN branch from the required source and revision with the form above. Choose a new local name, such as `work/fix-login-new`, and switch to it.
3. Replay the intended local commits, oldest first. For a linear local range based on a known SVN baseline:

   ```bash
   git cherry-pick <old-svn-baseline>..backup/old-work
   ```

4. Resolve any conflicts through Git. Restore saved working changes deliberately; `git stash apply --index` can restore staged changes when applicable, but may conflict against the new history.
5. In **연결 설정**, select **연결 다시 확인** and check the destination URL. Review the pending commits in Git-SVN Shuttle, then use the existing publish confirmation and dcommit operation.

For example, create the backup while still on the old branch with `git branch backup/old-work`. The baseline is the imported SVN commit immediately preceding the intended local commits; inspect first-parent history to identify it. A cherry-pick creates new Git commit IDs on the destination's SVN ancestry while keeping the original branch intact. Do not include imported SVN commits or merge commits in the replay range.

## Actual dcommit destination

The management panel queries Git-SVN's current history and revision-map association, then resolves the matching fetch/branch/tag mapping and any `svn.commiturl`, remote `commiturl`, or remote `pushurl` override.
Connection settings show the effective destination and put the imported SVN URL, revision, and tracking ref under **연결 상세**.
Repository rows and publish confirmation also use the resolved destination.
Refresh after changing branches or configuration.
The panel's connection query has a 20-second limit; a failed or cancelled query leaves the form inputs available for correction.

Ambiguous mappings or unsupported metadata rewrite settings produce an unresolved result rather than inferring a destination from the local branch name. Branch creation rejects an existing transport override that would send the new branch's commits elsewhere. Publication retains its existing snapshot validation and Git-SVN dry-run checks.

## Failures and support limits

- The panel shows the current operation above the form and keeps its action buttons visible at the bottom.
  **닫기** leaves an active operation running; use **작업 취소** to stop it.
  Connection errors show a next action and a read-only retry button, with command output under **결과 상세**.
  A server branch that was created, or whose copy outcome is unknown, cannot be created again at the same destination within the current form.
- If tracking fetch fails, saved mappings and any imported history remain. Apply the same settings again after correcting the fetch failure. Git config changes are committed together under `config.lock`; a conflicting writer or invalid settings leave the existing configuration intact.
- If SVN copy succeeds but fetch, local branch creation, or checkout fails, the server branch remains. The result includes its URL. Continue by fetching the reported remote and creating or switching the local branch from its imported ref; do not run SVN copy again for the same destination. Cancellation while copy is running can leave its server outcome unknown, so inspect SVN before retrying.
- A preexisting destination is not overwritten. The creation form creates server branches; attaching an arbitrary already-existing server branch can be done by adding its tracking path, fetching, and explicitly creating a local Git branch from the imported ref.
- Paths use one `*` at most. Recursive patterns such as `branches/*/*`, brace alternatives, and arbitrary-depth discovery are unsupported. Configure each required container separately.
- Overlapping mappings for the same SVN URL are rejected, including overlaps with retained history. Keep one mapping for each SVN destination. Separate prefixes, such as `ProjectA-*` and `ProjectB-*`, can coexist.
- `noMetadata`, `rewriteRoot`, `rewriteUUID`, SVM, and SVNSync metadata modes are outside this migration workflow. Metadata-free or rewritten histories require a separate migration strategy.
- Server URL relocation, cross-repository SVN copies, automatic replay of local commits, trunk merges, and deployment-tag creation are not provided. Tags can be fetched and inspected as part of the configured layout.
- SVN server changes and local Git operations cannot form one atomic transaction. The application reports partial completion and preserves existing work rather than deleting a successfully created server branch.

## Verification

The tracking configuration tests use real local Git configuration and refs to verify lock ownership, concurrent settings changes, fetch failure recovery, retained mapping reactivation, conventional `svn/*` ref prefixes, literal percent/space path round-trips, and preservation of local commits, staged content, working files, and untracked content.

The [disposable SVN integration probe](../tests/GitSvnShuttle.TrackingProbe/README.md) passed on Linux with .NET 10.0.402, git-svn 2.52, and SVN 1.14.5. It verified old, new, and mixed layouts; exact older numeric-revision copies; Korean, space, and percent branch names; flat project-prefix patterns; nested containers; tags; clean unborn branch connection; and branch preparation without switching while local work is dirty. Actual dcommit was checked on the destination SVN path, including the retained old destination after migration and a new branch outside the configured patterns. Windows CI runs the same probe alongside the existing rebase/dcommit checks.

The earlier integration probe also published a new commit through a retained legacy wildcard branch binding after removing its discovery pattern.
Both that branch and the retained legacy trunk received their intended commits; the corresponding new-layout paths did not receive them.

For the management UI update, all 198 tests passed on Windows.
The Release VSIX built with zero warnings and zero errors.
The WPF preview checks cover eight management states in light and dark themes, including actual input bindings, advanced settings, source selection, refresh preservation, and busy-state controls.
They use fictional paths and reserved `.test` addresses with no Git or SVN process execution in the preview.
The local Windows integration run passed against a disposable SVN server bound to `127.0.0.1`.
It verified the production form's name-only branch creation, branch-only dcommit, and recovery from a real Git ref-lock failure after SVN copy without creating the server branch twice.
Existing rebase, nested repository publishing, layout migration, and dirty-state preservation checks also passed.
No company SVN server was used.
Interactive execution inside Visual Studio remains unverified.
