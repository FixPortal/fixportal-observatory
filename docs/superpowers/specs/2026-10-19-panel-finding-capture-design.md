# Panel finding capture and seat influence views: design

Status: draft for owner approval. Project 1 of 4 (see "Roadmap").

## Intent

The owner decides which models hold a seat on the adversarial review panel. Today the
Observatory records one row per reviewer per run: tokens, cost, duration, `IssuesRaised`
and `IssuesAccepted` (`AdversarialReviewRun`). Those are counts. They cannot say whether
a seat found something nobody else did, or only agreed with the room, so the seat decision
cannot be made from them.

This project makes the Observatory record every finding, who raised it, who weighed in,
and what became of it. It then charts cost against influence per model, over time and
between models, with two pivots on influence: unique accepted catches, and corroborated
accepted catches.

## Goals

- One stored row per finding per run, with enough attribution to compute both pivots.
- A chart of cost vs influence per model, over time and across models, with a pivot switch.
- Numbers that are honest about what is unknown: unknown cost is never zero, a run with no
  finding detail is never shown as if it had been measured.
- Idempotent re-emission: re-running aggregation overwrites the same rows.

## Non-goals and anti-goals

- No finding text, file paths or code in the Observatory. Severity and ids only. A finding
  is looked up in the run folder (`runId`) when the owner wants the words. (Owner-visible
  call: this keeps code content out of a shared store.)
- No auto-changes to `reviewers.json`. The views inform a human decision.
- No back-fabrication. Historic runs have counts only and stay labelled "counts only".
- The recommendation engine, glance-metric audit and in-app docs are separate projects.

## Vocabulary (fixed here so every package agrees)

- **Seat**: one reviewer slot in `reviewers.json`. **Vendor**: the slug (`anthropic`,
  `openai`, ...). **Model**: the resolved model id. Two seats can share a vendor; the
  finding row records the model so they stay distinguishable.
- **Independent raiser**: a seat that put the finding forward in Phase 1 before seeing the
  pool. A finding merged from duplicates has several.
- **Assessor**: a seat that gave a Phase 2 verdict on a pooled finding.
- **Outcome**: the final disposition after adjudication and verification.
- **Unique accepted catch**: outcome `accepted` and exactly one independent raiser.
- **Corroborated accepted catch**: outcome `accepted` and two or more independent raisers.
- **Peer agreement** (separate from corroboration): count of Phase 2 `agree` verdicts. Shown
  as detail, never mixed into corroboration, because assessors saw the finding first.

## Contract

### Driver to Observatory

The host already writes `aggregate-verdict.json` at synthesis. Extend it with an optional
`findings` array. `aggregate-and-emit.ps1` posts it once per run after the per-participant
rows, to a new endpoint.

```json
{
  "accepted": { "anthropic": 6 },
  "judge": { },
  "findings": [
    {
      "chunkId": "C1",
      "findingId": "F3",
      "severity": "high",
      "outcome": "accepted",
      "raisedBy": [ { "reviewer": "openai", "model": "gpt-6-astra" } ],
      "assessments": [
        { "reviewer": "zai", "model": "glm-5.3", "verdict": "agree" }
      ]
    }
  ]
}
```

- `severity`: `critical | high | medium | low | unknown`.
- `outcome`: `accepted | refuted | dropped | indeterminate`.
- `verdict`: `agree | false-positive | needs-evidence | needs-repo | abstained`.
- `raisedBy` has at least one entry. `assessments` may be empty.
- `chunkId` is `single` for an unchunked run.

`aggregate-verdict.json` without `findings` keeps working exactly as today.

### Endpoint

`POST /api/adversarial-review/findings`, ordinary write key (same group as the run
endpoint). Body: `{ runId, repo, findings: [...] }`, at most 2000 findings.

Validation (400 on failure, never echo input): `runId` as the run endpoint; ids
`^[A-Za-z0-9._-]{1,40}$`; reviewer slug as the run endpoint; model up to 200; enums exact;
`raisedBy` non-empty; no duplicate `(chunkId, findingId)` in one body; at most 12
participants per finding.

Run existence: the run must already have at least one run row (the driver posts those
first). No run rows for `runId` gives 404; a `repo` that differs from the run rows' repo
gives 400. There is no foreign key to the run table; the check is in the service.

Idempotency and replace semantics (builder-check ruling): a body is the FULL set of
findings for the run, across all chunks, and the endpoint replaces every finding and
participant row for that `runId` in one transaction (delete then insert). It is not a
per-finding upsert, so a re-aggregation that drops or renumbers F-ids leaves no stale
rows. `aggregate-and-emit.ps1` is run-level, so it already holds the whole run. Maximum
2000 findings per body.

Time: charts bucket by the run rows' `RecordedAt`, which `AdversarialReviewRepository`
preserves on update (observed in `UpdateMetricsAsync`: "RecordedAt is deliberately
preserved"). Finding rows therefore carry no time of their own that a chart reads;
re-emission cannot move a run to a later week.

Outcome meanings, set by the driver (package C):

| Outcome | Meaning |
|---|---|
| `accepted` | survived Phase 3 adjudication and Phase 4 verification |
| `refuted` | Phase 4 verdict REFUTED |
| `dropped` | removed at Phase 3 adjudication before verification |
| `indeterminate` | Phase 4 verdict INDETERMINATE or no verification possible |

Rates: acceptance rate = accepted / raised. False-positive rate = refuted /
(accepted + refuted), i.e. over findings whose truth was settled; `dropped` and
`indeterminate` are shown as their own counts and appear in neither numerator nor that
denominator. "Raised" counts every finding the seat independently raised, any outcome.

Consistency rule: when a run has finding rows, the endpoint does not alter the run's
`IssuesRaised` / `IssuesAccepted`; those stay as the driver emitted them. The charts use
finding rows when present and fall back to counts otherwise (flagged "counts only").

### Storage

Two tables, EF Core migration, NodaTime `Instant` for `RecordedAt`:

- `AdversarialReviewFinding`: `Id`, `RunId`, `ChunkId`, `FindingId`, `Severity`, `Outcome`,
  `RecordedAt`. Unique index `(RunId, ChunkId, FindingId)`.
- `AdversarialReviewFindingParticipant`: `Id`, `FindingId` (FK, cascade), `Reviewer`,
  `Model`, `Kind` (`raiser | assessor`), `Verdict` (null for raisers).

## Views

A new "Seats" tab on the adversarial review dashboard.

1. **Cost vs influence scatter.** One point per model. X is cost per run (known costs
   only), Y is the selected influence metric. Time slider or small-multiples by month so a
   model's trail shows drift. Point size is runs contributed.
2. **Over time.** Line chart per model of the selected metric and of cost, by ISO week.
3. **Pivot switch.** Unique accepted catches | corroborated accepted catches | all accepted
   | acceptance rate. Default is unique accepted catches per run.
4. **Table underneath.** Per model: runs, findings raised, accepted, unique, corroborated,
   peer-agreement received, false-positive rate (refuted / (accepted + refuted)), total known cost,
   cost per unique catch, runs with unknown cost.
5. **Drill-through.** Click a point or row for that model's findings: id, run, severity,
   outcome, co-raisers. No text (see non-goals).

Rules: unknown cost excluded from cost ratios and counted in a visible "n runs unknown
cost" figure. Runs with no finding rows are listed as "counts only" and excluded from the
unique/corroborated pivots, included in all-accepted and acceptance rate. Small samples
(under 5 runs) are marked, not hidden.

## Constraints

- Match the existing run endpoint and `AdversarialReviewGrouping` conventions; read the
  roster from the data, never hard-code seat names in new code (the existing
  `ACTIVE_REVIEWER_VENDORS` list is the one place that knows, and is not extended here).
- The driver lives in the `~/.agents/skills` live home: work in a worktree of it, never a
  second clone, and pull the home after merge.
- Do not touch `reviewers.json`.
- House gates per repo (CSharpier check, build, tests; prettier/eslint/tsc/vitest for web).

## Work packages

| # | Package | Owner | Depends on |
|---|---|---|---|
| A | Contract tests and fixtures: sample `findings` JSON, valid and invalid | Claude | this doc |
| B | Observatory: entities, migration, endpoint, service, WAF integration tests | Claude | A |
| C | Driver: build `findings` from `pooled-map.json`, Phase 2 verdicts and final dispositions; extend `aggregate-and-emit.ps1`; verify script | GPT (Codex) | A |
| D | Web: Seats tab, charts, pivots, table, drill-through, built against fixtures | GLM | A |
| E | Integration: end-to-end run against a real dev panel run, reconcile counts | Claude | B, C, D |

Rules: one branch and worktree per package; a delegate never reviews its own diff; each
diff is reviewed by Claude before its PR; ordinary PR gates apply.

## Open points for the builder (flagged, not settled)

- UNVERIFIED: whether the driver preserves the raisers of merged duplicates anywhere.
  `pooled-map.json` maps one F-id to one reviewer. If merges drop the other raisers,
  package C must record them at pooling time, and that is the riskiest part of the project.
  Refuted if `pooled-map.json` or the judge packet is found to carry all raisers per F-id.
- UNVERIFIED: whether severity is available per finding in machine-readable form, or only
  in `report.md` prose. If prose only, package C extracts it and uses `unknown` on failure.
- Cost attribution joins finding participants to run rows on `(runId, reviewer)`, not on
  the model string. Two seats of one vendor with different models are already summed into
  one run row with an `a+b` model string; that row is one chart point labelled "mixed:
  a+b", and its findings roll up to it. No cost is split by guesswork. Emitting per-seat
  run rows is a possible later improvement, out of scope here.

## Roadmap

2. Glance metrics: audit each headline number against its source; drill-through.
3. Recommendation engine: seats, task-to-model, plan keep/drop. Reads this data.
4. In-app documentation per the house documentation standard.
