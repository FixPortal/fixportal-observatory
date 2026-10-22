# Self-audit qualification — 2026-10-22

Qualification remains partial. Review batch 64 corrects the reviewed accounting,
coverage, capture-lifetime and report defects. Its focused regressions and fresh
native replay pass. The full Windows gate still times out, and acceptance 7
remains blocked on a transactional snapshot of the actual installation database.
No production data, network rule, deployment or feature setting was changed.

## Current qualification after follow-up review

The current qualified code is `499d668d87fc2740f9d852ad60cae87b625d9795`.
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
not establish CI success for this new code. The full Windows **NONPASS** below
is unchanged; no full retry or timeout increase was made.

Capture admission remains one candidate per server process before byte
allocation. Full byte transfer and validation run outside the evidence lock;
SQL revision binding and fresh publication fences reject changed generations
and authority. Multiple processes each have their own budget, and the serialized
512 MiB ceiling remains distinct from a maximum-memory guarantee.

Actual-installation acceptance 7 remains **PARTIAL/BLOCKED**. The actual database
snapshot and GitGuardian disposition await explicit authorization, and an actual
Gitar verdict has not been established. Default detailed paging, unsupported
formats, real Entra, macOS execution and browser file-saving limits remain as
documented below. The feature remains default-disabled.

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

Release qualification remains blocked on the actual installation database
snapshot and its repeated full audit. Additional explicit limits are the observed
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
