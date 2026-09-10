# Performance sweep — 2026-08-29

> **Archival experiment record, not documentation.** This describes the codebase
> as it was on the date in the filename; where it and the current code disagree,
> the code is right. It is kept as the record of why the repricing path looks the
> way it does. Audit artefacts it references live in the maintainer's private
> archive and are not published.

## Accepted finding

`PERF-001` — "Aggregate delta pair issues four PostgreSQL round trips per repriced event where one upsert suffices".

- Audit: `2026-08-29-0022-pricing-repricing-performance-audit.md` (+ `.manifest.json`, schema v2), held in the maintainer's private audit archive.
- Experiment record: `2026-08-29-0052-PERF-001-experiment.md`, same archive.
- Approval: Chris approved this single finding and its exact proposed experiment ("go for it") after being shown the three published findings and the recommendation to run PERF-001 alone. PERF-002 and PERF-003 were explicitly excluded.
- Audited commit `9fca4f1d0ebaadaf7f83fb813753748540181086`; baseline commit identical (no drift); candidate on branch `performance/perf-001-net-aggregate-delta`.

## Change and rollback

`UsageRepository.UpdateEventPricingAsync` previously called `ApplyAggregateDeltaAsync(existing, -1)` and then `(existing, +1)` around the cost mutation. Each issued an `INSERT ... ON CONFLICT DO UPDATE` on `DailyAggregates` plus an `ExecuteDeleteAsync` `RequestCount = 0` cleanup — four round trips per repriced event.

Repricing mutates only `CostUsd` and `CacheSavingsUsd`, and neither is part of the conflict key `("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis")`. Both legs therefore always resolved to the same row and reduce to a single net delta.

The path now calls one new private `ApplyRepricingCostDeltaAsync` issuing a single upsert:

- `INSERT` branch carries the full event values with `RequestCount` 1 — the repair path when the aggregate row is missing, identical in effect to the old `+1` leg.
- `ON CONFLICT DO UPDATE` branch adds only net `CostUsd`, `UnknownCostCount`, `CacheSavingsUsd` and `UnknownCacheSavingsCount` deltas. `RequestCount` and all token columns are absent from the `SET` list, so this path can never decrement `RequestCount` and the zero-row cleanup is unreachable — hence dropped here.

`ApplyAggregateDeltaAsync` is **unchanged** and still serves the ingest correction path `ApplyLockedSnapshotAsync`, where `CopyCanonicalValues` can move key dimensions and the delta pair is genuinely not collapsible. Do not extend the net-delta form to that path.

Behavioural surface: the aggregate arithmetic. Rollback: revert the single change to `src/AiObservatory.Data/Repositories/UsageRepository.cs` (+51/−2). No migration, configuration change, data repair, or dependency is involved.

## Evidence

Baseline and candidate ran the identical command, in the same worktree, on the same harness, in the same session:

```
$env:TEST_DB_CONNECTION='Host=localhost;Port=55432;Database=postgres;Username=postgres;Password=postgres'
dotnet run --project tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --configuration Release -- --explicit only --filter-method AiObservatory.Data.Tests.Pricing.PricingRepricingServiceTests.QualificationRepricesEveryEligibleEventAndItsAggregate --show-live-output on --output Detailed
```

Environment: .NET SDK 10.0.400, net10.0 Release, Windows 10.0.26200 X64, ephemeral `postgres:17` with `shared_preload_libraries=pg_stat_statements`. Attribution instrument was `pg_stat_statements`, reset immediately before each run.

| Statistic | Baseline | Candidate | Delta |
| --- | --- | --- | --- |
| Median activation (1,000 events) | 7,104.3 ms | **5,079.3 ms** | **−28.5%** |
| Throughput | 140.8 events/s | 196.9 events/s | +39.8% |
| Activations | 6,407.0 / 7,104.3 / 7,846.6 ms | 5,056.4 / 5,112.3 / 5,079.3 ms | spread 20.3% → 1.1% of median |
| Statements, whole run | 41,387 | **29,387** | −12,000 (exactly 3 per event) |
| Round trips per repriced event | 10 | **7** | −30% |
| `INSERT` on `DailyAggregates` | 8,000 | 4,000 | halved |
| `DELETE` on `DailyAggregates` | 8,000 calls, 0 rows matched | **0** | eliminated |

Materiality threshold (at most 7 calls/event **and** at least 20% median improvement against a same-session baseline) is met on both counts. Predicted improvement from removing 3 round trips at the audited ~0.651 ms each was ~1.95 ms/event; measured was 2.03 ms/event — agreement within 4%.

Raw artefacts, with SHA-256, in the `2026-08-29-0022-raw` set of the same private archive: `perf001-baseline-stats.txt`, `perf001-candidate-stats.txt`, `perf001-candidate.diff`, `pg_stat_statements.txt`.

### Correctness and normal gates

| Gate | Command | Result |
| --- | --- | --- |
| Focused correctness | the qualification workload's own assertions (all 1,000 event prices, aggregate `CostUsd`, `RequestCount`) | passed |
| Data tests | `dotnet test tests/AiObservatory.Data.Tests/...` | 182 total, 0 failed |
| Format | `dotnet csharpier check .` | 244 files, exit 0 |
| Restore | `dotnet restore AiObservatory.slnx` | success |
| Build | `dotnet build AiObservatory.slnx --configuration Release --no-restore` | 0 errors, 1 pre-existing `S3776` warning in `PromptBuilder.cs` (untouched file) |
| Test | `dotnet test --solution AiObservatory.slnx --configuration Release --no-build --report-xunit-trx --results-directory ./TestResults --timeout 5m` | **1,050 total, 0 failed** |

Frontend gates were not applicable — no web source is touched.

Invariants protected by existing tests, all passing: `RepricingUpdatesOnlyEligibleEstimatesAndRepairsAggregateCoverage` (missing-aggregate repair, unknown-cost counting), `ActivationCallbackCommitsEffectiveDateRepricingWithTheSnapshot` (effective-date windows), `ActivationCallbackFailureRollsBackSnapshotEventsAndAggregates` (rollback on failure and cancellation), `EstimatedInsertPausedAcrossActivationCannotCommitTheOldPriceAfterActivation` (advisory-lock overlap). No new test was needed.

## Boundary and review

`test-managed-product-boundary.ps1 -BaseRef 9fca4f1 -ProductPath src` returned `passed: true`, zero violations, one `lexical-review` warning at `UsageRepository.cs:315`.

Disposition: accepted, not a violation. The warning flags the raw SQL string the guard cannot interpret. Every identifier in it is literal SQL; every interpolation hole is a typed CLR local; no `DBNull.Value` is passed. Verified by reading the executed statement back from the server — all values bind as `$1`–`$21`. Manual diff review confirms no `unsafe`, intrinsics, interop, native dependency, reflection, or runtime patching, and nothing moves outside the activation transaction or advisory lock.

Composition review (one frontier reviewer, verified read-only): Q1 restart-replay `clear`, Q2 idempotency `clear`, Q4 fence pairing `clear`, Q5 partial failure `clear`. Q5 independently confirms the dropped cleanup strands nothing; Q2 independently confirms the `INSERT`-branch repair path is value-identical to the old two-leg outcome.

**Not addressed by this change, since closed:** Q3 ordering returned a `finding` that the reviewer states is **pre-existing and not introduced by this diff** — in the standalone (non-activation) reprice path, where no advisory lock is held, a concurrent estimated-ingest correction can land between the `AsNoTracking` read and the locked write, so a price is computed from tokens the event no longer has. Event and aggregate stay mutually consistent but both wrong until the next daily pass recomputes and self-heals. Fixed separately in PR #198: `UpdateEventPricingAsync` now takes the event as it was read and compares the locked row against it, leaving a moved row for the next pass.

Displaced costs: none identified. The change removes work rather than trading it — no new allocation, cache, retained memory, connection or configuration. A shorter `pg_advisory_xact_lock` hold also lowers the contention ceiling for concurrent ingest during an activation.

Commercial impact: `currencyCost: unknown`. No repricing volume, deployment topology, unit rate, utilisation assumption, or activation SLO was supplied, and the saving crosses no evidenced capacity, replica, SKU or billing boundary.

## Benchmark

`not retained` as a new artefact. The workload already lives in the repository as the opt-in `[Fact(Explicit = true)]` qualification added by PR #196 and is reused as-is. No CI performance gate was added and none is proposed.

---

# Accepted finding — `PERF-003`

"Pricing snapshot catalog is re-queried and re-materialised once per event within a single activation".

- Audit: as above. Experiment record: `2026-08-29-0905-PERF-003-experiment.md`, same archive.
- Approval: Chris, "Please continue with 1) first and then 2)", where item 2 was PERF-002 and PERF-003.
- Audited commit `9fca4f1`; **baseline commit `59cd019`** (`reviewer-findings-batch16` = `main` at `bdaa846` plus the Q3 fix), candidate on `performance/perf-003-catalog-memo`.

## Re-baselining

HEAD had moved from the audited `9fca4f1` through `bdaa846` (PERF-001) to `59cd019`, and PERF-001 changed this same path, so the audit's published baseline of 10 round trips per event no longer described it. Both arms were re-measured in one session on one harness from `59cd019`. Every figure below is against that fresh baseline.

## Change and rollback

`PricingSnapshotStore.GetCatalogForDateAsync` issued one query per event for **all** snapshot rows of the source, materialised each into an EF entity carrying its `NormalizedCatalog` and `RawEvidence` JSON, then discarded all but the covering one. Within one pass those rows cannot change: an activation holds `pg_advisory_xact_lock` across its whole repricing, and a standalone pass reprices only what it read.

`RepriceProviderAsync` now creates a `Dictionary<string, List<PricingSnapshot>>` as a **local** and threads it through `UsagePriceResolver.ResolveAsync` to the store, which reads each source's rows once and serves the rest of the pass from it. `UsagePriceResolver` and `PricingSnapshotStore` gain one `internal` overload each; the existing public signatures are unchanged and pass `null`, so `RecordEstimatedEventAsync` and every other caller are unaffected.

The cache holds the **row list**, not the resolved snapshot, and `Covers(...)` still runs per event — so two events on different dates in one pass still resolve to different snapshots. That is what `ActivationCallbackCommitsEffectiveDateRepricingWithTheSnapshot` checks, and caching the resolved snapshot instead would fail it.

Lifetime is structural rather than asserted: the dictionary is a local, no field or static holds it, so it cannot outlive the pass or the advisory lock.

Behavioural surface: none — same queries, fewer of them. Rollback: revert the three-file change. No migration, configuration, data repair or dependency.

## Evidence

Same command, harness and environment as PERF-001 above, on container `aiobs-perf003-harness` (`postgres:17`, `shared_preload_libraries=pg_stat_statements`, port 55432), `pg_stat_statements` reset before each arm.

| Statistic | Baseline (`59cd019`) | Candidate | Delta |
| --- | --- | --- | --- |
| Median activation (1,000 events) | 5,227.7 ms | **4,581.9 ms** | **−12.4%** |
| Throughput | 191.3 events/s | 218.2 events/s | +14.1% |
| Activations | 5,114.2 / 5,227.7 / 5,360.4 ms | 4,757.4 / 4,494.5 / 4,581.9 ms | spread 4.7% / 5.7% of median |
| `SELECT` on `PricingSnapshots` | 4,000 calls, 14,000 rows, 95.4 ms | **4 calls**, 14 rows, 0.1 ms | one per pass |
| Statements, whole run | 29,375 | **25,379** | −3,996 |
| Server execution, whole run | 877.1 ms | 794.7 ms | −82.4 ms |
| Round trips per repriced event | 7 | **6** | −14% |

Materiality threshold (at most 8 calls of that shape per 4,000 repricings **and** at least 5% median improvement against a same-session baseline) is met on both counts.

Predicted improvement from removing one round trip at the audited ~0.651 ms was 0.651 ms/event; measured was **0.646 ms/event** — agreement within 1%. Server execution fell by only 82.4 ms of the 645.8 ms of wall time saved, so round-trip overhead is again the mechanism rather than server work.

**A discarded first pair.** The first baseline ran while the box was still busy and returned 10,918.5 ms, which against the candidate reads as −56% — about four times what removing one of seven round trips can account for. That discrepancy was treated as a measurement fault, not a result: both arms were re-run on a quiet box and only the quiet pair is recorded here.

### Correctness and normal gates

| Gate | Result |
| --- | --- |
| Focused correctness | the qualification workload's own assertions passed in every run of both arms |
| Format | `dotnet csharpier check .` — 244 files, exit 0 |
| Build | `dotnet build AiObservatory.slnx` — 0 errors, same pre-existing `S3776` warning |
| Test | `dotnet test AiObservatory.slnx` — **1,050 total, 0 failed** on this branch; 1,052 on the measured base, whose two extra tests are PR #198's |

Invariants, each against an existing passing test rather than a new one: `ActivationCallbackCommitsEffectiveDateRepricingWithTheSnapshot` (two dates in one pass, expecting 3.00 and 2.00 — the load-bearing one), `RepricingUpdatesOnlyEligibleEstimatesAndRepairsAggregateCoverage` (two activations, `Notional` basis), `ActivationCallbackFailureRollsBackSnapshotEventsAndAggregates` (rollback). No new test was needed.

## Boundary and review

`test-managed-product-boundary.ps1 -BaseRef reviewer-findings-batch16` returned `passed: true`, zero violations, **zero warnings**. Manual diff review: no generated code, reflection, interop, package change, or ambiguous raw source. The diff is two `internal` overloads, one local, and a `TryGetValue`/`Add` around an existing query.

Displaced costs: peak working set rises for the duration of a pass by the retained catalog rows for one source — the manifest's expected trade-off, unmeasured here.

Limitations: the wall-time figure comes from a synthetic catalog smaller than production's, on one machine. The gain scales with rows-per-source, so it grows as activation history accumulates; the measured 3.5 rows per event is not a stable figure.

Commercial impact: `currencyCost: unknown`, on the same missing inputs as PERF-001.

## Rejected in the same pass — `PERF-002`

`PERF-002` proposed removing `ctx.Entry(existing).ReloadAsync(ct)` from `FindEventByIdForUpdateAsync` as a redundant round trip, **conditional** on establishing that it does not reconcile EF Core change-tracker state. It does, so the finding's own rejection condition fired and no product code changed.

Record: `2026-08-29-0817-PERF-002-experiment.md`, same archive. Removing the reload and running a focused test on the by-id path leaves the aggregate at 6.00 instead of 5.00. That test is retained, in PR #198.

## Benchmark

`not retained`. The qualification workload is unchanged and was already retained under PERF-001's separate approval. No CI performance gate was added and none is proposed.
