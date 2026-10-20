# TinyClips Project Pulse

A Copilot canvas for the TinyClips core team's app, website, and marketing work: recent commits, open/merged/closed PRs, issue ownership, Unassigned work, launch progress, and an on-demand momentum chart.

## Opening and using the canvas

Reload project extensions after changing extension files, then open canvas **`tinyclips-project-pulse`**. The canvas takes no open input. Reusing an instance ID reuses its loopback server and reloads saved personal preferences.

- **Overview** combines recent activity, unassigned issues, open PRs, and the workbench.
- **Activity** expands the timestamped feed; **Pull requests** distinguishes open, merged, and closed-without-merge work.
- **Team** groups open work by issue assignee or PR author/assignee/requested reviewer. Click an avatar to focus a person; Clear filters restores everyone.
- **Momentum** has its own tab for the on-demand chart, daily counts, and 7/30/90-day history controls. The chart is hidden from other tabs, retains its loaded state while switching views, and stops replaying when you leave the tab.
- The compact roster includes James Montemagno, Katie Savage, Julia Kasper, Pierce Boggan, and Burke Holland, plus Unassigned and Other contributors. Each teammate card shows open issues by assignee and open PRs they created or have a pending individual review request for, counted once per PR even when both apply. A PR assignee alone does not contribute to that card's PR count; person-filtered work lists still include assigned work as well as authorship and review requests. Counts follow repository/platform/launch filters, independently of the selected person and recent-history window. Other contributors includes PR creators/requested reviewers outside the core team; Unassigned continues to mean open issues/PRs without assignees.
- By default, combine `jamesmontemagno/tiny-clips`, `jamesmontemagno/tiny-clips-website`, and `jamesmontemagno/tiny-clips-marketing`. The repository filter scopes every work view and launch summary.
- Toggle **2.0 Launch only**, platform, or the 7/30-day recent-history window. Open work and launch completion accounting are not restricted by the activity window.
- Choose **Every 5 / 10 / 15 / 20 / 30 min** or **Never** beside **Refresh now**. The default is five minutes; the selection is saved privately with your other preferences. Changing it restarts the countdown without fetching immediately. Automatic refresh pauses while hidden and resumes the remaining countdown (or refreshes if overdue) when visible, respecting GitHub retry limits. **Never** disables both timed and return-to-page refreshes, not the initial load or **Refresh now**. Multiple panels share cached GitHub data and in-flight requests; the personal interval does not change the shared five-minute data cache.
- After the initial load, a small in-canvas notification and **New / Updated** badges identify changes since each repository section's previous successful refresh. Dismiss the notification without losing the badges; the next fresh section result replaces those badges. Cached responses never repeat notifications, failed sections do not advance their baseline, and first-time recovery is not treated as new work. These comparison baselines live only in the open page, not on disk. There are no OS notifications.
- A short, silent confetti burst celebrates newly opened/reopened/merged/closed PRs and newly opened/reopened/completed issues observed during a successful section refresh. The notification names the milestones. First load, cached/older responses, historical work entering the snapshot, commit-only changes, and metadata edits do not trigger confetti. One burst covers the entire refresh and clears within two seconds; Dismiss stops it immediately. Reduced motion, a hidden page, or an open detail editor suppresses the animation without removing the notification. No celebration is queued for later, and failed sections never invent milestones.

### Momentum over time

Open the **Momentum** tab, then select **Explore momentum** to gather history on demand. Opening the tab alone does not fetch expanded history. Choose **7, 30, or 90 days** and **Replay timeline** to reveal activity chronologically. Select a chart day, use arrow keys on chart columns, or scrub the date slider for exact counts. A daily-counts table provides the same data without the graphic. Reduced-motion users see the complete chart without the timeline sweep. The shared repository/person/platform/launch filters still apply; the other tabs' recent-activity window is hidden here in favor of the chart's own History range.

The chart follows the repository, person, platform, and launch filters at the moment history is gathered; its scope and gather time remain visible. Changing those filters prompts an explicit chart refresh rather than fetching expensive history automatically. History is cached for five minutes and reused for different person/platform filters. Routine dashboard refreshes never fetch the expanded chart history.

Bars show daily commits, PRs opened, PRs merged, issues opened, and issues currently closed as completed. All five series appear in the legend/totals, daily readout, and accessible table. Dates use UTC, including today as a partial day. Issue openings count each issue once on its creation date regardless of its current state; later reopenings are not new creations. Commit counts include merge commits and are deduplicated within each repository, not across different repositories; the same PR or issue can contribute once when opened and once when merged/completed. Cancelled/not-planned closures and reopened issues are not completions. This is activity, not release readiness or a team productivity ranking.

Animation is a bounded, user-triggered replay; it stops while the page is hidden and respects reduced motion. Failed repositories are explicitly excluded and flagged as partial history, never silently represented as zero activity.

No GitHub Projects, live presence, local sessions, unpushed work, PR merges, or publishing operations are included.

## Shared configuration

Edit [config.json](config.json) through the normal repository workflow. It contains the versioned format, primary GitHub repository, `repositories` list, ordered core-team logins, exact existing launch/platform label names, and the 300-second refresh default. Unsupported versions or invalid configuration are reported rather than silently replaced.

The default labels are `2.0 Launch`, `macOS`, and `windows`. A missing launch label in the primary app repository produces a visible error. Additional repositories need not have this label: their work remains visible in the combined view, but only explicitly labeled/linked work contributes to launch progress. The extension never creates labels or milestones.

## Permissions and edits

Install Node.js with ES module support and GitHub CLI (`gh`), then authenticate `gh` for github.com. The runtime supplies `@github/copilot-sdk/extension`; do not add a package manifest or install the SDK in this folder.

Reading requires access to the configured repositories. Assignment and issue-state edits require the appropriate permission in the item's own repository; GitHub remains the final authority. **No Projects scopes are required.** A repository failure does not hide healthy work from the others.

Select an issue/PR title to open current details. Review the comma-separated assignee list and check the confirmation before **Apply assignees**; an omitted existing assignee will be removed. Issues also support explicitly closing as completed/not planned or reopening. PR lifecycle is read-only.

Edits are disabled when the item's repository is stale or the account cannot update the item. Only the latest detail request for an open editor may populate it; late responses/errors from a closed or replaced editor are ignored. All editor inputs, selects, and buttons stay disabled during a write and its follow-up refresh. Every item carries its repository identity through details, confirmations, and writes, so identical issue numbers in different repositories cannot collide. Before writing, the extension checks current item state against the detail record; after writing, it verifies the result. This is best-effort conflict detection, not an atomic transaction across clients. A failed or ambiguous write requires reloading and reconciling, never a blind automatic retry.

GitHub credentials stay in `gh`. The loopback server uses an ephemeral port, same-origin routes, Host/Origin validation, and a header-only mutation nonce. GitHub/avatar links are host-restricted. Diagnostics use the Copilot session logger.

## What the data means

- Repository totals are GitHub totals; scoped issue totals come from paginated results, not visible cards. Repository-qualified issue numbers and commit identities remain distinct across all three sources.
- All open issues/PRs and all launch-labeled issues are loaded. Recent issue/PR history covers 30 days; lists provide Show more controls.
- Pending PR review requests are paginated, shown in work lists/details, and included in person filters and refresh notifications. Completed reviews without a pending request do not count as assigned review work. Team review requests are displayed but not attributed to individual teammates without verified membership; no membership or co-authorship is guessed.
- Commits cover the default branch and open PR heads for 30 days, deduplicated by SHA. Enriched history at unchanged commit SHAs is cached for at most five minutes so mutable PR associations and launch/linked-issue labels are re-read even when no new commits arrive. Commit time is GitHub's committed date, not proof of when a push happened.
- Activity uses created/closed/merged/updated timestamps. An updated record does not identify the actor who edited it; only opening and commit records attribute a supplied author.
- Issues use platform labels. PRs use labels and changed-file paths when the first 100 paths are complete. Commits inherit known associated-PR platforms; otherwise they remain Unclassified.
- Launch PRs have the launch label or a GitHub closing relationship to a labeled issue; commits use their associated PRs. Association metadata is bounded to five PRs per commit, 20 closing issues per PR, and 20 labels per linked item. Unknown associations are not guessed from titles or body text.
- A multi-assignee issue can appear under multiple people but is counted once in overall totals. Unassigned means no assignees, not no author.
- Completed, not-planned, and other closed issues are separate. Issue completion is **not release certification**. Missing CI/check data is shown as UNKNOWN.
- API failures preserve available last-good sections and show their repository, timestamps, and errors. Initial failures never masquerade as a successfully empty repository. GitHub subprocess concurrency is bounded across all three repositories. Rate-limit reset times gate both automatic and manual refresh; transient HTTP 502/503/504 reads get one logged retry. Writes are never automatically retried.

## Personal state

Personal view/filter preferences are saved in:

```text
<Copilot home>\extension-data\tinyclips-project-pulse\<repository-identity-sha256>.json
```

Copilot home is non-empty `COPILOT_HOME` exactly as supplied, otherwise `.copilot` under the user's home directory. Identity is the **primary** `github.com/owner/repository` in lowercase, so adding related repositories preserves existing personal settings and clones/worktrees/panels share preferences. Complete preference saves across sessions are last-save-wins. Version 1 and 2 preferences are validated and migrated to version 3 on the next save, preserving existing filters and defaulting automatic refresh to five minutes. Version 1 preferences also default the repository selection to all repositories.

Writes use unique same-directory temporary files and atomic rename. Every read validates the format, version, identity, and view shape. Corrupt/unknown data is reported and never overwritten; repair or move the named file to reset it. Repository snapshots and GitHub business state are never stored on disk.

## Agent actions

| Action | Purpose |
| --- | --- |
| `refresh_status` | Refresh snapshot, timestamps, totals, coverage, and section errors |
| `get_momentum` | Gather daily activity with `days: 7 \| 30 \| 90` and optional repository/person/platform/launch filters |
| `get_item_detail` | Read `{ repository, kind: "issue" \| "pr", number }`, including current `expectedState` |
| `get_view_state` | Read personal preferences |
| `set_view_state` | Save a partial preference update (`view`, `person`, `platform`, `launchOnly`, `days`, `repository`, `refreshMinutes: 5 \| 10 \| 15 \| 20 \| 30 \| 0`); `0` means Never |
| `update_assignees` | Apply `repository`, `kind`, `number`, exact desired `assignees`, current `expectedState`, and `confirmed: true` |
| `set_issue_state` | Apply `repository`, `number`, `state: "OPEN" \| "CLOSED"`, current `expectedState`, and `confirmed: true`; closing requires `reason: "COMPLETED" \| "NOT_PLANNED"` |

Repository inputs must match the configured allowlist, case-insensitively. Momentum normalizes them to the configured identity before filtering, coalescing requests, and caching history. Omitting the repository on an item action retains the primary-repository default for compatibility; use the explicit identity from the snapshot. Agent mutations require the user's intent/confirmation just like browser edits. Read a fresh detail record first. Actions return structured results; expected failures are `CanvasError`s.

## Validation

From the repository root:

```powershell
node --check .github\extensions\tinyclips-project-pulse\extension.mjs
node --test .github\extensions\tinyclips-project-pulse\tests\pulse.test.mjs
```

Tests use injected GitHub fixtures and temporary private-storage roots; they never change real GitHub issues or PRs. After reload, inspect canvas capabilities, open it, invoke a read action, save/read a personal preference, and reopen the same instance to confirm preserved state.

This extension does not change either native app and needs no macOS/Windows app build.
