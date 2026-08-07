# Exact Port Plan

## Source of truth

- Source file: `design/git-svn-shuttle-ui-proposal.html`
- Source areas: command bar, repository cards, descending commit timeline, SVN boundary, icon actions, publish confirmation overlay
- Source behavior: refresh, receive all/one, publish all/one confirmation, collapse/expand, disabled actions for repository problems

## Target location

- `src/GitSvnShuttle.Vsix/GitSvnShuttleControl.xaml`
- `src/GitSvnShuttle.Vsix/GitSvnShuttleViewModel.cs`
- `src/GitSvnShuttle.Core/GitSvnWorkspaceService.cs`
- `tests/GitSvnShuttle.Core.Tests/GitSvnWorkspaceServiceTests.cs`

## Mapping

| Source | Target | Expected relation | Notes |
|---|---|---|---|
| Top metrics and icon commands | WPF command bar | Same order, counts, icons, labels, and enabled states | Native VS tool-window title bar remains outside the control |
| Rounded repository surface | WPF repository item template | Same grouping, path, warning, and per-repository icon actions | Uses VS theme brushes |
| Blue outgoing commit region | Pending commit item template | Same visual emphasis and upload icon | Commits displayed newest first |
| Thin server boundary and SVN row | Boundary plus baseline row | Same icon-only boundary and SVN state | No explanatory boundary prose |
| Repository chevron | RepositoryViewModel expansion state | Same collapse and expand behavior | Expanded initially |
| Publish overlay | WPF overlay bound to confirmation state | Same commit list, warning, cancel, and confirm behavior | Covers both all and one repository |

## Approved deviations

| Deviation | Reason | Approved before edit? |
|---|---|---|
| Omit the HTML-simulated title bar | Visual Studio supplies the real tool-window title bar | yes, inherent target host and earlier VS-native requirement |
| Replace fixed dark hex colors with semantic VS theme brushes | Required for VS 2022/2026 light, dark, and high-contrast themes | yes, earlier VS-native/theme requirement |

## Comparison checks

- Text/code comparison: inspect all mapped source and target areas
- Behavior tests: commit ordering, confirmation open/cancel/confirm, collapse state
- Edge cases: no pending commits, repository problem, no repository
- Build/typecheck: core tests and Release VSIX build
