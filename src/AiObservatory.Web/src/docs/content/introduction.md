# Introduction

Observatory is a dashboard that records what AI tools cost and how much they are used, and keeps unlike evidence apart. This page says what it is for, how it is built, and what each tab does.

## What Observatory is

Observatory is a .NET API and a React web app on top of a PostgreSQL database. It collects usage and cost evidence from three kinds of source:

- **Provider APIs.** Usage, cost and reporting APIs that a provider operates, polled by an ingest worker.
- **Local telemetry.** Token snapshots that scripts on a developer machine read from CLI session files and post to the API. See [Local producers](/docs/local-producers).
- **Manual entries.** Spend ledger rows that a person types in on the Spend tab.

Every figure carries its origin. The dashboard never merges evidence of different kinds into one number, and it never turns a missing value into zero.

## Who it is for

It is for the operator of an Observatory instance: someone who pays for, or is accountable for, AI tooling and wants to see what was billed, what was only estimated, and how plans compare with real use. You can run it for one person or for an organisation.

## The evidence-first idea

A usage figure by itself is not enough. A token count from a transcript, a number on an invoice and a price-list calculation answer different questions. Observatory therefore stores, with each observation:

- where it came from (the source),
- what kind of use it describes (the scope), and
- how its money value was obtained (the basis).

The screens label figures with those facts, and the totals respect them. Billed spend, estimated cost and subscription notional value are shown in separate cards and are never added together. When a source reports usage but no cost, the cost reads `Not reported`. The full model is in [Concepts](/docs/concepts).

## A tour of the tabs

The tab bar sits under the header. The header also holds the theme control; see [Access](/docs/access).

| Tab                | What it is for                                                                                                                                                              | Chapter                                        |
| ------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------- |
| Overview           | The 31-day picture: billed spend, three kinds of estimate, tokens, suggestions, usage charts, subscriptions, model breakdown, insights and the health of every data source. | [Overview](/docs/overview)                     |
| Adversarial Review | Cost and yield of multi-model code review runs: stats by reviewer and model, seat influence, and the recent runs.                                                           | [Adversarial Review](/docs/adversarial-review) |
| Reporting          | Billed spend over a period you choose, compared with another period, split by vendor, plus budget rules and alert settings.                                                 | [Reporting](/docs/reporting)                   |
| Activity           | Active working time by project, taken from local session records.                                                                                                           | [Activity](/docs/activity)                     |
| GitHub             | Pull requests, review agents, commits and CI health for the repositories Observatory polls.                                                                                 | [GitHub](/docs/github)                         |
| Spend              | The billed-spend ledger: entries, vendors and categories, filters and period comparison.                                                                                    | [Spend](/docs/spend)                           |
| Docs               | This documentation.                                                                                                                                                         | [Introduction](/docs/introduction)             |

Some tabs are hidden in read-only mode. The Activity, GitHub and Spend tabs are not shown to a viewer who opens a share link or uses a demo build, because the data behind them is available only to the admin key or a signed-in user. See [Access](/docs/access#read-only-mode).

### What lives on the Overview

The Overview is the longest page. From top to bottom it holds:

1. Six summary cards: `Billed spend · 31 days`, `List-price estimate`, `Provider estimate`, `Subscription notional`, `Tokens` and `New insights`.
2. The Suggestions card. See [Suggestions](/docs/suggestions).
3. Two charts: `Usage value · last 31 days` and `Provider usage share`.
4. A collapsed `Caveman` panel. See [Usage and insights](/docs/usage-insights#caveman-stats).
5. The `Subscriptions` panel. See [Subscriptions](/docs/subscriptions).
6. The `Model breakdown` table and the `Insights` feed, side by side. See [Usage and insights](/docs/usage-insights).
7. A collapsed `Data sources` panel. See [Data sources](/docs/data-sources).

## Where to go next

| If you want to ...                                      | Read                                                                      |
| ------------------------------------------------------- | ------------------------------------------------------------------------- |
| Understand what a figure means before you trust it      | [Concepts](/docs/concepts)                                                |
| Sign in, share a read-only link, or embed the dashboard | [Access](/docs/access)                                                    |
| Read each Overview card and chart                       | [Overview](/docs/overview)                                                |
| Connect a provider or a local tool                      | [Providers](/docs/providers) and [Local producers](/docs/local-producers) |
| Change a setting                                        | [Configuration](/docs/configuration)                                      |
| Fix something that looks wrong                          | [Troubleshooting](/docs/troubleshooting)                                  |
| Look up a term                                          | [Glossary](/docs/glossary)                                                |

## References

- `README.md` for the project summary and the list of supported sources.
- `docs/truth-and-pricing.md` for the source, scope and basis definitions.
- `src/AiObservatory.Web/src/pages/Dashboard.tsx` for the tab list and the Overview layout.
