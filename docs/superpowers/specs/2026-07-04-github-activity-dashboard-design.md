# GitHub Activity Dashboard — Design

**Date:** 2026-07-04
**Status:** Approved (brainstorming) — ready for implementation plan
**Branch:** not yet created — to be branched at implementation-plan time

## Problem

`2026-06-30-claude-activity-dashboard-design.md` deliberately deferred GitHub
activity (PRs, commits, CI) as a separate spec — it needs a brand-new GitHub
API ingestion pipeline with its own auth, unlike Claude time tracking which
reads local transcripts. This spec covers that follow-up: PR, commit, and
CI/Actions activity across Chris's fix-portal repos, surfaced as a new
dashboard tab.

## Goals

- Track PR activity: opened/merged/closed state, author, timestamps, review
  count, review turnaround time.
- Track commit activity: per-repo commit counts and code churn (+/-).
- Track CI/Actions activity: per-repo, per-workflow run counts and pass/fail
  rates.
- Scope to the same repo set as Claude Activity — fix-portal / chris-fixportal
  repos — via an in-repo allowlist (see below; unlike Claude Activity, there is
  no out-of-repo hook to hold this filter).
- Surface as a 5th dashboard tab, "GitHub", consistent with the existing visual
  language and admin-only tab-visibility gating.

## Non-goals

- Issue tracking (opened/closed, labels, triage lag) — out of scope for this
  pass.
- Per-commit or per-run drill-down UI beyond the tables described below —
  commits and CI runs are exposed as repo-level rollups, not individual rows.
- Tracking any account/org beyond the existing allowlist — no "all repos I can
  see" mode.
- GitHub App auth or any multi-user auth model — single PAT, personal-scale
  use.

## Design overview

A 4th provider client in `AiObservatory.Ingest`, alongside the existing
Anthropic/OpenAI/Copilot clients, polled by the same
`ProviderPollingWorkerService` on the same cadence. Unlike Claude Activity
(captured out-of-repo by a private hook script), this pipeline lives entirely
in-repo: the Ingest worker calls GitHub's REST API directly.

### Capture (in-repo: `AiObservatory.Ingest`)

New `GitHubActivityClient`, following the existing provider-client pattern
(`AnthropicUsageClient`, `CopilotUsageClient`, `OpenAiUsageClient`):

- Auth: a personal access token (fine-grained, read-only scopes covering pull
  requests, contents, and actions) read from config — same
  secret-from-config pattern as the other three clients.
- Repo scope: `IngestOptions` gains `GitHubRepoAllowlist` (`string[]` of
  `owner/repo`). This is the in-repo equivalent of Claude Activity's
  out-of-repo project allowlist — there is no external hook here to hold it.
- Per allowlisted repo, per poll cycle: calls `GET /repos/{owner}/{repo}/pulls`
  (all states, paginated), `GET /repos/{owner}/{repo}/pulls/{number}/reviews`
  (count only, for `ReviewCount`), `GET /repos/{owner}/{repo}/commits`, and
  `GET /repos/{owner}/{repo}/actions/runs`.
- Lookback: reuses `IngestOptions.LookbackDays` (currently 3) for the rolling
  poll window; initial backfill is a one-time 30-day pull, same pattern as the
  other providers' first-run behavior.
- Rate limiting: checks the `X-RateLimit-Remaining` response header; if it
  drops near zero mid-cycle, the client stops calling further repos for that
  cycle, logs a warning, and resumes on the next scheduled poll. Not a hard
  failure.
- Per-repo access failure (private repo, PAT lacks permission, 404/403):
  logged and skipped; does not fail the rest of the poll cycle.

### Data model (in-repo)

Three new entities in `AiObservatory.Data.Entities`, all timestamps `Instant`
(NodaTime), following the raw-per-entity pattern used by `UsageEvent`:

**`GitHubPullRequest`**

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `Repo` | `string` | `owner/repo` |
| `Number` | `int` | PR number |
| `Title` | `string` | |
| `Author` | `string` | GitHub login |
| `State` | `string` | `open` / `merged` / `closed` |
| `CreatedAt` | `Instant` | |
| `MergedAt` | `Instant?` | |
| `ClosedAt` | `Instant?` | |
| `FirstReviewAt` | `Instant?` | Timestamp of the first submitted review, for turnaround |
| `ReviewCount` | `int` | Total reviews submitted |
| `IngestedAt` | `Instant` | Server-assigned, set on first insert |

Unique index: `(Repo, Number)`.

**`GitHubCommit`**

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `Repo` | `string` | |
| `Sha` | `string` | |
| `Author` | `string` | |
| `CommittedAt` | `Instant` | |
| `Additions` | `int` | |
| `Deletions` | `int` | |
| `IngestedAt` | `Instant` | |

Unique index: `(Repo, Sha)`.

**`GitHubWorkflowRun`**

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `Repo` | `string` | |
| `RunId` | `long` | GitHub's numeric run id |
| `WorkflowName` | `string` | |
| `Status` | `string` | `success` / `failure` / `cancelled` |
| `CreatedAt` | `Instant` | |
| `IngestedAt` | `Instant` | |

Unique index: `(Repo, RunId)`.

**Upsert on conflict**, keyed on each unique index — the 3-day rolling
lookback re-polls recent data every cycle; already-seen rows are cheap no-ops,
matching `UsageEvent`'s idempotent-poll behavior. A PR row is updated in place
as its `State`/`MergedAt`/`ReviewCount` change across polls.

### API (in-repo, `AiObservatory.Api`)

New `GitHubActivityEndpoints.cs`, mapped under `/api/github`:

- **`GET /api/github/prs?from=&to=`** — `{ repo, number, title, author, state,
  createdAt, mergedAt, reviewCount, turnaroundHours }[]`. `turnaroundHours` is
  computed as `FirstReviewAt - CreatedAt` where `FirstReviewAt` is set,
  otherwise `null`.
- **`GET /api/github/commits/summary?from=&to=`** — `{ repo, commitCount,
  additions, deletions }[]`, grouped by repo. No individual commit rows
  exposed.
- **`GET /api/github/ci?from=&to=`** — `{ repo, workflowName, totalRuns,
  failedRuns, successRate }[]`, grouped by repo + workflow name. `successRate`
  is `successful runs / totalRuns` (runs where `Status == "success"`), not
  `(totalRuns - failedRuns) / totalRuns` — non-terminal states such as
  `queued`/`in_progress` and `cancelled` count toward `totalRuns` but toward
  neither `failedRuns` nor the success count.

Same `yyyy-MM-dd` range-parsing convention as `/api/aggregates` and
`/api/activity`. All three GETs use `AdminOnlyApiKeyEndpointFilter` (the same
filter added for Claude Activity) — repo names and PR titles are as revealing
as project names, so the readonly viewer key must not see this data.

### Frontend (in-repo, `AiObservatory.Web`)

A 5th tab, "GitHub", added to `Dashboard.tsx`'s `page-nav`, next to Overview /
Adversarial Review / Reporting / Activity. Same admin-only tab-visibility
gating as Activity (hidden when `urlApiKey` is present / viewer mode).

`GitHubPage.tsx` layout:

1. **PR table**, full width, top of page — modeled on `ModelBreakdown.tsx`'s
   sort/search table. Columns: Repo, PR (#/title), Author, State, Created,
   Merged, Review count, Turnaround (formatted duration, blank when not yet
   reviewed).
2. **Two-panel row** below (`main-grid`, same pattern as Reporting/Activity):
   - **Left — commit summary table**: Repo, Commits, Churn (+/-), sortable.
   - **Right — CI health table**: Repo, Workflow, Runs, Failed, Success rate
     (color-coded, e.g. red below an 80% threshold).

All three tables share one `DateRangePicker` at the page top, same as
Activity/Reporting.

## Testing

- `GitHubActivityClientTests`: mocks the GitHub API, covers pagination,
  rate-limit backoff, per-repo access-failure skip, and upsert-on-conflict
  dedup — same shape as `AnthropicUsageClientTests`/`CopilotUsageClient` tests.
- Endpoint tests in `AiObservatory.Api.Tests` for all three GETs: date-range
  parsing, admin-only rejection of the readonly key.
- Frontend: sort/search behavior for the PR table, commit summary table, and
  CI health table, mirroring the existing `ModelBreakdown` tests.

## Known limitations

- Commit and CI data are repo-level rollups only; no per-commit or per-run
  drill-down. Revisit only if a real need for that granularity shows up.
- Review turnaround uses *first* review timestamp only — does not account for
  re-review cycles or requested-changes-then-approved loops. A simple proxy,
  not a full review-lifecycle model.
- Single PAT, single GitHub identity (Chris's). No multi-user auth. Fine for
  personal-scale use; would need rework if this ever needs to track
  someone else's activity.

## Open questions / follow-ups

- None outstanding — issue tracking and per-entity drill-down were explicitly
  scoped out above; revisit only if a real need arises.
