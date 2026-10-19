# Local producers

Local producers read the session files that AI command-line tools leave on a developer machine and post token usage to Observatory. This page covers how to run the sweeper script, what it captures from each tool, how it avoids double counting, and what its figures can and cannot mean.

## What a local producer is

A provider API can tell you what an organisation used. It cannot see a personal subscription, a coding agent running on a laptop, or a model reached through a flat plan. A local producer fills that gap by reading the tool's own files.

The repository ships one: `clients/observatory-sweep.mjs`. It has no dependencies beyond Node.js 24 or later (it uses the built-in `fetch` and SQLite support). One run:

1. reads the session files of every enabled tool,
2. builds one cumulative snapshot per tool, model and day,
3. posts each snapshot to `POST /api/events`,
4. compares what it holds with what the server already has and zeroes anything that has disappeared locally.

Everything it posts is local telemetry: subscription notional evidence, a list-price estimate or a provider estimate. It is never an invoice. The source, scope and basis it attaches are described in [Concepts](/docs/concepts). Costs are not computed by the script: for most tools it posts `costUsd: null` and the server rates the tokens. See [Pricing](/docs/pricing).

## Run it

Set the API key and, if the API is not at the default address, its URL. Then run the script.

```powershell
$env:OBSERVATORY_API_KEY = '<observatory-api-key>'
node clients/observatory-sweep.mjs
```

To preview without posting, add `--dry-run --verbose`:

```powershell
node clients/observatory-sweep.mjs --dry-run --verbose
```

- The key must be the admin key. The script sends it as the `X-Observatory-Key` header; the read-only key cannot post. See [Authentication](/docs/authentication).
- With no `OBSERVATORY_API_KEY` the script prints `OBSERVATORY_API_KEY not set; nothing to do.` and exits with code 0.
- A dry run posts nothing, reads nothing from the server and does not write the state file. It needs `--verbose` to print the snapshots it would post.
- Progress messages are printed only with `--verbose`. Warnings and `UNPLACED` lines are always printed to standard error.
- Running again is safe. Re-posting an unchanged snapshot is a no-op on the server.

### Schedule it

Run the same command on a schedule, for example every 15 minutes, with cron, Windows Task Scheduler or whatever you already use. Give the scheduled process the same environment variables.

### Environment

| Variable                    | Default                                   | Purpose                                                                                                                                                                                                                                                                                             |
| --------------------------- | ----------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OBSERVATORY_API_KEY`       | none (required)                           | Admin key, sent as `X-Observatory-Key`.                                                                                                                                                                                                                                                             |
| `OBSERVATORY_URL`           | `http://localhost:5039`                   | API origin. It must be an absolute `http` or `https` URL with no credentials, query or fragment. Plain `http` is accepted only for loopback (`localhost`, `127.0.0.1`, `[::1]`). With the Compose stack, `http://localhost:4173` reaches the API through the frontend proxy. Redirects are refused. |
| `OBSERVATORY_STATE`         | `~/.ai-observatory/sweep-state.json`      | The parse cache and migration marker. Safe to delete.                                                                                                                                                                                                                                               |
| `OBSERVATORY_LOCAL_SOURCES` | `codex,copilot,claude,gemini,antigravity` | Comma-separated list of tools to collect. An unknown name stops the run with an error. `kimi`, `grok` and `pi` are off unless named.                                                                                                                                                                |
| `OBSERVATORY_MACHINE`       | the operating system host name            | Suffix that separates machines; see [Several machines](#several-machines).                                                                                                                                                                                                                          |
| `CODEX_HOME`                | `~/.codex`                                | Codex home directory.                                                                                                                                                                                                                                                                               |
| `COPILOT_HOME`              | `~/.copilot`                              | Copilot home directory.                                                                                                                                                                                                                                                                             |
| `CLAUDE_HOME`               | `~/.claude`                               | Claude home directory.                                                                                                                                                                                                                                                                              |
| `KIMI_HOME`                 | `~/.kimi-code`                            | Kimi home directory.                                                                                                                                                                                                                                                                                |
| `GEMINI_HOME`               | `~/.gemini`                               | Gemini home. Used for both Gemini review and Antigravity.                                                                                                                                                                                                                                           |
| `GROK_HOME`                 | `~/.grok`                                 | Grok home directory.                                                                                                                                                                                                                                                                                |
| `PI_HOME`                   | `~/.pi/agent`                             | PI home directory.                                                                                                                                                                                                                                                                                  |

The same variables are listed with the other settings in [Configuration](/docs/configuration).

> **Warning.** If `claude-code-usage-api` already covers the same account (see [Providers](/docs/providers)), leave `claude` out of `OBSERVATORY_LOCAL_SOURCES`. The two lanes do not deduplicate against each other and the activity would be counted twice.

> **Note.** A machine that leaves `OBSERVATORY_LOCAL_SOURCES` unset collects only the five default tools. If it used to collect `kimi`, `grok` or `pi`, name them explicitly to keep collecting.

## What each tool contributes

Each tool has a fixed source id of the form `<name>-local@<machine>` (Gemini review is `gemini-review-local@<machine>`).

| Tool name               | Reads                                                                                                | Provider, scope and basis                                                 | Notes                                                                                                                     |
| ----------------------- | ---------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| `codex`                 | `<CODEX_HOME>/sessions`, any `.jsonl`; the last cumulative `token_count` of each session             | Chosen per session; see [Codex provider routing](#codex-provider-routing) | Each session is filed on the day of its last `token_count`; sessions are then summed into one snapshot per model and day. |
| `copilot`               | `<COPILOT_HOME>/session-state/**/events.jsonl`; the last `session.shutdown` event's per-model totals | OpenAI, Anthropic or Copilot by model name; subscription; notional        | A session that has no `session.shutdown` event (still running, or ended abnormally) contributes nothing yet.              |
| `claude`                | `<CLAUDE_HOME>/projects`, any `.jsonl`; assistant messages with usage                                | Anthropic; subscription; notional                                         | Deduplicated by message id across all files; see below.                                                                   |
| `kimi` (off by default) | `<KIMI_HOME>/sessions/**/wire.jsonl`; `usage.record` rows only                                       | Moonshot; subscription; notional                                          | Mirrored `step.end` rows are ignored.                                                                                     |
| `gemini`                | `<GEMINI_HOME>/tmp/gem-review-*/chats/session-*.jsonl`; Gemini response token counts                 | Google; API; list-price estimate                                          | Only the review wrapper's API-key sessions are read. Other Gemini history is not inferred.                                |
| `antigravity`           | `<GEMINI_HOME>/antigravity-cli/conversations/*.db` and the matching transcript                       | Google; subscription; notional                                            | Needs the transcript at `brain/<id>/.system_generated/logs/transcript.jsonl`; without it the conversation is skipped.     |
| `grok` (off by default) | `<GROK_HOME>/sessions/**/usage.json`; per-turn model usage                                           | xAI; subscription; provider estimated                                     | Posts the cost the CLI recorded.                                                                                          |
| `pi` (off by default)   | `<PI_HOME>/sessions`, any `.jsonl`; assistant usage on `meta/` models                                | Meta; subscription; provider estimated                                    | Posts the cost PI recorded.                                                                                               |

### Token conventions

The script converts every tool to Observatory's convention, in which `inputTokens` excludes cache reads:

- Codex, Copilot, Gemini review and Grok report an input total that includes cached input. The script subtracts the cached part.
- Grok's output total already contains its reasoning tokens. Reasoning is reported for visibility and is not added to output again.
- Codex reasoning tokens and Copilot reasoning tokens are posted as `thoughtTokens`.
- Claude cache writes are split into five-minute and one-hour parts when the transcript carries the split.

### Codex provider routing

Codex can call several vendors. The script reads the session's `model_provider`:

| `model_provider` | Provider   | Basis and scope                                              |
| ---------------- | ---------- | ------------------------------------------------------------ |
| `openai`         | OpenAI     | notional; subscription                                       |
| `zai`            | Zai        | notional; subscription                                       |
| `deepseek`       | DeepSeek   | list-price estimate; API (pay as you go, not a subscription) |
| anything else    | not placed | The session is not posted.                                   |

When a session names no provider, the model name decides: names starting `glm-` are Zai, `deepseek` is DeepSeek, and `gpt-`, `o1`, `o3`, `o4` and `codex` are OpenAI. A session with no model at all, or none of these, is not placed.

### Copilot model routing

Copilot fronts other vendors' models. The script files a model under OpenAI when its name starts with `gpt-5.6-sol` or `gpt-5.4`, under Anthropic when it starts with `claude-opus-4-5`, `claude-opus-4-8`, `claude-sonnet-5` or `claude-opus-5`, and under Copilot otherwise. A model outside those lists lands under Copilot, which has no price catalog, so its cost reads `Not reported`.

## Reading the numbers

- **Snapshots are cumulative per day.** The posted figure for a tool, model and day is the whole of that day, not a delta. A changed snapshot replaces the previous one on the server; an identical one is a no-op.
- **Session totals are filed on one day.** For Codex, Copilot and Antigravity the whole session's total goes on the day the session ended (Antigravity: the last transcript time). A session that runs across UTC midnight moves its entire total to the later day when it ends. Those tools do not record usage per day.
- **Model switches are not split.** A Codex session that changes model is attributed wholly to the last model it used. An Antigravity conversation is attributed wholly to the last model selected. Antigravity usage rows that do not decode, or that carry implausible values, are skipped; the count is printed with `--verbose`. A conversation whose transcript names no recognised model is filed under the model name `unknown`, which has no price.
- **Event time.** The snapshot's `occurredAtUtc` is the start of its day, moved forward to the latest record time in the group.
- **Zero-usage groups are not posted.**
- **Freshness means the sweeper is running.** Any post for a local source marks it fresh, including a repeat of an unchanged snapshot. A local source goes stale after two days with no post. Fresh therefore says nothing about whether you used the tool. See [Data sources](/docs/data-sources).

## Avoiding double counting and drift

- **Source-scoped keys.** Each snapshot has an event key (`<tool>:<date>:<model>`, for example; Claude adds `:<service tier>:<speed>:<geo>` and Gemini review adds `:<context>`) that is unique within its source. The server identifies a snapshot by source id plus key.
- **Claude message ids.** Claude transcripts repeat messages across files and branches. The script keeps one copy per `message.id`, preferring the copy that carries the most detail, and then the earliest.
- **Corrections for what disappears.** Before posting, the script fetches the server's current snapshots for each enabled source. A key the server holds that the local scan no longer produces is posted again with zero tokens and a tombstone marker, which removes its contribution. Tombstoned keys are not listed again.
- **Corrections are withheld when the evidence is weak.** For a given source the script posts no corrections when any of these is true: its own snapshots did not all post successfully, a file could not be read and has no cached copy, the source's directory is missing or empty (an unmounted home or a mistyped `*_HOME`), or the server's snapshot list could not be fetched. A subset run (`OBSERVATORY_LOCAL_SOURCES=codex`) never touches other sources' keys.
- **The state file is a cache.** It stores parsed results by file path and modification time, and a record that the legacy migration ran. Deleting it forces a full re-read; it never loses server data.

### Several machines

Source ids carry the machine name, so two machines never share a namespace and cannot zero each other's history. The name is lower-cased and every run of characters outside `a-z` and `0-9` becomes one hyphen; an empty result becomes `unknown-machine`. Two host names that reduce to the same slug (`build.example` and `build-example`) would collide. Set `OBSERVATORY_MACHINE` to a unique value on such a host.

Snapshots posted by older versions, before the suffix existed, sit under the bare ids (`codex-local`). On the first run under a suffixed id the script zeroes every key in the matching bare id once and records that in the state file. If a fetch or post fails, it retries on the next run.

### Sessions that cannot be placed

When the script cannot assign a session to a provider, it does not post it. It prints `UNPLACED <tool> model=<model> sessions=<n>` to standard error and still exits with code 0. The number counts parsed usage records, which is one per session for Codex but one per message for Claude. After a complete scan it also posts the counts, never paths or content, to `POST /api/sources/sweep-status`. The server shows the machine as an unavailable source named `sweep-placement@<machine>`, with a message such as `5 sessions unplaced: codex gpt-x (3), codex unknown (2)`. It turns healthy again when a sweep places everything. If the scan was incomplete, the previous report stands.

A session that was posted earlier and can no longer be placed is zeroed on the next fully successful run, and its history leaves the dashboard.

## Measured cost: Grok and PI

Grok and PI are the only producers that post a cost, because both CLIs record what their vendor charged. Those rows carry basis `providerEstimated`, and the server stores the figure rather than rating tokens from a catalog.

- PI reports USD directly.
- The Grok CLI reports `costUsdTicks`, an integer. The script divides it by 10,000,000,000 to get USD. That scale is not documented by the vendor; it was derived by checking one published model's rates against recorded ticks. If a turn priced from published rates stops matching `ticks / 1e10` beyond rounding, stop posting Grok costs.
- PI is read only for model ids that start with `meta/`. Its other vendors arrive through their own lanes, so reading them here would double count.

## Limits and retries

- Each snapshot is one request. The API allows 120 requests a minute per client address by default (`RateLimiting:ApiPermitLimit`), so a first run with a long history can be slowed by `429` responses.
- Each request times out after 10 seconds and is tried up to three times. A `429` or a `5xx` is retried after the `Retry-After` value, capped at 61 seconds; without one a `429` waits 61 seconds and a `5xx` one second.
- A run first fetches the server's snapshot list for each enabled tool: five requests with the default set. Until the legacy migration has completed for a tool, it also fetches that tool's old list once per run.
- Snapshot keys are at most 200 characters; longer keys are rejected by the server.

## Posting your own events

The sweeper is one client of `POST /api/events`. Any script can post usage the same way. A minimal example:

```json
{
  "provider": "OpenAI",
  "model": "gpt-5.4",
  "inputTokens": 1200,
  "outputTokens": 400,
  "costUsd": null,
  "eventKey": "my-tool:2026-08-25:gpt-5.4",
  "occurredAtUtc": "2026-08-25T10:00:00Z",
  "sourceId": "my-tool-local@my-machine",
  "sourceKind": "localTelemetry",
  "usageScope": "subscription",
  "costBasis": "notional",
  "rawPayload": "{\"processing\":\"standard\",\"context\":\"short\",\"region\":\"global\"}"
}
```

This is an example, not a captured event. The server enforces:

- `provider` is one of `Anthropic`, `Copilot`, `Google`, `OpenAI`, `Moonshot`, `Xai`, `Meta`, `Zai`, `DeepSeek` (case-insensitive).
- `sourceId`, `sourceKind`, `usageScope` and `costBasis` are all required, or the request is rejected. `sourceId` is lower-cased and at most 100 characters.
- `costBasis` of `billed` is refused; billed money goes through the spend ledger. `none` with a non-zero `costUsd` is refused.
- For `listPriceEstimate` and `notional`, any `costUsd` you send is ignored and the server rates the tokens. See [Pricing](/docs/pricing).
- Token counts and cost cannot be negative. `cacheWrite1hTokens` cannot exceed `cacheWriteTokens`.
- `rawPayload` must be valid JSON without duplicate property names. It carries the dimensions the price calculators need.
- `eventKey` is at most 200 characters. Without a key the event is append-only and cannot be corrected.
- `occurredAtUtc` and `observedAtUtc` cannot be more than five minutes in the future.
- `runtime` is at most 100 characters; `sessionId` and `agentId` at most 200.

A new event answers `201`. A repeat answers `200` with `duplicate: true`, or `corrected: true` when the key replaced earlier values. The full route list is in [API](/docs/api).

## References

- `clients/observatory-sweep.mjs`, `clients/README.md`
- `src/AiObservatory.Api/Endpoints/EventsEndpoints.cs`, `SweepStatusEndpoints.cs`
- `src/AiObservatory.Data/Repositories/UsageRepository.cs` (local telemetry marks its source fresh with a one-day expected interval)
- `src/AiObservatory.Api/Program.cs` (the `api` rate-limit policy)
