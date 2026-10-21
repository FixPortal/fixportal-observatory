# Mutation testing

Stryker.NET runs weekly and on demand against the API (`.github/workflows/mutation.yml`,
`stryker-config.json`). The score is informational (`break: 0`); execution and report
failures are real failures.

This document exists because the setup looks over-specified and is not. Every constraint
below was established by measurement after the nightly run failed for three consecutive
days in July 2026, and three plausible fixes shipped without changing the outcome.

The September 10 serial qualification took **60–84 seconds locally**, including the final
billing assertions below. Diagnose material slowdowns using the lane inventory and per-mutant
cost, not the historical 40-second parallel baseline.

## Run Stryker from the unit test project directory

This is the single most important line in the workflow:

```yaml
working-directory: tests/AiObservatory.Api.Tests
run: dotnet stryker --config-file ../../stryker-config.json
```

**Stryker's `test-projects` config key does not restrict execution.** Run from the
repository root and Stryker discovers *every* test project that references
`AiObservatory.Api` — including `AiObservatory.Api.IntegrationTests` — and runs all of them
against every mutant, whatever `test-projects` says. Run from the unit test project and it
uses that project alone.

The difference, same config otherwise, as measured in July 2026:

| Invocation | Tests in the lane | Runtime |
|---|---|---|
| From repository root | 246 (unit + integration) | >55 min (timed out) |
| From `tests/AiObservatory.Api.Tests` | 373 (unit only, re-counted 2026-09) | ~40s |

**Do not use those test counts as the regression check.** They were a usable tell only while
the unit project was small; it has since grown past 240 tests on its own (see *What the score
means now*), so "how many tests" no longer distinguishes a correct run from a broken one, and
the two numbers will keep converging.

Check the **runtime and the per-mutant cost** instead — a correct run is seconds per mutant at
worst, a broken one is tens of seconds. Stryker's startup log reports the count, and the
`Number of tests found` line is still the right place to look; what matters is whether it is in
the neighbourhood of `AiObservatory.Api.Tests`' own test count, which you can get from:

```bash
dotnet test tests/AiObservatory.Api.Tests --list-tests
```

If the lane holds materially more tests than that project has, the integration project has been
pulled in and this has regressed. Check it first whenever the run slows down.

## The test-project split is what makes that possible

`AiObservatory.Api.Tests` is unit-only. Everything that boots a host via
`WebApplicationFactory` or touches PostgreSQL lives in `AiObservatory.Api.IntegrationTests`.

This was previously attempted with `test-case-filter: "Category!=Integration"`. That does
not work: **`test-case-filter` is implemented only in Stryker's VSTest runner.** In Stryker
4.16.0's source, `TestCaseFilter` appears solely under `src/Stryker.TestRunner.VsTest/**`;
the MTP runner project has no reference to it. The config parses, the run proceeds, no
warning is emitted, and the filter does nothing. The traits are still correct for the
`dotnet test` lanes — they are simply not load-bearing for Stryker, and must never be
relied on to be.

Two guards enforce the split, because a convention would erode:

- `ArchitectureTests.Unit_test_project_must_not_reference_database_or_host_packages` fails
  the build if the unit project gains `Mvc.Testing`, `Npgsql` or `Testcontainers`.
- The mutation job runs with **no PostgreSQL service and no `TEST_DB_CONNECTION`**, so a
  database-backed test landing in the unit project fails immediately and visibly rather
  than costing an hour a night.

Note `tests/PostgresTestAssemblyFixture.cs` is an xUnit **assembly** fixture: it starts a
Testcontainers PostgreSQL when `TEST_DB_CONNECTION` is unset, and assembly fixtures run
whatever the test filter says. It is linked into the integration project only, and must
stay that way.

## `test-runner` must be `mtp`

`vstest` looks attractive because it honours `test-case-filter`. It produces **invalid
results** here: against xunit.v3 built as `OutputType=Exe`, Stryker's VSTest runner executes
the tests but never attributes a failure to a mutant.

Measured on `InsightResponseParser`, which has dedicated unit tests: `vstest` killed **0 of
19** mutants. On the full scoped set it reported `Killed: 0, Survived: 224, Timeout: 194` —
and a "46.41%" score that was purely the timeouts, since Stryker counts a timeout as a kill.
Its coverage capture fails under the same runner (`It looks like the test coverage capture
failed`) for what is presumably the same reason.

A slow correct answer beats a fast wrong one; here `mtp` is both correct and fast.

## `coverage-analysis` is `perTest`

Correct *given* the split, and worth understanding, because it was measured as catastrophic
before it:

| Configuration | Per mutant |
|---|---|
| `mtp`, `perTest`, mixed unit + integration lane | ~39s |
| `mtp`, `perTest`, unit-only lane | ~0.4s |

`perTest` runs only the tests that **cover** each mutant. While the integration tests were
in the lane, the tests covering an endpoint mutant were exactly the expensive
`WebApplicationFactory` ones, so coverage analysis selected the worst possible set. With a
unit-only lane it selects a handful of fast tests, which is what it is for.

## `mutate` is scoped, on purpose

Mutating the whole API produced 1988 mutants. Most bought nothing: a surviving mutant in DI
wiring, a dashboard read endpoint or a prompt builder does not change an engineering
decision. The globs cover the surfaces where a silent wrong answer costs money or leaks
data — FX conversion, billed spend and its idempotency keys, the ledger write path, and the
auth filters.

### What the score means now

**2026-09-04 baseline: 46.3% — 222 of 436 valid mutants uncovered, up from ~19%.** The ~19% was an honest reading of a real gap:
most scoped mutants reported `NoCoverage` because the money paths — FX conversion, the GitHub
billing sync, the ledger's own validation — were exercised only by integration tests, which
are deliberately not in this lane. That gap has now been closed where it can be, by unit tests
that take the services directly rather than through HTTP:

- `GitHubBillingSyncServiceTests` — the product/vendor/category map, per-(month, product, SKU)
  aggregation, the open-month upsert, and every skip decision (missing catalog row,
  unresolvable rate, rounds-to-zero, rejected save).
- `SpendEntryValidationTests` / `SpendCatalogValidationTests` — the two validators every
  charge and catalog row passes through.
- `FxRateProviderTests` — both rate paths, including the uncached-fallback retry.
- `GitHubBillingDateConverterTests` — the month marker each charge is filed under.

Doing this needed two things worth knowing about:

- **`Microsoft.EntityFrameworkCore.InMemory` in the unit project.** Not a database: no server,
  no container, nothing to connect to, microseconds per mutant. It is what makes a service
  taking a `DbContext` mutation-testable at all. It does **not** enforce check constraints or
  unique indexes, so anything asserting on those still belongs in the integration project.
  `ArchitectureTests.Unit_test_project_must_not_reference_database_or_host_packages` documents
  why it is not on the forbidden list.
- **A few validators are `internal` rather than `private`,** with `InternalsVisibleTo` on the
  API project — `SpendEntriesEndpoints.Validate`, `SpendCatalogEndpoints.Slug`/`ValidateName`/
  `ValidateColorVar`. Reaching them through the HTTP pipeline would put them in the
  integration project, outside this lane. Same precedent as
  `GitHubActivityEndpoints.ComputeSuccessRate`.

The 2026-09-10 local run reports 222 `NoCoverage` mutants. These are outside this
unit-only lane; that is a limitation of its scope, not proof that every mutation
would be caught by the integration suite:

| File | Mutants | Why |
|---|---:|---|
| `SpendCatalogEndpoints.cs` | 93 | private async HTTP handler bodies |
| `SpendEntriesEndpoints.cs` | 119 | private async HTTP handler bodies |
| `GitHubBillingRegistration.cs` | 9 | DI wiring |
| `ApiKeyEndpointFilter.cs` | 1 | HTTP authentication path |

Those handler bodies are exercised by the WAF tests in the integration project, which this
lane deliberately excludes. Chasing them here would mean either booting a host (which is what
made the run time out in the first place) or refactoring endpoints for testability, and
neither buys a better engineering decision.

Most surviving mutants are string literals and removed log statements, which are only killable
by asserting on log text. One is genuinely equivalent: `isNew = true` on the insert path is
unobservable because `Aggregate` guarantees one line per entry key per run, so no later line in
the same run can collide with it.

## Warnings-as-errors qualification (2026-09-10)

Two local Stryker 4.16.0 runs used the same API source at `7ea496f`, the same
`stryker-config.json`, and the unit-project invocation above. Only
`TreatWarningsAsErrors` changed between runs; it was restored to `true` afterwards.
Both discovered 428 unit tests. Counts below come from the scoped JSON reports,
not Stryker's broader compilation-stage console count.

| Setting | Valid | Compile errors | No coverage | Killed | Survived | Score |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `true` (retained) | 455 | 143 | 222 | 222 | 11 | 48.8% |
| `false` (comparison only) | 455 | 143 | 222 | 221 | 12 | 48.6% |

Both had zero timeouts and 426 ignored mutants. The compile-error sets matched
by file, start location, and mutator. This refutes warnings-as-errors shrinking
the testable set **for this source/configuration**: both expose 233 covered,
compiling mutants. Matching by file, location, mutator, and replacement reveals
19 killed/survived outcome changes (a net difference of one), including auth and
billing aggregation mutations. Their cause remains unverified; this comparison
does not establish repeatable kill attribution or justify relaxing compiler enforcement.
The existing compile errors and uncovered mutants have not been eliminated.

Reports are locally generated under the unit project's `StrykerOutput/`:
`2026-09-10.14-49-40/reports/mutation-report.json` (`true`) and
`2026-09-10.14-53-43/reports/mutation-report.json` (`false`). Summarize either with
`scripts/summarize-stryker.ps1 -ReportPath <report>`; these generated reports are
not committed. This is a local qualification, not a replacement for the weekly
GitHub artifact.

One concrete gap was isolated and fixed in the tests: routing every method through
GET authorization survived a serial run of `ApiKeyEndpointFilter.cs`. The new
four-case theory rejects POST/PUT/PATCH/DELETE with a valid read-only key. Manually
injecting that routing fault failed all four cases; restoring production logic
passed. Serial Stryker on the same file then reported 36 killed, zero survived,
one uncovered (97.30%, not 100%). No production authorization code changed.

Reproduce from the unit-test directory:

```powershell
dotnet stryker --config-file ../../stryker-config.json --mutate '**/ApiKeyEndpointFilter.cs' --concurrency 1
```

The before/after reports are `2026-09-10.15-09-46` and `2026-09-10.15-12-00`
under `StrykerOutput/`. This verifies that specific regression guard; it does not
resolve the broader run-to-run outcome differences above. The whole-scope score
must remain informational, and critical surviving/killed claims need targeted
verification rather than inference from its total.

## Repeatability qualification (2026-09-10 follow-up)

Repeating the unchanged source/configuration at `deabf21` reproduced the problem
without changing warnings-as-errors. Both runs discovered 432 Stryker test cases;
the ordinary MTP unit invocation expanded 437 cases.

| Workers | Run | Killed | Survived | No coverage | Compile errors | Timeouts | Seconds |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 4 | `repeatability-current-1` | 222 | 11 | 222 | 143 | 0 | 54.4 |
| 4 | `repeatability-current-2` | 221 | 12 | 222 | 143 | 0 | 58.5 |
| 1 | `repeatability-serial-1` | 189 | 44 | 222 | 143 | 0 | 65.2 |
| 1 | `repeatability-serial-2` | 189 | 44 | 222 | 143 | 0 | 76.5 |

The parallel pair changed **15 individual killed/survived outcomes**. The serial
pair matched **all 1,024 scoped records**, keyed by file, source location, mutator,
and replacement, not unstable mutant IDs. Each report also had 426 ignored mutants.
The serial score is 41.54%; the lower number is not a test-suite regression.

A concrete false kill was checked independently: deleting the final informational
log statement in `GitHubBillingSyncService.SyncAsync` passed all 437 unit cases.
Parallel Stryker called that deletion killed in one run and survived in the other;
both serial runs called it survived. The log statement was restored afterwards.
This is consistent with the MTP attribution problem reported in
[Stryker.NET #3742](https://github.com/stryker-mutator/stryker-net/issues/3742), not
proof of its precise internal mechanism in this application.

The retained configuration therefore uses **one worker**, with the same MTP runner,
coverage analysis, mutation scope, compiler enforcement, and time budgets. Do not
restore parallelism solely to recover the higher score. An upgrade must first pass
the same per-mutant repeatability comparison and targeted fault checks.

The stable survivors also exposed missing billing assertions. The aggregation test
now checks gross 31, credits -6, net 25, and the retained gross/discount/net evidence
for two records. Replacing any of those three `Sum` calls with `Max` fails that test;
all temporary mutations were restored. This adds protection without changing billing
logic or making tests depend on log wording.

After the gross/discount assertions, `repeatability-final-1` and `repeatability-final-2` again
matched all 1,024 records: 190 killed, 43 survived, 222 uncovered, 143 compile errors,
426 ignored, zero timeouts (41.76%; 74.4 and 69.8 seconds). Gross and discount
`Sum` to `Max` mutations were killed; the reported-net mutation survived because the
ledger net is reconstructed from gross minus discount. Disabling mutant mixing did
not change those counts. Independent review caught an initial misidentification of
that survivor as discount; it was a real missing assertion, not a false survivor.
The added raw `netAmount` assertion rejects a manually injected net `Sum` to `Max`
fault (expected 25, observed 15).

With all three assertions retained, `repeatability-reviewed-1` and
`repeatability-reviewed-2` matched all 1,024 records again: 191 killed, 42 survived,
222 uncovered, 143 compile errors, 426 ignored, zero timeouts (41.98%; 83.7 and
59.7 seconds). All three billing `Sum` to `Max` mutations are now reported killed.

A diagnostic run with coverage analysis off, scoped to `GitHubBillingSyncService`,
reported all 77 tested mutants killed, including the independently checked harmless
log deletion. That is another false kill, not evidence of 100% protection. Neither
diagnostic setting is retained. Until the runner is qualified against these canaries,
use direct fault injection for consequential billing/security claims and keep the
mutation score informational.

Reports remain local generated artifacts under
`tests/AiObservatory.Api.Tests/StrykerOutput/<run>/reports/mutation-report.json`.
Repeat from that test directory, using a different output name for each run:

```powershell
dotnet stryker --config-file ../../stryker-config.json --reporter json --output StrykerOutput/qualification-1
```

This mitigates the observed parallel instability; it does not prove every surviving
mutant is a real test gap. Static-initializer mutation attribution is not qualified
by this comparison, and integration-only handlers remain outside this unit lane.

## Reading a slow run

Total cost is `mutants x tests-per-mutant x test cost`, and only the first is visible in a
timeout. Establish, in order:

1. **Tests per mutant** — the `Number of tests found` line. This is the one that fails
   silently.
2. **Per-mutant cost, measured** — run Stryker locally with `mutate` narrowed to one small
   directory. It finishes in minutes and gives seconds-per-mutant directly.
3. **Mutant count** — `N total mutants will be tested`.

Multiply before concluding. Skipping to (3) is how three separate fixes shipped against this
workflow without changing the outcome — each addressing a genuine defect, none of them the
dominant term.
