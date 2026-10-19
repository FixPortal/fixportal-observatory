# GitHub

The GitHub tab shows pull requests, reviewer activity, commits and CI results for the repositories Observatory polls. This page covers each panel, how every figure is derived, what the ingest worker fetches and how often, its limits, and the separate daily sync of GitHub billing into the spend ledger.

## What the tab shows

The tab reads repository activity that the ingest worker has copied from the GitHub REST API into the database. It never calls GitHub itself. It is hidden in read-only mode and its routes refuse the read-only key; see [Access](/docs/access#read-only-mode).

From top to bottom:

1. The period control (the same control as the Activity tab: presets, custom dates and a comparison range). See [Activity](/docs/activity#period-and-comparison) for how the ranges and the `Previous period` comparison are chosen.
2. A `Repo` filter and the selected date range.
3. A comparison strip: `Pull requests`, `Commits` and `CI success`.
4. Four panels: `Pull requests`, `Review agents`, `Commits by repo` and `CI health`.

Repository names are shown in lower case. The ingest worker lower-cases every name before it stores it.

## Repo filter

The `Repo` drop-down starts on `All repos`. Its options are the repositories that appear in any of the loaded data for the selected and comparison periods. Choosing one filters all four panels and the comparison strip. If the repository stops appearing after you change the period, the filter falls back to `All repos`.

## Comparison strip

The strip compares the selected period with the comparison period, and ends with `vs previous period` or `vs comparison period`.

| Metric          | Selected value                                                                                                         | Comparison value                          |
| --------------- | ---------------------------------------------------------------------------------------------------------------------- | ----------------------------------------- |
| `Pull requests` | The number of rows in the `Pull requests` panel for the selected period.                                               | The same count for the comparison period. |
| `Commits`       | The sum of commits across repositories in `Commits by repo`.                                                           | The same sum for the comparison period.   |
| `CI success`    | Runs-weighted success percentage across the visible workflows, rounded to a whole number. Zero when there are no runs. | The same for the comparison period.       |

Each metric shows the difference as `+n`, `-n` or `No change`. For `CI success` the difference is in percentage points and carries ` pp`. Reviewers are not compared.

The strip waits for all six underlying requests. While any is loading it shows a placeholder (`Loading GitHub period comparison`). If any fails it shows `Comparison unavailable — the period data couldn’t be loaded, so no deltas are shown.` rather than printing zeros. A separate banner reads `Couldn’t load GitHub activity data. It may be unavailable or you may not be authorised — try refreshing.`

## Pull requests

A pull request is listed when it was created, merged, or first reviewed inside the selected period. A pull request created before the period can therefore appear.

| Column       | Meaning                                                                                                                                |
| ------------ | -------------------------------------------------------------------------------------------------------------------------------------- |
| `Repo`       | Repository, `owner/name` in lower case.                                                                                                |
| `Title`      | `#<number> <title>`. Titles are stored up to 500 characters.                                                                           |
| `Author`     | The GitHub login that opened it.                                                                                                       |
| `State`      | `open`, `closed` or `merged`. GitHub reports only open or closed; Observatory shows `merged` when a merge time is present.             |
| `Created`    | Creation date, in your browser's locale.                                                                                               |
| `Merged`     | Merge date, or `—`.                                                                                                                    |
| `Reviews`    | The number of reviews on the pull request, including ones not yet submitted. Never decreases once recorded.                            |
| `Turnaround` | Hours from creation to the earliest submitted review by anyone, to one decimal place (`4.5h`), or `—` when nothing has been submitted. |

Controls:

- `Search PRs...` filters by a case-insensitive substring of the title or the repository name.
- Sort by `Repo`, `Created`, `Reviews` or `Turnaround`. The default is `Created`, newest first. The first select on a new column sorts descending; a second select reverses it. Pull requests with no turnaround always sort last, in both directions.
- Empty states: `No PR activity for this period.` and `No matching PRs found.`

## Review agents

Despite its title, this panel lists every reviewer, people and agents. A reviewer whose GitHub account type is `Bot` carries an `Agent` badge. The type is taken from GitHub's account type, not guessed from a `[bot]` suffix in the login. A deleted account is listed as `ghost`.

| Column              | Meaning                                                                                                                                 |
| ------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| `Repo`              | Repository. Reviewers are counted per repository, not across the organisation.                                                          |
| `Reviewer`          | Login, with the `Agent` badge where it applies.                                                                                         |
| `PRs`               | Distinct pull requests the reviewer submitted at least one review on, inside the period.                                                |
| `Reviews`           | Submitted reviews inside the period.                                                                                                    |
| `Approved`          | Reviews in state `APPROVED`.                                                                                                            |
| `Changes requested` | Reviews in state `CHANGES_REQUESTED`.                                                                                                   |
| `First review`      | The mean time from a pull request being opened to this reviewer's first review of it, averaged over the pull requests counted in `PRs`. |

Details that change how to read it:

- Only submitted reviews are counted. A pending review has no submission time and is left out.
- Reviews in any other state (for example `COMMENTED` or `DISMISSED`) count toward `Reviews` but not toward `Approved` or `Changes requested`.
- `First review` uses the reviewer's first review of each pull request wherever it falls. A re-review does not change it, and a first review made before the period still sets the clock for later activity in the period.
- A reviewer with no review inside the period is not listed.
- `First review` is formatted for scale: minutes under one hour (`12m`), hours with a decimal under 48 hours (`5.5h`), days with a decimal beyond that (`3.2d`).
- Rows are ordered by `Reviews`, most first; this table has no sort controls.
- Empty state: `No reviews submitted in this period.`

## Commits by repo

| Column    | Meaning                                                   |
| --------- | --------------------------------------------------------- |
| `Repo`    | Repository.                                               |
| `Commits` | Commits whose author date falls inside the period.        |
| `Churn`   | `+<additions> / -<deletions>`, summed over those commits. |

Sort by `Repo` or `Commits`; the default is `Commits`, most first. The empty state is `No commit activity for this period.`

## CI health

One row per repository and workflow name, for workflow runs created inside the period.

| Column             | Meaning                                                                       |
| ------------------ | ----------------------------------------------------------------------------- |
| `Repo`, `Workflow` | Repository and workflow name. A run with no name is grouped as `(unnamed)`.   |
| `Runs`             | All runs created in the period.                                               |
| `Failed`           | Runs whose result is `failure`, `timed_out` or `startup_failure`.             |
| `Success rate`     | Runs with result `success` divided by all runs, as a whole-number percentage. |

The success rate counts every run in the denominator. Cancelled, skipped, queued and in-progress runs are neither successes nor failed, but they lower the rate. A rate under 80% is drawn in the warning colour and carries hidden text for screen readers (`(low success rate)`).

Each run stores its conclusion, or its status while no conclusion exists yet. The stored value is capped at 20 characters.

Sort by `Repo`, `Runs` or `Success rate`. The default is `Success rate`, lowest first, so the weakest workflows lead. A new column also starts ascending here, unlike the other tables. The empty state is `No CI activity for this period.`

## What the ingest worker collects

The ingest worker polls GitHub, on the same schedule as the other provider sources (every 60 minutes by default). The source id is `github-activity-api`, shown in the Data sources panel as `Repository activity`. See [Data sources](/docs/data-sources).

### Turning it on

The source is active only when `GITHUB_TOKEN` is set and there is something to poll. Otherwise it shows as not connected.

| Setting                          | Effect                                                                                                                                                                                                                                                                                                     |
| -------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GITHUB_TOKEN`                   | Required. The token also serves the Copilot report source. It needs `contents:read`, `pull-requests:read` and `actions:read`, plus metadata read to list an organisation's repositories.                                                                                                                   |
| `Ingest__GitHubRepoAllowlist`    | An explicit list of `owner/repo` entries. When non-empty it is used as given and no discovery happens. It can also reach repositories outside your organisation. Entries that are not exactly two non-blank segments are dropped, duplicates are removed ignoring case, and each is lower-cased on ingest. |
| `GITHUB_ACTIVITY_ORG`            | Used when the allowlist is empty. Every non-archived repository in the organisation is discovered on each cycle, so a new repository needs no change. If unset, `GITHUB_BILLING_ORG` is used.                                                                                                              |
| `Ingest__PollingIntervalMinutes` | Minutes between cycles. Default 60.                                                                                                                                                                                                                                                                        |
| `Ingest__LookbackDays`           | Trailing days (ending yesterday) re-read on each cycle. Default 3.                                                                                                                                                                                                                                         |
| `Activity__ProjectOwners`        | Display filter only. Restricts which stored repositories the tab shows, by owner, ignoring case. Empty shows all. See [Activity](/docs/activity#project-names-and-the-owner-filter).                                                                                                                       |

Full descriptions are in [Configuration](/docs/configuration). A value that is an unresolved Key Vault reference (`@Microsoft.KeyVault(...)`) counts as unset.

### What each cycle fetches

For each repository the worker fetches three things and stores them with upserts, so a second pass over the same data changes nothing:

1. **Pull requests**, newest-updated first, each with all of its reviews. Fetching stops at the first pull request last updated before the start of the window.
2. **Commits** since the start of the window. The commit list does not carry line counts, so the worker makes one extra request per commit to read additions and deletions. The request names no branch.
3. **Workflow runs** created since the start of the window.

The start of the window is the earliest of three dates: the start of the lookback window, the date of the last successful cycle, and any date left pending by a failed cycle. The first time a repository is seen, the start moves back a further 30 days, separately for pull requests, commits and workflow runs. Reviews have their own one-time 30-day pass, so an instance that already held pull requests still fills in its reviews.

Pull requests update in place when they change. Title, state, merge time and close time are replaced. A known first-review time is not cleared by a later fetch that lacks one, and the review count never goes down.

### Limits and rate limiting

| Limit                  | Value                                                                                                                                                                                                                                                 |
| ---------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Page size              | 100 items per request.                                                                                                                                                                                                                                |
| Rate-limit floor       | When GitHub reports fewer than 50 requests remaining, the worker stops for that cycle. The remaining repositories wait for the next cycle.                                                                                                            |
| Workflow-run listing   | A single window returns at most 1,000 runs. The worker then walks backwards in windows, at most five per call, and saves where it stopped. The backfill for that repository stays open and resumes next cycle until a window completes under the cap. |
| Organisation discovery | Lists at most 20 pages of 100 repositories, archived ones included in the count. If the 20th page is full (2,000 or more repositories), discovery fails rather than silently polling a subset; set an explicit allowlist.                             |
| Field lengths          | Repository 200, title 500, author 200 and commit author 200 characters; longer values are truncated.                                                                                                                                                  |

How failures show in the Data sources panel:

| Situation                                       | Source state                                                                                                      |
| ----------------------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| Rate-limit floor reached                        | `unavailable`, with the error `GitHub API rate limit exhausted`.                                                  |
| Organisation discovery fails or returns nothing | `unavailable`, with the cause.                                                                                    |
| Some repositories fail                          | `failing`: `<n> repo(s) failed to ingest this cycle`. The window does not advance, so the next cycle re-reads it. |
| Every configured repository fails               | `failing`.                                                                                                        |

A large first pass on many repositories or a very active repository can exhaust the token's hourly limit before every repository is covered. Cycles then complete over several hours; this shows as `unavailable` while it is happening.

## GitHub billing sync

GitHub billing is a separate source, `github-billing-api`, shown as `GitHub billing`. It is not part of this tab: it puts GitHub's billed usage into the spend ledger, so it appears on [Spend](/docs/spend) and [Reporting](/docs/reporting).

- **Where it runs.** In the API process, not the ingest worker. It runs when the API starts and then once a day at UTC midnight.
- **Settings.** `GITHUB_TOKEN` and `GITHUB_BILLING_ORG`, both set in the API's environment. The token needs organisation billing read: the fine-grained organisation permission `Administration` (read), or `admin:org` on a classic token. The user-scoped `Plan` permission is not enough, and neither is the repository-level `Administration` permission.
- **What it reads.** The organisation's billed usage for the previous calendar year and the current one. GitHub reports usage per month, so entries are monthly.
- **What it writes.** One billed observation per month, product and SKU, in USD, with source scope `Mixed` and basis `Billed`. Gross, discount and net are kept, with the discount stored as a negative credit.
- **Where it lands in the ledger.**

| GitHub product | Vendor           | Category       |
| -------------- | ---------------- | -------------- |
| `actions`      | `github-actions` | `ci`           |
| `packages`     | `github`         | `cloud`        |
| `code_quality` | `github`         | `code-review`  |
| `ghas`         | `github`         | `subscription` |
| anything else  | `github`         | `subscription` |

- **Re-running is safe.** A month is updated in place, so the open month stays current.
- **Failures.** A `403` or `404` from the billing endpoint is recorded as a source failure (usually a token without billing scope), not as an empty year. A failure for the previous year alone is logged and the current year is still synced.

## Troubleshooting

| Symptom                                                       | Likely cause                                                                                                                            |
| ------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| The tab is empty and `Repository activity` is `Not connected` | `GITHUB_TOKEN` is unset, or neither an allowlist nor an organisation is set.                                                            |
| The tab is empty and the source is fresh                      | `Activity__ProjectOwners` excludes the repositories, or the period contains no activity.                                                |
| The source is `unavailable` with a rate-limit message         | The token's hourly budget was spent. It recovers on a later cycle; reduce the number of repositories or use a token with more headroom. |
| `CI success` looks low                                        | Cancelled or skipped runs count against the rate.                                                                                       |
| Old pull requests appear                                      | They were reviewed or merged inside the period.                                                                                         |

More are in [Troubleshooting](/docs/troubleshooting).

## References

- `src/AiObservatory.Web/src/pages/GitHubPage.tsx`
- `src/AiObservatory.Web/src/components/GitHubPrTable.tsx`, `GitHubReviewerTable.tsx`, `GitHubCommitTable.tsx`, `GitHubCiTable.tsx`, `githubSort.ts`
- `src/AiObservatory.Api/Endpoints/GitHubActivityEndpoints.cs`
- `src/AiObservatory.Ingest/Services/GitHub/GitHubIngestionService.cs`, `GitHubActivityClient.cs`
- `src/AiObservatory.Data/Repositories/GitHubActivityRepository.cs`
- `src/AiObservatory.Ingest/IngestOptions.cs`
- `src/AiObservatory.Api/Services/GitHub/GitHubBillingSyncService.cs`, `GitHubBillingRegistration.cs`
- `docs/provider-setup.md`
