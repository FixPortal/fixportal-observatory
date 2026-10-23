# Independent usage self-audit

> Operator guide as of 2026-10-22. Limits use MiB/GiB; retention uses days from sealing or completion. All examples and public replay fixtures are synthetic.

Self-audit compares retained local usage facts with a frozen Observatory snapshot using an independent verifier. It reports matched, mismatched, incomplete or unsupported coverage for each check. Agreement within declared tolerances does not establish complete source history or invoice accuracy. The feature remains opt-in pending release qualification.

## Enable and register a companion

`Audit__Enabled` defaults to `false`. An administrator can set it to `true` in private API configuration after validating the `Audit` settings. Disabled audit endpoints return 404. Ordinary pricing application provenance is recorded even when audit is disabled. Enabling audit does not reprice existing usage or call providers.

1. Register a machine as administrator with `POST /api/audits/machines` and `{ "schemaVersion": 1 }`.
2. Store the returned machine ID and one-time credential in a private local configuration, outside Git and exported bundles. The credential is scoped to that machine's assigned captures and is separate from the ordinary API key.
3. Install Node 24 or later and configure explicit source roots, a dedicated identity directory and private state directory. The companion performs no home-directory discovery.
4. Start the companion, then create a run from the dashboard's Audit page. Inspect readiness before acknowledging partial captures. The default range is the previous seven completed UTC days; the maximum is 31 days.

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

Claude and Codex have independent extractors. Real private source format qualification remains installation-specific. The other six local sources and unqualified billing feeds are explicitly unsupported. Pricing checks require frozen application/catalog provenance; older rows with missing evidence remain incomplete. Presentation checks require a probe tied to the exact snapshot and frontend identity. A probe is a declared observation, not external attestation.

Viewer keys can read readiness, run summaries, sanitized findings and the readable report (`GET /api/audits/runs/{id}/report`). Admin authority is required for detailed evidence, bundle download (`GET /api/audits/runs/{id}/bundle`), run creation/cancellation/replay, retention changes, deletion and presentation-probe submission. A read-only key remains read-only even with an Entra session. Demo access remains synthetic and read-only.

Each capture is bounded to 512 MiB and one million usage records, with 8 MiB chunks. A complete frozen snapshot is bounded to 512 MiB. One run executes per instance; capture requests expire after 24 hours. Counts retain exact integer precision.

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

The **Audit verifier artifacts** workflow runs on a published GitHub release or manual dispatch and requires default-branch ancestry for the selected commit. It uploads a GitHub Actions artifact containing `win-x64`, `linux-x64` and `osx-arm64` ZIPs, per-archive `SHA256SUMS` sidecars and metadata. It does not create a release or attach release assets. Actions artifacts expire after 90 days; preserve archives separately for long-term replay.

Each ZIP contains the CLI/library DLLs, dependency/runtime configuration, license, this guide and `release.json`. Metadata records the actual source commit, runtime identifier, schema, verifier and rule versions, plus SHA-256 of each payload file. Compare the ZIP with its sidecar before extraction; verify provenance against the trusted workflow/source commit. No production captures or credentials are included.

From an extracted archive:

```sh
dotnet AiObservatory.Audit.Cli.dll replay <private-bundle.zip> --output <new-output-directory> --verifier-version 1.0.0
```

Use a new output directory each time; existing result files are never overwritten. Outputs are canonical `result.json`, `result.sha256` and `report.txt`. Exit codes: 0 matched, 2 mismatched, 3 incomplete, 4 invalid/incompatible/failed. Read check-level coverage even when individual comparisons match.

Normal replay requires compatible installed verifier/rule versions. A newer installed verifier can explicitly reevaluate retained facts:

```sh
dotnet AiObservatory.Audit.Cli.dll replay <private-bundle.zip> --output <new-evaluation-directory> --reevaluate
```

Reevaluation preserves `recorded-result.json` and writes linked `evaluation.json`, creating a new evaluation. Future published semantic changes require a verifier version change. The initial unreleased identity is verifier 1.0.0, schema 1, rules 1.

## Reproduce the synthetic smoke

Build the CLI explicitly, then run the committed invented fixture:

```sh
dotnet build src/AiObservatory.Audit.Cli --configuration Release
```

```sh
node scripts/audit-replay-smoke.mjs
```

The smoke runs the built DLL twice in separate temporary directories and requires exact canonical bytes and SHA-256 `5a194bb9f85af8731a076e0fbea4baad6a57e0f8a32c5c746f3cea90d9a78f5b`. The correct result is **Incomplete**, exit 3: pricing publication evidence and a presentation probe are absent. It needs no provider credentials, server, database or private paths, and qualifies deterministic replay rather than installation source completeness.

The displayed-total action requests `GET /api/audits/runs/{id}/evidence?projection=presentation&limit=200` and drains every page of frozen aggregate and spend rows before calling the dashboard summary and formatter. This projection retains full snapshot, capture, hash and verifier validation; it omits unrelated record kinds from the paginated response. Cursors bind to the run, snapshot, input scope/digest and projection. Unknown projection names and cursors from another projection or run are rejected. Ordinary detailed evidence requests retain all sanitized record kinds and can be expensive for large cohorts because each page validates the complete retained evidence.
