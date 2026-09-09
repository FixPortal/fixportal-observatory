# Local usage producers

> Machine-local Codex, Copilot, Claude, Kimi, Gemini review, and Antigravity telemetry guide as of 2026-08-26.

`observatory-sweep.mjs` reads installed CLI logs and posts cumulative daily/model snapshots to `POST /api/events`. It has no dependencies beyond Node 24+.

Each posted snapshot carries explicit provenance so the API preserves its subscription/notional meaning:

```json
{
  "provider": "OpenAI",
  "model": "codex",
  "inputTokens": 1200,
  "outputTokens": 400,
  "cacheReadTokens": 150,
  "cacheWriteTokens": 80,
  "cacheWrite1hTokens": 0,
  "thoughtTokens": 120,
  "costUsd": null,
  "eventKey": "codex:2026-08-25:codex",
  "occurredAtUtc": "2026-08-25T10:00:00.000Z",
  "sourceId": "codex-local@my-machine",
  "sourceKind": "localTelemetry",
  "usageScope": "subscription",
  "costBasis": "notional",
  "observedAtUtc": "2026-08-25T10:00:00Z",
  "runtime": "codex",
  "rawPayload": "{\"source\":\"observatory-sweep\",\"tool\":\"codex\",\"thinking_tokens\":120,\"processing\":\"standard\",\"context\":\"short\",\"region\":\"global\"}"
}
```

## What it reads

| Tool | Local path and fact | Meaning and safeguard |
| --- | --- | --- |
| Codex | `~/.codex/sessions/**/rollout-*.jsonl`; final cumulative `token_count` | `codex-local` / subscription / notional; final cumulative value wins |
| Copilot | `~/.copilot/session-state/**/events.jsonl`; final `session.shutdown` per-model totals | `copilot-local` / subscription / notional; final cumulative totals win |
| Claude | `~/.claude/projects/**/*.jsonl`; assistant usage | `claude-local` / subscription / notional; global `message.id` dedupe retains the richest copy |
| Kimi | `~/.kimi-code/sessions/**/wire.jsonl`; `usage.record` only | `kimi-local` / subscription / notional; mirrored `step.end` rows do not count; turn and session scopes count |
| Gemini review | `~/.gemini/tmp/gem-review-*/chats/session-*.jsonl`; response usage | `gemini-review-local` / API / list-price estimate; only the review wrapper's API-key-isolated sessions are included |
| Antigravity | `~/.gemini/antigravity-cli/conversations/*.db` plus matching transcript | `antigravity-local` / subscription / notional; SQLite step totals are attributed to the transcript's selected model |

Stable source-scoped keys make resubmission safe. Source ids carry a per-machine suffix (`codex-local@<host>`, from `OBSERVATORY_MACHINE` or the OS hostname) so sweeps on different machines never share a namespace and can never tombstone each other's history. Before posting, the sweeper reads server inventory for the enabled sources only, and emits zero corrections for removed snapshots of a source only when that source's own current snapshots all posted successfully or its clean scan verified an empty local history under a populated discovery root — a subset run (`OBSERVATORY_LOCAL_SOURCES=codex`), a failed replacement, or a missing or entry-less home (unmounted, or a mistyped `*_HOME` override) leaves every other key untouched. Its state file holds the parse cache and the legacy-migration marker; losing it causes a safe full rescan and one repeated (idempotent) legacy migration, not a loss of server truth. Until the rescan completes, a source with an unreadable file withholds its corrections and tombstones.

> [!NOTE]
> Snapshots posted before the per-machine suffix was introduced sit under the un-suffixed ids (`codex-local` etc.). On its first run under a suffixed id, the sweeper fetches each enabled tool's legacy namespace and tombstones every key in it, recording completion in the state file so it happens once; a failed fetch or post leaves the marker unset and the migration retries on the next run.

> [!WARNING]
> Set `OBSERVATORY_LOCAL_SOURCES` without `claude` when `claude-code-usage-api` covers the same account/activity. The two lanes do not cross-deduplicate.

## Run

Set `OBSERVATORY_API_KEY` to `<observatory-api-key>` and, when needed, `OBSERVATORY_URL` to the Observatory API origin, then run:

```powershell
node clients/observatory-sweep.mjs
```

Preview without posting:

```powershell
node clients/observatory-sweep.mjs --dry-run --verbose
```

## Environment

| Variable | Default | Purpose |
| --- | --- | --- |
| `OBSERVATORY_API_KEY` | Required | Sent as `X-Observatory-Key`; absent means no post. |
| `OBSERVATORY_URL` | `http://localhost:5039` | API origin; HTTPS is required except for loopback development. Use `http://localhost:4173` for Compose's frontend proxy. |
| `OBSERVATORY_STATE` | `~/.ai-observatory/sweep-state.json` | Safe-to-delete parse cache. |
| `OBSERVATORY_LOCAL_SOURCES` | `codex,copilot,claude,kimi,gemini,antigravity` | Comma-separated collector allowlist. |
| `OBSERVATORY_MACHINE` | OS hostname | Per-machine source-id suffix (`<tool>-local@<machine>`); slugified to lowercase `[a-z0-9-]`. Set it when the hostname is unstable or shared. |
| `CODEX_HOME`, `COPILOT_HOME`, `CLAUDE_HOME`, `KIMI_HOME`, `GEMINI_HOME` | Tool homes above | Optional home overrides. |

Schedule the same one-line command with your platform's scheduler. Re-running is safe and idempotent; there is no throttle or separate server-side sweeper state to maintain.

## Schedule

Run the sweep every 15 minutes with cron, Task Scheduler, or the scheduler already used for your developer machine. Give the scheduled process the same `OBSERVATORY_API_KEY`, optional `OBSERVATORY_URL`, and home overrides; preview with `--dry-run --verbose` before enabling posts.

## Test

```powershell
node --test clients/observatory-sweep.test.mjs
```
