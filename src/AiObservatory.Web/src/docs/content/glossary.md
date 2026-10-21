# Glossary

Short definitions of the terms used across these pages, in alphabetical order. Each entry links to the page that explains it in full.

## A

- **Accepted.** In an adversarial review, a raised issue that survived adjudication. A finding with outcome `accepted`. [Adversarial Review](/docs/adversarial-review#terms-used-on-this-tab)
- **Active seconds.** The working time a coding-agent session record holds, as decided by the script that produced it. Observatory stores it and does not compute it. [Activity](/docs/activity#how-sessions-get-in)
- **Admin API key.** The key set in `OBSERVATORY_API_KEY`. It is accepted on every `/api` route for any method, in the `X-Observatory-Key` header. [Authentication](/docs/authentication#credentials-at-a-glance)
- **Admin-only read.** A `GET` route that accepts an Entra token or the admin key but not the read-only key, such as the Activity, GitHub and ledger row routes. [Authentication](/docs/authentication#admin-only-reads)
- **Aggregate.** A daily total of requests, tokens and cost for one provider, model, source, scope and basis. It carries a request count and an unknown-cost count. [Concepts](/docs/concepts#not-reported-versus-zero)
- **Alert History.** The Reporting panel that lists the latest budget-alert insights, up to ten. [Reporting](/docs/reporting#alert-history)
- **Archived.** A vendor or category that is retired from the pickers. It still resolves its name on past entries and stays in every total. [Spend](/docs/spend#archiving)
- **Assessment.** A verdict (`agree`, `false-positive`, `needs-evidence`, `needs-repo` or `abstained`) that another participant gives on a finding. [Adversarial Review](/docs/adversarial-review#finding-outcomes-and-the-seat-counters)

## B

- **Basis.** How a value's money was obtained: `billed`, `providerEstimated`, `listPriceEstimate`, `notional`, `none` or `unknown`. Shown as a column and legend entry. [Concepts](/docs/concepts#basis)
- **Billed.** A cost that is provider-reported financial evidence or a ledger entry. Billed money is recorded in the spend ledger, not posted as a usage event. [Concepts](/docs/concepts#four-kinds-of-money-value)
- **Billed spend.** The sum of signed ledger amounts in GBP for entries in a period. Charges add, refunds and credits subtract. It excludes estimates and notional value. [Overview](/docs/overview#billed-spend)
- **Budget rule.** A limit on billed spend for a provider (or all providers) over a day, week or month. It raises an alert when the window's spend is strictly greater than the threshold. [Reporting](/docs/reporting#budget-rules)
- **Bundled catalog.** A price catalog shipped inside a release rather than fetched. Gemini Developer API, z.AI and DeepSeek are bundled only. [Pricing](/docs/pricing#catalogs)

## C

- **Cache hit rate.** Cache-read tokens divided by observed input tokens, rounded to a whole percent. [Overview](/docs/overview#tokens)
- **Cache savings.** What cached tokens would have cost at the plain input rate, minus what they did cost. Stored with the cost. [Pricing](/docs/pricing#how-an-event-is-rated)
- **Catalog.** A table of per-million-token rates for one provider, kept as snapshots. Observatory rates tokens against it. [Pricing](/docs/pricing#catalogs)
- **Caveman session.** A record that an outside tool posts, holding a mode, a model, output tokens and the tool's own estimate of tokens and money saved. [Usage and insights](/docs/usage-insights#caveman-stats)
- **Chunk.** One cohesive piece of a large change in a batched adversarial review, reviewed by the full panel. [Adversarial Review](/docs/adversarial-review#run-header)
- **Comparison period.** The second range that Reporting, Spend, Activity and GitHub set beside the selected range. It is the previous period unless you edit it. [Reporting](/docs/reporting#comparison-period)
- **Complete (run).** A panel run with a reviewer row for each of the five active vendors and a judge row. Otherwise it is incomplete. [Adversarial Review](/docs/adversarial-review#completeness-and-validity)
- **Configured.** A source state: set up, but no collection has yet succeeded. [Data sources](/docs/data-sources#the-six-states)
- **Corroborated catch.** An accepted finding that more than one reviewer raised. [Adversarial Review](/docs/adversarial-review#finding-outcomes-and-the-seat-counters)
- **Cost basis.** See Basis.
- **Cost / 1M.** Cost divided by observed tokens, times one million. Shown only when every request in the row has a cost. [Usage and insights](/docs/usage-insights#how-cost-and-cost-1m-are-computed)
- **Cost per finding.** A reviewer's cost divided by its accepted issues. Aggregated as a ratio of sums. [Adversarial Review](/docs/adversarial-review#stats-by-reviewer-model)

## D

- **Daily digest.** An email or Slack message listing sources that are `Unavailable` or `Failing`, sent at most once per UTC day. [Data sources](/docs/data-sources#the-daily-digest)
- **Demo mode.** A setting (`OBSERVATORY_DEMO_MODE`) that makes the API a read-only instance of synthetic data. [Authentication](/docs/authentication#demo-mode)
- **Detailed run.** A panel run that has finding rows. Seat measures use detailed runs. [Adversarial Review](/docs/adversarial-review#pivot)
- **Dismiss.** To hide an insight or a suggestion. A dismissed suggestion reopens only if its main figure moves enough or its sample doubles. [Suggestions](/docs/suggestions#dismissing)
- **Dropped.** A finding outcome. Dropped findings are left out of the false-positive rate. [Adversarial Review](/docs/adversarial-review#derived-measures)

## E

- **Embedded mode.** The dashboard running inside a WebView2 host, switched on by `?embed=fixportal-ide` and a real host channel. [Access](/docs/access#embedded-mode)
- **Entra.** Microsoft Entra ID, the sign-in service. A valid token gives full read and write access to `/api`. [Authentication](/docs/authentication#entra-sign-in)
- **Event key.** The identifier that, with a source id, names one usage observation. Reposting the same key replaces the earlier contribution. [Concepts](/docs/concepts#provenance-and-identity)
- **Evidence-first.** The design rule that every figure carries its source, scope and basis and that missing values are never shown as zero. [Introduction](/docs/introduction#the-evidence-first-idea)
- **Extra usage.** An optional amount paid beyond a plan, such as overage. It is added to the plan price before the notional comparison. [Subscriptions](/docs/subscriptions#extra-usage)

## F

- **Failing.** A source state: the latest attempts failed and the consecutive failure count is above zero. [Data sources](/docs/data-sources#failing)
- **Finding.** One distinct issue in an adversarial review, with a severity, an outcome, its raisers and any assessments. Observatory stores identifiers only, never the text. [Adversarial Review](/docs/adversarial-review#what-the-tab-holds)
- **Fresh.** A source state: the last success is within twice the expected interval. It describes collection, not use. [Data sources](/docs/data-sources#freshness-the-expected-interval)
- **Frozen GBP amount.** The GBP value and exchange rate stored on a ledger entry when it is recorded or corrected. Billed totals sum these, so they do not move with the market. [Spend](/docs/spend#currency-and-exchange-rates)

## I

- **IDE API key.** The key in `OBSERVATORY_IDE_API_KEY`, sent as `X-Observatory-IDE-Key`. It opens only `/api/ide/v1`. [Authentication](/docs/authentication#credentials-at-a-glance)
- **Indeterminate.** A finding outcome. It is left out of the false-positive rate. [Adversarial Review](/docs/adversarial-review#derived-measures)
- **Ingest worker.** The separate process that polls provider APIs and refreshes price catalogs. It never applies migrations. [Deployment](/docs/deployment#ingest-worker)
- **Insight.** A written observation about usage, typed `summary`, `efficiency`, `anomaly`, `recommendation` or `budgetalert`. Most are generated by a language model. [Usage and insights](/docs/usage-insights#insights)

## J

- **Judge.** The participant that adjudicates a panel's findings. A judge row carries no raised or accepted counts. [Adversarial Review](/docs/adversarial-review#terms-used-on-this-tab)

## K

- **Key Vault reference.** An App Service setting value of the form `@Microsoft.KeyVault(...)`. When unresolved it arrives as literal text, which most settings treat as unset. [Configuration](/docs/configuration#how-settings-are-supplied)

## L

- **List-price estimate.** API usage valued by rating tokens against an observed public price catalog at the date of use. It is an estimate, not billed spend. [Pricing](/docs/pricing#what-is-rated-and-what-is-not)
- **Local producer.** A script on a developer machine that reads CLI session files and posts token snapshots. The repository ships `clients/observatory-sweep.mjs`. [Local producers](/docs/local-producers#what-a-local-producer-is)
- **Local telemetry.** A source kind: data read from a CLI, session or transcript on a machine. [Concepts](/docs/concepts#source-kind)
- **Lookback.** The trailing days (default 3, ending yesterday) that the ingest worker re-reads on each cycle. [Providers](/docs/providers#where-sources-run)

## M

- **Manual.** A source kind: an observation a person entered. [Concepts](/docs/concepts#source-kind)
- **Mixed.** A usage scope used when an upstream source explicitly combines scopes. [Concepts](/docs/concepts#scope)
- **Model breakdown.** The Overview table of usage by provider, model, source, scope and basis. [Usage and insights](/docs/usage-insights#model-breakdown)

## N

- **Not connected.** A source state: not configured, or no state held for it. [Data sources](/docs/data-sources#not-connected)
- **Not reported.** The label for usage with no known cost. It is not zero. [Concepts](/docs/concepts#not-reported-versus-zero)
- **Notional.** A basis for an API-list-price comparison of subscription or local activity, where no money changed hands. [Concepts](/docs/concepts#basis)
- **Notional usage value.** The Subscriptions panel figure: a provider's notional usage in the current billing period, compared with the plan price. [Subscriptions](/docs/subscriptions#how-the-notional-value-is-derived)

## O

- **Observed time.** When Observatory obtained an observation, as against the occurrence time, when the use happened. [Concepts](/docs/concepts#time-when-it-happened-when-it-was-seen-when-the-source-last-worked)
- **Observed tokens.** Plain input plus cache-read plus cache-write tokens, and output. The Tokens card counts input plus output. [Overview](/docs/overview#tokens)
- **Outcome.** A finding's final result: `accepted`, `refuted`, `dropped` or `indeterminate`. [Adversarial Review](/docs/adversarial-review#finding-outcomes-and-the-seat-counters)

## P

- **Peer agreement.** The count of `agree` assessments on a seat's findings from participants other than the seat's own reviewers. [Adversarial Review](/docs/adversarial-review#finding-outcomes-and-the-seat-counters)
- **Provenance.** The record of where an observation came from. Observations are identified by source and event key. [Concepts](/docs/concepts#provenance-and-identity)
- **Provider.** The vendor whose model was used: Anthropic, OpenAI, Copilot, Google, Moonshot, xAI, Meta, z.AI or DeepSeek. [Concepts](/docs/concepts#source-scope-and-basis)
- **Provider estimate.** A cost the provider or a local tool calculated and reported. It is not an invoice. [Overview](/docs/overview#provider-estimate)

## R

- **Raised.** Issues a reviewer reported in an adversarial review. [Adversarial Review](/docs/adversarial-review#terms-used-on-this-tab)
- **Read-only key.** The key in `OBSERVATORY_READONLY_API_KEY`. It allows `GET` requests only, and not the admin-only reads. [Authentication](/docs/authentication#credentials-at-a-glance)
- **Read-only mode.** The web mode used for a share link or a demo build. It hides the Activity, GitHub and Spend tabs and the write controls. [Access](/docs/access#read-only-mode)
- **Refuted.** A finding outcome. The false-positive rate is refuted findings over settled findings. [Adversarial Review](/docs/adversarial-review#derived-measures)
- **Repricing.** Recalculating stored list-price estimate and notional events when a catalog is activated. [Pricing](/docs/pricing#repricing)
- **Run (sweep).** All participant rows that share one run id in an adversarial review. [Adversarial Review](/docs/adversarial-review#terms-used-on-this-tab)

## S

- **Scope.** What kind of use an observation describes: `api`, `subscription`, `mixed` or `unknown`. [Concepts](/docs/concepts#scope)
- **Seat.** A reviewer model in an adversarial review, identified by its model string. [Adversarial Review](/docs/adversarial-review#what-a-seat-is)
- **Settled.** A suggestions term: unique, corroborated and refuted findings together. [Suggestions](/docs/suggestions#seat-rules)
- **Share link.** A URL of the form `https://<host>/?key=<read-only key>` that opens the dashboard in read-only mode without Entra sign-in. [Access](/docs/access#viewer-share-link)
- **Snooze.** To hide a suggestion for a number of days. It returns at the next evaluation after the snooze ends. [Suggestions](/docs/suggestions#snoozing)
- **Source.** One way Observatory obtains evidence, with an id such as `openai-costs-api`. [Providers](/docs/providers#how-sources-work)
- **Source kind.** The type of system that produced an observation: `providerApi`, `localTelemetry`, `manual`, `synthetic` or `legacy`. [Concepts](/docs/concepts#source-kind)
- **Stale.** A source state: the last success is older than twice the expected interval. [Data sources](/docs/data-sources#stale)
- **Subject.** What a suggestion is about: a seat or a provider. A rule and a subject identify one suggestion. [Suggestions](/docs/suggestions#the-rules)
- **Subscription notional.** What subscription or local activity would have cost at API list prices. [Overview](/docs/overview#subscription-notional)
- **Suggestion.** Rule-based advice about review-panel seats or subscription plans. It is not an instruction. [Suggestions](/docs/suggestions#what-suggestions-are)
- **Sweep placement.** The health-only source `sweep-placement@<machine>` that counts sessions a sweeper could not attribute to a provider. [Local producers](/docs/local-producers#sessions-that-cannot-be-placed)
- **Sweeper.** The script `clients/observatory-sweep.mjs`. [Local producers](/docs/local-producers#run-it)
- **Synthetic.** A source kind for demonstration data generated by Observatory, never evidence from a provider. [Concepts](/docs/concepts#source-kind)

## T

- **Tombstone.** A snapshot reposted with zero tokens and a marker so that the server drops its contribution when the local record has disappeared. [Local producers](/docs/local-producers#avoiding-double-counting-and-drift)

## U

- **Unavailable.** A source state: the source says it cannot supply data. It outranks failing. [Data sources](/docs/data-sources#unavailable)
- **Unique catch.** An accepted finding that exactly one reviewer raised. [Adversarial Review](/docs/adversarial-review#finding-outcomes-and-the-seat-counters)
- **Unknown cost (review).** A panel run whose cost could not be established. It is stored with a zero placeholder and excluded from averages. [Adversarial Review](/docs/adversarial-review#unknown-cost)
- **Unknown price.** An event with no computed cost because no calculator, catalog entry, model match, dimension or rate was available. It reads `Not reported`. [Pricing](/docs/pricing#unknown-prices)
- **Unplaced.** A local session that the sweeper could not assign to a provider and so did not post. [Local producers](/docs/local-producers#sessions-that-cannot-be-placed)

## V

- **Valid (run).** A panel run in which at least two active-roster reviewer vendors took part. [Adversarial Review](/docs/adversarial-review#completeness-and-validity)
- **Viewer key.** The read-only key held in the tab's session storage after a share link is opened. [Access](/docs/access#viewer-share-link)

## W

- **Wall-clock time.** On the Activity chart, the union of the time spans of all sessions in a day, so parallel sessions count once. [Activity](/docs/activity#active-time-chart)
- **Window.** A date range a figure covers: the 31-day Overview window, a budget rule's window or a seat or plan window for suggestions. [Overview](/docs/overview#the-31-day-window)
