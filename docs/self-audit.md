# Independent usage self-audit

> Operator guide. Limits use MiB/GiB; retention uses days from sealing or completion. All examples and public replay fixtures are synthetic. For the dashboard workflow, see the [user guide](self-audit-user-guide.md); for subsystem boundaries, see the [architecture guide](self-audit-architecture.md).

Self-audit compares retained local usage facts with a frozen Observatory snapshot using an independent verifier. It reports matched, mismatched, incomplete or unsupported coverage for each check. Agreement within declared tolerances does not establish complete source history or invoice accuracy. The feature is opt-in. Fresh Claude and Codex samples passed prospective acceptance for their selected scope; the historical run remains closed with an Incomplete verdict because source evidence was lost. See [qualification](self-audit-qualification.md) and the [current release boundary](oss-qualification.md).

## Enable and register a companion

`Audit__Enabled` defaults to `false`. An administrator can set it to `true` in private API configuration after validating the `Audit` settings. Disabled audit endpoints return 404. Ordinary pricing application provenance is recorded even when audit is disabled. Enabling audit does not reprice existing usage or call providers.

1. Register a machine as administrator with `POST /api/audits/machines` and `{ "schemaVersion": 1 }`.
2. Store the returned machine ID and one-time credential in a private local configuration, outside Git and exported bundles. The credential is scoped to that machine's assigned captures and is separate from the ordinary API key.
3. Install Node 24 or later and configure explicit source roots, a dedicated identity directory and private state directory. The companion performs no home-directory discovery.
4. Start the companion, then create a run from the dashboard's Self-audit page. Inspect readiness before acknowledging partial captures. The default range is the previous seven completed UTC days; the maximum is 31 days.

For each selected extractor, enter the exact configured `sourceId` in the wizard. A machine-specific source ID can differ from `claude-local` or `codex-local`; the extractor family and the source instance are separate choices.

```sh
node clients/observatory-audit.mjs poll --config <private-local-config.json>
```

Append `--once` for scheduled invocation. Example private configuration:

```json
{
  "origin": "https://observatory.example.invalid",
  "key": "<one-time-machine-credential>",
  "machineId": "11111111-1111-1111-1111-111111111111",
  "identityDirectory": "./private-audit-identity",
  "stateDirectory": "./private-audit-state",
  "sources": [
    { "sourceId": "claude-jsonl", "tool": "claude", "roots": ["./private-claude-source"] },
    { "sourceId": "codex-jsonl", "tool": "codex", "roots": ["./private-codex-source"] }
  ]
}
```

Use configured source IDs consistently in run selections. HTTPS is required; explicit loopback development can opt into `allowLoopbackHttp`. Uploads use `X-Observatory-Audit-Key`, refuse redirects and send only allowlisted manifest/chunk facts. Conversation bodies, tool arguments/results, secrets, environment variables and absolute source paths are excluded. `local.json` must never be uploaded. See the [companion protocol](../clients/audit/README.md) for limits and restart behavior.

Rotate with `POST /api/audits/machines/{id}/rotate` and `{ "schemaVersion": 1 }`, then replace the private local key. Revoke with `DELETE /api/audits/machines/{id}/credential`. Both immediately deny the old credential, including uploads and sealing under active leases. Losing the identity directory creates a new identity generation; preserve it privately when continuity is required.

## Coverage and access

Claude and Codex have independent extractors. Their native files have different counter, session-closure and revision semantics, so a new source needs its own privacy-safe, versioned extractor and independent reconciliation rules. Reusing the ingestion parser would only repeat its mistakes. Real private source format qualification remains installation-specific. The other six local sources and unqualified billing feeds are explicitly unsupported by self-audit even when their usage appears on the dashboard. Pricing checks require frozen application/catalog provenance; older rows with missing evidence remain incomplete. Presentation checks require a probe tied to the exact snapshot and frontend identity. A probe is a declared observation, not external attestation.

Viewer keys can read readiness, run summaries, sanitized findings and the readable report (`GET /api/audits/runs/{id}/report`). Admin authority is required for detailed evidence, bundle download (`GET /api/audits/runs/{id}/bundle`), run creation/cancellation/replay, retention changes, deletion and presentation-probe submission. A read-only key remains read-only even with an Entra session. Demo access remains synthetic and read-only.

Each capture is bounded to 512 MiB and one million usage records, with 8 MiB chunks. A complete frozen snapshot is bounded to 512 MiB. One run executes per instance; capture requests expire after 24 hours. Counts retain exact integer precision.

The bounded selected-run path requires a fresh run with complete capture readiness. It refuses a run with stored `PartialEvidenceAcknowledged=true` using `selected_partial_not_supported`, without linking captures or consuming an attempt. This includes a run queued by ordinary `Proceed`, even if a shared unfinished request later seals and a fresh revision binding is supplied. Scope `AllowPartial=true` remains eligible when no partial acknowledgement was recorded. Selected execution provides no partial fallback; ordinary `Proceed` and worker behavior remain unchanged.

### Optional exact selected invocation

`AuditSelectedInvocation` defaults to disabled and unbound. Its startup fields are `Enabled`, `RunId`, `FullScopeDigest`, `ExpectedRevision`, `InvocationId` and `ExpiresAtUtc`. A complete binding requires nonempty GUIDs, the full canonical 64-character lowercase hexadecimal scope digest, a nonnegative Int64 revision and explicit UTC expiry. Partial bindings fail startup validation even when disabled. Authority is captured once at startup; changing its configuration requires a restart. No binding or activation is supplied by default.

With Audit enabled and a complete binding, strict admin `GET /api/admin/audit-selected-run/binding` returns only exact read-only metadata, at most 32 KiB. It remains available when the selected action is off or its admission expiry has passed. It reports configured authority alongside separate current revision/state/stage and actual linked captures; it does not refresh execution authority. Removing the binding or disabling Audit removes this surface. Viewer keys are denied even with an Entra session, and Development does not permit anonymous access.

`POST /api/admin/audit-selected-run/invoke` additionally requires the startup action enabled, the ordinary audit worker disabled and unexpired admission authority. Its closed UTF-8 JSON body is at most 1024 bytes and contains only `schemaVersion:1`, `expectedRevision` and `fullScopeDigest`. Values must exactly match the configured tuple; integer lexemes are canonical. `Idempotency-Key` must be the configured InvocationId in canonical lowercase GUID form. This header binds correlation; it does not provide response replay. Queries and `If-Match` are rejected. No caller can choose another run, revision, scope or worker.

Both operations share one request per minute for each verified authority, in addition to the global IP limit. GET can consume the subsequent POST slot or delay reconciliation; there is no separate allowance per operation. Responses are `no-store`. Protected bodies, credentials, options, SQL parameter values and raw exceptions are not logged by this transport. Rejected raw URL query text may still be recorded by in-process framework diagnostics, optional request telemetry or reverse proxies. Do not put secrets in URLs. No URL-redaction policy or invocation receipt is supplied.

POST awaits one selected attempt under a single four-minute cancellation budget started before body parsing and linked to caller disconnect and host stopping. Cancellation is cooperative; owned verification and renewal are awaited. Four minutes is not a completion guarantee for a 512 MiB audit or a hard process stop. Disconnect or shutdown can prevent delivery after backend settlement; an infrastructure 500 during shutdown is not a classified transport outcome.

The compact POST envelope is at most 8 KiB and contains status/code, the configured tuple and optional current run metadata, without request/capture arrays. Normal pending (202) and busy (409) mean this call admitted no work. Completed (200) means the observed persisted state is completed, not that the verifier returned Pass. Admitted (200) can report failed, cancelled or nonterminal executing state, including `Attempts=1`; it is not a claim that work continues. Parser failures return rejected with their HTTP 400, 413 or 415 and fixed code; stale binding returns conflict (409). Definite outer conflicts reject before core entry. Once the combined core call begins, every exception returns 503 ambiguous, including familiar store codes and an actual rollback. A rollback may physically leave authority unconsumed; this transport cannot establish that phase from the exception. Failed follow-up reads, exhausted budgets and uncertain serialization are also ambiguous. Never automatically retry, refresh the revision or interpret correlation/current state as historical invocation attribution.

### Reconcile an incomplete selected attempt

The default-off HTTP transport does not authorize enablement or a live invocation, and no CLI command is provided. These recovery steps belong to a separately approved invocation procedure and grant no live access or configuration authority.

1. After an exception, timeout, cancellation or `attempt_finished`, preserve the exact run ID and private binding/request records. The return value does not establish completion or Pass. An uncertain commit may have consumed the attempt even when no execution started. Do not automatically resubmit, change the revision, or create another run.
2. Keep the hosted worker disabled under the approved procedure and await the owned attempt and renewal work. Cancellation, flag-off or lease expiry alone does not prove that backend work stopped. Reconcile through an already authorized read surface. While its binding remains configured, exact selected GET reports current revision and separate state/stage without granting a refreshed attempt. Existing `GET /api/audits/runs/{id}` reports attempts, diagnostics and result metadata, but its `state` contains the stage for executing/collecting runs; it exposes neither revision nor lease expiry. A missing or unavailable summary leaves the outcome unknown. Obtain a separately scoped reconciliation procedure if these observations are insufficient; do not enable audit or query production SQL to fill the gap.
3. A consumed attempt (`Attempts = 1`) cannot be admitted again by this selected path. Lease expiry alone does not terminalize it. If it remains nonterminal and the operator explicitly authorizes abandonment, the existing admin `POST /api/audits/runs/{id}/cancel` workflow can terminalize that exact run. This is cancellation, not resumption. The route has no client revision/If-Match fence. Cancellation changes state, revision, ownership and retention metadata, may initialize retention defaults, and can fence orphan requests and remove their partial chunks; it does not promise evidence erasure or a hard process stop.
4. Reread the same run and preserve the terminal outcome. A concurrent completion or failure may have won; a successful cancellation response alone does not prove the run became cancelled. Failed, missing or ambiguous verification remains unknown and requires scoped reconciliation, not blind retry. Any later attempt needs a separate reviewed decision. Do not reset `Attempts` in SQL, manually requeue, enable the generic worker, replay or clean up as an implicit recovery step.

## Retention, exports and private backups

Administrators can separately enable `OperationalInventory__Enabled` (default `false`) for
`GET /api/admin/operational-inventory`, including while audit is disabled. This endpoint
returns only fixed aggregate metadata in a read-only database transaction: stored retention
policy presence, run and export reservations for the application instance, and database-wide
capture, credential and machine counts. It accepts no query parameters or request body.
Viewer credentials are rejected, including alongside an Entra session; anonymous access is
never enabled by Development mode. Responses use `no-store`, a 32 KiB limit, the existing IP
limit and a separate one-request-per-minute authenticated identity or admin-key-class limit.
Database errors and timeouts report unavailable, rather than zero counts. Counts describe
one transaction snapshot; they do not establish complete history, active credential use,
evidence deletion or future inactivity. Enabling or deploying this endpoint is a separate
operator action. Normal application startup still runs migrations and legacy settings seeding.

Evidence defaults to **90 days** from sealing; report summaries default to **365 days** from completion. `GET /api/audits/retention` and `PATCH /api/audits/retention` require admin authority. A patch such as `{ "evidenceDays": 90, "summaryDays": 365 }` affects future deadlines only. An explicit `null` deliberately selects indefinite retention. Existing deadlines remain fixed.

Deleting or expiring evidence removes replay availability; a retained summary can remain readable. Capture deletion requires `GET /api/audits/captures/{id}/deletion-impact`, followed by `DELETE /api/audits/captures/{id}` with the returned impact digest in `If-Match`; stale impact returns 412. Deletion and cancellation invalidate racing publication/export work. Expired or deleted bundles return 410.

Exports have a hard **two minutes** lifetime and at most **two concurrent exports per instance**. At the defaults, an admitted export cohort is bounded to **512 MiB**, with a **1 GiB** combined temporary export budget. Slow or disconnected downloads terminate and release reservations; retry while evidence is retained.

Download private bundles before replay expiry and keep an independent backup of the ZIP, matching verifier archive, metadata and checksums. Server deletion cannot erase downloaded copies; protect and delete those yourself. A report alone cannot reconstruct evidence. Local companion state packages are not automatically pruned. Self-audit is not an installation-wide database backup or disaster-recovery service.

## Offline replay

Runner archives are framework-dependent and require the matching architecture's **.NET 10** runtime (`Microsoft.NETCore.App`). Node is needed for capture, but not for offline verification. Install prerequisites separately before going offline. No bundle can request executable downloads, provider calls, database access or code execution.

The **Audit verifier artifacts** workflow runs on a published GitHub release or manual dispatch and requires default-branch ancestry for the selected commit. It uploads a GitHub Actions artifact containing `win-x64`, `linux-x64` and `osx-arm64` ZIPs, per-archive `SHA256SUMS` sidecars and metadata. It does not create a release or attach release assets. The v1.2.0 release provides verified copies; for v1.2.1, confirm that matching archives, metadata and sidecars are attached to its release before relying on them. Actions artifacts expire after 90 days. Preserve the matching files for long-term replay.

Each ZIP contains the CLI/library DLLs, dependency/runtime configuration, `LICENSE`, `NOTICE`, this guide and `release.json`. Metadata records the actual source commit, runtime identifier, schema, verifier and rule versions, plus SHA-256 of each payload file. Compare the ZIP with its sidecar before extraction; verify provenance against the trusted workflow/source commit. The product's 1.2.1 version is separate from the verifier's 1.1.0 compatibility identity. No production captures or credentials are included.

From an extracted archive:

```sh
dotnet AiObservatory.Audit.Cli.dll replay <private-bundle.zip> --output <new-output-directory> --verifier-version 1.1.0
```

Use a new output directory each time; existing result files are never overwritten. Outputs are canonical `result.json`, `result.sha256` and `report.txt`. Exit codes: 0 matched, 2 mismatched, 3 incomplete, 4 invalid/incompatible/failed. Read check-level coverage even when individual comparisons match.

Normal replay requires compatible installed verifier/rule versions. A newer installed verifier can explicitly reevaluate retained facts:

```sh
dotnet AiObservatory.Audit.Cli.dll replay <private-bundle.zip> --output <new-evaluation-directory> --reevaluate
```

Reevaluation preserves `recorded-result.json` and writes linked `evaluation.json`, creating a new evaluation. Future published semantic changes require a verifier version change. The current identity is schema 1, verifier 1.1.0, rules 2; the initial unreleased identity was verifier 1.0.0, schema 1, rules 1.

## Reproduce the synthetic smoke

Build the CLI explicitly, then run the committed invented fixture:

```sh
dotnet build src/AiObservatory.Audit.Cli --configuration Release
```

```sh
node scripts/audit-replay-smoke.mjs
```

The smoke checks compatible replay of a current invented bundle (**Matched**, exit 0) and explicit reevaluation of an older invented bundle (**Incomplete**, exit 3) against pinned canonical results and hashes. The older evidence lacks pricing publication evidence and a presentation probe. It needs no provider credentials, server, database or private paths, and qualifies deterministic replay rather than installation source completeness.

The displayed-total action requests `GET /api/audits/runs/{id}/evidence?projection=presentation&limit=200` and drains every page of frozen aggregate and spend rows before calling the dashboard summary and formatter. This projection retains full snapshot, capture, hash and verifier validation; it omits unrelated record kinds from the paginated response. Cursors bind to the run, snapshot, input scope/digest and projection. Unknown projection names and cursors from another projection or run are rejected. Ordinary detailed evidence requests retain all sanitized record kinds and can be expensive for large cohorts because each page validates the complete retained evidence.
