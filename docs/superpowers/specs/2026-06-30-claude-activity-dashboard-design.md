# Claude Activity Dashboard — Design

**Date:** 2026-06-30
**Status:** Approved (brainstorming) — ready for implementation plan
**Branch:** `feat/claude-activity-dashboard`

## Problem

The observatory tracks token spend and cost across providers, but nothing
about how Chris actually spends his time using Claude. Two adjacent questions
came up while discussing this — "how much time have I spent in Claude" and
"GitHub activity (PRs, most-changed repo)" — but they are independent
subsystems with no shared plumbing beyond "another tile on a dashboard": one
reads local Claude Code transcripts, the other needs a brand-new GitHub API
ingestion pipeline with separate auth. This spec covers **Claude time only**;
GitHub activity is a deferred follow-up with its own spec.

## Goals

- Show active time spent in Claude Code, broken down by day and by project.
- "Active time" excludes long idle gaps (a session left open overnight must
  not count as 8 hours).
- Backfill from existing local transcripts so the dashboard isn't empty on
  day one.
- Surface it as a new tab in the existing dashboard, consistent with the
  current visual language (sortable tables, `main-grid` two-panel layout).

## Non-goals

- GitHub PR/commit/repo activity — separate spec, separate ingestion pipeline.
- Tracking time in Codex, Copilot, or Antigravity — Claude Code only for this
  pass. (The schema's `Project` resolution and active-time algorithm could be
  reused for other tools later, but nothing here assumes that.)
- A second additive daily-rollup table (à la `DailyAggregate`). This is
  single-provider, personal-scale data — the API aggregates `GROUP BY` at
  query time instead of maintaining incremental rollups.
- Per-message or per-tool-call granularity in the UI. The unit of display is
  the session and the project; nothing finer.

## Design overview

Three components: an out-of-repo capture change, an in-repo data/API layer,
and an in-repo frontend tab. The capture change is described precisely enough
to implement, but it does not live in this repo's source tree or its PR.

### Capture (out-of-repo: `observe-sweep.ps1`)

`observe-sweep.ps1` is the private Claude Code hook that already walks local
transcript JSONL files on a timer to capture token-usage deltas. It gains a
second responsibility: per session, compute active time and POST it.

**Active-time algorithm:** for a session's transcript, take the timestamp of
every line (message or tool-result). Sum the gaps between consecutive
timestamps, but only count a gap if it is **≤5 minutes**. The result is
`ActiveSeconds` for that session — a focused-time proxy that excludes long
idle gaps (compile waits, short thinking pauses count; an idle terminal left
open overnight does not).

**Project resolution:** for the session's working directory, run
`git remote get-url origin`, normalize SSH/HTTPS forms, strip a trailing
`.git`, and take the last two path segments as `owner/repo`. If the directory
isn't inside a git repo (e.g. a notes folder), fall back to the directory's
leaf name.

**Why upsert, not append:** a session is swept repeatedly while it's still
open — each sweep re-walks the full transcript and recomputes `ActiveSeconds`
from scratch, then POSTs the latest total. The API upserts keyed on
`SessionId`: insert if new, otherwise replace `ActiveSeconds`/`LastSeenAt`
only if the new value is greater (defensive against an out-of-order delivery
undercounting a session that already recorded more time).

**Backfill:** a one-time pass over every local transcript already on disk,
running the same algorithm and POSTing each session once. Whether this is a
flag on the live sweep or a separate one-off script is an implementation
detail for whoever edits the hook — the wire contract (the POST body below)
is what this repo depends on.

### Data model (in-repo)

New entity, `AiObservatory.Data.Entities.ClaudeActivitySession`:

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `SessionId` | `string` | Unique index. Claude Code's own session id (transcript filename). |
| `Project` | `string` | `owner/repo` or leaf folder name, as resolved above. |
| `StartedAt` | `Instant` | First event timestamp in the transcript. |
| `LastSeenAt` | `Instant` | Most recent event timestamp as of the last sweep. |
| `ActiveSeconds` | `long` | Idle-gap-filtered active time. |
| `IngestedAt` | `Instant` | Server-assigned, set on first insert. |

No `RawPayload` field — unlike `UsageEvent`, this isn't billing-derived data
with an audit requirement; the transcript itself is the audit trail if ever
needed.

### API (in-repo, `AiObservatory.Api`)

New `ActivityEndpoints.cs`, mapped under the existing `/api` group:

- **`POST /api/activity/sessions`** — upsert (see above). Body:
  `{ sessionId, project, startedAtUtc, lastSeenAtUtc, activeSeconds }`.
  Already admin-key-only by default (`ApiKeyEndpointFilter` requires the
  admin key for any non-GET).
- **`GET /api/activity/daily?from=&to=`** — `{ date, activeSeconds }[]`,
  grouped by UTC date. Same `yyyy-MM-dd` range-parsing pattern as
  `/api/aggregates`.
- **`GET /api/activity/by-project?from=&to=`** — `{ project, sessionCount,
  activeSeconds, sharePercent }[]`, grouped by project, sorted descending by
  active time.

**Admin-only GETs.** Project names are more revealing than aggregate dollars
(client/employer names show up in repo paths), so — unlike every other GET
endpoint in this API — these two must reject the readonly viewer key, not
just accept admin-or-readonly. `ApiKeyEndpointFilter` doesn't support that
distinction today (its GET branch treats admin and readonly keys
identically). Add a second filter, `AdminOnlyApiKeyEndpointFilter`, applied
only to these two routes: same Entra-bypass and fixed-time-comparison
behavior, but its GET branch checks only the admin key.

### Frontend (in-repo, `AiObservatory.Web`)

A 4th tab, "Activity", added to `Dashboard.tsx`'s existing `page-nav` next to
Overview / Adversarial Review / Reporting. The tab button itself is only
rendered when **not** in viewer-key mode (mirror of the admin-only API
gating — `urlApiKey` absent, same check `AuthGate` already makes). A
colleague following the read-only share link never sees the tab exists.

Page layout (`ActivityPage.tsx`, mirroring `ReportingPage.tsx`'s structure):

1. **Trend chart**, full width, top of page — daily active time over the
   selected date range, same `DateRangePicker` + chart-panel shape as
   `SpendChart`.
2. **Two-panel row** below (`main-grid`, same pattern as Reporting's
   spend-chart + provider-split row):
   - **Left — sortable table** (`ProjectBreakdown.tsx`, modeled on the
     existing `ModelBreakdown.tsx`: search box, sortable columns). Columns:
     Project, Sessions, Active time, and a Share column combining an inline
     proportional bar with the percentage — this merges the bar-list and
     table options from the mockup into one column rather than two
     components.
   - **Right — treemap** (`ProjectTreemap.tsx`, new — no existing
     area-proportional viz in the app today). Block size proportional to
     active time. Clicking a block filters the table on the left to that
     project.

## Testing

- `AnthropicActivityClientTests`-equivalent: not applicable — there's no
  external billing API here, so no client to mock. The interesting logic is
  the upsert (insert vs. replace-if-greater), which belongs in repository
  tests against the real provider (xUnit + the project's Postgres
  integration-test setup), mirroring `UsageRepositoryTests`.
- Endpoint tests for `POST /api/activity/sessions` (upsert semantics, bad
  payload rejection) and both GETs (date-range parsing, admin-only
  rejection of the readonly key) in `AiObservatory.Api.Tests`.
- Frontend: `ProjectBreakdown` sort/search behavior tested the same way as
  the existing `ModelBreakdown` tests; `ProjectTreemap` click-to-filter
  interaction tested with React Testing Library.
- No unit coverage for the capture algorithm itself in this repo — it lives
  in the out-of-repo hook script, outside this repo's test suite.

## Known limitations

- **Midnight-spanning sessions are attributed to their start day.** Each session
  is one cumulative row (`StartedAt` + `ActiveSeconds`), and `/api/activity/daily`
  groups by the `StartedAt` UTC date. A session active across midnight therefore
  counts entirely toward the day it began, not split across the two days.
  Accepted deliberately: this is a personal-scale focused-time proxy, and
  day-slicing would require raw per-event rows (a materially larger schema and
  capture change) to correct an edge case that barely moves the totals. Revisit
  only if accurate per-day splitting becomes a real need.

## Open questions / follow-ups

- GitHub activity (PRs, most-changed repo) — separate spec, not started.
- Whether to extend the same active-time capture to Codex/Copilot/Antigravity
  sessions later — explicitly out of scope here, but the `Project` resolution
  and gap-filter algorithm would carry over unchanged if so.
- The exact form of the out-of-repo `observe-sweep.ps1` change (backfill
  flag vs. one-off script) is left to whoever implements that half; this spec
  only fixes the wire contract it must produce.
