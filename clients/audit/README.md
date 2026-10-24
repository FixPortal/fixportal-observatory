# Local usage evidence companion

Requires Node 24+. This separate companion captures allowlisted usage facts for offline audits and can poll the machine-scoped capture API. It imports no collector, normalization, or daily accounting code.

## Bounded selected capture

The explicit one-shot entry point is `node clients/observatory-audit.mjs selected-capture --config <private-config.json>`. It requires a separately authorized exact request, complete startup server binding and independently verified protected filesystem namespaces. The command itself neither grants native-root access nor verifies Windows namespace protection. The private pilot supervisor and API-flag helpers are separate preparation tools, not public capabilities of this command.

The server must start with `Audit:Enabled=true`, `Audit:WorkerEnabled=false`, `AuditSelectedInvocation:Enabled=false` and a complete `AuditSelectedInvocation` authority: `RunId`, `InvocationId`, `FullScopeDigest`, `ExpectedRevision` and `ExpiresAtUtc`. The disabled selected-action flag disables audit execution; its immutable startup authority can still bind capture. This command performs capture only. Audit execution, snapshots, export/replay and UI presentation require separate scope and authorization.

Configuration is a closed JSON object. Unknown or missing required properties are refused; duplicate JSON keys are refused. The CLI reads only the explicitly supplied file, with a fixed 1 MiB bootstrap ceiling and 4096-byte config-path ceiling before parsing. The configured `maxMetadataBytes` and `maxPathBytes` may tighten subsequent metadata/path handling.

| Property | Required value |
| --- | --- |
| `origin` | HTTPS origin without path/query/fragment or embedded credentials. `allowLoopbackHttp: true` permits explicit loopback HTTP for synthetic development only. |
| `key` | Issued machine capture credential; the ordinary admin/API key does not authorize capture routes. Keep the file private. |
| `approval` | Closed object with `schemaVersion: 1`, `runId`, `expectedRevision`, `fullScopeDigest`, `requestId`, `machineId`, `requestScopeDigest`, `authorityExpiresAt`; optional `instanceId` pins the server instance. |
| `requestScope` | Exact canonical version-one request subset: `from`, `to`, `sources`, `requestedChecks`, `excludedChecks`, `allowPartial`, `predecessorRunId`, `evaluationOfRunId`. Sources belong to the selected machine. Include null predecessor/evaluation IDs when absent. |
| `identityDirectory` | Explicit absolute path to an already-private, empty directory in an independently verified protected namespace. Existing identity material is refused. |
| `sources` | Exactly one `claude` and one `codex` entry, each with `sourceId`, `tool` and `roots`. Each `roots` array contains exactly one approved absolute root; source IDs match `requestScope`. Roots and identity directory must be distinct paths. |
| `caps` | All fields below, with no defaults or additional fields. |
| Optional | `allowLoopbackHttp` and `includeRawHashes` are booleans. Raw hashes remain local in memory; selected mode writes no retained package. |

Approval GUIDs use lowercase canonical nonzero form; digests use 64 lowercase hex characters. `fullScopeDigest` identifies the complete run scope, including other machines when selected; `requestScopeDigest` identifies only this machine's canonical subset. They can differ. `expectedRevision` is a nonnegative safe integer. The authority expiry must be a valid future UTC timestamp. The exact response tuple, instance, token, subset and expiry are checked before identity creation or source discovery.

Every cap is mandatory and uses a positive safe integer, except `maxDepth`, which permits zero:

| Work | Cap fields |
| --- | --- |
| Discovery | `maxDirectoryEntries`, `maxDepth`, `maxQueuedPaths`, `maxFiles` |
| Reads | `maxFileBytes`, `maxReadBytes`, `maxLines`, `maxLineBytes` |
| Extraction/encoding | `maxRecords`, `maxDiagnostics`, `maxEncodedBytes`, `maxChunkBytes` |
| Transport/time | `maxHttpAttempts`, `maxUploadBytes`, `maxDurationMs` |
| Seal reserves | `sealReserveAttempts`, `sealReserveBytes`, `sealReserveMs` |
| Owned writes/metadata | `maxOwnedWriteBytes`, `maxOwnedEntries`, `maxPathBytes`, `maxMetadataBytes` |

Protocol ceilings remain 10,000 files, 1,000,000 records, 512 MiB per source file and encoded evidence, 1 MiB per line and metadata object, and 8 MiB per chunk. `maxDurationMs` is at most 2,147,483,647. `maxLineBytes` cannot exceed `maxFileBytes`; `maxChunkBytes` cannot exceed `maxEncodedBytes`. Each seal reserve must be smaller than its corresponding total cap. These ceilings are not pilot sizing advice: choose explicit approved caps from bounded source knowledge.

One shared budget covers both roots, retries and renewals. Directory entries are charged before enumeration; queued-path admissions accumulate. `maxReadBytes` charges every requested native read length, including EOF, short-read remainders, growth sentinels and discarded changed-file attempts, without refunds. Records, ordinary diagnostics, encoded bytes, HTTP attempts and upload bytes are admitted before work/allocation. `maxDiagnostics` bounds ordinary diagnostic admissions; terminal `capture_limit` markers can still describe exhaustion after that cap. Those markers remain bounded by the stopped phases and metadata ceiling, rather than by the ordinary diagnostic counter. Owned-write counters charge attempted logical bytes and entries before identity writes; they are not physical disk quotas or an amplification guarantee. Native work withholds the seal time reserve; seal attempts/bytes remain part of the total transport budget.

During extraction, an ordinary diagnostic admission is one `(file, code)` group. Repeated occurrences increment that group's exact count without consuming another admission or allocating repeated code entries; the same code in a later file needs a new admission. Discovery and read-failure charges are unchanged. Line, byte, file, record, encoding, time and lease limits still apply to every operation. Capture summaries merge these counts directly, without expanding occurrence counts back into arrays. Unsupported facts remain reported and the capture remains incomplete; grouping does not validate an unknown format or establish historical closure.

Lease time uses the server's postcommit observation minus the full measured monotonic round trip and a timestamp-rounding allowance. Local wall-clock changes cannot extend that lease or the fixed operation deadline. Renewals keep the same selected request/token and never reset the budget. Whole admitted sessions remain available for baseline reconstruction, including pre-window Codex observations.

The command makes one selected claim, uses selected renewal, uploads to that request and requires exact chunk `204` acknowledgements plus a seal `200` containing the matching capture ID. It has no generic-claim fallback, retained-package recovery, automatic fresh claim, reclaim or rescan. Individual chunk/seal transport attempts may retry only within the same lease and shared caps. Claim and selected renewal each have one attempt. After a failed or uncertain outcome, stop for an explicit decision; do not automatically rerun the command.

| Exit | Meaning |
| --- | --- |
| `0` / `capture_complete` | Matching seal acknowledgement received and local scan completed without diagnostics. This does not certify historical source closure or audit completeness. |
| `3` / `capture_incomplete` | Matching seal acknowledgement received for observed incomplete facts. Counts/diagnostics describe admitted work; unvisited files are not invented. |
| `4` / `audit_capture_failed` | FAILED/UNCERTAIN. A missing or invalid claim/seal reply does not prove rollback or an unsealed server state. No success acknowledgement is claimed. |

On budget exhaustion, native work stops. Available incomplete facts can be sealed only while the exact lease, fixed deadline and counted seal reserves still permit it; otherwise the command stops without a known seal acknowledgement. Hard termination promises no seal. Optional `--supervised-stdin` treats one byte, EOF or pipe error as cooperative stop; it does not create a Windows Job or provide a hard process ceiling. SIGINT/SIGTERM request the same stop.

Before any real use, independently establish protected root/ancestor namespaces and private configuration/identity destinations. Fresh selected mode creates only `identity.json`, uses evidence packages in memory and does not change existing ACLs. Its path checks/fingerprints do not constitute race-proof handle binding. The machine credential retains the existing generic claim/chunk/seal capabilities: the narrower selected client does not narrow that credential grant. Live consent must cover that residual authority and any separately owned process termination.

The concrete handoff must identify the reviewed client/server release and the actual Node 24+ executable, plus any separately qualified supervisor. Keep historical qualification and captured evidence associated with their original release identities. Synthetic composition tests do not authorize native access or establish the identity of a later deployed process.

## Local capture and generic polling

Run `node clients/observatory-audit.mjs capture --request <local-request.json> --output <new-local-directory>`. Existing output directories are refused. Exit codes: 0 capture scan completed, 3 incomplete scan, 4 sanitized failure. A completed scan does not prove source closure or complete historical usage; the verifier evaluates those separately. Empty scans are incomplete.

The local request file contains `{ "request": ..., "configuration": ... }`. `request` has `captureId`, `requestId`, `machineId` GUIDs and the version-one `scope` (`from`, `to`, selected `sources`, requested/excluded checks, `allowPartial`, optional predecessor/evaluation IDs). Only the selected machine's sources may be captured.

`configuration` contains a dedicated `identityDirectory` and `sources`: each entry has a unique `sourceId`, `tool` (`claude` or `codex`), and explicitly configured `roots`. No home-directory discovery occurs. Only `.jsonl` regular files are read; symlinks/junctions and redirected paths are refused. Files changing during reads get two retries after the initial attempt. Whole selected sessions are retained, including observations before the requested dates, for baseline reconstruction.

Optional `limits` may tighten `maxChunkBytes` (8 MiB), `maxCaptureBytes` (512 MiB, also the total source-read budget), `maxRecords` (1,000,000), and `maxFiles` (10,000). JSONL lines are limited to 1 MiB and 64 nesting levels. Malformed JSON, duplicate properties, invalid counters, unsupported shapes, excessive timestamp precision, read failures, and limits produce sanitized diagnostic codes. No counter is silently rounded or clamped.

Output contains `manifest.json`, `chunks/000000.ndjson` etc., and `local.json`. **Only the manifest and exact chunk bytes are the wire package.** `local.json` holds optional local-only raw-file hashes when `includeRawHashes` is true; it must not be uploaded. No raw source file, source pathname, conversation body, key, or configuration is included. CapturePackage exposes `manifest`, `records`, `chunks` (`index`, `sha256`, `ndjson`), `complete` (scan status), and `local` (local-only metadata).

The dedicated identity directory/key is restricted to the current OS user (0700/0600 on POSIX; private DACL on Windows). Losing `identity.json` creates a new generation and HMAC key. IDs include machine/source/generation/domain; session/parent-session IDs share the session domain, message IDs span file copies, and agent/file IDs have separate domains. Conversation-chain `parentUuid` is not misrepresented as a parent session. Only explicit parent-session fields are extracted. Source format qualification on real private histories remains a release task; no history is read by tests.

`extractClaudeEvidence(text, identityContext)` and `extractCodexEvidence(text, identityContext)` return `{ records, diagnostics }` source facts, never normalized totals. `captureEvidence(request, configuration, dependencies)` and `writeCapture(capture, outputDirectory)` provide local capture and persistence. The only capture dependency overrides are `readFile` (fault-injection seam) and `now` (Date-returning clock).

For coordinated capture, an administrator first enables validated `Audit:Enabled` settings and provisions a machine with `POST /api/audits/machines` and `{ "schemaVersion": 1 }`. Create/rotate responses reveal the dedicated machine credential once. Store it in a private local configuration file; the ordinary Observatory API key does not authorize machine routes. A read-only key still forbids provisioning mutations when an authenticated human session is also present. Rotation or `DELETE /api/audits/machines/{id}/credential` revokes the old credential immediately, including active captures. Capture-request creation is reserved for the audit run orchestration layer.

Run `node clients/observatory-audit.mjs poll --config <private-local-config.json>` continuously, or append `--once` for scheduled invocation. The configuration contains `origin` (HTTPS origin only, without a path/query), `key` (issued machine credential), `machineId`, dedicated `stateDirectory`, `identityDirectory`, and the same explicit `sources`/optional `limits` as local capture. `allowLoopbackHttp: true` permits HTTP only for explicit loopback development. No environment credential discovery, server-provided roots, URLs, or commands are accepted.

An idle companion polls every 60 seconds with at most five seconds of jitter; an active capture renews its lease every minute. Requests use only the configured origin, block redirects and log only fixed status codes. Claims have a ten-second timeout; upload timeouts scale with payload size up to 90 seconds per attempt. Network/429/server failures receive at most three attempts within the remaining capture lease and a 270.75-second total budget. Caller cancellation stops retries. One-shot mode exits after its assigned capture (or one idle claim), returning 0 on success and 4 on sanitized failure.

Before uploading, the companion atomically retains the complete original package below `stateDirectory/<requestId>-<leaseToken>/`. A restarted companion reuses those exact bytes for the live token, even if source files changed; damaged retained packages fail closed. Keep the private state directory intact during capture. Expired ownership produces a fresh token and atomically resets the server's unsealed partial chunks and counters. Already sealed evidence remains immutable. State directories have private OS permissions; local retained packages are not automatically pruned by this companion. Only manifest and original chunk bytes are transmitted; `local.json` remains local.

Run `node --test clients/observatory-audit.test.mjs clients/audit-poll.test.mjs` and `dotnet run --project tests/AiObservatory.Audit.Tests -- --filter-class '*NodeCaptureTests'` for synthetic extraction, transport/polling, and Node→.NET canonical/bundle checks.

## Context evidence versions and rollout

### Recognized Claude administrative records

Claude `ai-title`, `agent-name`, `mode`, `permission-mode`, `atis-latch`, `pr-link`, `last-prompt`, `file-history-delta`, `attachment`, `api-request`, `api-request-shape` and `api-request-blob` records are recognized without copying their contents. The producer stores session metadata, context, file backup metadata or request replay material in these records. Request blobs can contain earlier assistant messages; they are not additional usage observations. `last-prompt` does not establish session closure. Unrecognized kinds still produce diagnostics.

This classification was checked against the embedded producer code in Claude Code 2.1.310, executable SHA-256 `f9a3920634ae3b961ee92c2eb58d0051f5f8852b120983d0a4f57b6a5efeba61`: metadata writers at byte offsets 253397026–253399269, request recorder at 253242925, replay writers at 253417594 and 253418058, history writer at 253437564, attachment constructor at 256571657 and rendering helper at 258153295. These are executable offsets, not transcript locations. Recognition changes no emitted usage fields, source versions or closure rules.

### Corroborating Codex usage ledgers

Codex `token_usage_record` is usage-bearing. It is recognized as redundant evidence only when its complete `thread_token_usage` vector exactly matches a complete `event_msg/token_count` cumulative vector in the same file, session epoch, UTC usage day and validated model/provider context, and its `thread_id` matches the validated session metadata. A replacement `session_meta` starts a new epoch even if it repeats the same ID; a rejected replacement cannot retain the old binding. Matching may use a following legacy event because the producer writes the ledger first. A cross-midnight pair remains unmatched. Pending unique vectors share the existing record allocation ceiling with normalized records; deferred findings use the same diagnostic, time and cancellation budgets.

All five supported counters must be present and valid. The producer's sixth counter, `cache_write_input_tokens`, must be absent (the producer defaults it to zero) or explicitly zero; null, nonzero values and unknown token meter keys cannot be corroborated. All three ledger counter vectors and required identifier fields are validated; cached input cannot exceed input, reasoning output cannot exceed output, and total must equal input plus output. Each response counter must be at most its turn counter, which must be at most its thread counter. Ledgers add no accounting records or deltas. Unmatched, partial, foreign-session or contradictory ledgers remain diagnosed; ledger-only usage is still unsupported. Only the existing legacy records supply accounting and attribution. This does not prove closure, historical coverage or source revision.

The classification follows official Codex 0.164.0 source, commit `6aaac694803004a63c6f393d153c96d22521a6b9`: [counter and ledger schema](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/protocol/src/protocol.rs#L2370), [thread total accumulation](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/state/session.rs#L228) and [ledger persistence before legacy emission](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/session/turn.rs#L3202). The [world-state record](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/protocol/src/protocol.rs#L3405) stores a model-visible baseline; it is recognized without copying its contents.

New captures use extractor **1.1.0**, Claude `claude-jsonl` source version **2**, and Codex source version **1**. Manifests advertise those exact emitted versions. Wire schema remains **1**. The independent verifier is **1.1.0**, audit rule **2**. Catalog application rule **1** and the `exact-zero` rule **1** are separate identities and retain their arithmetic conventions.

Claude source version 1 keeps its original reconstruction, including richer-copy selection and grouping. Version 2 independently derives `prompt_le_100000` or `prompt_gt_100000` from uncached input + cache-read input + total creation once, before grouping. Output follows its message's band. The bounded qualified model identities are case-insensitive `claude-haiku-5-5` and calendar-valid `yyyyMMdd` suffixes. Model capacity in `contextTokens` is retained as capacity. It never proves prompt size. Other models receive no derived Context.

A complete message candidate and agreement of every observed prompt counter/model/service/speed/geography across copies are required. Disjoint incomplete copies never make a complete observation. Missing identity, inconsistent subsets or checked integer overflow produce sanitized unknown-context coverage; no cheap band is selected. The extractor emits allowlisted native counters only, never a derived Context claim. The verifier ignores companion-supplied Context. Admitted cross-file v1/v2 copies of the same message are ambiguous: reconstruction omits that message and reports `mixed_source_versions` rather than counting both or upgrading v1 evidence. Different versions at the exact same source/generation/file/sequence position violate the existing wire identity and reject the evidence with `conflicting_identity` before reconstruction; that case yields no coverage result.

Install the version-aware server/verifier before enabling a new collector or catalog candidate. Old binaries reject the new catalog property. A retained capture package resumes with its original sealed manifest and chunk bytes, even when the companion has been upgraded. A resumed server snapshot pinned to an incompatible schema/verifier/rule fails with `incompatible_audit_verifier`; it is never certified under the old identity by the new engine. No production activation or historical repair follows from this source change.

The new CLI rejects a 1.0.0/rule1 bundle by default. Explicit reevaluation uses a new identity and preserves `recorded-result.json` plus a linked `evaluation.json`:

```text
dotnet <installed-cli.dll> replay <old-bundle.zip> --output <new-directory> --reevaluate
node scripts/audit-replay-smoke.mjs <installed-cli.dll>
```

The smoke check preserves both original v1 hashes, checks default rejection and linked reevaluation, then replays the synthetic v2 bundle twice. See [portable fixtures](../../tests/fixtures/audit/README.md) for original-runner reproduction from public source commits and fixed hashes. Do not regenerate v1 results with the new verifier or replace recorded audit evidence.
