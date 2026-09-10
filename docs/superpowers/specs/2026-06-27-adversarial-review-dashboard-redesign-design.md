# Adversarial Review Dashboard Redesign — Design

**Date:** 2026-06-27
**Status:** Approved (brainstorming) — ready for implementation plan
**Branch:** `feat/adv-review-dashboard-redesign`

## Problem

The observatory's **Adversarial Review** tab shows garbage. Live data
(`GET /api/adversarial-review/runs`) at time of writing: **31 rows across 31
distinct `runId`s — every recorded run has exactly one participant**, and almost
all are Anthropic/Sonnet. A correct three-vendor panel run should record **3
reviewers** (Sonnet + Gemini + GPT), and going forward also the **Opus judge**.

Observed symptoms in the UI:

- "Sonnet twice" — two summary rows for the same reviewer: `anthropic /
  claude-sonnet-4-6` (21 runs) and `anthropic / sonnet` (4 runs).
- Gemini model drift — `google / gemini-3.1-pro-preview` in the data vs
  `gemini-2.5-pro` configured in the skill's `reviewers.json`.
- No Opus anywhere — the judge is never recorded.
- Costs mostly `$0.0000` — only GPT carries a cost.
- A flat, ungrouped table — you cannot see which reviewers belong to one run.

## Root causes

1. **PRIMARY — server-side dedup collapses a run to one row.**
   `AdversarialReviewRun.RunId` carries a **unique** index
   (`AiObservatoryDbContext.cs:84`) and the repository dedups on `RunId` alone
   (`AdversarialReviewRepository.cs:15`). When a run's reviewers emit with the
   **same shared `runId`** (as intended), the first insert wins and every
   subsequent participant returns `200 {duplicate:true}` and is **silently
   dropped**. This is why each `runId` has exactly one participant. The
   occasional Google/OpenAI solo rows are runs where Anthropic didn't emit (or
   lost the race) and a run with a malformed/unshared `runId`.

2. **Malformed / unshared `runId`s.** Several stored ids are invalid
   (`20260624T755388Z` — hour "75", `20260624T000000Z`). The host generated ids
   inconsistently, so even absent the unique-index bug, a run's participants
   would not always group together.

3. **Model id not normalized.** The emit passed the `reviewers.json` CLI alias
   (`sonnet`) on some runs and the resolved id (`claude-sonnet-4-6`) on others.
   The panel groups by `(reviewer, model)`, so one reviewer splits into two rows.

4. **Judge never emitted.** `emit-review-telemetry.ps1` is called only for the
   three reviewers (B/G/X). The Opus adjudicator (and verifiers/synthesis) emit
   nothing, so the panel structurally cannot show the judge.

5. **Cost not joined to the run record for Claude/Gemini.** The token cost *is*
   captured globally — on the Overview page via `/api/events` (Sonnet as
   Anthropic via the transcript sweep; Gemini via `gemini-review.ps1`; GPT via
   `openai-review.ps1`). But the per-run record only gets a real cost for GPT,
   because only `openai-review.ps1` writes a usage sidecar that
   `emit-review-telemetry.ps1` reads. Sonnet and Gemini emits pass `0`.

6. **Duration always 0.** No wall-clock is captured for any participant.

## Goals

- A run is a first-class, grouped unit in the UI: one collapsible panel per
  `runId`, **totals in the collapsed header** (raised / accepted / cost /
  duration), expandable to the per-participant rows.
- Every run records its **3 reviewers + the Opus judge**, sharing one
  well-formed `runId`, with **real per-participant cost and duration**.
- One **canonical model id per reviewer** — no duplicate summary rows.
- **Incomplete runs** (fewer than 3 reviewers, or no judge) are visibly flagged.
- The top **"Stats by reviewer & model"** summary stays, normalized, with the
  judge included and an average-duration column.
- A clean **wipe** of the existing garbage rows, then correct tracking forward.

## Non-goals

- Recording Phase-4 verifiers or the synthesis pass as participants (judge only).
- Changing the review *methodology* (phases, briefs, blind independence).
- Backfilling cost/duration onto historical runs (they are being wiped).

## Design overview

Two halves with a clean seam at the HTTP boundary:

- **Repository (in this repo, shipped via PR):** data model + migration, API
  service/repository/endpoints, and the React panel. This makes the observatory
  *correctly store and display* whatever telemetry arrives, and stop discarding
  a run's participants.
- **Skill (in `~/.claude/skills/adversarial-review/`, applied directly — NOT in
  the PR, mirrors the existing convention for personal config):** emit all four
  participants with a shared well-formed `runId`, canonical model ids, and real
  cost + duration.

Both are required for correct data; the repository half is the one that ships
and is reviewable.

---

## Part 1 — Data model (`AiObservatory.Data`)

`AdversarialReviewRun` gains two fields:

| Field | Type | Notes |
|---|---|---|
| `Role` | `string` | `"reviewer"` or `"judge"`. Distinguishes the Opus judge (`reviewer="anthropic"`, `role="judge"`) from the Sonnet reviewer (`reviewer="anthropic"`, `role="reviewer"`). Required. |
| `Repo` | `string?` | Repository name (`git rev-parse --show-toplevel` basename) for the run-panel header. Nullable. |

`ReviewDurationMs` already exists — it just needs to be populated.

**Index change (the core fix):** drop the unique index on `RunId`; add a
**composite unique index on `(RunId, Reviewer, Role)`**. This lets a run's four
participants share one `runId` while still deduping a re-emitted participant.
Keep the non-unique `(Reviewer, Model)` and `RecordedAt` indexes.

**Check constraint:** `CK_AdversarialReviewRun_IssuesAccepted_Valid`
(`IssuesAccepted >= 0 AND IssuesAccepted <= IssuesRaised`) is unchanged — the
judge emits `IssuesRaised=0, IssuesAccepted=0`, which satisfies it.

**Migration:** add `Role` (NOT NULL; default `'reviewer'` for the backfill of
any rows that survive the wipe), add `Repo` (nullable), drop
`IX_AdversarialReviewRuns_RunId`, create unique
`IX_AdversarialReviewRuns_RunId_Reviewer_Role`. Generated migration only — do
not hand-edit (per repo convention; `generated_code = true`).

---

## Part 2 — API (`AiObservatory.Api`)

**`AdversarialReviewRunRequest`** gains `Role` (required) and `Repo` (optional).

**`AdversarialReviewService.RecordRunAsync`:**

- Validate `Role` ∈ {`reviewer`, `judge`} (400 otherwise).
- **Normalize the model id** to canonical before persisting, via a small map:
  `sonnet`/`claude-sonnet`→`claude-sonnet-4-6`, `opus`/`claude-opus`→
  `claude-opus-4-8`, `haiku`→`claude-haiku-4-5`. Unknown ids pass through
  unchanged (so a genuinely new model is not silently rewritten). This is a
  defensive backstop; the emit side also sends canonical ids.
- Persist `Role` and `Repo` alongside the existing fields.

**`AdversarialReviewRepository`:**

- Dedup query keys on `(RunId, Reviewer, Role)`, not `RunId` alone (matches the
  new unique index; the `UniqueViolation` catch re-queries on the same triple).
- `GetStatsAsync` keeps grouping by `(Reviewer, Model)` — the judge appears
  naturally once judge rows exist — and additionally averages
  `ReviewDurationMs`. Add `AvgDurationMs` to the `AdversarialReviewStats` record.
- Add `DeleteAllRunsAsync` → `ExecuteDeleteAsync` over the table.

**Endpoints (`AdversarialReviewEndpoints`):**

- `GET /adversarial-review/runs` projection adds `Role` and `Repo`.
- New `DELETE /adversarial-review/runs` — admin-key gated (the `ApiKeyEndpointFilter`
  already gates DELETEs, per the aggregates/insights pattern) — calls
  `DeleteAllRunsAsync` and returns `{ deleted }`. Used for the wipe and future
  resets.
- `GET /adversarial-review/stats` unchanged in shape beyond the new
  `avgDurationMs` field.

Runs are returned flat (newest first); **grouping by `runId` happens client-side**
— a small audit table, so no server-side grouping endpoint is warranted.

---

## Part 3 — Web (`AiObservatory.Web`)

**`api/queries.ts`:** extend the run type with `role`, `repo`; extend the stats
type with `avgDurationMs`.

**`theme/providerColors.ts`:** add an amber tone for the judge row (keyed on
`role === 'judge'`, falling back to provider color for reviewers).

**`components/AdversarialReviewPanel.tsx`:**

- *Stats by reviewer & model* (top): unchanged structure, plus an **Avg dur**
  column. Rows are data-driven, so the judge (`anthropic / claude-opus-4-8`)
  appears once judge rows exist; normalization removes the duplicate Sonnet row.
- *Recent runs*: group the flat run list by `runId`; render each group as a
  **collapsible panel (Direction A)** reusing the existing `CollapsiblePanel`
  component, keyed by `runId` (localStorage-persisted open state):
  - **Collapsed header:** date/time, repo, participant count, a status badge
    (**complete** = all 3 reviewer vendors present **and** a judge row;
    **incomplete** otherwise, with a one-line reason), and the run **totals** —
    Σ raised, Σ accepted, Σ cost, Σ duration.
  - **Expanded body:** one row per participant (reviewers first in
    anthropic/google/openai order, then the judge in amber), columns: reviewer,
    model, raised, accepted, cost, $/finding, duration. Judge shows `—` for
    raised/accepted/$-per-finding; cost where the reviewer/judge reports it,
    `—` otherwise.
- **Incomplete detection** is client-side: a group with fewer than 3 distinct
  reviewer-role vendors, or no judge-role row, is `incomplete`.

---

## Part 4 — Telemetry / skill (`~/.claude/skills/adversarial-review/`, out of PR)

Applied directly to personal config; documented here for completeness.

- **`runId`** — one well-formed UTC slug per run (`yyyyMMddTHHmmssZ`, the
  workdir name), passed to **every** emit for that run. Fix the malformed-id
  generation.
- **`emit-review-telemetry.ps1`** — add `-Role` (`reviewer|judge`, `ValidateSet`),
  `-Repo`, real `-CostUsd` and `-ReviewDurationMs`; send the **canonical** model
  id. Emit **four** participants per run (3 reviewers + judge), each with the
  shared `runId` and a distinct `(reviewer, role)`.
- **`gemini-review.ps1`** — write a usage sidecar (tokens + `costUsd` + duration)
  like `openai-review.ps1`; wrap the call in a `Stopwatch` for duration. It
  already computes `costUsd` for its `/api/events` post; reuse that.
- **`openai-review.ps1`** — add duration to its existing sidecar (`Stopwatch`).
- **Sonnet reviewer + Opus judge (Agent-tool path)** — the host times each
  `Agent` call (`Stopwatch`) for duration; for cost it identifies that
  subagent's `agent-*.jsonl` transcript (snapshot the `agent-*.jsonl` set
  immediately before and after the `Agent` call; the new file is this
  participant's) and sums its usage, priced with the **same Anthropic pricing
  table** `observe-sweep.ps1` uses. This is the trickiest piece — call it out in
  the plan and verify the before/after-snapshot correlation holds when only one
  Claude agent runs per phase.
- **`SKILL.md` §3 / §3a** — rewrite the telemetry instructions to match: shared
  `runId`, canonical ids, emit the judge, cost/duration sourcing per participant.

---

## Part 5 — Wipe & rollout sequence

1. Ship Part 1–3 (migration + API + Web) via the PR; deploy.
2. `DELETE /api/adversarial-review/runs` (admin key) to clear the 31 garbage
   rows.
3. Apply the Part 4 skill changes to `~/.claude`.
4. Run one adversarial review to confirm a single `runId` now records 4
   participants with real cost/duration, and the panel renders one complete
   collapsible group.

The wipe is destructive but in scope (user-approved "start from scratch"); it
only touches the adversarial-review runs table, not usage aggregates,
subscriptions, or budget rules.

## Testing

- **API unit (`AiObservatory.Api.Tests`, xUnit — match existing project):**
  model-id normalization (alias → canonical; unknown passes through); `Role`
  validation; the judge's `0/0` issue counts pass validation.
- **Repository (`AiObservatory.Data.Tests` — integration, needs Postgres; will
  not run without a live DB, per existing convention):** four participants
  sharing one `runId` coexist; re-emitting the same `(runId, reviewer, role)`
  dedups; `DeleteAllRunsAsync` clears the table.
- **Web (`vitest`, `globals:false` — explicit imports):** grouping a flat run
  list by `runId`; per-column totals; incomplete-run detection (missing reviewer
  / missing judge); judge row renders `—` for raised/accepted.

## Files

**In the PR (repo):**
- `src/AiObservatory.Data/Entities/AdversarialReviewRun.cs` — add `Role`, `Repo`
- `src/AiObservatory.Data/AiObservatoryDbContext.cs` — index swap, properties
- `src/AiObservatory.Data/Migrations/*` — generated migration
- `src/AiObservatory.Data/Repositories/AdversarialReviewRepository.cs` +
  `IAdversarialReviewRepository.cs` — dedup triple, `AvgDurationMs`,
  `DeleteAllRunsAsync`
- `src/AiObservatory.Api/Services/AdversarialReviewService.cs` — request fields,
  validation, normalization; `AdversarialReviewStats` gains `AvgDurationMs`
- `src/AiObservatory.Api/Endpoints/AdversarialReviewEndpoints.cs` — projection,
  DELETE
- `src/AiObservatory.Web/src/api/queries.ts` — types
- `src/AiObservatory.Web/src/theme/providerColors.ts` — judge color
- `src/AiObservatory.Web/src/components/AdversarialReviewPanel.tsx` — grouping,
  collapsible panels, judge, incomplete flag, top-summary duration column
- tests as above

**Out of the PR (`~/.claude/skills/adversarial-review/`):**
- `emit-review-telemetry.ps1`, `gemini-review.ps1`, `openai-review.ps1`,
  `SKILL.md`, and the run-id generation in the skill procedure.

## Open risks

- **Sonnet/judge cost correlation.** The before/after `agent-*.jsonl` snapshot
  assumes one Claude agent per phase. Phase 1 has exactly one (Reviewer B; G/X
  are subprocess wrappers, not agents); the judge runs alone in Phase 3. Verify
  no other agent is spawned concurrently within those windows, or the snapshot
  picks the wrong file.
- **"Complete" definition couples UI to the judge.** Once the judge is expected,
  pre-judge historical runs (post-wipe there should be none) would read as
  incomplete. Acceptable given the wipe.
