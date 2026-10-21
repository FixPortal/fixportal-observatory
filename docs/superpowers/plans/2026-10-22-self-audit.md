# Dashboard Self-audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver dashboard-led, independently calculated usage audits with retained private evidence and deterministic offline replay.

**Architecture:** A separate Node companion extracts allowlisted source facts; an independent .NET library evaluates frozen inputs; a PostgreSQL-backed API worker coordinates captures and stores results. The React dashboard exposes readiness, runs, discrepancies, exports, and retention. The library and offline runner have no dependency on production parsing, accounting, pricing, EF, or network services.

**Tech Stack:** Existing .NET 10/C#, EF Core/PostgreSQL, Node 24+, React/TypeScript/TanStack Query, xUnit v3, Node test runner, Vitest/Testing Library. Use built-in cryptography and ZIP support; no new cloud service or LLM dependency.

**Spec:** [Approved dashboard self-audit design](../specs/2026-10-21-self-audit-design.md), approved 2026-10-22.

## Global Constraints

- Default detailed evidence retention is 90 days from sealing. Default report-summary retention is 365 days from run completion.
- Initial defaults: 8 MiB uncompressed per chunk, 512 MiB per capture or complete snapshot, 1 million usage records per capture, 31-day maximum run window, one executing run per instance, and a 24-hour capture request deadline.
- The default is the previous seven completed UTC days; no range is implicitly certified complete.
- The independent verifier library does not reference production repositories, calculators, or ingest adapters. Shared transport and schema primitives are allowed; shared numerical transformations are not.
- Never transmit transcript lines, conversation text, arbitrary raw payloads, tool arguments/results, environment variables, secrets, or absolute filesystem paths.
- Money uses decimal arithmetic, explicit rounding points, and per-check tolerances derived from stored precision. Tokens require exact equality.
- Identical evidence, verifier version, and options produce identical canonical result bytes and result hash.
- A fresh audit creates a new run linked to its predecessor. A replay with a newer verifier is a new evaluation linked to the original.
- No automatic repairs, production repricing, ingestion, tombstones, vendor calls, or code execution from bundles occurs during an audit.
- Initial usable release includes the dashboard, independent Claude/Codex source accounting, event aggregation, pricing/presentation checks with honest coverage, and explicit unsupported entries for the other six local sources and unqualified billing feeds.
- Preserve the current sweeper, API key, Entra, demo, subscription, and read-only contracts. Keep audit-only endpoints and storage behind `Audit:Enabled`, default false; enabling requires validated settings.
- All examples/committed bundles are synthetic. Private installation evidence and secrets never enter Git or public CI artifacts.

## Review Focus

1. A revoked machine credential with an unexpired lease must lose upload/seal access immediately (Task 7).
2. Cancellation or evidence deletion racing a worker must prevent publication of a matched result (Tasks 9 and 12).
3. A long session can straddle the requested dates without closing; include its baseline and report provisional attribution rather than inventing a daily split (Tasks 2 and 5).
4. Counts above JavaScript's exact integer range must never be rounded into a plausible match (Tasks 1, 5, and 11).
5. Browser access through a read-only share key must remain read-only even when an Entra session is present (Tasks 10 and 11).

## Execution and decomposition

The feature is one coupled subsystem with a shared evidence protocol. Use one ordered plan, with reviewable vertical increments rather than separate plans that could drift on that protocol. Tasks 1–4 produce a usable offline verifier; Tasks 5–10 connect capture and durable server execution; Tasks 11–14 complete dashboard operation and release qualification. Keep the feature disabled for normal users until Task 14 passes.

At execution time, use an isolated worktree and follow applicable project/test/frontend skills. This plan does not authorize deployment or public upload of private evidence. Design and planning have not implemented any of these tasks.

## Contracts and file boundaries

Create `src/AiObservatory.Audit/` and `src/AiObservatory.Audit.Cli/`, with matching `tests/AiObservatory.Audit.Tests/` and `tests/AiObservatory.Audit.Cli.Tests/`. Add them to `AiObservatory.slnx`; test projects use the existing xUnit v3/MTP pattern and centrally managed dependencies. Audit has no project references. CLI references Audit only. Data/API may reference Audit's contracts, never the reverse.

Within Audit use `Contracts/`, `Serialization/`, `Checks/`, `Sources/`, and `Bundles/`; one named public type per file where practical. New Data models/configurations live in `Audit/`; API services and DTOs live in `Audit/`. Keep new EF configuration out of the already large `AiObservatoryDbContext.OnModelCreating` body by invoking `ApplyConfiguration` for explicitly named audit configurations.

### Version 1 protocol

- All DTOs reject unknown fields. `SchemaVersion = 1`; wire enum values are lower camel case. No arbitrary JSON extension/raw-payload properties.
- `AuditScope`: inclusive `From`, `To` dates; sorted distinct `SourceSelection(MachineId, SourceId)`; ordered requested check codes; explicitly excluded check codes; `AllowPartial`; optional `PredecessorRunId` and `EvaluationOfRunId`.
- Check codes: `sourceAccounting`, `dailyAttribution`, `eventAggregation`, `pricingArithmetic`, `presentationTotals`, `billingReconciliation`.
- `SourceUsageRecord`: source format/version, tool, record kind, opaque file/session/message/agent/parent IDs, identity-generation ID, source sequence, UTC timestamp, model/provider identifiers, typed nullable integer counters, nullable decimal money/currency, typed pricing dimensions, and explicit closure evidence. Use discriminated Claude/Codex counter records retaining the source names and inclusive/subset semantics; no production-normalized totals in this reference record.
- `CaptureManifest`: capture/request/machine IDs, identity generation, extractor version, scan start/end, scope, source formats, discovered/read/skipped/changed counts, record count, observed date bounds, sanitized diagnostic counts, ordered chunk hashes, and seal time.
- `AuditInput`: schema/verifier/rule versions, scope, capture manifests and records, frozen event/aggregate/spend/source-status/pricing/FX projections, options, and named tolerances. Comparison projections include only explicitly typed usage/accounting fields; strip `RawPayload` and turn required dimensions into typed values.
- `AuditResult`: scope, input content digest, verifier/rule versions, ordered check outcomes, coverage, findings, and overall verdict. Findings contain stable rule code, group key, opaque evidence references, field, expected/actual/delta values as typed strings, unit/currency/basis, and diagnostic code. No run ID, execution timestamp, elapsed duration, or progress logs enter canonical results.
- `CheckOutcome(CheckCode Code, CheckVerdict Verdict, AuditCoverage Coverage, IReadOnlyList<AuditFinding> Findings)` is the common check return type. `SourceReconstruction` contains `CompatibleGroups`, `ChronologicalGroups`, `Coverage`, and `Findings`; groups use the complete accounting key and typed counters. Contracts use `DateOnly`/UTC `DateTimeOffset`, standalone string enums and `long`/`decimal`, without production entity or NodaTime dependencies. Persistence adapters translate to existing NodaTime types.
- `FrozenEvent` includes row ID, event key, source/provenance, occurrence/observation/ingestion time, model, token fields, cost/savings, correction marker, revision digest, and optional pricing-application provenance. `FrozenAggregate` retains the complete production grouping key and every reported additive field/count. `FrozenSpend` retains currency, basis, amounts, FX provenance and source/account/period identities.
- Optional `FrozenPresentationProbe` contains the frozen snapshot hash, production frontend build/source identity, summary input digest, and captured production summary values. It is a declared observation from that frontend, not external attestation. An absent probe means presentation is incomplete even when independent reference totals are available.
- .NET token arithmetic is checked `long`; additions that overflow fail with a sanitized code. Node reads source integer lexemes losslessly, uses `BigInt`, rejects fractional/negative/out-of-Int64 counters, and emits integer JSON lexemes. Web DTOs encode counts as decimal strings; UI never converts them to `number` for arithmetic.
- Canonical JSON: UTF-8, ordinal object-key sorting, stable collection sort keys, invariant decimal strings without exponent/trailing insignificant zeros, UTC ISO timestamps, integers as exact JSON integer lexemes, explicit nulls. SHA-256 digests identify canonical bytes.
- Stable collection keys: records `(sourceId, generation, fileId, sequence)`; events by row ID; aggregates by complete aggregate key; catalogs by source/ID/hash; findings by `(checkCode, ruleCode, groupKey, evidenceRef, field)`. Duplicate stable identities with conflicting contents are invalid input.
- Token tolerance is zero. Money tolerance is half the least representable unit in the compared stored column; inspect EF/migration numeric scale and freeze it in each snapshot's tolerance map. Unconstrained PostgreSQL numeric/decimal values compare exactly. UI currency formatting compares at two decimal places with midpoint-away-from-zero; preserve the unrounded value separately. Rounding policy and tolerances are input data.

### HTTP routes and authority

All routes below use `/api/audits`. API DTOs are versioned and separate from EF entities. Ordinary read-only GETs use the existing API filter; privileged GETs additionally use `AdminOnlyApiKeyEndpointFilter`. Mutations are admin-only except explicitly machine-scoped operations. Machine routes are mapped outside the ordinary `/api` API-key filter, with their own credential filter and the same rate limiter.

| Method and suffix | Body/result and authority |
| --- | --- |
| `GET /capabilities` | Feature flag, check/source capability matrix, supported schema/verifier versions; ordinary viewer. |
| `GET /readiness?from=&to=&sources=` | Coverage/freshness/capture availability/limits, no record details; ordinary viewer. |
| `GET /runs?cursor=&limit=` | Stable `(createdAt,id)` pagination, default 50/max 200; viewer-safe summaries. |
| `POST /runs` | `CreateAuditRunRequest(AuditScope Scope, Guid[] CaptureIds, bool RequestFreshCapture)` plus `Idempotency-Key`; returns `202` with run ID/location; admin. |
| `GET /runs/{id}` | State, progress, verdict, coverage, versions/cutoff/replay expiry; viewer-safe. |
| `POST /runs/{id}/cancel` | Idempotent cancellation request; admin. |
| `POST /runs/{id}/proceed` | Explicitly continue with partial captures after readiness acknowledgement; admin, only collecting state. |
| `GET /runs/{id}/findings?cursor=&limit=` | Viewer-safe group/count/value diagnostics; no opaque session/file/message identifiers. |
| `GET /runs/{id}/evidence?cursor=&limit=` | Detailed sanitized records and reference mapping; admin. |
| `GET /runs/{id}/bundle` | Stream ZIP without compression of already compressed content; admin. Expired/deleted evidence returns `410`. |
| `GET /runs/{id}/report` | Download sanitized readable summary; viewer. |
| `POST /runs/{id}/replay` | New run pinned to retained frozen inputs and installed version, plus idempotency key; admin. Unsupported version returns `409`. |
| `POST /runs/{id}/presentation-probe` | Typed `FrozenPresentationProbe` plus idempotency key; admin. Creates a linked evaluation of retained frozen inputs, never mutates the completed original. Stale snapshot/input digest returns `409`. |
| `GET /retention`, `PATCH /retention` | `AuditRetentionDto(int? EvidenceDays, int? SummaryDays)`; null means explicit indefinite. GET/PATCH admin. New settings affect future seals/completions, not existing deadlines. |
| `GET /captures/{id}/deletion-impact` | Affected runs/replay loss; admin. Returns impact digest. |
| `DELETE /captures/{id}` | `If-Match` impact digest; delete evidence, invalidate dependent replays; admin. Stale digest returns `412`. |
| `DELETE /runs/{id}` | Delete run report/details; remove only unreferenced snapshots/chunks; admin. |
| `POST /machines`, `POST /machines/{id}/rotate`, `DELETE /machines/{id}/credential` | Issue/rotate/revoke scoped random credential; admin. Secret appears once in create/rotate response. |
| `POST /capture-requests/claim` | Machine credential, no body beyond protocol version; returns assigned capture scope/lease or `204`. |
| `PUT /capture-requests/{id}/chunks/{index}` | Bounded uncompressed canonical NDJSON records plus content hash and lease token; assigned machine only. |
| `POST /capture-requests/{id}/seal` | Manifest/hash with idempotency key and lease token; assigned machine only. |

Use `X-Observatory-Audit-Key` for machine credentials, never a query parameter. No redirects forwarding credentials. Ordinary validation gives sanitized `400`; unauthenticated `401`; wrong scope `403`; idempotency/state conflict `409`; exceeded limits `413`; expired resources `410`. Unknown resource IDs do not reveal other-machine scope. Feature-disabled endpoints return `404`. Demo allows synthetic GETs only; all mutations return `403`.

## Task 1: Independent contracts and canonical result bytes

**Files:** Create Audit project, `Contracts/{AuditScope,SourceUsageRecord,CaptureManifest,AuditInput,AuditResult,AuditFinding,FrozenEvent,FrozenAggregate,FrozenSpend,AuditOptions}.cs`, `Serialization/AuditCanonicalJson.cs`; create Audit test project and `Serialization/CanonicalJsonTests.cs`, `Architecture/DependencyBoundaryTests.cs`; modify solution.

**Interfaces:** `AuditCanonicalJson.Serialize<T>(T value): byte[]`, `Deserialize<T>(ReadOnlySpan<byte> utf8): T`, `ContentHash(ReadOnlySpan<byte> utf8): string`. Define all shared contracts above before downstream work.

- [ ] Write `CanonicalJsonTests.OrderAndPrecisionAreStable`: shuffled equivalent inputs serialize identically; `9007199254740993` remains exact; null differs from zero; `1.2300m` emits `"1.23"`; duplicate conflicting identities and unknown fields fail. `DependencyBoundaryTests` asserts Audit references neither Data/Api/Ingest nor production files and contains no HTTP/database packages.
- [ ] Run `dotnet run --project tests/AiObservatory.Audit.Tests -- --filter-class '*CanonicalJsonTests'`; require a red assertion or missing implementation, not an unrelated restore failure. Project creation needed to host a red test is part of this task.
- [ ] Implement strict DTO validation/canonicalization with checked arithmetic and ordinal stable sorting. Schema/verifier identity is explicit input, never inferred from current time.
- [ ] Run `dotnet run --project tests/AiObservatory.Audit.Tests`; require all tests passing, including architectural boundaries.
- [ ] Commit only Task 1 files with message `feat(audit): add independent versioned evidence contracts`.

## Task 2: Independent Claude/Codex accounting and daily attribution

**Files:** Create `Sources/{ISourceAuditAdapter,ClaudeAuditAdapter,CodexAuditAdapter,AuditCapabilities}.cs`, `Checks/SourceAccountingCheck.cs`, `Checks/DailyAttributionCheck.cs`; tests `Sources/{ClaudeAuditAdapterTests,CodexAuditAdapterTests}.cs`; synthetic fixtures under `tests/AiObservatory.Audit.Tests/Fixtures/sources/`.

**Interfaces:** `ISourceAuditAdapter.Reconstruct(IReadOnlyList<SourceUsageRecord> records, AuditScope scope): SourceReconstruction`; `SourceReconstruction` contains production-compatible groups, chronological groups, coverage, and findings. `SourceAccountingCheck.Evaluate(AuditInput input): CheckOutcome`; same signature for `DailyAttributionCheck`.

- [ ] Write hand-calculated assertions: Claude repeated message copies dedupe globally, richest copy chosen deterministically without summing TTL subsets; source output and reasoning inclusion rules remain distinct. Codex counters input=100/cache=60/output=10 become uncached=40/read=60/output=10; a later total 180/100/30 adds 80 input-inclusive and 20 output, not 180 again. Crossing midnight preserves compatibility total on the last day and separate observed increments; missing baseline/closure, resets and uncertain model switches return incomplete findings. Parent/subagent identities count unique usage only.
- [ ] Run `dotnet run --project tests/AiObservatory.Audit.Tests -- --filter-namespace 'AiObservatory.Audit.Tests.Sources'`; confirm the new assertions fail.
- [ ] Implement independent adapters from source contract descriptions and fixtures. Declare all eight local producers in capabilities; Claude/Codex supported, six others unsupported with reason codes. Stable richness ties use sequence/opaque identity, not input order. No match for empty/missing history without affirmative coverage evidence.
- [ ] Run the full Audit test project; require the source fixtures, overflow tests and reordered-input checks to pass.
- [ ] Commit with message `feat(audit): independently reconcile Claude and Codex usage`.

## Task 3: Event aggregation, verdicts and presentation reference totals

**Files:** Create `Checks/{EventAggregationCheck,PresentationTotalsCheck,AuditVerdictReducer}.cs`, `AuditVerifier.cs`; tests `Checks/{EventAggregationCheckTests,PresentationTotalsCheckTests,VerdictTests}.cs`; create `Fixtures/dashboard-summary.v1.json` for cross-language tests.

**Interfaces:** `AuditVerifier.Verify(AuditInput input): AuditResult`; `EventAggregationCheck.Evaluate(AuditInput input): CheckOutcome`; `PresentationTotalsCheck.Evaluate(AuditInput input): CheckOutcome`. Presentation expected buckets are billed native/GBP ledger totals, list-price USD, provider-estimated USD, notional USD, unknown counts, cache savings, and unclassified cost.

- [ ] Write tests for full grouping `(UTC date,provider,model-or-empty,sourceId,sourceKind,usageScope,costBasis)`, zero/tombstone events still contributing request counts, null token sum behavior, known zero `none`, unknown money/savings counts, negative cache savings, corrected events, zero-only aggregates. Deliberately add one token or duplicate one event; assert `mismatched` and exact delta. Require mismatch > incomplete/unsupported > matched, and reject an empty executed-check set as incomplete. Summary fixture has explicit expected bucket values and missing qualifiers.
- [ ] Run `dotnet run --project tests/AiObservatory.Audit.Tests -- --filter-namespace 'AiObservatory.Audit.Tests.Checks'`; establish red tests.
- [ ] Implement grouping independently of `UsageRepository`, retain every coverage gap, and compare server-wide event/aggregate fields only when their scope is identical. Audit does not claim thought-token aggregate coverage because current aggregate rows have no thought column. Presentation verification without a captured production calculation is incomplete.
- [ ] Run all Audit tests; both intentional corruptions must produce findings and the clean fixtures must match exactly.
- [ ] Commit with message `feat(audit): verify aggregates and explicit coverage verdicts`.

## Task 4: Portable bundles and offline runner

**Files:** Create `Bundles/{AuditBundleManifest,AuditBundleReader,AuditBundleWriter}.cs`; create CLI project and `Program.cs`, `ReplayCommand.cs`; matching CLI test project; `Bundles/BundleTests.cs`; synthetic `tests/fixtures/audit/golden-v1.zip` and expected result JSON.

**Interfaces:** `AuditBundleReader.ReadAsync(Stream source, BundleLimits limits, CancellationToken ct): Task<AuditInput>`; `AuditBundleWriter.WriteAsync(Stream destination, AuditBundle bundle, CancellationToken ct): Task`; CLI `replay <bundle> --output <directory>` with optional `--reevaluate`, `--verifier-version <installed-version>`. Replay output includes canonical result, its SHA-256, and readable report. Exit 0 matched; 2 mismatched; 3 incomplete; 4 invalid/incompatible/execution failure.

- [ ] Write tests asserting two replays of the synthetic golden bundle yield identical canonical bytes and digest; reordered ZIP entries do not affect result. Reject traversal, duplicate names, missing/extra entries, corrupt hashes, decompression bombs, >512 MiB total uncompressed data and incompatible verifier/schema. A bundle's suggested executable/URL is rejected as unknown data. Verify replay runs without network credentials/access.
- [ ] Run `dotnet run --project tests/AiObservatory.Audit.Tests -- --filter-class '*BundleTests'` and the CLI tests; require red for missing behavior.
- [ ] Implement `manifest.json`, `captures/<id>/manifest.json`, chunk NDJSON, `snapshot/*.ndjson`, `pricing/*.json`, `options.json`, and `result.json`. Each entry is declared and hashed. Hash the canonical manifest's sorted entry list excluding the manifest itself for bundle content identity. Never execute/download bundle content. Newer-version reevaluation creates a distinct evaluation identity.
- [ ] Run Audit and CLI test projects and replay the golden fixture through the CLI; compare result bytes against the committed expected JSON.
- [ ] Commit with message `feat(audit): add deterministic portable audit replay`.

## Task 5: Local evidence extraction without conversation content

**Files:** Create `clients/observatory-audit.mjs`, `clients/audit/{protocol,identity,discovery,claude-evidence,codex-evidence,lossless-json}.mjs`, `clients/observatory-audit.test.mjs`; synthetic fixtures in `clients/audit/fixtures/`.

**Interfaces:** `extractClaudeEvidence(text, identityContext): ExtractionResult`, `extractCodexEvidence(text, identityContext): ExtractionResult`; `captureEvidence(request, configuration, dependencies): Promise<CapturePackage>`; result contains typed records plus diagnostics, not normalized totals. CLI `node clients/observatory-audit.mjs capture --request <local-request.json> --output <local-directory>`; polling/upload is added in Task 7.

- [ ] Write Node assertions that duplicate source usage records and source integer lexemes survive extraction, source input/cache subset names remain intact, parent/child IDs share the correct HMAC domain, 9007199254740993 remains exact, and identity generation changes after key loss. Plant conversation text/secrets in arbitrary fields and malformed usage objects; assert they appear nowhere in capture/errors/logs. Stable but unclosed files stay closure-unknown. Changing files retry twice after the initial attempt, then report incomplete. Long sessions include pre-window baseline records. Symlinks/reparse paths outside configured roots are refused.
- [ ] Run `node --test clients/observatory-audit.test.mjs`; require the new tests to fail.
- [ ] Implement audit-only discovery/extractors, HMAC-SHA256 with domain prefixes and machine-local key file permissions, bounded lossless parsing of integer lexemes, strict output allowlist and local manifest. Do not import `observatory-sweep.mjs` or pass source objects through wholesale. Reject unsupported format signatures instead of silently producing zero. Use optional local-only raw-file hashes.
- [ ] Run audit Node tests and `node --test clients/observatory-sweep.test.mjs`; require new coverage and unchanged production collector tests to pass. Verify dependency/import boundaries.
- [ ] Commit with message `feat(audit): capture private usage-only source evidence`.

## Task 6: Audit persistence, leases and validated settings

**Files:** Create Data `Audit/{AuditMachine,AuditMachineCredential,AuditCaptureRequest,AuditEvidenceCapture,AuditEvidenceChunk,AuditRun,AuditServerSnapshot,AuditFindingRecord,AuditRetentionSettings,AuditEntityConfigurations,AuditStore}.cs`; modify DbContext; create `AddAuditEvidence` migration/snapshot; API `Audit/{AuditOptions,AuditOptionsValidator}.cs`; Data tests `Audit/AuditStoreTests.cs` and API options tests.

**Interfaces:** `AuditStore.TryLeaseRunAsync(Guid workerId, Instant now, CancellationToken ct): Task<RunLease?>`, `RenewRunLeaseAsync(RunLease lease, Instant now, CancellationToken ct): Task<bool>`, `TryPublishAsync(RunLease lease, AuditResult result, CancellationToken ct): Task<bool>`. Other store operations are endpoint-oriented transactions with explicit revision/lease predicates, never exposed EF entities.

- [ ] Write PostgreSQL tests for two workers claiming one run, expired lease retry with stale-worker publication rejected, immutable sealed captures, unique request/index/hash chunks, conflicting retry rejection, and atomic seal/count/hash checks. Test settings defaults exactly match Global Constraints, zero/negative durations rejected, null retention means indefinite. Test migration on existing populated usage data leaves it untouched.
- [ ] Run `dotnet run --project tests/AiObservatory.Data.Tests -- --filter-namespace 'AiObservatory.Data.Tests.Audit'`; require red behavior assertions in a disposable test database.
- [ ] Implement entities/configurations/constraints, bytea chunks, sorted hashes, unique idempotency keys, database leases. Use 5-minute leases renewed every minute, 3 execution attempts then failed with diagnostics. Bound outstanding requests to 2 per machine and queued runs to 20 per instance; reject with `429` rather than discard. Configure `Audit:Enabled=false`; validate other settings on enable.
- [ ] Run focused Data/API options tests and build the solution; require zero migration/build warnings. Generate migrations with existing EF tool/configuration using disposable database settings.
- [ ] Commit with message `feat(audit): persist immutable captures and durable run leases`.

## Task 7: Machine-scoped capture protocol and companion polling

**Files:** Create API `Audit/{AuditMachineEndpoints,AuditCaptureEndpoints,AuditMachineKeyFilter,AuditCredentialService,AuditCaptureService,AuditCaptureDtos}.cs`; modify Program route registration; extend `clients/observatory-audit.mjs`, create `clients/audit/{transport,poll}.mjs`; integration `AuditCaptureEndpointsWafTests.cs`; extend Node tests.

**Interfaces:** Implement machine/capture routes in HTTP table. `AuditCredentialService.IssueAsync(Guid machineId, CancellationToken ct): Task<IssuedAuditCredential>` uses 256-bit random secrets and stored SHA-256 digest; fixed-time verification of digest and enabled/revocation state on every request. `AuditCaptureService.SealAsync(Guid requestId, SealCaptureRequest body, MachineLease lease, CancellationToken ct): Task<SealCaptureResult>`.

- [ ] Write tests for admin-only issuance/rotation, secret shown once and never logged, cross-machine claim/upload refusal, immediate revocation during a lease, wrong lease token, replayed chunks, 8 MiB chunk/512 MiB capture/1m record boundaries, unknown fields, and expired request. Node tests assert configured-origin-only transport, blocked redirects, bounded retries and no body/credential logging. Claim every 60 seconds with bounded jitter; a one-shot poll mode supports scheduled invocation.
- [ ] Run `dotnet run --project tests/AiObservatory.Api.IntegrationTests -- --filter-class '*AuditCaptureEndpointsWafTests'` and Node audit tests; require red assertions.
- [ ] Implement privileged provisioning and isolated machine-route mapping. Add `poll --once` and default continuous polling. Capture local configured roots only; reject path/URL/command fields from requests. Claim/seal operations never update normal usage. Upload plaintext canonical NDJSON over TLS except explicit loopback development; server validates records before sealing.
- [ ] Run integration/Node tests; assert machines cannot call `/api/events`, ordinary credentials cannot masquerade as machines, and revoked credentials cannot finish a capture.
- [ ] Commit with message `feat(audit): coordinate scoped machine evidence capture`.

## Task 8: Consistent server exports and pricing provenance

**Files:** Create API `Audit/{AuditSnapshotService,AuditSnapshotProjector}.cs`; Data `Audit/PricingApplication.cs`; modify `Pricing/UsagePriceResolver.cs`, `Pricing/IProviderPriceCalculator.cs`, `Pricing/PricingRepricingService.cs`, provider calculators, `Repositories/UsageRepository.cs`, DbContext/migration; tests `Audit/AuditSnapshotServiceTests.cs`, Data `Pricing/PricingApplicationTests.cs`.

**Interfaces:** `AuditSnapshotService.CreateAsync(Guid runId, CancellationToken ct): Task<Guid>`; `UsagePriceResolver.ResolveWithEvidenceAsync(UsageEvent usage, CancellationToken ct): Task<ResolvedPriceApplication?>`. `ResolvedPriceApplication` includes quote, snapshot ID/hash, resolved rate key/dimensions, pricing-rule version. Existing `ResolveAsync` remains a compatibility wrapper. Freeze `AuditInput` projections within a single repeatable-read transaction.

- [ ] Write integration tests with >10,000 events, concurrent insert/correction/reprice and rollback during snapshot export; snapshot must be a coherent all-before or all-after view with no truncation. Test stripping secret canaries from event payloads, exact full aggregate grouping, frozen pricing/FX/source statuses, and 512 MiB failure without a sealed partial snapshot. Test cost and application provenance commit/rollback together, zero-cost quotes without catalog marked rule-derived, explicit correction/source costs superseding catalog provenance, and old events remaining unassociated.
- [ ] Run focused snapshot/provenance tests; require red assertions before changing production pricing paths.
- [ ] Implement streaming projection/sealing without long-running verifier work in the transaction. Resolve calculators to return rate identity alongside quote; all write paths persist application provenance atomically with cost. Use immutable application rows linked to an event's current pricing revision, and append-only historical rows; revisions digest pricing-relevant event fields. Project only allowlisted dimensions from `RawPayload`. Include frozen financial observations/spend/FX needed by selected checks; mark unqualified billing checks unsupported.
- [ ] Run focused snapshot tests plus existing repository, pricing/repricing and provider-calculator tests; verify no existing fallback, correction preservation, lock or zero-usage behavior regresses.
- [ ] Commit with message `feat(audit): freeze consistent server evidence and pricing applications`.

## Task 9: Durable orchestration, readiness and freshness

**Files:** Create API `Audit/{AuditWorkerService,AuditRunService,AuditReadinessService,AuditRunEndpoints,AuditRunDtos,AuditCompatibilityEvaluator}.cs`; modify Program; tests API unit `Audit/AuditWorkerTests.cs`, integration `AuditRunsEndpointsWafTests.cs`.

**Interfaces:** `AuditReadinessService.GetAsync(AuditScope scope, CancellationToken ct): Task<AuditReadinessDto>`; `AuditRunService.CreateAsync(CreateAuditRunRequest request, string idempotencyKey, CancellationToken ct): Task<Guid>`; `AuditWorkerService.ExecuteOneAsync(Guid workerId, CancellationToken ct): Task<bool>`. Run transitions: queued → collectingEvidence → snapshotting → verifying → completed; cancelled/failed terminal; explicit partial proceed goes from collectingEvidence to snapshotting.

- [ ] Write tests for idempotent create and conflict, offline-machine wait/deadline, mixed ready/missing sources, partial proceed, failed capture, stale prices, absent sweep-revision proof, active sessions, and unsupported formats. Cancel during snapshot/verification and expire a lease before publish; assert no matched result is committed. Replaying uses pinned captures/snapshot, never latest rows/catalogs. An entirely unsupported selection cannot match.
- [ ] Run focused worker/run integration tests; require red assertions.
- [ ] Implement worker using Task 6 leases and Task 8 snapshots, persisted per-stage progress and stable diagnostic codes. Known capture/server revision skew makes affected checks incomplete. Where revision is unknown but historical totals match, retain the spec's limited compatibility claim and a visible revision-unverified qualification. Never interpret a mismatched live capture as a proven accounting defect without temporal alignment evidence. Capability/readiness are observational GETs.
- [ ] Run worker/run tests including process-restart simulation and same-run concurrent claims; require successful resumption without duplicate publication or production writes.
- [ ] Commit with message `feat(audit): orchestrate durable scoped audit runs`.

## Task 10: Independent pricing and server report/export endpoints

**Files:** Create Audit `Checks/{PricingArithmeticCheck,BillingReconciliationCheck}.cs`, `Pricing/{FrozenRateResolver,OpenAiAuditPricing,ClaudeAuditPricing}.cs`; API `Audit/{AuditReportService,AuditExportEndpoints,AuditEvidenceEndpoints,AuditViewerProjection,AuditPresentationProbeEndpoints}.cs`; Audit pricing tests and integration `AuditExportAuthorizationWafTests.cs`.

**Interfaces:** `PricingArithmeticCheck.Evaluate(AuditInput input): CheckOutcome`; `BillingReconciliationCheck.Evaluate(AuditInput input): CheckOutcome`; `AuditReportService.WriteBundleAsync(Guid runId, Stream destination, CancellationToken ct): Task`. Implement findings, evidence, report, replay and bundle routes in the HTTP table with stable pagination.

- [ ] Write hand calculations for OpenAI standard/cached/context/processing rates and Claude base/cache-read/5m/1h/service-tier rates, current notional versus historical observed pricing, unknown dimensions, stale source, catalog fallback and manual corrections. Assert missing old provenance is incomplete for historical-rate verification even when a declared-catalog calculation matches. Corrupt one rate and one TTL subset; exact findings must identify the line. Unsupported provider formulas and all unqualified billing feeds remain unsupported. Test read-only export/detail refusal with normal viewer summaries permitted, including when a request carries a viewer key rather than an available browser Entra identity. Test presentation probe input/hash validation, linked evaluation immutability, altered production summary detection and offline replay of a retained probe.
- [ ] Run Audit pricing tests and export authorization integration tests; require red assertions.
- [ ] Implement independent formulas and explicit equation traces. Ordinary findings projection removes record identities and private evidence references; admin detail supplies them. Stream authorized bundles from sealed retained datasets, verify hashes before export, and refuse revoked/expired evidence. Historical verification needs frozen first-party evidence as well as valid catalog provenance; do not infer authenticity from a hash. A presentation-probe submission copies retained inputs into a linked evaluation with the newly frozen probe and reruns only against those inputs; validate snapshot and summary input digests and clearly retain the frontend's claimed build identity.
- [ ] Run full Audit tests and focused API integration tests; repeat export/replay to prove canonical result equality and verify that no vendor/network fetch occurs.
- [ ] Commit with message `feat(audit): verify frozen prices and expose authorized reports`.

## Task 11: Dashboard readiness, runs and discrepancy investigation

**Files:** Create Web `src/pages/{SelfAuditPage.tsx,SelfAuditPage.test.tsx}`, `src/components/audit/{AuditReadiness,AuditRunWizard,AuditRunHistory,AuditRunDetails,AuditFindingsTable,AuditCoverage,AuditMachineSetup}.tsx` and adjacent tests; `src/api/{auditClient,auditQueries,auditTypes}.ts`; modify existing `api/client.ts`, `pages/Dashboard.tsx`, dashboard tests and `lib/costSummary.test.ts`.

**Interfaces:** `SelfAuditPage(): React.JSX.Element`; typed client methods mirror the HTTP table; `useAuditReadiness(scope)`, `useAuditRuns(cursor)`, `useAuditRun(id)`, create/cancel/proceed/replay mutations. Export existing authenticated transport through `requestApi(path, init): Promise<Response>`; preserve current URL-viewer-key > Entra > configured-key precedence. `AuditToken = string`; decimal values remain strings in audit UI.

- [ ] Write tests for seven completed UTC-day default, inclusive 31-day boundary, selected sources/checks retained, offline waiting and partial acknowledgement, unsupported coverage, stable pagination, keyboard navigation, cancelling/progress, precise >2^53 counts, expected/actual/delta/currency/basis rendering, expired replay, report/export errors, one-time machine key display. Read-only share link hides mutations/exports even with an Entra session. Feature-disabled tab is absent; demo synthetic label persists. Execute shared presentation fixture through production `summarizeCosts` and compare all frozen expected buckets.
- [ ] Run `npm --prefix src/AiObservatory.Web test -- src/pages/SelfAuditPage.test.tsx src/lib/costSummary.test.ts`; require new behavioral assertions to fail.
- [ ] Implement lazy-loaded tab and focused components using existing design tokens and accessible controls. Poll active runs every 2 seconds while visible, stop on terminal state and pause hidden tabs. Admin action `Verify displayed totals` invokes the existing production summary function on frozen API-shaped data, captures its build identity and exact input digest, and submits the presentation-probe route; navigate to the linked evaluation. It must not certify presentation without that probe. Viewer-only pages show retained outcomes but cannot submit probes. Node-free frontend never downloads/executes verifier binaries or parses raw transcripts. Probe coverage is limited to arithmetic representable by production number-based functions; unsafe integers yield incomplete presentation coverage instead of rounded success.
- [ ] Run focused component/client tests and `npm --prefix src/AiObservatory.Web run gate`; require unchanged coverage floors, formatting, lint and build success. Qualify the actual browser flow at Task 14.
- [ ] Commit with message `feat(web): add dashboard self-audit workflow and evidence drilldown`.

## Task 12: Retention, deletion and replay availability

**Files:** Create Data `Audit/AuditRetentionStore.cs`; API `Audit/{AuditRetentionService,AuditRetentionEndpoints}.cs`; Web `components/audit/{AuditRetentionPanel,AuditEvidenceDeletionDialog}.tsx`; corresponding Data/API/UI tests.

**Interfaces:** `AuditRetentionService.ExpireAsync(Instant now, CancellationToken ct): Task<ExpiryCounts>`; `GetDeletionImpactAsync(Guid captureId, CancellationToken ct): Task<DeletionImpact>`; `DeleteCaptureAsync(Guid captureId, string expectedImpactDigest, CancellationToken ct): Task<DeleteResult>`. Implement retention/deletion routes above.

- [ ] Write clock-controlled tests for 90/365-day exact boundaries, null indefinite, retained capture reuse not extending expiry, minimum dependency expiry, shared capture deletion invalidating all affected runs, stale impact digest `412`, and no deletion of usage/spend rows. Race deletion with worker publication/export; assert no matched result is published with vanished evidence and replay becomes `410`. Expired details leave no record IDs/cost traces in summaries/logs. Delete run removes only unreferenced chunks; abandoned uploads expire after 24 hours.
- [ ] Run focused retention Data/API/UI tests; require red assertions.
- [ ] Implement transactional tombstone/revision checks and deletion under the same evidence lock used when publishing results. Exports hold a bounded read lease; explicit deletion invalidates new exports immediately, cancels active exports and waits for their lease release before removing chunks. Worker publishes only if evidence revision is still present. Hourly bounded cleanup removes expired details and unreferenced artifacts. UI explains affected runs and backup/download limits before explicit confirmation; no hidden retention pins.
- [ ] Run retention and run orchestration tests together and Web gate; verify normal data row counts unchanged and replay loss visible immediately.
- [ ] Commit with message `feat(audit): manage evidence retention and explicit deletion`.

## Task 13: Public CI and offline release artifacts

**Files:** Modify `.github/workflows/ci.yml`; create `.github/workflows/audit-release.yml`, `scripts/audit-replay-smoke.mjs`, `tests/audit-release-contract.test.mjs`; docs `docs/self-audit.md`; modify README, clients README, docs content index as appropriate.

**Interfaces:** `audit-replay-smoke.mjs` runs the explicitly built CLI on the committed synthetic bundle twice, compares canonical result/digest and exits nonzero on drift. Release workflow runs on published release plus manual dispatch, publishes runner artifacts for win-x64/linux-x64/osx-arm64, checksum manifest, source commit, schema and verifier identities.

- [ ] Write release-contract tests asserting replay is part of public CI, release artifacts include version/digest/source metadata, no production credentials/private paths are referenced, and no script downloads executables named in bundle content. Include feature-flag, companion registration, scoped key rotation/revocation, retention and replay examples in documentation assertions only where externally required contracts warrant them.
- [ ] Run `node --test tests/audit-release-contract.test.mjs`; require red for missing workflow/commands.
- [ ] Add pinned-action workflows following existing CI policy; run Audit/CLI tests via solution and Node audit tests explicitly. Publish framework-dependent offline runner archives with a documented .NET 10 runtime requirement, not a promise of zero runtime dependencies. Do not trigger deployment/release to test workflow definitions.
- [ ] Run `node scripts/audit-replay-smoke.mjs`, release-contract tests and repository workflow lint; verify golden replay without provider credentials and inspect output artifacts for synthetic-only content.
- [ ] Commit with message `ci(audit): qualify offline replay and publish verifier artifacts`.

## Task 14: Full qualification and installation audit

**Files:** Create `docs/self-audit-qualification.md` and synthetic fixtures for any newly discovered edge cases; update tests owning actual defects. Private evidence location is selected outside the repository and excluded from all public artifact collection.

**Interfaces:** Uses the shipped dashboard/companion/runner, without bespoke production-parser imports. Public report states commit, verifier/schema versions, source coverage, comparison cutoff, findings and limits; private file/session identifiers remain private.

- [ ] Run Release solution restore/build/tests, Node collector and audit tests, frontend gate, repository contracts and workflow checks. Use disposable PostgreSQL databases and the existing fixture setup; never point migrations/tests at production. Record exact commands and counts after execution, not predicted counts.
- [ ] Exercise synthetic dashboard flow in a browser: admin setup, companion claim, capture, readiness, run, mismatch drilldown, cancellation, partial evidence, read-only share view, export, offline replay, retention and deletion. Inspect keyboard/focus behavior and narrow/wide layouts. Fix failures with regression tests in owning tasks.
- [ ] Create a private frozen backup/copy of surviving source history and a read-only database snapshot/export before qualification. Record origin/capture times and digests privately. Choose completed days and explicitly flag open cumulative sessions; preserve source evidence before any collector reconciliation can alter it.
- [ ] Run through the dashboard against those private copies; compare every independent check, investigate discrepancies without repairing originals, export and replay offline twice. Deliberately corrupt copies of tokens, rate, duplicated event, and unknown-count fields; verify distinct stable findings and restore the clean bundle. Production writes require a separate operational action and are not part of this qualification.
- [ ] Document scoped outcomes separately for pipeline agreement, independent parser accounting, daily attribution, catalog arithmetic, published-rate verification, and historical completeness. Keep unsupported source/billing entries visible. Assert no matched verdict represents unverified missing history. Commit only sanitized qualification findings and synthetic regression fixtures with message `docs(audit): record reproducible self-audit qualification`.

## Review and release completion

At the end of each task, inspect its diff, verify imports and protocol names against Tasks 1/5/8, run its stated checks and commit. Native execution receives a fresh whole-branch review after Task 14; subagent-driven execution receives task reviews plus whole-branch review. Use the review/model skills applicable at execution time, without silently changing the selected method.

Before calling this feature ready, reconcile every approved spec section against Tasks 1–14. In particular, UI completion, privacy validation, stale pricing, exact integer handling, durable failures, cleanup/export races and offline artifact availability are release requirements. No task in this plan deploys the feature or turns it on for a production installation.

## Plan self-review

- Scope and contracts: all spec areas map to tasks; the six unqualified local adapters and unqualified billing feeds remain explicitly unsupported as permitted by the initial-release boundary.
- Independence: Audit has no production references; Node extraction duplicates no numerical parser imports; presentation deliberately exercises production calculations only as the actual value, not the reference value.
- Consistency: contracts, stable identities, DTO routes, provenance, limits and retention defaults are specified once and referenced by tasks. All checks consume `AuditInput` and return `CheckOutcome`; orchestration alone deals with persistence/time.
- Review Focus: each of the five conditions above has a named owning task and explicit regression scenario.
- Reproducibility: deterministic results exclude operational metadata; content/ZIP hashes differ; matching runtime/version availability is a stated prerequisite, not an implicit download.
- Qualification: the plan calls for tests and private live-evidence verification but does not claim they have happened.
