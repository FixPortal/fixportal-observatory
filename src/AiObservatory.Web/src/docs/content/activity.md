# Activity

The Activity tab shows how much active working time was recorded in coding-agent sessions, by day and by project. This page covers what each panel shows, how the figures are computed, how project names are filtered, and how sessions get into Observatory.

## What the tab shows

Activity reads session records, not token usage or cost. A session record holds a project name, a start time, a last-seen time and a count of active seconds. Nothing on this tab is money; for cost, see [Overview](/docs/overview).

The tab has a period control and three panels:

| Panel             | Shows                                                                                                      |
| ----------------- | ---------------------------------------------------------------------------------------------------------- |
| `Active time`     | A bar chart of active minutes per day (or per week), selected period against the comparison period.        |
| `Project share`   | A treemap of the projects that took the most active time in the selected period.                           |
| `Time by project` | A sortable, searchable table of projects with sessions, active time, the comparison period and the change. |

The tab is hidden in read-only mode, and the three routes behind it refuse the read-only key. See [Access](/docs/access#read-only-mode) and [Authentication](/docs/authentication).

## Period and comparison

The period control at the top is the same control the GitHub tab uses. The selection is held inside the tab, so it is not carried to another tab and resets to `31 days` when you come back.

- **Presets.** `7 days`, `31 days`, `90 days`, `This month`, `Last month` and `This quarter`. Day presets end today. `Last month` ends on the last day of last month.
- **Custom dates.** Change `Selected from` or `Selected to` and the preset highlight clears. If you enter a start after the end, the two are swapped.
- **Comparison.** The `Compare` side defaults to `Previous period`. For `7 days`, `31 days`, `90 days` and custom ranges that is the block of the same length that ends the day before your range starts. For `This month` and `Last month` it is the whole calendar month before the range's start month. For `This quarter` it is the three calendar months before the quarter start. Edit `Compare from` or `Compare to` to use any other range; select `Previous period` to go back.

The browser sends dates as plain calendar dates (`yyyy-MM-dd`) taken from your local clock. The API reads them as inclusive UTC days. Near midnight, or in a time zone far from UTC, the days you see can differ from the UTC days the figures are cut on.

## Active time chart

The chart plots wall-clock minutes. Each bar pair is one slot of the selected period and the comparison period, aligned by position, not by date: slot 1 is the first day of each range.

- **Series names.** The selected series is named `Selected period`. The comparison series is named `Previous period`, or `Comparison period` when you chose your own comparison dates.
- **Slots.** The x axis reads `Day 1`, `Day 2`, and so on. When either range is longer than 92 days, slots become weeks and read `Week 1`, `Week 2`, and so on; each week adds seven days of that range.
- **Unit.** The y axis is in minutes. The tooltip formats the same value as hours and minutes.
- **Wall-clock time, not summed session time.** The bar for a day is the union of the time spans of all sessions that day. If you ran three sessions in parallel for the same hour, that hour counts once. The API returns both the per-session sum (`activeSeconds`) and the merged figure (`wallClockSeconds`); this chart plots the merged one.
- **Empty and error states.** `No activity data for either period.` appears when both ranges have no rows. `Couldn’t load activity — try refreshing.` appears when either request fails.

### How a day's figures are computed

For each stored session that ends after it starts, the API clips the session to your range and cuts it at UTC midnights. Each day fragment gets a share of the session's active seconds in proportion to the fragment's share of the session's elapsed time, rounded to whole seconds. The day's wall-clock figure is the length of the union of its fragments.

A session with no elapsed time (its last-seen time equals its start time) contributes nothing to the chart.

## Project share treemap

The treemap sizes each block by active seconds in the selected period.

- It shows the top eight projects by active time. Everything else is folded into one `Other` block with a neutral colour. The `Other` block cannot be selected.
- A block's label shows the project name and its active time when the block is at least 96 pixels wide and 44 pixels tall. Every block also carries a tooltip with the name, the time and the percentage of the period total, to one decimal place.
- Select a block to filter the table to that project. Select it again to clear. Blocks work with the keyboard: focus one and press Enter or Space.
- It uses the selected period only. If the period has no active time, it shows `No activity data for this period.`

## Time by project table

| Column            | Meaning                                                                                                                                                                                                  |
| ----------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Project`         | The project name as stored. Select it to filter to that project.                                                                                                                                         |
| `Sessions`        | The number of distinct session ids for the project in the selected period.                                                                                                                               |
| `Active time`     | The sum of the sessions' active seconds, as hours and minutes.                                                                                                                                           |
| `Previous active` | The same project's active time in the comparison period. A project with none shows `0m`.                                                                                                                 |
| `Change`          | The change in active time from the comparison period: a rounded percentage with a sign (`+12%`, `-30%`), `New` when the comparison period has none and this period has some, and `—` when both are zero. |
| `Share`           | The project's percentage of the period's total active time, rounded to a whole number. The bar is scaled to the longest project in the period, not to 100%.                                              |

A project that appears only in the comparison period is listed with zero sessions and zero active time, so its drop is visible as `-100%`.

Controls above the table:

- `Search projects...` filters by a case-insensitive substring of the project name.
- A chip reading `Filtered: <project> ✕` appears while a project is selected, from the treemap or the table. Select the chip to clear it.
- Select `Project`, `Sessions` or `Active time` to sort. The first select on a new column sorts descending; a second select reverses it. The default is `Active time`, descending.
- `No matching projects found.` appears when the search and filter leave nothing; `No activity data for either period.` appears when both ranges are empty.

### Why the table and the chart can disagree

The two panels cut the period differently.

- The chart takes every session that overlaps the period and prorates it across days.
- The table takes every session that **started** inside the period and counts all of its active seconds.

A session that starts before your range and runs into it is partly in the chart and not in the table. A session that starts on the last day and runs past the end is wholly in the table and only partly in the chart. The chart also plots merged wall-clock time, while the table sums per-session time, so parallel sessions make the table larger.

## Project names and the owner filter

Project names come from whatever the session producer wrote. The design the endpoint was built for resolves a project to `owner/repo` from the working directory's git remote, and falls back to the folder name when the directory is not a git repository. Observatory does not enforce this; it stores the string it is given (up to 200 characters).

The setting `Activity:ProjectOwners` decides which projects the Activity and GitHub tabs show.

- **Empty means show everything.** This is the default.
- **When set**, a project is shown if it equals an owner exactly, or starts with the owner followed by `/`. With `Activity__ProjectOwners=acme`, `acme/web` and `acme` are shown; `acme-labs/web` and `Acme/web` are not.
- **Matching on this tab is case-sensitive.** Use the casing the producer writes. The GitHub tab matches case-insensitively because GitHub names are stored in lower case.
- **How to supply it.** As an indexed array (`Activity__ProjectOwners__0`, `Activity__ProjectOwners__1`) or as one comma, semicolon or newline separated string. In the Compose file the API setting is filled from `OBSERVATORY_PROJECT_OWNERS`.
- **What is dropped.** Each owner is trimmed. An entry is silently discarded if it is empty, contains `/` or whitespace, or is an unresolved Key Vault reference (`@Microsoft.KeyVault(...)`). If every entry is discarded the list is empty, so the filter is off and everything is shown.

The filter is applied when reading. Stored sessions outside the list stay in the database. `DELETE /api/activity/sessions/disallowed-projects` removes them permanently and cannot be undone; with an empty list it removes nothing. See [Configuration](/docs/configuration) and [API](/docs/api).

## How sessions get in

Observatory ships no activity producer. Sessions arrive from a script outside this repository through `POST /api/activity/sessions`, which needs the admin API key. The request holds up to 1,000 sessions, each with `sessionId`, `project`, `startedAtUtc`, `lastSeenAtUtc` and `activeSeconds`.

The API rejects a batch with `400` if any session has:

- a `sessionId` or `project` that is blank or longer than 200 characters,
- a negative `activeSeconds`,
- a `lastSeenAtUtc` earlier than `startedAtUtc`,
- a start or last-seen time more than five minutes in the future,
- `activeSeconds` greater than the elapsed time between start and last-seen,
- a `sessionId` repeated in the same batch.

Sending a session again is safe. A known `sessionId` is updated only if the new `activeSeconds` or last-seen time is larger, and each field keeps the larger of the stored and sent values. The project and start time never change. A concurrent insert of the same id answers `409`; send the batch again.

What counts as "active" is decided by the producer, not by Observatory. The design document for this feature describes counting the gaps between consecutive transcript timestamps and ignoring gaps longer than five minutes; check your own producer for the rule it actually uses.

## Troubleshooting

| Symptom                                                                                             | Likely cause                                                                                                         |
| --------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| Both tabs are empty                                                                                 | No producer has posted sessions, or `Activity:ProjectOwners` excludes them. Check the value and its casing.          |
| `Couldn’t load activity data. It may be unavailable or you may not be authorised — try refreshing.` | The request failed or the key is not the admin key. The read-only key is refused on these routes.                    |
| A project you expect is missing                                                                     | The producer wrote a different name, for example the folder name instead of `owner/repo`. Names are matched exactly. |
| Table total is larger than the chart                                                                | The table sums parallel sessions and counts by start time; see above.                                                |

More symptoms are in [Troubleshooting](/docs/troubleshooting).

## References

- `src/AiObservatory.Web/src/pages/ActivityPage.tsx`
- `src/AiObservatory.Web/src/components/ActivityTrendChart.tsx`, `activityTrendRows.ts`
- `src/AiObservatory.Web/src/components/ProjectTreemap.tsx`, `treemapBlocks.ts`
- `src/AiObservatory.Web/src/components/ProjectBreakdown.tsx`, `projectBreakdownSort.ts`
- `src/AiObservatory.Web/src/lib/dateRange.ts`, `comparisonSeries.ts`
- `src/AiObservatory.Api/Endpoints/ActivityEndpoints.cs`
- `src/AiObservatory.Api/ActivityOptions.cs`
- `docs/superpowers/specs/2026-06-30-claude-activity-dashboard-design.md` (producer design, outside this repository's source tree)
