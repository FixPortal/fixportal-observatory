# Dashboard self-audit

> Proposed design for Observatory, 2026-10-21. Usage counts are integers; money retains its currency and cost basis. This document specifies a feature, not completed audit or implementation evidence.

## Intent and agreed direction

Observatory should let its users establish whether reported usage and money reconcile, inspect disagreements, and reproduce the result later. The maintainer wants a first-class dashboard experience and has approved server retention of usage-only evidence, excluding prompts and responses, with configurable retention and deletion. Quality and reproducibility take precedence over delivery speed.

The initial installation is the first real qualification case. Surviving local history can support a scoped reconciliation; missing history cannot support a completeness claim. An earlier comparison reused production collectors and therefore established pipeline agreement, not independent parser correctness. The feature must make that distinction visible.

This is an architectural addition. This written spec requires review before implementation planning. The defaults and interfaces below are proposed design decisions rather than additional user instructions.

## Approach and alternatives

Use a dashboard-backed audit service, a local evidence companion, and a deterministic independent verifier. Retain immutable, usage-only evidence on the server so completed audits remain inspectable and replayable without the originating machine.

A command-only tool is easier to ship but does not meet the desired dashboard experience. A server-only aggregate checker cannot independently examine local sources. Uploading whole transcripts would expose conversation content unnecessarily. The selected approach preserves source usage fields locally before transferring a strictly bounded evidence format.

## User experience

Add a Self-audit tab to the existing dashboard. It contains audit history, evidence readiness, a run wizard, and run details. It follows existing authentication, date controls, accessible tables, loading states, and read-only behavior.

1. Select an inclusive UTC date range, source/machine combinations, and checks. The default is the previous seven completed UTC days; no range is implicitly certified complete.
2. Review readiness: evidence availability, capture freshness, scan failures, active-session uncertainty, supported source formats, pricing availability, and retention expiry. Explicitly identify sources with server data but no independent evidence.
3. Request fresh local capture or use an existing frozen capture. Offline machines remain visibly waiting until an explicit deadline; users can proceed with partial coverage, which cannot produce an overall matched verdict.
4. Run against a frozen server snapshot. Display queued, collecting evidence, snapshotting, verifying, completed, cancelled, or failed separately from the audit verdict.
5. Inspect results by check, source, machine, date, model, and affected evidence record. Show expected, actual, delta, units, and the rule applied. Money comparisons retain basis and currency.
6. Export a portable bundle or report; replay an available bundle; delete retained evidence with an explicit description of what replay capability will be lost.

Do not silently refresh a completed result with live data. A fresh audit creates a new run linked to its predecessor. Show observation time and the selected coverage in every report. A synthetic demo run is labelled synthetic throughout.

## Evidence and trust boundaries

The local companion reads installed CLI sources using an audit-specific extractor. It must not import production parsing, deduplication, attribution, grouping, or pricing code. Shared transport and schema primitives are allowed; shared numerical transformations are not.

Use a positive allowlist of typed source fields: source format/version, record kind, observation timestamps, model/provider identifiers, usage counters, cumulative-versus-incremental semantics, cache-duration breakdowns, pricing dimensions, and source-reported money/currency where present. Preserve individual usage records and the fields needed to choose among duplicate copies, rather than uploading calculated daily totals as the reference truth.

Reject unknown properties at upload. Never transmit transcript lines, conversation text, arbitrary raw payloads, tool arguments/results, environment variables, secrets, or absolute filesystem paths. Model and identifier strings have format and length limits; do not copy arbitrary objects into string fields. Parse failures produce sanitized reason codes and counts, not source content in errors.

Derive opaque session, message, file, and agent identities using a persistent machine-local HMAC key. Preserve equality relationships required for deduplication and parent/child linkage. Include an identity-key generation identifier; key loss creates a new generation and must not be silently treated as comparable identity. Do not expose the secret key to the server. Raw-file hashes remain in an optional local manifest; server/export integrity hashes cover the sanitized evidence it actually retains.

A capture manifest records extractor version, machine/source scope, scan start/end, files discovered/read/skipped/changed, supported format signatures, record counts, coverage dates, and failures. Stat files before and after reading and retry changed files a bounded number of times. A stable file read does not prove the session is closed: use explicit closure evidence where available and otherwise label completion unknown. Missing paths or empty scans do not prove zero consumption.

The independent verifier can reproduce calculations over retained usage fields. It cannot prove that a compromised or faulty extractor preserved every relevant field, that a local source contains all vendor activity, or that locally reported usage equals provider billing. State these limits in the run. Raw transcript verification remains a local qualification option, not a prerequisite for server replay.

## Components and repository integration

- `clients/`: add a separate audit companion and extraction modules beside `observatory-sweep.mjs`. The scheduled companion polls for capture requests and uploads evidence; the browser never reaches into the local filesystem.
- `src/AiObservatory.Audit/`: add an independent, deterministic .NET verifier library. It accepts versioned evidence and comparison snapshots without database or network dependencies. It does not reference production repositories, calculators, or ingest adapters.
- `src/AiObservatory.Api/`: add audit endpoints, capture coordination, snapshot creation, and a durable worker. The worker hosts the verifier and stores results; it does not call the intelligence/LLM service.
- `src/AiObservatory.Data/`: add audit persistence and migrations using existing PostgreSQL/EF conventions. Audit evidence is separate from mutable usage events and never contributes to their totals.
- `src/AiObservatory.Web/`: add the Self-audit page, readiness/run/detail components, and typed query hooks through the existing client.
- Add an offline runner referencing only the audit library. Its release artifact, source commit, supported schemas, dependencies, and integrity digest are published with the corresponding Observatory release.

This design uses the existing database and hosted-worker deployment. It introduces no cloud-specific object store, message broker, or AI dependency.

## Capture coordination and authentication

Dashboard run creation identifies a fixed machine/source/date scope. The companion performs only defined evidence-capture operations; requests cannot contain shell commands, filesystem paths, or arbitrary URLs. It reads its locally configured source roots and sends requests only to its configured Observatory origin, with redirect protections equivalent to the sweeper.

Introduce revocable, machine-bound audit credentials restricted to claiming capture requests and uploading their evidence. Provisioning requires the existing administrator authority. These credentials cannot read other machines' evidence or write normal usage events. Credential values are shown only at creation and stored hashed on the server. Existing sweeper credentials and behavior remain compatible.

Admin users can create/cancel runs, configure retention, and delete evidence. Existing read-only users can inspect audit summaries and findings, but cannot obtain evidence bundles or detailed record-level evidence. Bundle and detailed-evidence access require administrator authority. The public demo exposes only synthetic audit content and disables mutations.

## Consistent server snapshots

Do not build a snapshot by repeatedly calling the existing capped `/api/events` endpoint. A dedicated snapshot service copies all selected events, aggregates, relevant source status, correction markers, and pricing inputs inside one PostgreSQL repeatable-read transaction. Commit the immutable snapshot before computation; never hold the transaction open while waiting for a machine or running checks.

Capture first, then snapshot server data. Ordinary sweeps may have ingested different source revisions between those steps. Pin capture IDs and the server cutoff; compare file/session revision and last-sweep information when available. If freshness cannot be aligned, classify the affected comparison as incomplete with a temporal-skew finding instead of asserting a numerical defect. Matching stable historical data is still useful when the source revision is unrecorded, but its claim is explicitly limited.

For cumulative sessions, collect enough lifetime evidence to derive changes even if the requested window starts mid-session. Preserve the server row identities needed for compatibility grouping and explain contributions crossing the window boundary. A session extending beyond the cutoff is provisional. Audit comparisons never tombstone, ingest, reprice, or repair production rows.

## Independent checks

| Check | Expected evidence and comparison | Limit |
| --- | --- | --- |
| Source accounting | Independently deduplicate and interpret retained source usage records; compare with frozen stored events under the documented collector contract. | Missing source evidence means incomplete, never matched zero. |
| Daily attribution | Compare production-compatible grouping and separately calculate per-day deltas when timestamps/cumulative counters support them. | A compatibility match does not prove the chart represents consumption on that day. |
| Event aggregation | Independently group frozen events by the full production aggregate key and compare every additive field and unknown count with frozen aggregates. Include corrections and zero snapshots. | Independent of transcript coverage; can match while source coverage is incomplete. |
| Pricing arithmetic | Independently apply frozen rates and dimensions to usage; compare stored cost and cache savings. | Retained rate documents do not establish invoice accuracy. Missing dimensions or rate provenance mean incomplete. |
| Presentation totals | Independently derive dashboard summary buckets from frozen API-shaped data and compare against production summary calculations exercised by the frontend contract test/preview. | Arithmetic coverage is separate from browser rendering; release qualification includes browser checks. |
| Billing reconciliation | Compare retained financial evidence and ledger entries within identical source/account/currency/period scopes. | Unsupported or unconfigured feeds are reported explicitly. Notional cost never reconciles to an invoice. |

Token rules distinguish cache subsets from additive counters, reasoning already included in output, cache write TTL subsets, cumulative snapshots from deltas, parent/subagent duplication, and unknown values from zero. Counter resets or missing timestamps produce findings; do not silently clamp them into plausible totals.

Codex lifetime totals assigned to the last usage date are a documented compatibility rule, not a daily consumption guarantee. Keep that view separate from a chronological reconstruction using observed cumulative increments. An increment's timestamp supplies observed attribution, not exact consumption timing between observations. Preserve initial counters and reset boundaries; never invent missing daily splits. Model switches require attributable model evidence; otherwise model-level chronological results are incomplete.

Money uses decimal arithmetic, explicit rounding points, and per-check tolerances derived from stored precision. Tokens require exact equality. Record tolerances in the bundle. Separate billed, provider-estimated, list-price, notional, none, and unknown bases; preserve native currencies and frozen FX inputs. An operator-corrected cost is verified as a correction, not falsely flagged for differing from list price.

## Pricing evidence

Freeze the exact selected catalog records, normalized contents, source URLs, retrieval/observation/effective dates, content digests, selected processing/context/region dimensions, and independent calculation trace. Copy relevant published pricing evidence where available. Keep catalog-arithmetic verification separate from confirmation against a first-party source.

Current usage rows do not persist a pricing snapshot ID, and repricing can change their money. Do not claim an old catalog was used when that association cannot be established. Historical cases without provenance can compare stored figures to a declared frozen catalog, labelled as such; historical-rate verification remains incomplete.

Add pricing-application provenance for future estimated/notional writes and repricing: event ID/revision, catalog ID/hash, resolved rate key/dimensions, pricing-rule version, and application time. Persist it atomically with the corresponding cost update and capture it in audit snapshots. Explicit source costs and manual corrections retain their own provenance. Never manufacture associations during migration.

Failed or stale pricing refreshes are readiness findings. A frozen old rate may support arithmetic replay while current-rate verification remains incomplete. Offline replay never fetches a current catalog or FX rate.

## Verdict and coverage

Each check has `matched`, `mismatched`, `incomplete`, or `unsupported`, plus expected/actual values and evidence references where applicable. Integrity failure or execution failure is a failed run, not a matched or ordinary numerical-mismatch result.

Overall verdict precedence is mismatched if any supported executed check mismatches; otherwise incomplete if any requested check is incomplete or unsupported; otherwise matched. A separate coverage summary always lists supported, executed, and excluded checks and sources, even when an overall mismatch takes precedence. User-excluded checks never disappear from the recorded scope.

Coverage is expressed as observed files/records/dates and explicit gaps. Do not compute a percentage of all consumption without an authoritative denominator. Historical deletion is a known limitation where reported; absence alone cannot identify its cause. Findings use stable rule codes and stable evidence identities so reruns can be compared.

## Persistence and worker behavior

Store `AuditMachine`, `AuditCaptureRequest`, immutable `AuditEvidenceCapture` with bounded chunks, `AuditRun`, immutable `AuditServerSnapshot`, and structured `AuditFinding`/result records. Retention settings and machine credentials are separate mutable configuration. Capture/run manifests reference schema versions and SHA-256 content hashes.

Use database-backed leases and compare-and-set state transitions so multiple API instances cannot execute the same claimed job concurrently. Leases expire and retry safely. A sealed capture or snapshot cannot be modified; corrected uploads create a new capture. Chunk uploads are idempotent by request/chunk identity and hash; conflicting retries are rejected. Seal only after all declared chunks, counts, and hashes verify. Partial uploads never become valid evidence.

Bound uploads and decompression, record counts, string sizes, active capture requests, concurrent runs, and verification work. Initial defaults: 8 MiB uncompressed per chunk, 512 MiB per capture or complete snapshot, 1 million usage records per capture, 31-day maximum run window, one executing run per instance, and a 24-hour capture request deadline. Exceeding limits fails visibly; no truncation. Paginate findings and stream exports. Report expiry, storage exhaustion, lost leases, and unsupported schemas explicitly.

Cancellation stops future processing and preserves completed diagnostic metadata; an incomplete run cannot have a matched verdict. Crash recovery uses durable state rather than assuming an in-memory task completed. No arbitrary code supplied by a bundle is executed on the server.

## Reproducibility and portability

Export a versioned ZIP with a manifest, sanitized evidence chunks, frozen comparison datasets, pricing/FX material, rules/options/tolerances, verifier build identity, and deterministic result JSON. Relative archive entries only; bound extraction and reject traversal, duplicate entries, bad hashes, and unsupported schemas.

Canonical serialization sorts object keys and record collections by declared stable keys, retains integer precision, encodes decimals as strings, uses UTF-8 and UTC timestamps, and distinguishes null from zero. Identical evidence, verifier version, and options produce identical canonical result bytes and result hash. Execution timestamps, run IDs, compression timestamps, and progress logs are outside that deterministic result. Separate the bundle content digest from the physical ZIP byte hash.

Replay uses the explicitly selected, locally installed verifier artifact; it does not automatically download or execute a binary from a bundle. If the required verifier is unavailable, report that the result cannot be reproduced with the installed version. A replay with a newer verifier is a new evaluation linked to the original, not an identical replay claim.

Hashes demonstrate integrity and identity, not independent authenticity. An administrator able to replace evidence and hashes can forge a self-consistent bundle. This is reproducible operational assurance, not external attestation.

## Retention and deletion

Default detailed evidence retention is 90 days from sealing. Default report-summary retention is 365 days from run completion. Admins can configure either duration or explicitly retain indefinitely; show estimated retained size and impending expiry. A run's replay expiry is the earliest expiry of its required evidence/snapshot components. Reusing a capture does not silently extend its lifetime.

Detailed evidence includes capture rows, server snapshot rows, pricing copies, and record-level finding details. Expiry deletes these and leaves only scope, aggregate verdict/counts, completion time, version identifiers, and expiry/deletion metadata until summary expiry. Mark replay unavailable immediately; do not retain sensitive details in error messages or logs.

Explicit deletion lists affected runs and requires confirmation in the UI. It removes the selected evidence even when shared by multiple runs; there are no hidden pins. Summaries identify the resulting loss of replay. Summary deletion removes its stored findings and report metadata. Garbage collection removes unreferenced chunks and abandoned uploads. Deletion does not alter normal usage or spend records.

Document that database backups and user-downloaded bundles have their own retention; online deletion cannot recall them. This feature does not promise secure erasure from third-party backups.

## HTTP contract shape

Use versioned DTOs under `/api/audits` rather than returning EF entities. Provide readiness, paginated run history, run creation/detail/cancellation, paginated findings, snapshot inspection, bundle export, retention configuration, evidence deletion, machine registration/revocation, and capture claim/chunk/seal operations. Mutations use POST/PATCH/DELETE as appropriate; GET has no state changes. Detailed route and DTO definitions belong in the implementation plan.

Run creation and capture sealing accept idempotency keys; repeating an identical operation returns its original identity, while conflicting reuse fails. Explicitly include machine/source scope and evidence IDs rather than selecting the latest evidence during verification. Responses expose coverage, schema/verifier versions, capture IDs, snapshot cutoff, and replay expiry. Export and inspection enforce the authority described above.

## Delivery boundaries

Deliver vertical slices behind an explicit audit feature setting: deterministic engine and bundle format; local capture and server storage; durable runs and snapshot consistency; dashboard readiness/run/details/export; then pricing provenance and remaining adapters. Dashboard support is part of the first usable release, not deferred to a command-only product.

The initial release must support independent Claude and Codex source accounting, event-to-aggregate checks for selected sources, and pricing/presentation checks with honest coverage. Each of the other six local producers has a capability entry and must be labelled unsupported until its independent adapter is qualified. Billing reconciliation is available only for individually qualified feeds. Do not claim full-provider support by passing synthetic fixtures alone.

Exclude automatic repair, automatic production repricing, automatic deletion reconciliation, LLM-generated verdicts, external attestation, and a historical backfill guarantee. Any later repair workflow needs its own reviewable change set and authorization; an audit remains evidence-preserving.

## Validation and acceptance

1. Hand-calculated synthetic fixtures cover additive/cumulative sources, duplicate copies, cache subsets/TTL splits, reasoning subsets, null counters, resets, model changes, midnight boundaries, manual corrections, and cross-machine identities.
2. Independent-adapter tests use synthetic source-shaped records and forbid production numerical/parser imports. Deliberately corrupting a production total, duplicated event, rate, or unknown counter is detected at the appropriate comparison stage.
3. PostgreSQL integration tests prove coherent snapshots during concurrent ingest/correction/repricing, durable claim/recovery, idempotent chunks, sealing, expiry/deletion of shared evidence, complete large exports, and rollback on interrupted snapshot creation.
4. Golden bundles replay offline to byte-identical canonical results. Test reordered inputs, numeric precision, schema incompatibility, corrupt hashes, archive traversal, missing chunks, and unavailable verifier versions.
5. Privacy fixtures plant secrets and conversation text in source records, unknown fields, malformed counters, and error cases; assert that uploads, exports, reports, and logs contain none of them. Missing/filtered usage must still appear in coverage diagnostics.
6. Authorization tests cover administrator, read-only, machine-scoped, revoked, other-machine, and demo access. UI tests and browser qualification cover scope selection, waiting/offline capture, progress, partial coverage, mismatch drilldown, accessible controls, export, and deletion consequences.
7. Qualify on a frozen private copy of the maintainer's surviving history and server snapshot. Publish only sanitized aggregate findings and reproducible synthetic fixtures. Separate agreement, parser independence, daily attribution, price arithmetic, rate verification, and historical completeness in the report.
8. Add the offline replay command and a synthetic golden-bundle replay to public CI. Documentation explains assumptions, capability coverage, retention, backup limits, and how to reproduce a report without vendor credentials or network access.

Success means a dashboard user can explain a reported total, locate a discrepancy, understand missing evidence, export the relevant inputs, and reproduce the same scoped verdict offline. A clean result is never presented as proof of consumption outside the evidence supplied.
