# Self-audit qualification — 2026-10-22

Qualification now includes a consistent snapshot of the actual installation,
export and deterministic replay, plus a passing full serial Windows lane on the
corrected code. The audit result remains **Incomplete** where retained facts
cannot establish source completeness, historical pricing or fresh presentation.
Earlier projection-copy evidence and parallel Windows **NONPASS** results remain
historical; they are not substituted for the current evidence.

## Actual installation snapshot and corrected export

The current exporter and replay code is
`1cc5b4de76bd9c9b3df3c8782d90b2cc6166ff71`. The original completed run and
immutable server snapshot were produced by
`499d668d87fc2740f9d852ad60cae87b625d9795`. The correction caches immutable
nullable-property annotations by reflected property, with thread-safe
publication. It does not cache evidence values, omit validation or change
canonical semantics, schema `1`, verifier `1.0.0` or rules `1`.

With explicit authorization, a consistent read-only dump of the actual
installation database was taken through a temporary rule limited to the current
client address. The rule was removed immediately; an independent read confirmed
its absence and that every pre-existing rule was unchanged. The installation
was not migrated, repaired, repriced or re-ingested. The original dump and an
owned post-audit copy are preserved privately outside Git and the user home.

The snapshot came from PostgreSQL 16.15 using the pinned PostgreSQL 17.10 dump
client. Restore to the owned PostgreSQL 16 server omitted exactly one client-only
`SET transaction_timeout = 0` from a separate SQL copy; the original dump and all
schema/data statements remain intact. Four existing audit migrations ran only
on the owned copy. All original columns in 29 application tables were unchanged.
The existing startup worker changed only `IsConfigured` and `IsAvailable` on one
excluded billing-source status row because its optional client was unconfigured;
the selected source statuses and all original row counts were unchanged. The
audit therefore identifies the post-start owned-copy snapshot precisely. No
historical pricing-application associations were manufactured.

Fresh copies of the same 24 frozen files matched their preserved hashes. The
companion sealed 13,863 sanitized records in 33.90 seconds for the completed
2026-10-21 UTC window. The actual-copy run completed in 150.02 seconds. Its
22,438,595-byte snapshot includes 87 catalogs and 11,993 rate records.

| Check | Actual snapshot result |
|---|---|
| Event aggregation | Matched, 2 / 2 groups |
| Source accounting | Incomplete, 0 / 31 groups |
| Daily attribution | Incomplete, 0 / 32 groups |
| Pricing arithmetic | Incomplete, 0 / 2 events |
| Displayed totals | Incomplete, 0 / 12; `missing_presentation_probe` |
| Billing reconciliation | Excluded |

The overall result is **Incomplete**. Source/daily checks each retain 38
last-usage disagreements; daily attribution records 191 boundary diagnostics.
These are per-observation findings. Independent extraction remains qualified
only for the supported shapes. Missing closure, unsupported source facts and an
unknown surviving-history denominator prevent a completeness claim. Catalog
presence does not establish which historical rates were applied: the selected
events lack pricing-application evidence, so neither price arithmetic nor
historical rate correctness is established. Agreement of stored event/aggregate
groups is a separate result from those limitations.

Fresh actual-database browser presentation was unavailable: automatic policy
rejected the shell browser launch before execution, and the supported browser
connector reported no available browser. No retry bypassed that restriction.
The prior UI qualification under `499d668` below matched 11 / 11 values on its own
projection-copy scope; it is not fresh actual-database UI evidence. Acceptance 7
has actual consistent-snapshot numerical and replay evidence; the separate
presentation limitation remains explicit.

The first completed-state export on code `499d668` returned HTTP 500 near the
unchanged two-minute export deadline. Repeated nullable-metadata reflection was
isolated with a synthetic allocation regression: 1,024 rate facts allocated
54,835,992 bytes before the correction and 1,065,496 afterward. Canonical golden,
required-null, cross-type and concurrent checks pass. All 276 Audit tests and
15 export integration cases passed; no validation or timeout was removed.

One export of the same immutable run through corrected code returned HTTP 200
with 25,930,244 bytes in 51.60 seconds. Its lease and reservation were released.
Two Windows replays returned expected exit 3 in 33.07 and 32.29 seconds. Linux,
with no network, read-only root and a 2 GiB limit, returned exit 3 in 45.03
seconds. All three outputs exactly match both the bundle result and the original
recorded database result, SHA-256
`c679626fcee59a3f721b90b30c1257c27614a682149a067c9c2c1485d08d9688`.
This establishes successful bounded export and canonical preservation for this
cohort; it is not a repeatable throughput benchmark.

The 13-entry bundle privacy check found none of 1,632 tested raw path strings,
43,621 native identifiers or six credential values. Compatible runners and all
prior immutable evidence remain separately preserved. The feature remains
default-disabled; no deployment or production feature setting changed.

## Current full Windows qualification on code 1cc5b4d

One full Windows Release `--no-build` run on
`1cc5b4de76bd9c9b3df3c8782d90b2cc6166ff71` completed all six modules with
**2,327 passed, zero failed and one existing explicit skip; 2,328 total**.
Actual exit was **0**. Wall time was **544.025 seconds (9m 04.025s)**; the runner
reported 542.789 seconds. All six TRX counters and actual test entries agree.

| Module | Passed | Existing skipped | Module duration |
|---|---:|---:|---|
| Audit | 276 | 0 | 9.288s |
| Audit CLI | 8 | 0 | 9.721s |
| Ingest | 568 | 0 | 1m 02.629s |
| Data | 426 | 1 | 4m 47.822s |
| API unit | 618 | 0 | 7.414s |
| API Integration | 431 | 0 | 2m 40.332s |

The command used `--max-parallel-test-modules 1` and the unchanged **five-minute
per-module cap**, against the owned test database only. TRX intervals show no
module overlap. No API, browser, replay or hashing workload overlapped the suite.
There were no filters, retries, extra skips or timeout increases. The skipped
case remains the existing explicit
`QualificationRepricesEveryEligibleEventAndItsAggregate`; no historical data was
repriced to make this audit pass. Source/index content and all six Release test
assembly hashes were unchanged during the run. The solution build and changed
C# formatting checks passed with zero build warnings or errors.

The historical serial pass and parallel **NONPASS** below used different code.
These single observations establish their respective outcomes, not a scheduling
or disk-I/O root cause, repeatability guarantee or cross-version performance delta.

The previously pending database-access and security-disposition authorizations
were fulfilled. CodeRabbit and Gitar approved the preceding `d6a3e3f` head;
the scoped GitGuardian false-positive disposition was verified. Those decisions
do not substitute for review and CI of the later codec/export correction. This
qualification document does not itself establish merge readiness.

## Historical projection-copy qualification on code 499d668

The code qualified at this earlier stage was `499d668d87fc2740f9d852ad60cae87b625d9795`.
It corrects three follow-up findings: daily attribution at a session/window
boundary, evidence-lock duration during capture byte transfer, and test-container
cleanup after an owned template fails. A fresh native run qualifies these
semantics; the earlier runs below remain historical. Schema `1`, verifier
`1.0.0` and rules `1` remain the initial unpublished versions.

The same preserved 24 files were copied into a new owner-restricted evidence
location and verified unchanged before and after capture. The ordinary companion
sealed 13,863 sanitized records in 19.59 seconds. The owned API ran the exact
committed binaries against the existing projection-copy database on an internal
network; its external TCP probe failed. No original histories or installation
data were changed.

| Check | Fresh linked evaluation |
|---|---|
| Event aggregation | Matched, 2 / 2 groups |
| Source accounting | Incomplete, 0 / 31 groups |
| Daily attribution | Incomplete, 0 / 32 groups |
| Pricing arithmetic | Incomplete, 0 / 2 events |
| Displayed totals | Matched, 11 / 11 values |
| Billing reconciliation | Excluded |

The result remains **Incomplete**. Daily attribution now explicitly qualifies
boundary-dependent groups with `daily_attribution_boundary`; the earlier
implementation reported 0 / 30 groups. This affects open as well as closed
straddling sessions, so the earlier native evidence could not qualify the fix.
The fresh result retains 38 last-usage disagreements in each source/daily check
and records 191 boundary diagnostics; diagnostics are per observation, not a
count of unique sessions. Independent complete groups remain comparable.
Missing closure, unsupported source shapes and historical pricing gaps remain.

The actual production browser created the linked display evaluation in 95.30
seconds: one presentation page took 45.71 seconds and the probe POST took 11.68
seconds. Its six-check panel showed the boundary qualification and 11 matching
displayed values without horizontal overflow. Browser automation used DOM button
clicks to invoke the actual production handlers after native CLI clicks failed
to select the audit. This proves the handler/API path, not a trusted native-click
path for this run. Previous unchanged UI layout/theme qualification is retained
below. Requests made before the linked evaluation completed returned
`report_unavailable`; they are not counted as successful exports.

After completion, HTTP export returned 200 with 18,767,042 bytes in 93.86 seconds.
Two Windows replays returned expected exit 3 in 63.86 and 64.46 seconds. Linux
replay, with networking disabled, a read-only root and a 2 GiB cap, returned exit
3 in 74.82 seconds. All three canonical outputs are byte-identical to the new
bundle's recorded result, SHA-256 `ce32b6a28373c061312da56f8d89eba230e23dc9db3048f107ce5e2f35cd9c50`.
The compatible runner is preserved with this evidence; earlier bundles and
runners remain separate and unchanged.

The 13-entry bundle privacy check found no raw source paths, raw native
session/message identifiers or tested owned credential values. An operator
inventory command did print one generated capture identifier; that diagnostic
handling lapse is recorded privately and is distinct from the product privacy
check. No spend, native identifiers, source paths or credentials were printed
by that command.

All 271 Audit unit tests, 35 focused seal cases, six assembly-cleanup fault cases
and three template-lifetime cases passed serial Release runs. The solution built
with zero warnings/errors, and changed C# formatting passed. The prior exact
`8e375533` Linux CI result of 2,302 passed / one skipped is historical; it does
not establish CI success for this new code. The later full serial Windows
qualification is recorded below; the historical parallel **NONPASS** is retained.

Capture admission remains one candidate per server process before byte
allocation. Full byte transfer and validation run outside the evidence lock;
SQL revision binding and fresh publication fences reject changed generations
and authority. Multiple processes each have their own budget, and the serialized
512 MiB ceiling remains distinct from a maximum-memory guarantee.

At this earlier qualification point, actual-installation acceptance 7 was
**PARTIAL/BLOCKED**. The later authorized snapshot, export and replay evidence
above closes the database-access gap. Default detailed paging, unsupported
formats, real Entra, macOS execution and browser file-saving limits remain as
documented below. The feature remains default-disabled.

## Historical full Windows qualification on code 499d668

A single full Windows Release run at source/documentation HEAD
`fe4a47134361417c4a92815d27f89d53c1c481b3` used the existing test binaries from
code `499d668d87fc2740f9d852ad60cae87b625d9795`. The intervening commit changed
only this qualification document. All six modules completed with **2,322 passed,
zero failed and one existing explicit skip, from 2,323 total tests**. Actual
process exit was **0**; console totals and all six TRX reports agree.

The run used the existing owned PostgreSQL server, Release `--no-build`, and
`--max-parallel-test-modules 1`. Every module retained its **five-minute cap**;
TRX intervals confirm that modules did not overlap. Wall time was **507.231
seconds (8m 27.231s)**; the test runner reported 506.394 seconds.

| Module | Passed | Existing skipped | Module duration |
|---|---:|---:|---|
| API unit | 618 | 0 | 5.744s |
| Data | 426 | 1 | 4m 50.558s |
| Audit | 271 | 0 | 7.568s |
| Ingest | 568 | 0 | 46.764s |
| Audit CLI | 8 | 0 | 3.992s |
| API Integration | 431 | 0 | 2m 27.421s |

The skipped pricing qualification case,
`QualificationRepricesEveryEligibleEventAndItsAggregate`, already has
`Fact(Explicit = true)` and is excluded by the runner's default explicit-test
policy. No additional filter, skip, retry or timeout increase was introduced.
The exact workload was:

```sh
dotnet test --solution AiObservatory.slnx --configuration Release --no-build --max-parallel-test-modules 1 --report-xunit-trx --results-directory <private-results> --timeout 5m
```

Source, staged/unstaged content and all six Release assembly hashes were
unchanged. CLI help inspection had refreshed ignored Debug outputs while
collecting extension help; the measured run used the preserved Release binaries.
No API, browser, native replay or hashing workload overlapped the suite.

This qualifies the sequential Windows lane. The historical parallel **NONPASS**
below remains intact, including its reporting discrepancy. That baseline used
different code, and only one serial run was measured: no performance delta,
repeatability guarantee or sole-cause attribution to scheduling or disk I/O is
established. This historical test run did not establish installation acceptance
or merge readiness; the later actual-snapshot evidence is recorded above.

## Historical first review batch 64 qualification

The implementation qualified by this earlier run is
`66af853e822eaef006b3485f22ef301f4ca85dbb`, with schema `1`, verifier `1.0.0`
and rules `1`. These remain the initial unpublished versions. The prior bundles
and their compatible runner are preserved separately; their canonical results
qualify the earlier implementation, not these revised semantics. After
publication, a semantic change requires a verifier or rule version boundary.

The fresh run used the same frozen completed window, **2026-10-21 UTC**, and
24 source files containing 466,105,398 bytes. The ordinary Windows companion
sealed 13,863 sanitized records in 20.07 seconds. Source hashes were verified
before and after capture, and identical new preservation copies are held outside
both Git and the user home. The API mounted the committed review-worktree
binaries, used only the owned projection-copy database, had blank vendor
credentials and suggestions disabled, and failed an external network probe.
The database still lacks the actual installation's transactional and historical
pricing provenance; this run does not close that qualification gap.

| Check | Fresh linked evaluation | Meaning |
|---|---|---|
| Event aggregation | Matched, 2 / 2 groups | Agreement within the owned frozen projection copy |
| Source accounting | Incomplete, 0 / 31 groups | Missing closure and incomplete extraction remain explicit |
| Daily attribution | Incomplete, 0 / 30 groups | Open sessions remain provisional, including those ending outside the selected window |
| Pricing arithmetic | Incomplete, 0 / 2 events | Historical catalog/application evidence remains absent |
| Displayed totals | Matched, 11 / 11 values | Actual production browser summary and formatter on the selected frozen scope |
| Billing reconciliation | Excluded | Unqualified feed; no invoice or publication confirmation |

The result remains **Incomplete**. Source and daily checks retain the 38
last-usage disagreements observed previously. The unsupported Codex
`token_usage_record` shape, missing historical denominator and other source
limitations described below remain unresolved. Production-calculator integration
fixtures separately prove the corrected OpenAI uncached-input/cache-read/write
contract and corruption detection; the native cohort cannot establish missing
historical prices.

The actual linked presentation action completed in 97.73 seconds: one projected
page took 48.34 seconds and the probe POST took 12.83 seconds. Export returned
HTTP 200 with 18,617,800 bytes in 91.91 seconds. Two ordinary Windows CLI replays
returned expected exit 3 in 63.08 and 66.58 seconds. A Linux container with no
network returned exit 3 in 75.69 seconds. All three canonical outputs equal the
new bundle's recorded result, SHA-256
`84638af21c4e8698651a2189eb91290e716bed65ccc5221aa7877f775e1f22be`.
This is fresh execution evidence; earlier timings below are retained historical
measurements.

The six-check results panel was inspected in the real production UI at desktop
and narrow widths, in light and dark themes. It exposes exact coverage counts,
qualifications and field exclusions without horizontal page overflow. Selecting
the completed evaluation focused its heading. The actual read-only viewer could
read the scoped report and was denied privileged evidence and bundle routes.
Full and expired report regressions cover scope, cutoff/completion context,
per-check coverage and explicit unavailable legacy qualifications. Existing
browser file-saving and real Entra issuer limitations remain unchanged.

## Review batch 64 gates and remaining limits

Release restore/build and C# formatting passed with zero build warnings/errors.
The Web gate passed 551 tests across 67 files, with the existing 120 advisory
warnings and bundle-size warning. Node contract/client suites passed 156 tests;
Python policy and packaging checks passed 75 and 2. Changed workflow lint,
workflow hygiene, gate coverage and canonical assets passed. The synthetic golden
replayed twice with its unchanged expected Incomplete result.

The single idle full Windows Release run retained the five-minute module cap
and **did not pass**. API Integration timed out at 5m 01.066s and Data at
5m 01.877s; total wall time was 5m 02.350s. The command reported 2,103 passed
and one existing explicit skip, plus two module errors. Partial TRX files contain
2,105 passed entries and that skip; this reporting discrepancy is preserved,
and neither count establishes completion. CLI, API unit, independent Audit and
Ingest modules completed successfully. All newly added regressions and their
changed fixture classes recorded passes. Eight uncovered cancellation,
shared-owner, partial-evidence and database-template/startup composition cases
then passed serial targeted runs under the unchanged two-minute limit. There was
no full retry or limit increase. Earlier public Linux CI below qualifies the
old commit; no new public CI result is claimed here.

Default detailed paging still validates/materializes the full cohort per page;
this review does not resolve its previously measured scaling limitation or
establish a maximum-memory guarantee. Actual installation database acceptance,
unsupported source formats, real Entra, macOS execution and browser file saving
remain bounded as described below. The feature stays default-disabled.

## Historical pre-review qualification

The following measurements and public CI results qualify implementation
`445570e121ce6e524d389226686da2e2eee905a5`, documented at
`0a9c879e1bace8b0ca87249c118286ecf5bbe60c`. They are preserved for comparison.

> Point-in-time qualification of the shipped dashboard, companion and offline
> verifier. Durations are measured wall time; sizes are bytes or MiB. This report
> contains no installation identifiers, credentials, transcripts or spend totals.

Qualification is partial. Linux integration gates and the exercised browser
paths pass; the final idle Windows full suite timed out in two modules.
**The actual installation audit remains partial and blocked:** a consistent
backup of the actual installation database could not be obtained through the
current network path. Repeated authenticated read-only API exports are stable,
but they are nontransactional projections with missing database-only facts.
They do not substitute for the required database snapshot. No production data,
firewall, deployment or feature setting was changed by this qualification.

### Reproduction identity and scope

The implementation under qualification is commit
`445570e121ce6e524d389226686da2e2eee905a5` with schema `1`, verifier `1.0.0` and rules `1`. The approved design is
[Self-audit design](superpowers/specs/2026-10-21-self-audit-design.md); operational
instructions are in [Self-audit](self-audit.md).

Release tests used .NET SDK 10.0.402, Node 24.19.0 and uniquely owned disposable
PostgreSQL databases. The supplied database was never migrated, cleared or
removed. Test templates are newly created, empty, fully migrated owned databases;
each database-owning factory or fixture receives an isolated clone. Tests in an
API collection can share that factory and database. Dedicated unmigrated startup
and historical migration tests retain their explicit migration boundaries. The production provenance foreign keys remain enforced.

Frozen source copies and inventories were preserved before capture, with origin
modification times, capture times and SHA-256 digests held privately. Additional
owner-restricted preservation exists outside both Git and the user home. The
selected completed window is **2026-10-21 UTC**, ending at
`2026-10-22T00:00:00Z`; whole surviving files retain earlier cumulative baselines.
The owned API ran against copies with blank vendor credentials, suggestions
disabled and demonstrably blocked outbound connectivity. Automatic collection,
ingest and provider access were not used to reconcile or repair the originals.

The native Windows capture read 24 files, 466,105,398 source bytes and produced
13,863 sanitized records in 17.85 seconds. Observed native history identifies
Claude Code 2.1.302–2.1.307 and Codex CLI 0.163.0. Installed executable versions
were observed separately; they are not evidence of historical source format.

### Check outcomes and their limits

| Question | Observed outcome | Qualification limit |
|---|---|---|
| Stored events agree with stored daily aggregates | Matched, 2 compared native projection groups | Agreement of the repeated API projection copied into an owned coherent database; not an installation database snapshot |
| Independent source accounting agrees with postings | Incomplete, 0 of 29 coverage groups compared | Missing closure and incomplete extraction prevent a numeric agreement claim |
| Daily attribution agrees with cumulative accounting | Incomplete, 0 of 28 coverage groups compared | Open sessions and last-usage disagreements remain provisional; cumulative lifetime attribution is not exact daily consumption |
| Catalog arithmetic agrees with applied pricing | Incomplete, 0 of 2 projection events compared | Historical catalog/application provenance is absent from the available API export |
| Actual dashboard summary and formatter agree with the frozen reference | Matched, 11 of 11 compared values after the real browser probe | Qualifies the selected frozen aggregate/spend inputs and recorded frontend build; not independent frontend attestation |
| Published prices or invoices agree with accounting | Unqualified; billing excluded | Typed catalog arithmetic, provider publication evidence and invoice reconciliation are different claims |
| Surviving files cover all historical consumption | Unknown | No authoritative historical denominator; absent history cannot establish its cause or prove completeness |

The overall native result is **Incomplete**. Source and daily checks each retain
38 `last_usage_disagreement` findings. Independent raw JSON inspection maps all
38 to unchanged cumulative observations repeated with a last-usage value; this
is not evidence of duplicated spend. There are no explicit session-end markers
in the selected native files. Missing-closure findings are per-record diagnostics,
not a count of unique open sessions.

The extractor reports 4 oversized source lines and 60,975 unsupported-format
observations. Independent inspection found newer Claude administrative kinds and
Codex metadata **and 1,275 Codex `token_usage_record` rows containing usage
counters**. That usage-bearing shape is unsupported by the qualified extractor.
Partial correspondence with captured `event_msg/token_count` records does not
establish safe deduplication or attribution semantics. Unknown shapes were not
silently treated as zero usage. The six other local source families and
unqualified billing feeds remain visibly unsupported.

### Browser, authority and lifecycle evidence

The ordinary shipped companion and API were exercised through a real production
browser build: machine registration, credential issuance, claim, capture,
readiness, run, mismatch drilldown, cancellation, acknowledged partial evidence,
read-only sharing, reports, bundles, retention changes and deletion. There were
no mocked network responses substituting for these flows.

Wide and narrow layouts, light and dark themes, keyboard scrolling, focus after
run selection, deletion focus and disabled acknowledgment controls were checked.
The findings region scrolls locally on the narrow viewport. Viewer credentials
can read sanitized summaries/reports and cannot read privileged evidence or
mutate audits. Synthetic authenticated-principal tests verify viewer-key
precedence; a real Entra tenant JWT issuer was not provisioned or qualified.

Deletion with a stale impact digest returned 412. Confirmed deletion made the
bundle unavailable while retaining its summary; ordinary usage and spend row
hashes were unchanged. Retention changes preserved existing evidence deadlines.
A post-fix browser cancellation released capacity, returned 204 on subsequent
claim, rejected the old lease's valid upload and seal with 409, and admitted two
fresh requests. Shared active dependents retain their request in regression tests.

Browser report/bundle requests returned 200, but the managed Chrome/Edge file
sink failed downloads, including an independent retained Blob diagnostic. Direct
authenticated HTTP exports and ordinary offline replay passed. Successful browser
file saving is therefore not claimed.

### Replay, privacy and adversarial copies

The native clean bundle and its linked presentation evaluation were exported
privately. Ordinary CLI replay of the clean evidence was run twice and produced
identical canonical result bytes with the expected Incomplete exit code 3.
Semantic drills changed only copies and regenerated integrity declarations and
recorded results; they did not rely on invalid ZIP hashes to produce failures.

| Copy changed | Stable semantic finding | Evidence boundary |
|---|---|---|
| One event token counter | `event_aggregation_difference/inputTokens` | Native projection copy |
| A duplicated event with a separate row identity | `event_aggregation_difference/requestCount` | Native projection copy; no claim the original installation duplicated spend |
| Aggregate unknown-cost count increased against the unchanged complete frozen server-event cohort | `event_aggregation_difference/unknownCostCount` | Exact count inconsistency; pricing remains Incomplete, with no inferred monetary drift |
| An applied catalog rate | `pricing_cost_difference/costUsd` | Separate invented catalog fixture because actual applied-rate provenance was unavailable |

Each corrupted copy replayed twice with identical canonical results and exit 2.
The clean originals were preserved. The published synthetic golden smoke also
replayed twice with expected exit 3 and SHA-256
`5a194bb9f85af8731a076e0fbea4baad6a57e0f8a32c5c746f3cea90d9a78f5b`.

The final native bundle contains 13 allowed entries. Inspection found no raw
source paths, raw native session/message identifiers or captured credential
values; planted privacy regression fixtures also passed. Private operator backup
handling required correction before all copy hashes and owner-only file and
directory access were verified. That handling incident is retained in the private
report and is separate from product privacy tests.

### Performance and bounds

| Actual workload | Measurement | Meaning |
|---|---|---|
| Native private bundle export | 18,613,967 bytes, 89.75 seconds | Real validated export of the linked presentation evaluation |
| Native browser presentation | One projected page, 48.07 seconds; probe POST 12.99 seconds; completed evaluation 96.91 seconds from action | Fully validated frozen inputs, actual production summary/formatter, 11 matching values |
| Synthetic pipeline and full presentation pagination | 1,000 ordinary events and 1,000 aggregates; all 5 pages, 2.42–2.58 seconds/page; probe POST 1.11 seconds | Actual paginated browser computation; event aggregation matched 1,000 groups |
| Synthetic scale bundle | 1,281,975 bytes, 5.25 seconds | Real validated export |
| Source file above 512 MiB | Actual 536,870,913-byte owned file rejected before reading; explicit `source_file_limit`, exit 3 | A byte-bound rejection, not throughput evidence |
| Capture upload byte ceiling | 536,868,126 accepted bytes in 257 chunks; the next valid 3,594-byte record crosses 512 MiB and returns 413 | Real byte limit, below the record ceiling; cancelled request retained zero bytes, records and chunks |
| Raw-record ceiling | 1,000,000 parsed repeated canonical synthetic descriptors, 534,000,000 bytes in 255 chunks; next record rejected 413 | Actual record ceiling; cancellation removed all partial chunks; repeated descriptors are not one million unique native records |

The presentation action now uses the strict `projection=presentation` evidence
view and drains every aggregate/spend page. Full snapshot, capture, canonical,
hash and semantic validation remains enforced. Cursors bind to the run, input,
snapshot and projection; unknown projections and foreign cursors are rejected.

Default detailed inspection still materializes and validates the entire cohort
per page. The native cohort has 18,394 detailed rows, requiring 92 pages at 200;
more than ten observed pages took roughly 55 seconds each before that drain was
stopped for the presentation fix. A complete 92-page inspection was not measured.
This remains a substantive scaling limitation for final review. The 512 MiB
serialized/input ceiling is **not a maximum-memory guarantee**: native API working
memory was observed around 883 MiB.

### Gates, corrections and remaining blockers

[Linux CI for commit 445570e](https://github.com/FixPortal/fixportal-observatory/actions/runs/39743335719)
passed every job and the required CI Gate: 2,253 backend tests, 2,252 passed,
0 failed and 1 existing explicit skip, in 2 minutes 54.356 seconds. Review policy
checks passed; deployment was correctly skipped. The earlier
[baseline qualification CI](https://github.com/FixPortal/fixportal-observatory/actions/runs/39736925048)
also passed. The full Web gate passed 547 tests across 66 files with the existing
120 advisory warnings and chunk-size warning. Node suites passed 153 tests;
Python policy and packaging checks passed 75 and 2 respectively. Workflow,
contract, gate-coverage and canonical-asset checks passed. The unchanged local
review-tier ShellCheck hang is distinguished from passing public Linux lint.

An earlier concurrent Windows full run timed out while native validation and
builds competed for resources. The final idle repeat, after all owned API, browser,
preview, export and backup work stopped, also **did not pass**: 2,220 reported
passed, 1 existing skip, and API/Data module timeouts at 5 minutes 1.398 seconds
and 5 minutes 1.253 seconds. Its TRX files retain the two in-flight tests cancelled
at the module deadline; unfinished work is unqualified. The five-minute cap was
unchanged, and no further full rerun was attempted.

Read-only diagnosis found no leftover active client workload or database-container
CPU/memory quota. Per-test database creation, migrations, clones and cleanup were
expensive, with substantial cumulative checkpoint I/O; that is a plausible shared
server limitation, not a proven sole cause. The same code passed the full Linux
gate above. Final review must assess the Windows nonpass independently.

| Action | Result |
|---|---|
| Reset test-owned pricing data in dependency order; reuse owned migrated templates | Provenance FK retained; isolated clone and unmigrated startup regressions passed |
| Select configured source instances separately from source family | Real native scope accepted; regression uses an ID without a family prefix |
| Fence cancelled unsealed requests; guard requested/excluded presentation checks | Focused regressions and real API/browser cancellation passed |
| Project only actual presentation inputs during pagination | Full Web and authorization/projection regressions passed; native browser probe completed |
| Clarify an exact Int64 diagnostic fixture | Precision assertion preserved; historical scanner false-positive disposition remains separately pending |
| Obtain the actual consistent installation database export | Blocked on connectivity/access; no firewall or cloud changes executed |

At this historical qualification point, release qualification remained blocked
on the installation snapshot and full audit, subsequently exercised above.
Additional explicit limits were the observed
unsupported Codex usage shape, default detailed paging cost, unavailable real
Entra issuer validation, managed-browser file saving, and macOS execution.
The deferred zero-GUID machine-rotation behavior remains a named
minor risk owned by final review. No review approval substitutes for the missing installation evidence.

### Commands and portability

Commands below omit private locations and connection values. Full commands,
cutoffs, identities, hashes, logs and copy provenance are preserved privately.
Set `TEST_DB_CONNECTION` only to an owned disposable PostgreSQL server/base.

```sh
dotnet restore AiObservatory.slnx
dotnet build AiObservatory.slnx --configuration Release --no-restore
dotnet test --solution AiObservatory.slnx --configuration Release --no-build --report-xunit-trx --results-directory <private-results> --timeout 5m
npm --prefix src/AiObservatory.Web run gate
node clients/observatory-audit.mjs poll --config <private-copy-configuration> --once
node scripts/audit-replay-smoke.mjs
dotnet AiObservatory.Audit.Cli.dll replay <private-bundle.zip> --output <new-private-output> --verifier-version 1.0.0
```

Task 13 built and inspected Windows, Linux and macOS runner archives. Windows
execution and isolated Linux Node filesystem/permissions tests passed. The actual
native bundle replayed in an owned Linux container with no network, a read-only
root and a 2 GiB memory cap: exit 3 in 73.32 seconds, exactly matching Windows
canonical result bytes. No macOS runtime was available.
Runner archives require the matching .NET 10 runtime, installed before going
offline. Artifact retention and independent backup obligations remain as
[documented](self-audit.md).

### Prospective Claude context evidence boundary

The context repair adds Claude source version 2 with extractor 1.1.0 and independent
verifier 1.1.0/audit rule 2; Codex source version 1 and wire schema 1 remain. Source version 1
retains its original reconstruction. Application/exact-zero rule 1 remains separate.
Synthetic v2 source/daily evidence is versioned alongside the original golden-v1,
which must never be regenerated by the new verifier. Public reproduction source
identity, immutable artifact hashes and commands are recorded in the
[portable fixture guide](../tests/fixtures/audit/README.md).

Roll out the version-aware verifier/server before the companion and any collector
or catalog candidate. Resume retained companion packages using sealed original
bytes. Pending server snapshots with old pinned identities fail closed under the
new worker; the new CLI requires explicit `--reevaluate` and retains the recorded
result plus a linked new evaluation identity. This boundary provides no production
activation, historical unknown-band recovery or migration/repricing qualification.
Later composed qualification must cover those separately. Historical commands and
1.0.0 qualification results above remain descriptions of their original evidence.

### Claude reconciliation and application qualification

Synthetic migration tests replace an old mixed daily key with lower, upper and
unknown keys while retaining an unchanged flat key. Retry after every posting
boundary, using only persisted server inventory, converges to exact token and
fixture-money totals and stable keys. Invalid, overflowing and incomplete source
discovery retain the existing correction fences. These are synthetic controls;
they do not establish complete historical source coverage.

Replacement is active-first and is not atomic. The interruption fixture reaches
340,047 input tokens before the old mixed key is zeroed, then converges to 170,029.
The temporary excess is exactly the old key's contribution, including its output,
cache and thought counters. Four active keys leave five retained usage postings
after correction: one old zero row remains. `RequestCount` counts those postings,
not native or provider requests. A rollout that requires atomic totals or removal
of that residual posting needs a separate operational decision.

Real store activation and repricing tests cover both context bands for Notional
and ListPriceEstimate amounts, exact cache-duration arithmetic, immutable prior
applications, selected snapshot/entry/context and aggregate reconciliation.
Repeated identical activation adds no application. Existing real-service controls
preserve missing-context known amounts without new successful quote provenance,
keep unknown amounts unknown, exclude billed/provider-estimated amounts, and
reject a quote after a RawPayload-only source correction. Retained known money
does not establish a historical prompt band.

Local composed verification passed the Release solution build with zero warnings,
1,999 tests across the five non-Data backend modules, 17 focused Data application
tests, all 552 frontend tests, 217 Node tests and replay smoke. The Node command
excludes the exact public-restore contract test that unconditionally launches
restore/build; it is not counted passed. The frontend retains 120 advisory lint
warnings and the existing chunk-size warning. The new interruption fixture was
then strengthened with nonzero one-hour cache tokens and its focused test passed.
Current-head Ubuntu CI at the unchanged five-minute cap remains required for full
Data-module authority; earlier Windows nonpasses are not reclassified by these
focused results.

An owned copy restored from the original immutable installation dump completed
candidate activation through the real store and typed repricing callback. The
saved terminal probe verified zero remaining sessions. Of 407 Anthropic rows,
378 received exact-zero rule applications and 24 received catalog quote
applications: 402 applications in total, with five rows remaining unknown.
Eleven cost rows and eleven savings rows changed. Thirteen catalog quotes kept
their amounts while gaining successful application provenance; unchanged amounts
alone would not prove successful pricing. This copy contained no retained-catalog
quote or retained known-but-unsupported case, and no successful lower- or
upper-band catalog quote. Those cases are covered by the separate real-service
regression tests above, including genuine upper-band fixtures.

All 1,557 usage rows retained their six token counters and non-pricing fields.
The 963 aggregate rows retained their token and posting counters, and their exact
cost, savings and unknown-count deltas matched the usage changes. All 402 new
application revisions matched their amounts, dimensions, pricing date and rule;
the 24 catalog applications also matched their selected snapshot, hash and rate
key. Existing catalog evidence was preserved, and
billing, spend and all 32 source-state rows were unchanged. Initialization
preserved the original-table integrity digests. The selected runtime bindings
were checked; the prior exact PowerShell Newtonsoft exception remains a stated
qualification limit, so this is not a hermetic production-runtime certification.

The approved frozen Claude cohort has 16 hash-verified files and yields one
snapshot for its selected UTC day. It links uniquely to one original source/day
posting with identical token totals and the same key. Its provisional plan has
one active replay and no zero correction, and was not posted. It supplies no
versioned lower/upper prompt-context evidence. This establishes a bounded linkage
control, not complete historical coverage or an observed mixed-key migration.
Actual native migration qualification remains **Incomplete**; interruption,
temporary overlap and residual-zero-row behavior are established by the
synthetic controls above. No historical band is inferred from totals or money.
No production activation, collector rollout or historical rewrite is authorized
by this owned-copy evidence. Current-head full Data-module CI remains pending.
