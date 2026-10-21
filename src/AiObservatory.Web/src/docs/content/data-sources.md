# Data sources

The Data sources panel at the foot of the Overview shows whether each data source is collecting. This page explains the panel, the six states a source can be in, how freshness is decided, and what to do for each state.

## What the panel tells you

The panel reports collection health: whether Observatory is receiving data from each source it knows about. It does not tell you that usage is missing. The panel says so itself: `Collection health for optional APIs and local telemetry. This does not indicate missing subscription usage.` A source can be fresh and still report little, because you used little.

The panel is collapsed by default and remembers whether you open it. The header shows a summary such as `5 reporting · 14 not connected · 1 needs attention`. While the status loads it reads `Checking collection status`. If the request fails it reads `Couldn’t load collection status`, and the body repeats `Couldn’t load collection status — try refreshing.`

### The summary counts

| Part                  | Counts sources whose state is                                  |
| --------------------- | -------------------------------------------------------------- |
| `N reporting`         | Fresh                                                          |
| `N not connected`     | Not connected                                                  |
| `N need(s) attention` | Stale, Failing or Unavailable. Omitted when the count is zero. |

A source in the `Configured` state is in none of the three counts.

## The rows

Each source is one row.

| Element      | Meaning                                                                                                                                                                                                                         |
| ------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Name         | The source's display name, such as `Organization report` or `Usage API`.                                                                                                                                                        |
| Provider     | The provider the source belongs to, for sources that have one.                                                                                                                                                                  |
| State badge  | One of the six states below.                                                                                                                                                                                                    |
| Last success | How long ago the last successful collection was, then the date and time, for example `3 hours ago · 12 Oct 2026, 14:05`. The time is shown in the Europe/London time zone. `Not reported` means the source has never succeeded. |
| Failures     | `N failure(s)`: the number of consecutive failed attempts since the last success.                                                                                                                                               |
| `Setup` link | Shown only for sources in the `Not connected` state. It opens the provider setup guide.                                                                                                                                         |
| `Error`      | An expandable line with the last error message, when there is one.                                                                                                                                                              |

### Which sources are listed

The list is the union of two sets:

1. **The built-in list.** Sources the dashboard knows about are always listed, in a fixed order: Anthropic, Copilot, Google, OpenAI, Moonshot, xAI and Meta sources, then `Repository activity`, `GitHub billing` and `Portal spend feed`. If the API holds no state for one of them, the row reads `Not connected`.
2. **Any other source the API reports.** These come after the built-in list, sorted by display name. A source id the dashboard does not recognise is shown as a tidied version of the id.

The built-in names are:

| Provider  | Sources                                                                                |
| --------- | -------------------------------------------------------------------------------------- |
| Anthropic | `Messages usage`, `Cost report`, `Claude Code usage`, `Claude local`, `Claude pricing` |
| Copilot   | `Organization report`, `Copilot local`                                                 |
| Google    | `Cloud Billing export`, `Cloud catalog`                                                |
| OpenAI    | `Usage API`, `Costs API`, `Codex local`, `OpenAI pricing`                              |
| Moonshot  | `Kimi local`, `Kimi pricing`                                                           |
| xAI       | `Grok local`, `xAI pricing`                                                            |
| Meta      | `PI local`, `OpenRouter pricing`                                                       |
| None      | `Repository activity`, `GitHub billing`, `Portal spend feed`                           |

z.AI and DeepSeek have bundled price lists and no source row. The Gemini review, Antigravity and sweep placement sources are not in the built-in list, so they appear only as additional rows when the API holds state for them.

What each source provides and needs is in [Providers](/docs/providers).

### Per-machine local sources

Local producers add the machine name to their source id, for example `codex-local@my-machine`, so that machines never overwrite each other. The panel shows this in two places:

- The built-in row (`Codex local`) stands for the tool as a whole. When nothing is posted under the plain id, it takes the state of the machine that reported most recently, so a tool that is posting reads `Fresh` rather than `Not connected`. If data is posted under the plain id, that exact row is used instead.
- Each machine also appears as its own additional row, with a name such as `Codex local@my-machine`, so you can see which machine is stale or failing.

The per-machine rows are detail under the built-in row. The summary counts (`N reporting`, `N not connected`, `N need attention`) leave them out, so one tool is never counted twice. The exception is health: a machine row that is `Failing` or `Unavailable` adds its tool to `need attention` even while the built-in row reads `Fresh`. A machine that is only `Stale` does not. A laptop that has been off shows `Stale` on its own row while the built-in row stays `Fresh` as long as another machine is reporting. See [Local producers](/docs/local-producers).

## The six states

| State         | Badge style | Meaning                                                                            |
| ------------- | ----------- | ---------------------------------------------------------------------------------- |
| Not connected | info        | The source is not configured, or Observatory has no state for it.                  |
| Configured    | info        | The source is configured but has never succeeded.                                  |
| Fresh         | ok          | The source succeeded recently enough.                                              |
| Stale         | warn        | The source worked before but has not succeeded within twice its expected interval. |
| Failing       | bad         | The latest attempt or attempts failed. The failure count is above zero.            |
| Unavailable   | warn        | The source says it cannot supply data.                                             |

A state is decided in a fixed order, and the first rule that matches wins:

1. Not configured: `Not connected`.
2. Marked unavailable: `Unavailable`.
3. One or more consecutive failures: `Failing`.
4. Never succeeded: `Configured`.
5. Last success older than twice the expected interval: `Stale`.
6. Otherwise: `Fresh`.

The order matters. An unconfigured source is never `Failing`, and an unavailable one is never merely `Stale`. The same rules decide the daily digest, so the screen and the alert agree.

### Freshness: the expected interval

A source is stale when its last success is older than **twice** its expected refresh interval. The interval depends on how the source is fed:

| Source type                                                                                                | Expected interval                                               | Stale after                   |
| ---------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------- | ----------------------------- |
| Provider polling sources (Anthropic, Copilot organization, Google billing export, OpenAI, GitHub activity) | `Ingest__PollingIntervalMinutes`, default 60 minutes, minimum 1 | More than twice that interval |
| Pricing sources                                                                                            | 1 day                                                           | More than 2 days              |
| GitHub billing                                                                                             | 1 day                                                           | More than 2 days              |
| Local telemetry (one source per tool and machine)                                                          | 1 day                                                           | More than 2 days              |
| Portal spend feed and sweep placement                                                                      | 1 day                                                           | More than 2 days              |

For local telemetry, any posted event from that source counts as a success, including a duplicate snapshot. A machine that stops sweeping goes stale after two days.

## What each state means and what to do

### Not connected

Observatory has nothing for this source. Either the credential or setting is missing, or, for a local tool, no machine has posted at all.

- Select `Setup` to open the setup guide for the source.
- Some sources are off by default. The Kimi, xAI and OpenRouter pricing sources do not poll unless `PRICING_RETIRED_SOURCES_ENABLED` is `true`, so they normally read `Not connected`.
- The Claude Code usage source needs both an organization Admin API key and `CLAUDE_CODE_USAGE_ENABLED=true`.
- For a local tool, the built-in row reflects the most recent machine. Check the per-machine rows for the others. See [Per-machine local sources](/docs/data-sources#per-machine-local-sources).

You do not need to connect every source. Connect the ones that cover your real usage. See [Providers](/docs/providers).

### Configured

The source is set up, but no collection has finished successfully yet. It holds this state between the first attempt and the first success. If it persists, check the ingest worker logs.

### Fresh

The source is collecting on schedule. No action is needed.

### Stale

The source worked in the past but nothing has arrived within twice its interval.

- For a provider polling source, check that the ingest worker is running. A stopped worker leaves every polling source stale together.
- For a local source, run the sweep on that machine. Stale local producers are routine when a laptop has been off. They do not trigger the daily digest.
- For a pricing source, a stale state means the daily refresh has not succeeded. The last catalog snapshot keeps rating usage.

### Failing

The last attempt or attempts failed. Expand `Error` for the message. The failure count counts consecutive failures and returns to zero on the next success. Errors are shortened to 500 characters, and URL query strings are removed from them.

A partly failed cycle also counts as a failure. If some repositories fail to ingest, the GitHub activity source reads `Failing` until every repository succeeds, so that the missed window is fetched again.

### Unavailable

The source has said it cannot supply data. Examples from the code:

- Anthropic reports that an Admin report is unavailable for the organization.
- The GitHub API rate limit is exhausted.
- The configured GitHub organization returned no non-archived repositories, or repositories could not be listed.
- A local sweep found sessions it could not place with a provider (`sweep-placement@<machine>`).
- The portal spend feed reported skipped expenses.

Unavailable is distinct from failing on purpose. A source that polls nothing must never read as healthy, so conditions that would otherwise yield an empty successful cycle are reported as unavailable. Read the `Error` text for the cause.

## The daily digest

Besides the panel, the API worker sends a daily source-health digest when something is wrong. It is sent at most once per UTC day, and only if at least one source is `Unavailable` or `Failing`. Stale sources are listed in the digest when one is sent, but never trigger it, because a local producer that has not swept is routine.

The digest goes out through the same email and Slack channels as budget alerts. It is silent if neither channel can deliver, and a stored recipient alone is not a channel. See [Reporting](/docs/reporting).

## The status API

The panel reads `GET /api/sources/status`. For every source it returns the id, the state, whether it is configured, the last attempt, last success and latest observation times, the consecutive failure count and the last error. See [API](/docs/api).

## References

- `src/AiObservatory.Web/src/components/SourceStatusPanel.tsx` for the panel.
- `src/AiObservatory.Web/src/config/providers.ts` for the built-in source list.
- `src/AiObservatory.Api/Services/SourceHealthClassifier.cs` for the state rules.
- `src/AiObservatory.Api/Services/SourceHealthDigest.cs` for the digest.
- `src/AiObservatory.Ingest/ProviderPollingWorkerService.cs` for how polling records success, failure and unavailability.
- `docs/provider-setup.md` for what each source needs.
