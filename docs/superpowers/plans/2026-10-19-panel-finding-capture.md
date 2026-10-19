# Panel Finding Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record every adversarial-review finding with who raised it, who weighed in and its outcome, and chart cost against influence per model.

**Architecture:** The review driver adds a `findings` array to `aggregate-verdict.json` and posts it once per run. A new Observatory endpoint replaces the run's finding rows in one transaction. The web app reads findings plus the existing runs endpoint and computes the seat metrics client-side in one pure module.

**Tech Stack:** .NET 10 / EF Core / Postgres, xUnit v3 + AwesomeAssertions, PowerShell 7 (driver), React/Vite/vitest (web).

**Spec:** `docs/superpowers/specs/2026-10-19-panel-finding-capture-design.md`

## Global Constraints

- Contract enums, exact: severity `critical|high|medium|low|unknown`; outcome `accepted|refuted|dropped|indeterminate`; verdict `agree|false-positive|needs-evidence|needs-repo|abstained`; kind `raiser|assessor`.
- Id pattern `^[A-Za-z0-9._-]{1,40}$`; reviewer slug as `AdversarialReviewService.IsReviewerSlug`; model at most 200 chars; at most 2000 findings per body; at most 12 participants per finding; `raisedBy` non-empty.
- No finding text, paths or code stored anywhere in the Observatory.
- `chunkId` is `single` for an unchunked run.
- No emoji anywhere (code, comments, commits, PR bodies). One push per branch at the end, after the full local gate. Rebase-merge only. `gh pr create --repo FixPortal/<repo>`.
- .NET gate order: `dotnet tool restore`, `dotnet csharpier check .` (read-only), restore, build Release, test Release. Web gate: `npm run format`, `npx tsc -b --noEmit`, `npm run lint`, `npx vitest run`.
- Never hard-code seat names in new code; read the roster from the data.
- A delegate never reviews its own diff. Claude reviews every delegated diff before its PR.

## Review Focus

- A re-post with fewer findings than before leaves no stale rows (replace, not upsert).
- A run with run rows but no finding rows renders as "counts only" and is excluded from unique/corroborated pivots.
- A run with unknown cost is excluded from every cost ratio and counted in a visible figure.
- A vendor with two models in one run shows one "mixed" point, not two guessed splits.
- A finding with a single raiser who is also its only assessor is not counted as peer agreement.

---

### Task 1: Contract fixtures (Claude)

**Files:**
- Create: `docs/superpowers/contracts/panel-findings/post-valid.json`, `post-invalid-cases.json`, `get-response.json`

**Interfaces:**
- Produces: the three fixtures every other task reads. `post-valid.json` is the POST body (spec "Driver to Observatory" shape plus top-level `runId`, `repo`) with at least four findings: one unique accepted, one corroborated accepted (two raisers), one refuted, one dropped. `post-invalid-cases.json` is an array of `{ "name", "body" }` covering: empty `raisedBy`, bad enum, duplicate `(chunkId, findingId)`, over 12 participants, bad id pattern, over 2000 findings (generated count noted, not inlined). `get-response.json` is the GET shape below.

GET `/api/adversarial-review/findings?runId=` response element:
`{ runId, repo, chunkId, findingId, severity, outcome, raisedBy: [{reviewer, model}], assessments: [{reviewer, model, verdict}] }`. Omitting `runId` returns every run's findings.

- [ ] **Step 1:** Write the three fixtures.
- [ ] **Step 2:** Commit `docs: panel finding contract fixtures`.

### Task 2: Observatory storage and endpoints (Claude)

**Files:**
- Create: `src/AiObservatory.Data/Entities/AdversarialReviewFinding.cs`, `AdversarialReviewFindingParticipant.cs`, migration `AddAdversarialReviewFindings`, `src/AiObservatory.Api/Services/AdversarialReviewFindingService.cs`
- Modify: `AiObservatoryDbContext` (two DbSets, unique index `(RunId, ChunkId, FindingId)`, participant FK with cascade), `IAdversarialReviewRepository` and `AdversarialReviewRepository`, `AdversarialReviewEndpoints.cs`
- Test: `tests/AiObservatory.Api.Tests/AdversarialReviewFindingServiceTests.cs`, `tests/AiObservatory.Api.IntegrationTests/AdversarialReviewFindingsWafTests.cs`

**Interfaces:**
- Consumes: Task 1 fixtures.
- Produces: `POST /api/adversarial-review/findings` and `GET /api/adversarial-review/findings?runId=`. Service method `Task<IResult> RecordFindingsAsync(AdversarialReviewFindingsRequest req, CancellationToken ct)`. Repository `Task ReplaceFindingsAsync(string runId, IReadOnlyList<AdversarialReviewFinding> findings, CancellationToken ct)` (delete then insert in one transaction) and `Task<IReadOnlyList<AdversarialReviewFinding>> GetFindingsAsync(string? runId, CancellationToken ct)` including participants.

- [ ] **Step 1: Write failing service tests**, one theory over `post-invalid-cases.json` asserting 400 and that the response body never contains any input string; one test for 404 when the run has no run rows; one for 400 on repo mismatch; one for a valid body returning 200 with the finding count.
- [ ] **Step 2:** Run `dotnet test --filter AdversarialReviewFindingServiceTests`; expect FAIL (types missing).
- [ ] **Step 3: Implement** entities, DbContext mapping, repository methods and service validation as specified. Follow `AdversarialReviewService` for slug and length guards. Add the migration with `dotnet ef migrations add AddAdversarialReviewFindings` and commit the generated snapshot.
- [ ] **Step 4: Write failing WAF tests**: replace semantics (post three findings, re-post two, GET returns two and no participants of the dropped one), idempotent double post, `GET` filtered by `runId`, read-only key 401, and a post for a run that was posted then deleted via the existing delete endpoint returns 404.
- [ ] **Step 5:** Map the two routes in `AdversarialReviewEndpoints.cs` inside the same group as the run routes. Run unit and integration tests (start only the db: `docker compose up -d db`; `docker compose down` afterwards); expect PASS.
- [ ] **Step 6:** Run the .NET gate and the AI-smell scan (`pwsh -File .github/ai-smells/pr-detect.ps1 -RepoPath . -Base origin/main -Head HEAD -OutFile <scratch>.json`); fix hits. Commit `feat: record adversarial review findings`.

### Task 3: Driver emits findings (GPT via Codex; own worktree of the skills home)

**Files (repo `fixportal-agents-skills`, checked out at `~/.agents/skills`; work in a worktree of it):**
- Modify: `adversarial-review/aggregate-and-emit.ps1`, `adversarial-review/docs/run-artefacts.md` (document the `findings` array)
- Create: `adversarial-review/emit-review-findings.ps1`, `adversarial-review/test/verify-findings-emit.ps1`

**Interfaces:**
- Consumes: Task 1 `post-valid.json` as the target shape; per-chunk `pooled-map.json`, Phase 2 verdict files, and the host-written `aggregate-verdict.json`.
- Produces: `emit-review-findings.ps1 -RunId <id> -Repo <name> -VerdictPath <path>` which reads `findings` from the verdict file and POSTs the body to `/api/adversarial-review/findings` using the same env vars and silent-no-op behaviour as `emit-review-telemetry.ps1`. `aggregate-and-emit.ps1` calls it once, after all participant emits, only when the verdict carries `findings`.

- [ ] **Step 1: Investigate first, report before coding.** Determine whether merged duplicate raisers survive anywhere (`pooled-map.json` maps one F-id to one reviewer) and whether per-finding severity exists in machine-readable form. Write the answers into `docs/run-artefacts.md`. If duplicates are lost, add a `raisers` list to `pooled-map.json` at pooling time in `run-review.ps1` and update its docs; if severity is prose-only, emit `unknown`.
- [ ] **Step 2: Write `verify-findings-emit.ps1`**: throws unless the emitter, given a verdict file with `findings`, posts a body equal to the fixture's finding shape (test against a local `HttpListener`), skips silently without env vars, posts nothing when `findings` is absent, and rejects an out-of-enum value before sending.
- [ ] **Step 3:** Run it; expect FAIL. Implement `emit-review-findings.ps1` and the call site. Run it; expect PASS.
- [ ] **Step 4:** Run the skills repo's own test runner (`run-skill-tests.ps1`) and its CI-equivalent checks. Commit `feat(adversarial-review): emit per-finding rows to the Observatory`.
- [ ] **Step 5:** Because the skills home is a live home, merge via PR, then `git pull --ff-only` the home as the last step.

### Task 4: Web Seats tab (GLM; own worktree of the Observatory)

**Files:**
- Create: `src/AiObservatory.Web/src/components/seatInfluence.ts`, `seatInfluence.test.ts`, `SeatsTab.tsx`, `SeatsTab.test.tsx`, `src/api/useAdversarialFindings.ts`
- Modify: `AdversarialReviewPanel.tsx` (tab entry only)

**Interfaces:**
- Consumes: Task 1 `get-response.json` and the existing runs response (`/adversarial-review/runs`).
- Produces: `computeSeatMetrics(runs: RunRow[], findings: FindingRow[], pivot: Pivot): SeatMetrics[]` where `Pivot = 'unique' | 'corroborated' | 'accepted' | 'acceptanceRate'`, and `SeatMetrics = { key: string; label: string; mixed: boolean; runs: number; raised: number; accepted: number; unique: number; corroborated: number; peerAgreement: number; refuted: number; dropped: number; indeterminate: number; falsePositiveRate: number | null; acceptanceRate: number | null; knownCostUsd: number; unknownCostRuns: number; costPerUnique: number | null; countsOnlyRuns: number; byWeek: { week: string; costUsd: number; influence: number }[] }`. Grouping key is the run row's `Model` string joined on `(runId, reviewer)`; `mixed` is true when it contains `+`.

- [ ] **Step 1: Write `seatInfluence.test.ts`** against fixtures: unique vs corroborated counts, false-positive rate = refuted / (accepted + refuted), acceptance rate = accepted / raised, unknown-cost runs excluded from cost ratios and counted, counts-only run excluded from unique/corroborated and included in accepted, a `a+b` model row yields one `mixed` entry, peer agreement excludes a self-assessment by the raiser, week buckets use the run row `recordedAt`, under-5-run entries carry a small-sample flag.
- [ ] **Step 2:** Run `npx vitest run seatInfluence`; expect FAIL. Implement `seatInfluence.ts` as a pure module with no React.
- [ ] **Step 3: Write `SeatsTab.test.tsx`** (renders table rows, pivot switch changes the Y metric label and values, mixed label, unknown-cost note, empty state). Implement `SeatsTab.tsx`: cost-vs-influence scatter (one point per key, size by runs), over-time line chart by week, pivot switch, table, drill-through list (id, run, severity, outcome, co-raisers). Use the chart library and icon set the repo already uses; no emoji.
- [ ] **Step 4:** Add the tab entry to `AdversarialReviewPanel.tsx`. Run the web gate. Commit `feat(web): seat influence tab`.

### Task 5: Integration (Claude)

- [ ] **Step 1:** After Tasks 2 to 4 merge, deploy-check locally: run the driver against a real dev panel run with `OBSERVATORY_URL` pointed at a local stack, then confirm GET findings matches the run folder's `report.md` finding count and that the Seats tab renders it.
- [ ] **Step 2:** Reconcile: per vendor, accepted findings from the finding rows equals the run row's `IssuesAccepted`. Any mismatch is a driver bug, not a view bug; record it and fix in the driver.
- [ ] **Step 3:** Update `docs/provider-setup.md` or the adversarial-review section of the docs with the new endpoint. Commit.

## Dispatch notes (Claude, not the plan's executors)

- Task 3 goes to Codex as a self-contained brief with inlined material (Codex command spawning is intermittently blocked on this box), via the `.claude/handoff/latest.md` pattern or an inlined `codex exec` over stdin. Task 4 goes to GLM through whichever harness the owner uses for it. Both briefs cite this plan's task text verbatim and the fixtures by path.
- Task 2 is blocked on Task 1 only; Tasks 3 and 4 are blocked on Task 1 only and can run in parallel with Task 2.
