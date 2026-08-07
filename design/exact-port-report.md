# Exact Port Report

## Overall status

Partially verified. The approved HTML structure, visual hierarchy, ordering, and non-destructive interactions are implemented in the WPF control and verified through compiled-control renders and automated checks. Interactive hosting inside Visual Studio and a real `git svn dcommit` initiated from the confirmation button were not run in this pass.

## Ported surfaces

| Approved HTML surface | WPF target | Result |
|---|---|---|
| Top repository and pending counts | Command bar | Implemented |
| Refresh, receive-all, and publish-all actions | Command bar buttons | Implemented |
| Rounded repository cards | Repository item template | Implemented |
| Dirty-worktree warning | Repository header | Implemented |
| Icon-only per-repository actions | Repository header | Implemented |
| Newest-first blue outgoing commits | Pending commit list | Implemented |
| Thin SVN server boundary | Repository timeline | Implemented |
| SVN baseline commit below the boundary | Baseline row | Implemented |
| Collapse and expand chevron | Repository expansion state | Implemented |
| Publish confirmation overlay | Bound confirmation state | Implemented |

## Approved deviations

| Deviation | Reason |
|---|---|
| The HTML-simulated title bar is omitted | Visual Studio supplies the native tool-window title bar. |
| Fixed dark colors are replaced with Visual Studio theme brushes | The extension must fit VS 2022/2026 light, dark, and high-contrast themes. |

## Verification evidence

| Check | Result | Evidence |
|---|---|---|
| Release core tests | Passed: 4/4 | `dotnet test tests/GitSvnShuttle.Core.Tests/GitSvnShuttle.Core.Tests.csproj -c Release` |
| Release VSIX build | Passed: 0 warnings, 0 errors | `dotnet build src/GitSvnShuttle.Vsix/GitSvnShuttle.Vsix.csproj -c Release --no-restore` |
| Packaged extension identity | Passed | VSIX `extension.vsixmanifest`: `GitSvnShuttle.VisualStudio`, version `0.3.0` |
| Compiled WPF main view | Visually inspected | `output/wpf/git-svn-shuttle-wpf-port.png` |
| Compiled WPF confirmation overlay | Visually inspected | `output/wpf/git-svn-shuttle-wpf-modal.png` |
| Collapse, confirmation open, confirmation cancel | Passed | `artifacts/exercise-view-model.ps1` returned PASS for all three states |
| Newest-first pending commit order | Passed | Core test asserts newest commit before the older commit and verifies no `--reverse` argument |

## Not verified

- Opening version 0.3.0 in an actual Visual Studio 2022/2026 process and clicking each control end to end.
- Confirming a publish against a real SVN repository. This would mutate repository state; the existing service path and preflight behavior remain in place, but the destructive endpoint was not exercised.
