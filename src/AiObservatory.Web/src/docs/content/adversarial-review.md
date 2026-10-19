# Adversarial Review

The Adversarial Review tab records the cost, output and quality of multi-reviewer ("panel") code reviews: several independent reviewer models examine the same change, and a judge adjudicates what they raised. This page explains the tab, every figure on it, and the API that fills it.

## What the tab holds

Observatory does not run reviews. External review tooling runs a panel and then reports the result to Observatory over HTTP (see [Recording data](#recording-data)). The tab shows two kinds of record:

- **Run rows.** One row per participant per run: a reviewer, or the judge. Rows that share a run id form one panel run (the UI calls a run a "sweep").
- **Finding rows.** Optional, per run: one row per distinct finding, with who raised it, its severity, its final outcome, and how other participants assessed it. Finding rows are what make the Seats view possible.

The tab is visible in read-only mode. In read-only mode the controls that delete data are hidden (see [Removing a run](#removing-a-run)).

The tab has two views, chosen with the tab buttons at the top: **Overview** and **Seats**.

### Terms used on this tab

| Term        | Meaning                                                                                                      |
| ----------- | ------------------------------------------------------------------------------------------------------------ |
| Reviewer    | A participant with role `reviewer`. Its `reviewer` field is a vendor slug such as `openai`.                  |
| Judge       | A participant with role `judge`. It adjudicates the findings. Judge rows carry no raised or accepted counts. |
| Run (sweep) | All participant rows that share one run id.                                                                  |
| Raised      | Issues a reviewer reported.                                                                                  |
| Accepted    | Raised issues that survived adjudication.                                                                    |
| Seat        | A reviewer model, identified by the `model` string. Seats are compared across runs on the Seats view.        |
| Panel       | The set of reviewer vendors plus the judge in a run.                                                         |

## Overview view

Overview shows two panels: **Stats by reviewer & model** and **Recent runs**.

### Stats by reviewer & model

One row per distinct combination of reviewer, model and role, aggregated over every run recorded. The judge appears as its own row, marked `(judge)` after the reviewer name.

| Column           | How it is derived                                                                                                                                                                                                                                                  |
| ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Reviewer         | The vendor slug, capitalised, with a coloured dot (the judge has its own dot colour).                                                                                                                                                                              |
| Model            | The model string reported with the run.                                                                                                                                                                                                                            |
| Runs             | Number of run rows in the group.                                                                                                                                                                                                                                   |
| Avg cost/run     | Mean cost over the runs whose cost is known. Runs with unknown cost are excluded, not counted as zero.                                                                                                                                                             |
| Avg raised       | Mean issues raised per run, shown rounded to a whole number.                                                                                                                                                                                                       |
| Avg accepted     | Mean issues accepted per run, shown rounded to a whole number.                                                                                                                                                                                                     |
| Avg cost/finding | Total known cost divided by total accepted findings across the known-cost runs: a ratio of sums, not an average of each run's own ratio, so a one-finding run does not weigh the same as a fifty-finding run. Shows `—` when no known-cost run accepted a finding. |
| Avg dur          | Mean review duration in minutes to one decimal place, for example `3.1m`.                                                                                                                                                                                          |

Cost cells follow these rules:

- If every run in the group has unknown cost, the cell reads `Unknown`.
- If some runs are unknown, the figure is followed by `(N unknown excluded)`.
- Costs are shown in US dollars to two decimals using banker's rounding (half to even).
- A cost for the `anthropic` reviewer carries a leading `~` and a tooltip; see [Estimated costs](#estimated-costs).

Controls:

- **Search reviewer or model...** filters rows by a case-insensitive substring of the reviewer or model.
- Click a column header to sort. The table starts sorted by Reviewer ascending. Clicking a different column sorts it descending first; clicking the active column flips direction.
- When sorting by Avg cost/run or Avg cost/finding, rows with no value always sort last, in both directions, rather than reading as the cheapest.

Empty and error states: `Loading review stats...`, `Couldn’t load review stats — try refreshing.`, `No adversarial-review runs recorded yet.`, and `No matching stats found.` when a search matches nothing.

### Recent runs

One collapsible panel per run, newest first by default. Expand a run to see its participant table.

#### Run header

The panel title is the run's summary text if one was supplied, otherwise the recorded date and time in your browser's local time zone. The line under the title shows, in order:

- The recorded date and time (only when a summary is the title) and the repo name, when known.
- `aggregate of N chunks`, when the run was a batched review: a large change split into cohesive chunks, each reviewed by the full panel, with per-participant numbers summed.
- A badge: `complete` or `incomplete`. An incomplete run also shows the reason (see [Completeness and validity](#completeness-and-validity)).
- Totals: `raised N · accepted N · cost · duration`.

How the totals are computed, over all participants in the run:

- **raised** and **accepted**: sums of the participants' counts (the judge contributes zero).
- **cost**: the sum over participants whose cost is known. If every participant's cost is unknown the total reads `Unknown`. If some are unknown the total is followed by `+ N unknown`, which tells you the total is a lower bound.
- **duration**: the sum of every participant's review duration, in whole seconds (for example `188s`). Participants run in parallel in practice, so this is total reviewer time, not wall-clock time.

The run's recorded time is the earliest recorded time among its participants.

#### Participant table

| Column       | Meaning                                                                                                               |
| ------------ | --------------------------------------------------------------------------------------------------------------------- |
| Reviewer     | Vendor slug with a dot. The judge is marked `(judge)`.                                                                |
| Model        | The model reported for that participant.                                                                              |
| Raised       | Issues raised. `—` for the judge.                                                                                     |
| Accepted     | Issues accepted. `—` for the judge.                                                                                   |
| Cost         | The participant's cost, or `Unknown`.                                                                                 |
| Cost/finding | Cost divided by accepted issues. `—` for the judge and when nothing was accepted; `Unknown` when the cost is unknown. |
| Duration     | Review duration in whole seconds.                                                                                     |

Participants are ordered by the panel's vendor order (`anthropic`, `google`, `openai`, `zai`, `deepseek`, then the retired `moonshot`), then any vendor Observatory does not recognise, with the judge last.

#### Run controls

| Control                   | Options                                                                        |
| ------------------------- | ------------------------------------------------------------------------------ |
| Search repo or summary... | Case-insensitive substring match on the repo name or the summary text.         |
| Completeness filter       | All runs, Complete only, Incomplete only.                                      |
| Validity filter           | All runs, Valid only, Invalid only.                                            |
| Sort by                   | Date, Repo, Raised, Accepted, Cost, Duration. The default is Date, descending. |
| Direction button          | Toggles ascending and descending.                                              |

When sorting by Cost, runs whose total is unknown sort last in both directions.

Empty and error states: `Loading runs...`, `Couldn’t load runs — try refreshing.`, `No runs recorded yet.`, and `No matching runs found.`.

#### Completeness and validity

The tab compares each run to a fixed roster of active reviewer vendors: `anthropic`, `google`, `openai`, `zai` and `deepseek`. This roster is written into the web app; it is not read from the server, so it changes only when the app is updated.

- A run is **complete** when it has a reviewer row for every one of the five active vendors and a judge row.
- Otherwise it is **incomplete**, and the reason reads `N of 5 reviewers`, followed by `· no judge` when the judge row is missing. Older runs recorded before a vendor joined the panel therefore read as incomplete.
- Reviewers from a vendor that is not in the active roster (including the retired `moonshot`) are displayed but do not count towards the five.
- A run is **valid** when at least two active-roster reviewer vendors took part. Fewer than two is too thin to carry cross-vendor signal. This is a triage default in the interface, not a stored property.

Use the validity filter to hide thin runs and the completeness filter to find runs missing a vendor or the judge.

#### Removing a run

Outside read-only mode, each expanded run has a **Remove sweep** button. It asks `Remove this sweep and every participant's numbers?` and needs **Confirm remove** (or **Cancel**). Removal deletes every participant row for that run id and all of its finding rows. It cannot be undone. A failure reads `Couldn’t remove the sweep — try again.`

#### Estimated costs

Costs for the `anthropic` reviewer are treated as estimates: they are derived from a combined token count for a subscription model, with no exact per-call billing. Such costs show a leading `~`, and a footer note appears under the tab: `~ putative cost — estimated from a combined token count (subscription model, no exact per-call billing)`. A run total shows `~` only when at least one known-cost participant is an `anthropic` reviewer. Costs for other vendors are shown without `~`.

These review costs are a separate record from the billed, estimated and notional figures on the Overview; see [Concepts](/docs/concepts) for why unlike figures are never totalled together.

#### Unknown cost

A zero cost is not treated as a measurement. When a run is recorded with a cost of zero, or flagged as unknown, Observatory stores it as unknown, with the cost held at zero as a placeholder. Unknown runs are excluded from every average and are shown as `Unknown`, never `0.00`. See [Pricing](/docs/pricing) for the wider treatment of unknown prices.

## Seats view

The Seats view compares reviewer models across runs and answers a different question from Overview: which seat finds things nobody else finds, and at what cost. It needs finding rows; runs recorded without them still count towards run, cost and raised or accepted totals, but cannot contribute unique or corroborated catches.

States: `Loading seat influence...`, `Couldn’t load seat data — try refreshing.`, and `No seat data recorded yet.`

### What a seat is

A seat is identified by the `model` string on the reviewer rows. Only reviewer rows form seats; the judge never does. When one model string contains a `+`, it is treated as a combination of models: the label reads `mixed: <model>` and the table adds the tag `mixed model row`.

### Pivot

The pivot buttons choose the single "influence" measure used on the charts and in the **Selected influence** column.

| Pivot                 | Influence measure                                                                                                     |
| --------------------- | --------------------------------------------------------------------------------------------------------------------- |
| Unique accepted       | Unique accepted findings divided by the seat's detailed runs.                                                         |
| Corroborated accepted | Corroborated accepted findings divided by the seat's detailed runs.                                                   |
| Accepted per run      | All accepted findings (including accepted counts from runs without finding detail) divided by all of the seat's runs. |
| Acceptance rate       | Accepted divided by raised.                                                                                           |

The line beneath the buttons names the active measure (for example `Unique accepted per detailed run`). A **detailed run** is a run that has finding rows. Beside it, `N runs unknown cost` counts reviewer rows with unknown cost across all seats.

### Finding outcomes and the seat counters

Each finding row has a severity (`critical`, `high`, `medium`, `low` or `unknown`) and one outcome: `accepted`, `refuted`, `dropped` or `indeterminate`. Each reviewer that raised it is listed with its model, and other participants may add an assessment with a verdict of `agree`, `false-positive`, `needs-evidence`, `needs-repo` or `abstained`.

For each seat, every finding that one of its reviewers raised is counted once:

| Counter        | Definition                                                                                                                  |
| -------------- | --------------------------------------------------------------------------------------------------------------------------- |
| Raised         | Findings the seat raised, plus raised counts from runs that have no finding detail.                                         |
| Accepted       | Findings with outcome `accepted`, plus accepted counts from runs with no finding detail.                                    |
| Unique         | Accepted findings raised by exactly one reviewer. A unique catch is something no other reviewer raised.                     |
| Corroborated   | Accepted findings raised by more than one reviewer.                                                                         |
| Refuted        | Findings with outcome `refuted`.                                                                                            |
| Dropped        | Findings with outcome `dropped`.                                                                                            |
| Indeterminate  | Findings with outcome `indeterminate`.                                                                                      |
| Peer agreement | Count of `agree` assessments on the seat's findings from participants who are not the seat's own reviewers on that finding. |

Uniqueness is decided by the number of reviewers listed on the finding, not by the number of seats. Two reviewers using the same model count as two raisers, so such a finding is corroborated.

### Derived measures

| Column              | Formula                                                                                                                                                                                                                              |
| ------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Selected influence  | Per the active pivot. Rates show as a percentage to one decimal; the others show to two decimals. Zero when the denominator is zero.                                                                                                 |
| False-positive rate | Refuted divided by (unique + corroborated + refuted). `—` when that denominator is zero. Dropped and indeterminate findings are left out of both sides.                                                                              |
| Acceptance rate     | Accepted divided by raised. `—` when nothing was raised.                                                                                                                                                                             |
| Known cost          | Sum of cost over the seat's runs with known cost. `Unknown` when every run is unknown.                                                                                                                                               |
| Cost / unique       | Known cost of the seat's detailed runs divided by its unique catches. `—` when there are no unique catches or no known cost. Runs without finding detail are excluded from the numerator because they cannot produce unique catches. |
| Unknown-cost runs   | Count of the seat's runs with unknown cost.                                                                                                                                                                                          |
| Counts-only runs    | Count of the seat's runs that have no finding rows. For these runs only the raised and accepted totals are available.                                                                                                                |
| Sample              | `small sample` when the seat has fewer than five runs, otherwise `sufficient`. Treat rates for small samples with caution.                                                                                                           |

The accepted figure mixes two sources (finding rows for detailed runs, run-level counts for counts-only runs), whereas the false-positive rate uses finding rows only, because counts-only runs have no refuted counterpart.

### Charts

Two charts appear above the table. They load on demand.

- **Cost vs `<pivot>`**: a scatter chart with one point per seat. The horizontal axis is known cost per run: the seat's known cost divided by its runs with known cost, in US dollars. The vertical axis is the selected influence. The point's area is the number of runs the seat contributed. A seat with no known-cost run has no horizontal value. Click a point to select that seat.
- **Weekly influence and known cost**: a line chart by ISO week (shown as `YYYY-Www`, calculated from the run's recorded time in UTC). Each seat has a solid line for the weekly influence (left axis) and a dashed line for the weekly known cost (right axis, whole dollars). Weeks in which a seat has no data plot as zero.

### Seat metrics table

One row per seat with every counter and derived measure above, in this order: Seat, Selected influence, Runs, Raised, Accepted, Unique, Corroborated, Peer agreement, Refuted, Dropped, Indeterminate, False-positive rate, Acceptance rate, Known cost, Cost / unique, Unknown-cost runs, Counts-only runs, Sample. Seats are listed alphabetically by label. Click a seat name to select it.

### Findings for the selected seat

The panel **Findings for `<seat>`** lists each finding the seat's reviewers raised, with columns Finding, Run, Severity, Outcome and Co-raisers. Co-raisers are the other reviewers that raised the same finding, shown as `reviewer · model`, or `—` when none did. Rows are ordered by finding id. The note under the table reads `IDs only; finding text stays in the run folder.`: finding text is never sent to Observatory. The table does not show the chunk id, so in a batched run two findings in different chunks can share a finding id.

If no seat has been selected, the first seat in the table is selected.

## Recording data

All review data enters through the HTTP API; nothing is read from the review tooling's disk by the server. See [Authentication](/docs/authentication) for keys and [API](/docs/api) for the full route list.

| Method and route                              | Purpose                                                                    | Access                       |
| --------------------------------------------- | -------------------------------------------------------------------------- | ---------------------------- |
| `POST /api/adversarial-review/runs`           | Record or correct one participant row.                                     | Admin key or signed-in user. |
| `GET /api/adversarial-review/runs`            | List all participant rows, newest first. Optional `runId` filter.          | Read key or above.           |
| `POST /api/adversarial-review/findings`       | Replace the whole finding set of one run.                                  | Admin key or signed-in user. |
| `GET /api/adversarial-review/findings`        | List findings with their raisers and assessments. Optional `runId` filter. | Read key or above.           |
| `GET /api/adversarial-review/stats`           | The grouped figures behind the Stats table.                                | Read key or above.           |
| `DELETE /api/adversarial-review/runs/{runId}` | Remove one run and its findings. `404` when nothing matched.               | Admin key or signed-in user. |
| `DELETE /api/adversarial-review/runs`         | Remove every run and every finding.                                        | Admin key or signed-in user. |

### Run row fields

`POST /api/adversarial-review/runs` takes one participant per call.

| Field                            | Rules                                                                                                                                                                                                                                       |
| -------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `eventType`                      | Part of the contract; not used by the server.                                                                                                                                                                                               |
| `reviewer`                       | Required. Trimmed and lower-cased. A slug: starts with a lower-case letter, then lower-case letters, digits or hyphens; at most 100 characters. Any slug that fits this shape is accepted, so a new reviewer vendor needs no server change. |
| `model`                          | Required, at most 200 characters.                                                                                                                                                                                                           |
| `role`                           | `reviewer` or `judge`.                                                                                                                                                                                                                      |
| `runId`                          | Required, at most 200 characters. Participants of one run share it.                                                                                                                                                                         |
| `repo`                           | Optional, at most 200 characters.                                                                                                                                                                                                           |
| `summary`                        | Optional run name. Trimmed; longer than 80 characters is cut to 80; blank is stored as none.                                                                                                                                                |
| `inputTokens`, `outputTokens`    | Must not be negative.                                                                                                                                                                                                                       |
| `costUsd`                        | Must not be negative.                                                                                                                                                                                                                       |
| `costUnknown`                    | When true, or when `costUsd` is zero, the row is stored as unknown cost with cost zero.                                                                                                                                                     |
| `reviewDurationMs`               | Duration in milliseconds.                                                                                                                                                                                                                   |
| `issuesRaised`, `issuesAccepted` | Must not be negative. A judge row must send zero for both.                                                                                                                                                                                  |
| `chunkCount`                     | Optional; must be positive when supplied.                                                                                                                                                                                                   |

The server stores cost per accepted finding as cost divided by accepted, or none when nothing was accepted or the cost is unknown. Failures return `400` with a short message.

A participant is identified by run id, reviewer and role. Posting the same participant again corrects the row in place and returns `200` with `Updated: true` (a new row returns `201`). The recorded time is preserved, so a corrected run keeps its place in time. Two details of a correction:

- Repo and summary are replaced by what the new request carries, including none.
- An incoming unknown cost never overwrites a known cost. A late placeholder post therefore cannot erase a measured figure; the model and token counts that the cost was measured with are kept alongside it.

### Finding fields

`POST /api/adversarial-review/findings` takes `runId`, an optional `repo`, and a `findings` array. It replaces the run's entire finding set in one transaction, so re-posting after a re-aggregation leaves no stale rows. The run must already exist (`404` otherwise), and a `repo` that disagrees with the run rows is rejected.

| Rule                     | Limit                                                                                                                             |
| ------------------------ | --------------------------------------------------------------------------------------------------------------------------------- |
| Findings per request     | At most 2000.                                                                                                                     |
| `chunkId`, `findingId`   | 1 to 40 characters from letters, digits, `.`, `_`, `-`. The pair must be unique within one request.                               |
| `severity`               | `critical`, `high`, `medium`, `low` or `unknown`.                                                                                 |
| `outcome`                | `accepted`, `refuted`, `dropped` or `indeterminate`.                                                                              |
| `raisedBy`               | At least one `{reviewer, model}`. The same reviewer and model must not repeat; one vendor with two different models is two seats. |
| `assessments`            | Optional `{reviewer, model, verdict}` entries. Verdicts: `agree`, `false-positive`, `needs-evidence`, `needs-repo`, `abstained`.  |
| Participants per finding | `raisedBy` plus `assessments` at most 12.                                                                                         |

Rejection messages are fixed strings; the request body is never echoed back.

## Reading the tab well

- Compare cost per finding only between rows with a similar number of runs and no `unknown excluded` note.
- Prefer unique catches to raw raised counts when judging a seat's value: a seat that raises many findings that others also raise adds corroboration, not coverage.
- Treat the false-positive rate as meaningful only when the sample is `sufficient`.
- An `incomplete` run is not an error. It means the run did not include all five active vendors and a judge.

## References

- `src/AiObservatory.Web/src/components/AdversarialReviewPanel.tsx`
- `src/AiObservatory.Web/src/components/adversarialReviewGrouping.ts`
- `src/AiObservatory.Web/src/components/adversarialReviewSort.ts`
- `src/AiObservatory.Web/src/components/SeatsTab.tsx`
- `src/AiObservatory.Web/src/components/seatInfluence.ts`
- `src/AiObservatory.Api/Endpoints/AdversarialReviewEndpoints.cs`
- `src/AiObservatory.Api/Services/AdversarialReviewService.cs`
- `src/AiObservatory.Api/Services/AdversarialReviewFindingService.cs`
- `src/AiObservatory.Data/Repositories/AdversarialReviewRepository.cs`
