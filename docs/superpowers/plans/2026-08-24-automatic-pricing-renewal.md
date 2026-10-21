# Automatic Pricing Renewal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refresh first-party OpenAI, Claude, Kimi, and relevant Google pricing every day while preserving a bundled and database-backed last-known-good catalog.

**Architecture:** Store immutable raw-and-normalized pricing snapshots in PostgreSQL, with one active snapshot per source. Provider-specific sources parse their own document shapes behind `IPricingSource`; a shared resolver applies only observed dimensions and a transactional repricer updates estimate/notional events when a valid catalog changes.

**Tech Stack:** .NET 10, EF Core 10/Npgsql, PostgreSQL jsonb, HttpClient, System.Text.Json, SHA-256, NodaTime, xUnit v3, NSubstitute, AwesomeAssertions.

**Spec:** `docs/superpowers/specs/2026-08-24-source-aware-observability-design.md`

## Global Constraints

- Run after `2026-08-24-source-aware-usage-foundation.md`.
- Refresh daily and shortly after startup when no source succeeded in the preceding 24 hours.
- Use only the first-party OpenAI, Claude, Kimi, and Google sources named in the spec.
- Keep a bundled current catalog for cold start, offline development, and upstream outages.
- Reject malformed, partial, duplicate, overlapping, non-USD, or non-positive catalogs.
- A failed or unchanged fetch never replaces the active snapshot.
- Unknown model or required dimension returns null; there is no generic fallback rate.
- Reprice only `ListPriceEstimate` and `Notional`; never rewrite `Billed`, `ProviderEstimated`, or ambiguous legacy rows.
- Use fixed HTTPS hosts, validated redirects, a 2 MiB response limit, a 20-second timeout, and non-executable parsing.
- Do not add a Markdown parser or generic pricing-rules package.

## File Structure

- `PricingSnapshot` stores evidence and normalized JSON without forcing all providers into one relational price schema.
- `Data/Pricing/Catalogs` contains provider-specific record types and bundled JSON catalogs.
- `FirstPartyDocumentFetcher` owns the shared network trust boundary for the three Markdown sources.
- Each `*PricingSource` owns one upstream shape and returns a validated candidate.
- `UsagePriceResolver` selects the usage-date catalog and delegates to provider-specific calculators.
- `PricingRefreshWorkerService` only schedules and isolates registered sources.

---

### Task 1: Persist immutable pricing snapshots and provider catalog shapes

**Files:**
- Create: `src/AiObservatory.Data/Entities/PricingSnapshot.cs`
- Create: `src/AiObservatory.Data/Pricing/PricingSnapshotCandidate.cs`
- Create: `src/AiObservatory.Data/Pricing/PricingSnapshotStore.cs`
- Create: `src/AiObservatory.Data/Pricing/Catalogs/OpenAiPriceCatalog.cs`
- Create: `src/AiObservatory.Data/Pricing/Catalogs/AnthropicPriceCatalog.cs`
- Create: `src/AiObservatory.Data/Pricing/Catalogs/KimiPriceCatalog.cs`
- Create: `src/AiObservatory.Data/Pricing/Catalogs/GooglePriceCatalog.cs`
- Modify: `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- Modify: `src/AiObservatory.Data/AiObservatory.Data.csproj`
- Create: `src/AiObservatory.Data/Migrations/20260824100000_AddPricingSnapshots.cs`
- Create: `src/AiObservatory.Data/Migrations/20260824100000_AddPricingSnapshots.Designer.cs`
- Modify: `src/AiObservatory.Data/Migrations/AiObservatoryDbContextModelSnapshot.cs`
- Create: `tests/AiObservatory.Data.Tests/Pricing/PricingSnapshotStoreTests.cs`

**Interfaces:**
- Produces: `PricingSnapshotCandidate(Provider Provider, string SourceId, Instant RetrievedAt, string SourceUrl, string ContentHash, string RawEvidence, string NormalizedCatalog)`.
- Produces: `PricingSourceIds.OpenAi = "openai-pricing"`, `Claude = "claude-pricing"`, `Kimi = "kimi-pricing"`, and `GoogleCloudCatalog = "google-cloud-catalog"`.
- Produces: `PricingActivationResult { Activated, Unchanged }` and `PricingSnapshotStore.ActivateAsync(PricingSnapshotCandidate candidate, CancellationToken ct, Func<PricingSnapshot, CancellationToken, Task>? beforeCommit = null)`.
- Produces: `GetActiveAsync(string sourceId)` and `GetCatalogForDateAsync(Provider provider, LocalDate usageDate)`.

- [ ] **Step 1: Write last-known-good persistence tests**

```csharp
var first = Candidate(hash: "aaa", normalized: ValidCatalogJson(1m));
(await store.ActivateAsync(first, ct)).Should().Be(PricingActivationResult.Activated);
(await store.ActivateAsync(first, ct)).Should().Be(PricingActivationResult.Unchanged);

var second = Candidate(hash: "bbb", normalized: ValidCatalogJson(2m));
(await store.ActivateAsync(second, ct)).Should().Be(PricingActivationResult.Activated);

var snapshots = await db.PricingSnapshots.AsNoTracking().OrderBy(x => x.RetrievedAt).ToListAsync(ct);
snapshots.Should().HaveCount(2);
snapshots.Single(x => x.IsActive).ContentHash.Should().Be("bbb");
snapshots.Single(x => !x.IsActive).ContentHash.Should().Be("aaa");
```

Also test unique `(SourceId, ContentHash)`, one active row per source, raw evidence retention, and future-effective entries selected by usage date.

- [ ] **Step 2: Run the focused test and confirm missing types fail**

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~PricingSnapshotStoreTests"
```

- [ ] **Step 3: Add the entity and atomic activation**

`PricingSnapshot` contains `Id`, `Provider`, `SourceId`, `RetrievedAt`, `SourceUrl`, 64-character `ContentHash`, `RawEvidence`, `NormalizedCatalog`, and `IsActive`. Map both JSON strings as `jsonb`, add a unique `(SourceId, ContentHash)` index, and a filtered unique `SourceId` index where `IsActive` is true.

`ActivateAsync` validates source/provider consistency, starts a transaction, returns `Unchanged` when the hash already exists, clears the old active flag, inserts the candidate, invokes the optional `beforeCommit` callback, and commits. Validation happens before the transaction; a rejected candidate never changes database state. The callback is the later repricing seam; do not add an event bus.

- [ ] **Step 4: Add provider-specific catalog records**

Use explicit records rather than a nullable universal row. Every entry includes aliases, `EffectiveFrom`, and `EffectiveDateIsProviderDeclared`. Examples:

```csharp
public sealed record AnthropicPriceEntry(
    string ModelPrefix,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite5m,
    decimal CacheWrite1h,
    decimal? BatchInput,
    decimal? BatchOutput,
    decimal? FastInput,
    decimal? FastOutput,
    decimal? UsInferenceMultiplier
);

public sealed record KimiPriceEntry(
    string ModelPrefix,
    LocalDate EffectiveFrom,
    decimal CacheHit,
    decimal CacheMiss,
    decimal Output,
    bool HighSpeed,
    decimal? BatchMultiplier
);
```

Each catalog validator enforces unique non-overlapping keys, positive rates, USD, ordered effective windows, and its provider's required dimensions. Add a `Pricing/Bundled/*.json` content glob to the Data project now; provider tasks create those generated normalized files after their parser fixtures are green.

- [ ] **Step 5: Generate and verify the migration**

```powershell
dotnet ef migrations add AddPricingSnapshots --project src/AiObservatory.Data --startup-project src/AiObservatory.Api
```

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~PricingSnapshotStoreTests|FullyQualifiedName~UsageMigrationTests"
```

- [ ] **Step 6: Commit**

```powershell
git add src/AiObservatory.Data tests/AiObservatory.Data.Tests
```

```powershell
git commit -m "feat(pricing): store last-known-good catalogs"
```

### Task 2: Parse OpenAI, Claude, and Kimi first-party Markdown

**Files:**
- Create: `src/AiObservatory.Ingest/Sources/IPricingSource.cs`
- Create: `src/AiObservatory.Ingest/Pricing/FirstPartyDocumentFetcher.cs`
- Create: `src/AiObservatory.Ingest/Pricing/OpenAiPricingSource.cs`
- Create: `src/AiObservatory.Ingest/Pricing/ClaudePricingSource.cs`
- Create: `src/AiObservatory.Ingest/Pricing/KimiPricingSource.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/FirstPartyDocumentFetcherTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/OpenAiPricingSourceTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/ClaudePricingSourceTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/KimiPricingSourceTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/Fixtures/openai-pricing.md`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/Fixtures/claude-pricing.md`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/Fixtures/kimi-k3.md`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/Fixtures/kimi-k27-code.md`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/Fixtures/kimi-k26.md`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/Fixtures/kimi-k25.md`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/Fixtures/kimi-batch.md`
- Create: `src/AiObservatory.Data/Pricing/Bundled/openai.json`
- Create: `src/AiObservatory.Data/Pricing/Bundled/claude.json`
- Create: `src/AiObservatory.Data/Pricing/Bundled/kimi.json`

**Interfaces:**
- Produces: `IPricingSource.SourceId` and `FetchAsync(CancellationToken) -> PricingSnapshotCandidate?`.
- Consumes: provider-specific catalog records and `PricingSnapshotCandidate` from Task 1.
- Produces source IDs `openai-pricing`, `claude-pricing`, and `kimi-pricing`.

- [ ] **Step 1: Save small exact upstream fixtures and write parser contracts**

Each fixture contains the required headings and representative current rows copied from the first-party document, not the whole web page. Test the important distinctions explicitly:

```csharp
[Theory]
[InlineData("gpt-5.4", "standard", "short", "global", 1.25, 10.00)]
[InlineData("gpt-5.4", "batch", "short", "global", 0.625, 5.00)]
public void OpenAi_parser_retains_lane_dimensions(string model, string processing, string context, string region, decimal input, decimal output)
{
    var catalog = OpenAiPricingSource.Parse(Fixture("openai-pricing.md"), ObservedOn);
    catalog.Resolve(model, processing, context, region)!.Input.Should().Be(input);
    catalog.Resolve(model, processing, context, region)!.Output.Should().Be(output);
}
```

Claude tests cover 5-minute/1-hour writes, Batch, fast mode, and inference geography only when their fixture row states them. Kimi tests assert K3, K2.7 standard/HighSpeed, K2.6, K2.5, and the 0.6 Batch multiplier for only eligible models.

Add rejection theories for missing required headings, duplicate keys, overlaps, partial tables, non-USD currency, zero/negative rates, and unknown columns that change the required table shape.

- [ ] **Step 2: Run tests and confirm the sources are missing**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~PricingSourceTests|FullyQualifiedName~FirstPartyDocumentFetcherTests"
```

- [ ] **Step 3: Implement the fixed network boundary**

`FirstPartyDocumentFetcher` accepts a fixed URI and allowed host set supplied by each source. It rejects non-HTTPS URIs, disables automatic redirects, follows at most three redirects after validating every destination host, uses a linked 20-second cancellation timeout, stops after 2 MiB, and returns UTF-8 text. It never logs response bodies, signed URLs, or query strings.

- [ ] **Step 4: Implement strict provider parsers**

Use line enumeration and exact Markdown table headers; no HTML execution and no Markdown dependency. Parse currency with `InvariantCulture`, convert all rates to USD per million tokens, sort aliases longest-first, and reject any ambiguous duplicate. When the provider gives no effective date, set `EffectiveFrom` to the fetch observation date and `EffectiveDateIsProviderDeclared = false`.

`FetchAsync` hashes the exact raw UTF-8 evidence with `SHA256.HashData`, serializes the typed catalog with the application's existing JSON options, and returns a candidate. A 304 response or identical hash returns the same candidate; the store decides `Unchanged`.

After the parser tests pass, serialize those complete relevant-table fixtures into the three bundled normalized catalogs with source URLs and retrieval date `2026-08-24`. Kimi must contain the exact five model/variant rows and 0.6 Batch eligibility from the spec. Claude must contain no `2026-09-01` Sonnet increase. Validate each bundle by deserializing it through the same catalog validator in the parser test.

- [ ] **Step 5: Run parser and security tests**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~PricingSourceTests|FullyQualifiedName~FirstPartyDocumentFetcherTests"
```

Expected: all valid fixture shapes normalize and every malformed/redirect/size/timeout case is rejected.

- [ ] **Step 6: Commit**

```powershell
git add src/AiObservatory.Ingest src/AiObservatory.Data/Pricing/Bundled tests/AiObservatory.Ingest.Tests
```

```powershell
git commit -m "feat(pricing): parse first-party model rates"
```

### Task 3: Acquire narrowly dimensioned Google catalog prices

**Files:**
- Create: `src/AiObservatory.Ingest/Pricing/GooglePricingSource.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/GooglePricingSourceTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/Fixtures/google-skus-page-1.json`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/Fixtures/google-skus-page-2.json`
- Create: `src/AiObservatory.Data/Pricing/Bundled/google.json`
- Modify: `src/AiObservatory.Ingest/IngestOptions.cs`
- Modify: `tests/AiObservatory.Ingest.Tests/IngestOptionsTests.cs`

**Interfaces:**
- Implements: `IPricingSource` with source ID `google-cloud-catalog`.
- Consumes: `GooglePriceCatalog` from Task 1.
- Produces: exact Google entries keyed by service, SKU, region, modality, tier, cache lane, and context threshold.

- [ ] **Step 1: Write pagination and exact-dimension tests**

```csharp
var candidate = await source.FetchAsync(CancellationToken.None);
var catalog = JsonSerializer.Deserialize<GooglePriceCatalog>(candidate!.NormalizedCatalog)!;

catalog.Resolve("Gemini Enterprise Agent Platform", "sku-input-us", "us", "text", "standard", "none", 128_000)
    .Should().NotBeNull();
catalog.Resolve("Gemini Enterprise Agent Platform", "sku-input-us", "europe", "text", "standard", "none", 128_000)
    .Should().BeNull();
handler.Requests.Should().HaveCount(2);
```

Add a test where page two fails and assert `FetchAsync` throws without returning a candidate.

- [ ] **Step 2: Run the test and confirm failure**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~GooglePricingSourceTests"
```

- [ ] **Step 3: Implement Catalog API pagination and filtering**

Bind `GOOGLE_CLOUD_CATALOG_API_KEY` and the configured service ID. Request every page from the official Cloud Billing Catalog API. Keep only explicitly mapped relevant SKUs; preserve the upstream SKU ID, description, service, region taxonomy, pricing unit, aggregation level, tier threshold, and effective time in normalized JSON. Convert nanos/units exactly to decimal USD per million pricing units.

Do not infer general Gemini API rates from the Agent Platform page. An unfamiliar SKU is ignored with one count in the completion log; a mapped SKU with an unrecognized pricing expression rejects the entire candidate.

Serialize the exact mapped fixture rows into `Pricing/Bundled/google.json`. If the official catalog does not expose every product/tier/region/modality/context dimension needed for a safe estimate, commit a valid empty entries array with the source URL and retrieval date instead of a broad fallback.

- [ ] **Step 4: Run focused tests**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~GooglePricingSourceTests|FullyQualifiedName~IngestOptionsTests"
```

- [ ] **Step 5: Commit**

```powershell
git add src/AiObservatory.Ingest src/AiObservatory.Data/Pricing/Bundled tests/AiObservatory.Ingest.Tests
```

```powershell
git commit -m "feat(pricing): import Google billing catalog rates"
```

### Task 4: Schedule daily refresh with persisted source status

**Files:**
- Create: `src/AiObservatory.Ingest/Pricing/BundledPricingCatalogLoader.cs`
- Create: `src/AiObservatory.Ingest/Pricing/PricingRefreshWorkerService.cs`
- Modify: `src/AiObservatory.Ingest/Program.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/BundledPricingCatalogLoaderTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Pricing/PricingRefreshWorkerServiceTests.cs`
- Modify: `tests/AiObservatory.Ingest.Tests/IngestHostTests.cs`

**Interfaces:**
- Consumes: `IEnumerable<IPricingSource>`, `PricingSnapshotStore`, `SourceSyncStateStore`, and `IClock`.
- Produces: immediate cold-start activation and at-most-daily remote refresh.

- [ ] **Step 1: Write scheduler and failure-isolation tests**

```csharp
await worker.RunOnceAsync(CancellationToken.None);

await broken.Received(1).FetchAsync(Arg.Any<CancellationToken>());
await healthy.Received(1).FetchAsync(Arg.Any<CancellationToken>());
(await store.GetActiveAsync("healthy-pricing", ct)).Should().NotBeNull();
(await states.GetAsync("broken-pricing", ct))!.ConsecutiveFailureCount.Should().Be(1);
```

Test that bundled catalogs activate when the database is empty, a remote source with success less than 24 hours ago is skipped, an older one runs, and parser/network failure retains the active hash.

- [ ] **Step 2: Run tests and confirm the worker is missing**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~PricingRefreshWorkerServiceTests|FullyQualifiedName~BundledPricingCatalogLoaderTests"
```

- [ ] **Step 3: Load bundles before remote sources**

The loader reads the four copied JSON files from `AppContext.BaseDirectory`, validates them through the same typed catalog validators used by remote sources, and activates only when that source has no active snapshot. Its source URL remains the first-party URL and its raw evidence is the bundled JSON.

- [ ] **Step 4: Implement the simple daily loop**

`ExecuteAsync` calls `RunOnceAsync`, then awaits `Task.Delay(TimeSpan.FromDays(1), stoppingToken)`. `RunOnceAsync` skips a source whose persisted `LastSuccessAt` is newer than `now - Duration.FromDays(1)`, records attempt/success/failure through `SourceSyncStateStore`, and isolates sources exactly like the usage worker. Register each definition with `ExpectedRefreshInterval = Duration.FromDays(1)`. Register all first-party sources conditionally only when their required configuration exists; OpenAI, Claude, and Kimi public documents require no secret.

- [ ] **Step 5: Verify host composition and worker tests**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~PricingRefreshWorkerServiceTests|FullyQualifiedName~BundledPricingCatalogLoaderTests|FullyQualifiedName~IngestHostTests"
```

- [ ] **Step 6: Commit**

```powershell
git add src/AiObservatory.Ingest tests/AiObservatory.Ingest.Tests
```

```powershell
git commit -m "feat(pricing): refresh catalogs daily"
```

### Task 5: Centralize estimation and transactionally reprice eligible events

**Files:**
- Create: `src/AiObservatory.Data/Pricing/IProviderPriceCalculator.cs`
- Create: `src/AiObservatory.Data/Pricing/UsagePriceResolver.cs`
- Create: `src/AiObservatory.Data/Pricing/OpenAiPriceCalculator.cs`
- Create: `src/AiObservatory.Data/Pricing/AnthropicPriceCalculator.cs`
- Create: `src/AiObservatory.Data/Pricing/KimiPriceCalculator.cs`
- Create: `src/AiObservatory.Data/Pricing/GooglePriceCalculator.cs`
- Create: `src/AiObservatory.Data/Pricing/PricingRepricingService.cs`
- Modify: `src/AiObservatory.Data/Repositories/IUsageRepository.cs`
- Modify: `src/AiObservatory.Data/Repositories/UsageRepository.cs`
- Modify: `src/AiObservatory.Data/ServiceCollectionExtensions.cs`
- Delete: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiPricingOptions.cs`
- Delete: `src/AiObservatory.Data/pricing.anthropic.json`
- Modify: `src/AiObservatory.Data/Pricing/AnthropicPricing.cs`
- Modify: `src/AiObservatory.Api/Endpoints/EventsEndpoints.cs`
- Modify: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiIngestionService.cs`
- Modify: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicIngestionService.cs`
- Modify: `src/AiObservatory.Ingest/appsettings.json`
- Modify: `src/AiObservatory.Ingest/Program.cs`
- Modify: `clients/observatory-sweep.mjs`
- Modify: `clients/observatory-sweep.test.mjs`
- Create: `tests/AiObservatory.Data.Tests/Pricing/UsagePriceResolverTests.cs`
- Create: `tests/AiObservatory.Data.Tests/Pricing/PricingRepricingServiceTests.cs`
- Modify: `tests/AiObservatory.Ingest.Tests/Services/OpenAiIngestionServiceTests.cs`
- Modify: `tests/AiObservatory.Ingest.Tests/Services/AnthropicIngestionServiceTests.cs`

**Interfaces:**
- Produces: `UsagePriceQuote(decimal CostUsd, decimal? CacheSavingsUsd)`.
- Produces: `IProviderPriceCalculator.Provider` and `Calculate(UsageEvent usage, string normalizedCatalog) -> UsagePriceQuote?`.
- Produces: `UsagePriceResolver.ResolveAsync(UsageEvent usage, CancellationToken) -> UsagePriceQuote?`.
- Produces: `IUsageRepository.UpdateEventPricingAsync(Guid eventId, UsagePriceQuote? quote, CancellationToken)` for atomic event/aggregate pricing changes.

- [ ] **Step 1: Write resolver and repricing tests**

```csharp
var eligible = Event(CostBasis.ListPriceEstimate, Provider.OpenAI, completeDimensions: true, cost: 1m);
var notional = Event(CostBasis.Notional, Provider.Anthropic, completeDimensions: true, cost: 2m);
var billed = Event(CostBasis.Billed, Provider.OpenAI, completeDimensions: true, cost: 3m);
var unknown = Event(CostBasis.ListPriceEstimate, Provider.Google, completeDimensions: false, cost: null);

await service.RepriceProviderAsync(Provider.OpenAI, ct);
await service.RepriceProviderAsync(Provider.Anthropic, ct);
await service.RepriceProviderAsync(Provider.Google, ct);

(await Reload(eligible.Id)).CostUsd.Should().NotBe(1m);
(await Reload(notional.Id)).CostUsd.Should().NotBe(2m);
(await Reload(billed.Id)).CostUsd.Should().Be(3m);
(await Reload(unknown.Id)).CostUsd.Should().BeNull();
```

Add calculator tests for every dimension named in the spec and an unknown-model test that returns null without a fallback.

- [ ] **Step 2: Run tests and confirm missing resolver failure**

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~PriceResolverTests|FullyQualifiedName~PricingRepricingServiceTests"
```

- [ ] **Step 3: Implement provider calculators and central resolution**

Register four calculators with `TryAddEnumerable`. `UsagePriceResolver` selects the calculator by persisted `Provider`, loads the active catalog covering `usage.OccurredAt.InUtc().Date`, and returns null when no calculator, catalog, model, or required raw-payload dimension matches. Rate-limit unknown-dimension warnings by `(Provider, Model, missing-dimension-set)` in memory; the warning cache is diagnostic only.

- [ ] **Step 4: Reprice through the repository transaction**

`PricingRepricingService` scans only the affected provider and eligible bases. For each changed result, call `UpdateEventPricingAsync(Guid, UsagePriceQuote?)`, which subtracts the old aggregate cost/cache-savings/unknown flags, changes the raw event, and adds the new values/unknown flags in the same transaction. Cache savings is the counterfactual full-input price minus the observed cache-lane price; it is null when either lane is not explicitly priced. The deliberate initial ceiling is a full affected-provider scan; add this comment:

```csharp
// ponytail: pricing changes are rare and Observatory volume is modest; target by effective date/model if this scan is measured as slow.
```

Pass repricing as `PricingSnapshotStore.ActivateAsync`'s `beforeCommit` callback. Because the store and repricer share the scoped `AiObservatoryDbContext`, activation plus every affected event/aggregate change commits in the store's one transaction before readers can observe the new active snapshot.

- [ ] **Step 5: Remove hand-maintained production pricing**

Delete `OpenAiPricingOptions`, remove pricing configuration from `appsettings.json`, and reduce `AnthropicPricing.cs` to the typed calculator/validator needed by the new catalog. API and provider ingestion call `UsagePriceResolver`; client-supplied `CostUsd` is ignored for `ListPriceEstimate` and `Notional`, and unknown prices store null with the requested basis. Reject explicit `Billed` on `/events` with guidance to use the spend/billing path; legacy payloads with omitted provenance remain accepted as `Unknown` for compatibility.

Remove `OPENAI_PRICING`, `COPILOT_PRICING`, their defaults, `pickRates`, and `costUsd` from the Node sweeper. Local payloads send tokens plus observed pricing dimensions and `costUsd: null`; the server is now the only price authority. Codex local events explicitly record the permitted `standard/short-context/global` assumption. Claude local events carry observed tier/speed/geography/cache duration. Copilot may carry an obvious catalog provider derived from an exact known model prefix but receives no missing tier/region defaults; ambiguous models remain unpriced. Generic `kimi-for-coding` remains unpriced until the local record identifies a first-party model/variant.

- [ ] **Step 6: Run price, ingestion, repository, and client tests**

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~Pricing|FullyQualifiedName~UsageRepositoryTests"
```

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~Pricing|FullyQualifiedName~OpenAiIngestionServiceTests|FullyQualifiedName~AnthropicIngestionServiceTests"
```

```powershell
node --test clients/observatory-sweep.test.mjs
```

- [ ] **Step 7: Run the complete pricing gate**

```powershell
dotnet csharpier check .
```

```powershell
dotnet build AiObservatory.slnx --configuration Release
```

```powershell
dotnet test --solution AiObservatory.slnx --configuration Release --no-build --timeout 5m
```

- [ ] **Step 8: Commit**

```powershell
git add src tests clients
```

```powershell
git commit -m "feat(pricing): centralize and renew usage estimates"
```
