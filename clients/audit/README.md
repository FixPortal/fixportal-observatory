# Local usage evidence companion

Requires Node 24+. This separate companion captures allowlisted usage facts for offline audits. It has no polling, upload, collector import, normalization, or daily accounting.

Run `node clients/observatory-audit.mjs capture --request <local-request.json> --output <new-local-directory>`. Existing output directories are refused. Exit codes: 0 capture scan completed, 3 incomplete scan, 4 sanitized failure. A completed scan does not prove source closure or complete historical usage; the verifier evaluates those separately. Empty scans are incomplete.

The local request file contains `{ "request": ..., "configuration": ... }`. `request` has `captureId`, `requestId`, `machineId` GUIDs and the version-one `scope` (`from`, `to`, selected `sources`, requested/excluded checks, `allowPartial`, optional predecessor/evaluation IDs). Only the selected machine's sources may be captured.

`configuration` contains a dedicated `identityDirectory` and `sources`: each entry has a unique `sourceId`, `tool` (`claude` or `codex`), and explicitly configured `roots`. No home-directory discovery occurs. Only `.jsonl` regular files are read; symlinks/junctions and redirected paths are refused. Files changing during reads get two retries after the initial attempt. Whole selected sessions are retained, including observations before the requested dates, for baseline reconstruction.

Optional `limits` may tighten `maxChunkBytes` (8 MiB), `maxCaptureBytes` (512 MiB, also the total source-read budget), `maxRecords` (1,000,000), and `maxFiles` (10,000). JSONL lines are limited to 1 MiB and 64 nesting levels. Malformed JSON, duplicate properties, invalid counters, unsupported shapes, excessive timestamp precision, read failures, and limits produce sanitized diagnostic codes. No counter is silently rounded or clamped.

Output contains `manifest.json`, `chunks/000000.ndjson` etc., and `local.json`. **Only the manifest and exact chunk bytes are the wire package.** `local.json` holds optional local-only raw-file hashes when `includeRawHashes` is true; it must not be uploaded. No raw source file, source pathname, conversation body, key, or configuration is included. CapturePackage exposes `manifest`, `records`, `chunks` (`index`, `sha256`, `ndjson`), `complete` (scan status), and `local` (local-only metadata).

The dedicated identity directory/key is restricted to the current OS user (0700/0600 on POSIX; private DACL on Windows). Losing `identity.json` creates a new generation and HMAC key. IDs include machine/source/generation/domain; session/parent-session IDs share the session domain, message IDs span file copies, and agent/file IDs have separate domains. Conversation-chain `parentUuid` is not misrepresented as a parent session. Only explicit parent-session fields are extracted. Source format qualification on real private histories remains a release task; no history is read by tests.

`extractClaudeEvidence(text, identityContext)` and `extractCodexEvidence(text, identityContext)` return `{ records, diagnostics }` source facts, never normalized totals. `captureEvidence(request, configuration, dependencies)` and `writeCapture(capture, outputDirectory)` provide local capture and persistence. The only capture dependency overrides are `readFile` (fault-injection seam) and `now` (Date-returning clock).

Run `node --test clients/observatory-audit.test.mjs` and `dotnet run --project tests/AiObservatory.Audit.Tests -- --filter-class '*NodeCaptureTests'` for synthetic extraction and Node→.NET canonical/bundle checks.
