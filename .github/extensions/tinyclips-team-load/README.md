# Team load canvas

A Copilot app canvas (`tinyclips-team-load`) that shows what each teammate and the Copilot coding agent is working on, so you can hand out new work to whoever has room.

Use the sun/moon button beside Refresh to switch between light and dark themes. The canvas follows the system theme until you choose one; your choice applies to every view until the page reloads.

## What it shows

- **Who's on what**: one row per tracked person. The row shows a load meter (open assigned issues + authored PRs + review requests, with a limit marker), a status label (Free / Has room / Busy / At limit / N over limit), and their most recent item. Expand a row to see every item. The arrow icon beside a name opens that person's profile.
- **Move an issue**: expand a person on the home screen, then drag an issue onto another teammate's row (including Copilot). This removes only the source person's assignment, keeps any other assignees, and offers Undo. PRs and reviews cannot be dragged. For a keyboard move, focus an issue and press Space, Tab to the target person's name button, and press Enter; Escape cancels.
- **Needs an owner**: open, unassigned issues in the tracked repos, each with one-click assign to a person or to Copilot.
- **Momentum**: daily commits, PRs opened and merged, and issues opened and completed across the tracked repos, drawn in the load-meter palette. It shows 7 days by default (or 30 / 90), for the whole team, one person or everyone, across all repos or just one. Hover a day for its numbers; the play button replays the animation. Days are UTC.
- **Person profile** (`#person/<login>`): that person's Momentum over time, plus everything currently assigned to them and an Open on GitHub link. Copilot has a profile too. Contributions cover the tracked repos only, not their GitHub-wide activity.
- Clicking any issue or PR opens it inside the canvas. You get the title, state, labels, assignees, the rendered body and the latest comments, plus Back and Open on GitHub. On an issue you can add or remove assignees (a tracked person or Copilot) even when it already has an owner.

## Actions (real GitHub writes)

| Action | How |
| --- | --- |
| Assign to a person | `gh issue edit N --repo o/r --add-assignee <login>`, re-read to verify |
| Assign to Copilot | `gh issue edit N --repo o/r --add-assignee @copilot` (starts the coding agent) |
| Add to an assigned issue | the same writes with `additional: true`; does nothing if the person is already on the issue |
| Remove an assignee | `gh issue edit N --repo o/r --remove-assignee <login>`, re-read to verify |
| Move to another person | `reassign` with `repository`, `number`, `from` and target `login`; adds and verifies the new owner before removing the source, without replacing the full assignee list |
| Start a session myself | assigns you, then `session.send({ prompt })` to the hosting conversation; rolls back the assignment if sending fails. It only rolls back an assignment it made itself. |
| Undo | reverses the last change: removes someone you just added, or puts back someone you just removed |

Before writing, it checks for conflicts: the issue is closed or a PR. Without `additional`, an already-assigned issue is also a conflict, which keeps Needs an owner a safe triage action; adding to an assigned issue is an explicit choice made from the issue's Assignees list. After each write it refreshes.

Moving also verifies that the source still owns the issue. If the target cannot be assigned, the source is not removed. If removing the source fails, the partial change is reported explicitly and the canvas refreshes; it never reports a successful move without verifying both assignments. Undo preserves a target who was already a co-assignee.

## Data

Everything is fetched live with the GitHub CLI (`gh`, must be signed in). Repos are fetched through GraphQL with pagination, at most 3 concurrent requests, and one retry for transient read errors. Profiles are cached and coalesced. A failing repo shows a warning without breaking the rest of the view. Auto-refresh runs every `refreshSeconds` (minimum 60) while the canvas is visible.

Momentum is history, so it is cached separately: each repo's activity is fetched once and reused for up to an hour, and a 30 or 90 day fetch also serves the shorter ranges. It is refetched only when the canvas needs it and the cache has aged out, never on its own timer. Only the Momentum card's refresh button forces a refetch; the app-wide refresh, auto-refresh, switching ranges and opening a profile all reuse the cache. If a refetch fails, the last good data for the same selection is shown and marked stale. Changing the person, repository, range, or tracked people/repos clears the previous selection's chart and supersedes its in-flight requests, so a failed load cannot show another selection's totals. The cache lives in memory, so reloading the extension starts it fresh.

Agents can read the same data with the `get_momentum` action (`days` 7/30/90, defaulting to 7; optional `person`, `everyone`, `repository` and `force`).

## Tracking

Use the **people** and **repos** pickers in the header to choose what's tracked. Defaults come from `config.json`. Your selection is saved to:

```
$COPILOT_HOME/extension-data/tinyclips-team-load/tracking.json   (default ~/.copilot/...)
```

Delete that file to reset to the defaults.

## Security

The canvas is served from a loopback server with a per-launch nonce, a host/origin check and a strict CSP (no inline script or style). Issue and comment HTML is sanitized client-side against an allowlist before rendering.

## Development

```bash
node --test .github/extensions/tinyclips-team-load/tests/team-load.test.mjs .github/extensions/tinyclips-team-load/tests/momentum.test.mjs
```

Tests mock `gh`; they never touch GitHub. Momentum request-state tests use a minimal DOM fixture and deferred responses to cover selection changes, tracking races, and same-selection stale-data fallback without browser dependencies.
