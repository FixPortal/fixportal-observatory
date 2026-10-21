# Provider Acquisition Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace retired or imaginary provider integrations with the strongest supported OpenAI, Anthropic/Claude, Copilot, and Google acquisition paths.

**Architecture:** Every remote client finishes and validates all pages/downloads before returning records. Usage sources write source-aware correctable snapshots; financial sources retain raw billing observations and upsert non-zero net charges into the existing spend ledger; Copilot report facts use a provider-specific entity instead of fake token rows.

**Tech Stack:** .NET 10, EF Core 10/Npgsql, PostgreSQL jsonb, NodaTime, HttpClient, Google Cloud BigQuery .NET client, xUnit v3, NSubstitute, AwesomeAssertions.

**Spec:** `docs/superpowers/specs/2026-08-24-source-aware-observability-design.md`

## Global Constraints

- Run after `2026-08-24-source-aware-usage-foundation.md` and `2026-08-24-automatic-pricing-renewal.md`.
- One source failure cannot abort another source in the polling cycle.
- Cancellation is always propagated.
- No source writes until every upstream page or signed report has completed and validated.
- API and subscription observations remain separate even when provider and model match.
- Billed cost comes from provider financial data; usage-derived estimates never enter billed spend.
- Copilot non-token metrics never become zero-token `UsageEvent` rows.
- Google billing retains service, SKU, currency, credits, billing period, and raw evidence.
- Unknown pricing dimensions retain usage with null cost rather than guessing.
- Explicit upstream plan/feature ineligibility throws `SourceUnavailableException`; authentication, timeout, and transient errors remain failures.
- Every source returns `SourceIngestionResult` with the greatest upstream observation/usage instant it committed, or null when the source supplies none.
- Do not introduce a runtime plugin framework or a universal provider client base class.

## File Structure

- `BillingObservation` retains normalized provider financial facts and raw evidence, including zero-net rows.
- `BillingObservationWriter` is the shared correction/upsert boundary for OpenAI, Anthropic, and Google; the existing ledger remains the reporting source.
- Provider folders keep their own wire records, client, source, and tests.
- `CopilotDailyReport` is deliberately separate because activity counts are not token usage or financial rows.
- Existing service registration gates remain in `Program.cs`; the central polling worker is not edited.

---

### Task 1: Add a lossless provider-billing write path

**Files:**
- Create: `src/AiObservatory.Data/Entities/BillingObservation.cs`
- Create: `src/AiObservatory.Data/Spend/BillingObservationWriter.cs`
- Move: `src/AiObservatory.Api/Services/Fx/FxRateProvider.cs` to `src/AiObservatory.Data/Spend/FxRateProvider.cs`
- Move: `src/AiObservatory.Api/Services/Fx/FxUnavailableException.cs` to `src/AiObservatory.Data/Spend/FxUnavailableException.cs`
- Modify: `src/AiObservatory.Data/AiObservatory.Data.csproj`
- Modify: `Directory.Packages.props`
- Modify: `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- Modify: `src/AiObservatory.Data/ServiceCollectionExtensions.cs`
- Modify: `src/AiObservatory.Api/Program.cs`
- Modify: `src/AiObservatory.Api/Services/GitHub/GitHubBillingSyncService.cs`
- Modify: `tests/AiObservatory.Api.Tests/Services/FxRateProviderTests.cs`
- Modify: `tests/AiObservatory.Api.IntegrationTests/Services/GitHubBillingSyncServiceTests.cs`
- Create: `tests/AiObservatory.Data.Tests/Spend/BillingObservationWriterTests.cs`
- Create: `src/AiObservatory.Data/Migrations/20260824110000_AddBillingObservations.cs`
- Create: `src/AiObservatory.Data/Migrations/20260824110000_AddBillingObservations.Designer.cs`
- Modify: `src/AiObservatory.Data/Migrations/AiObservatoryDbContextModelSnapshot.cs`

**Interfaces:**
- Produces: `BillingObservation` unique by `(SourceId, ObservationKey)`.
- Produces: `BillingObservationWriter.RecordAsync(BillingObservation observation, string vendorKey, string categoryKey, CancellationToken)`.
- Preserves: existing `FxRateProvider` behavior and GitHub billing ledger results.

- [ ] **Step 1: Write correction, zero-net, and provenance tests**

```csharp
var observation = NewObservation(key: "2026-08:sku-a", gross: 10m, credits: -2m, net: 8m);
(await writer.RecordAsync(observation, "openai", "api-usage", ct)).Should().Be(BillingWriteDisposition.Created);

var corrected = NewObservation(key: "2026-08:sku-a", gross: 12m, credits: -3m, net: 9m);
(await writer.RecordAsync(corrected, "openai", "api-usage", ct)).Should().Be(BillingWriteDisposition.Corrected);

var stored = await db.BillingObservations.AsNoTracking().SingleAsync(ct);
stored.NetAmount.Should().Be(9m);
var spend = await db.SpendEntries.AsNoTracking().SingleAsync(ct);
spend.Amount.Should().Be(9m);
spend.SourceId.Should().Be(UsageSourceIds.OpenAiCostsApi);
spend.CostBasis.Should().Be(CostBasis.Billed);
```

Add a zero-net case that stores the observation but creates no spend row, and a correction-to-zero case that removes the prior API-created spend row without removing the observation.

- [ ] **Step 2: Run tests and confirm the writer is absent**

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~BillingObservationWriterTests"
```

- [ ] **Step 3: Add the normalized billing entity**

`BillingObservation` contains `Id`, lower-case `ProviderKey`, `SourceId`, `SourceKind`, `UsageScope`, `CostBasis`, `ObservationKey`, `OccurredOn`, nullable `BillingPeriod`, nullable `Service`, nullable `Sku`, `Currency`, `GrossAmount`, `CreditAmount`, `NetAmount`, `RawPayload` jsonb, and `ObservedAt`. Use a string provider key because the existing ledger also covers GitHub, which is not a usage `Provider` enum member. Enforce `ProviderApi/Billed`, currency length 3, provider/source/key length 200, nonblank provider/source/key/currency, and exact `GrossAmount + CreditAmount = NetAmount`.

- [ ] **Step 4: Move, do not duplicate, date-correct FX**

Move the two FX files to `AiObservatory.Data.Spend`, update namespaces and existing callers/tests, and add a direct `Microsoft.Extensions.Caching.Memory` reference at the solution's current `10.0.10` Microsoft.Extensions version. Keep every fallback and cancellation rule unchanged.

- [ ] **Step 5: Implement transactional billing correction**

Resolve FX before opening the transaction. Within one transaction, insert/no-op/correct by `(SourceId, ObservationKey)`. For non-zero net amounts, upsert the corresponding `SpendEntry` with `SpendSource.Api`, provider provenance, billed basis, the observation's scope, raw payload, frozen FX, and entry key `billing:<sourceId>:<observationKey>`. Preserve manual vendor/category changes on update. If corrected net becomes zero, delete only that keyed API row.

Do not extract the existing GitHub product mapping. Refactor only its final ledger persistence to call this writer after building a `BillingObservation`; its source ID is `github-billing-api` and scope is `Mixed`.

- [ ] **Step 6: Generate the migration and run money-path tests**

```powershell
dotnet ef migrations add AddBillingObservations --project src/AiObservatory.Data --startup-project src/AiObservatory.Api
```

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~BillingObservationWriterTests|FullyQualifiedName~UsageMigrationTests"
```

```powershell
dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~FxRateProviderTests|FullyQualifiedName~GitHubBillingSyncServiceTests"
```

- [ ] **Step 7: Commit**

```powershell
git add Directory.Packages.props src tests
```

```powershell
git commit -m "feat(spend): retain provider billing observations"
```

### Task 2: Separate OpenAI usage activity from Costs API spend

**Files:**
- Replace: `src/AiObservatory.Ingest/Services/OpenAi/IOpenAiUsageClient.cs` with `src/AiObservatory.Ingest/Services/OpenAi/IOpenAiAdminClient.cs`
- Replace: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiUsageClient.cs` with `src/AiObservatory.Ingest/Services/OpenAi/OpenAiAdminClient.cs`
- Modify: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiUsageRecord.cs`
- Create: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiCostRecord.cs`
- Rename: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiIngestionService.cs` to `src/AiObservatory.Ingest/Services/OpenAi/OpenAiUsageSource.cs`
- Create: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiCostsSource.cs`
- Modify: `src/AiObservatory.Ingest/Program.cs`
- Replace: `tests/AiObservatory.Ingest.Tests/Services/OpenAiUsageClientTests.cs` with `tests/AiObservatory.Ingest.Tests/Services/OpenAiAdminClientTests.cs`
- Rename: `tests/AiObservatory.Ingest.Tests/Services/OpenAiIngestionServiceTests.cs` to `tests/AiObservatory.Ingest.Tests/Services/OpenAiUsageSourceTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Services/OpenAiCostsSourceTests.cs`

**Interfaces:**
- Implements: two `IUsageSource` registrations, `openai-usage-api` and `openai-costs-api`.
- Consumes: `UsagePriceResolver`, `IUsageRepository`, and `BillingObservationWriter`.
- Produces: usage events with service tier/context/region evidence and ledger-backed billed cost observations.

- [ ] **Step 1: Write complete-pagination and separation tests**

```csharp
var records = await client.GetUsageAsync(new(2026, 8, 1), new(2026, 8, 2), ct);
records.Should().HaveCount(2);
handler.Requests[1].Query.Should().Contain("page=cursor-2");

await usageSource.IngestAsync(new(2026, 8, 1), new(2026, 8, 2), ct);
await costsSource.IngestAsync(new(2026, 8, 1), new(2026, 8, 2), ct);

usageRepository.ReceivedCalls().Should().NotBeEmpty();
billingWriter.ReceivedCalls().Should().NotBeEmpty();
```

Add an invalid/missing next page response that throws and verify neither source writer is called. Assert usage events are `Api/ListPriceEstimate`; Costs rows are `Api/Billed` and never added to `DailyAggregate.CostUsd`.

- [ ] **Step 2: Run focused tests and confirm current partial-return behavior fails**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~OpenAi"
```

- [ ] **Step 3: Fetch every Usage and Costs page before returning**

`IOpenAiAdminClient` exposes:

```csharp
Task<IReadOnlyList<OpenAiUsageRecord>> GetUsageAsync(LocalDate from, LocalDate through, CancellationToken ct);
Task<IReadOnlyList<OpenAiCostRecord>> GetCostsAsync(LocalDate from, LocalDate through, CancellationToken ct);
```

Use `/v1/organization/usage/completions` grouped by model, batch, and service tier, and `/v1/organization/costs`. Follow `next_page` until null, reject repeated cursors, reject a page count above 10,000, and return only after all JSON buckets validate. Preserve raw bucket JSON and upstream observation times.

- [ ] **Step 4: Write activity and money through separate sources**

`OpenAiUsageSource` creates one stable event per provider bucket with source `openai-usage-api`, scope `Api`, basis `ListPriceEstimate`, and pricing dimensions in raw JSON. Resolve list cost centrally; a null result remains null.

`OpenAiCostsSource` maps every financial result to `BillingObservation`, preserving line item/project where present, and uses the seeded `openai` vendor and `api-usage` category. Amounts are stored in the unit/currency documented by the Costs API after exact conversion to USD.

- [ ] **Step 5: Register both sources and run tests**

Register both only behind `OPENAI_ADMIN_KEY`; add two `SourceDefinition` rows and do not edit `ProviderPollingWorkerService`.

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~OpenAi|FullyQualifiedName~ProviderPollingWorkerServiceTests"
```

- [ ] **Step 6: Commit**

```powershell
git add src/AiObservatory.Ingest tests/AiObservatory.Ingest.Tests
```

```powershell
git commit -m "feat(openai): ingest usage and billed costs separately"
```

### Task 3: Add Anthropic usage, cost, and Claude Code sources

**Files:**
- Replace: `src/AiObservatory.Ingest/Services/Anthropic/IAnthropicUsageClient.cs` with `src/AiObservatory.Ingest/Services/Anthropic/IAnthropicAdminClient.cs`
- Replace: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicUsageClient.cs` with `src/AiObservatory.Ingest/Services/Anthropic/AnthropicAdminClient.cs`
- Modify: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicUsageRecord.cs`
- Create: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicCostRecord.cs`
- Create: `src/AiObservatory.Ingest/Services/Anthropic/ClaudeCodeUsageRecord.cs`
- Rename: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicIngestionService.cs` to `src/AiObservatory.Ingest/Services/Anthropic/AnthropicUsageSource.cs`
- Create: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicCostsSource.cs`
- Create: `src/AiObservatory.Ingest/Services/Anthropic/ClaudeCodeUsageSource.cs`
- Modify: `src/AiObservatory.Ingest/Program.cs`
- Replace: `tests/AiObservatory.Ingest.Tests/Services/AnthropicUsageClientTests.cs` with `tests/AiObservatory.Ingest.Tests/Services/AnthropicAdminClientTests.cs`
- Rename: `tests/AiObservatory.Ingest.Tests/Services/AnthropicIngestionServiceTests.cs` to `tests/AiObservatory.Ingest.Tests/Services/AnthropicUsageSourceTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Services/AnthropicCostsSourceTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Services/ClaudeCodeUsageSourceTests.cs`

**Interfaces:**
- Implements: `anthropic-usage-api`, `anthropic-cost-report`, and `claude-code-usage-api` as independent `IUsageSource` registrations.
- Consumes: shared pricing resolver, usage repository, and billing writer.
- Produces: API usage/list estimates, billed cost rows, and subscription/provider-estimated Claude Code usage.

- [ ] **Step 1: Write three-source and pagination tests**

Test the Messages Usage endpoint's cache 5m/1h split and pagination, Cost Report fractional-cent conversion plus non-token charges, and Claude Code `customer_type` plus model breakdown. Every failing second page must throw before any writer call.

```csharp
claudeEvent.SourceId.Should().Be(UsageSourceIds.ClaudeCodeUsageApi);
claudeEvent.UsageScope.Should().Be(UsageScope.Subscription);
claudeEvent.CostBasis.Should().Be(CostBasis.ProviderEstimated);
claudeEvent.CostUsd.Should().Be(12.34m);
```

Use the upstream minor-unit definition from the fixture when converting `estimated_cost`; do not assume cents for both endpoints.

- [ ] **Step 2: Run the Anthropic tests and confirm missing sources**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~Anthropic|FullyQualifiedName~ClaudeCode"
```

- [ ] **Step 3: Implement one admin client with three explicit methods**

```csharp
Task<IReadOnlyList<AnthropicUsageRecord>> GetMessageUsageAsync(LocalDate from, LocalDate through, CancellationToken ct);
Task<IReadOnlyList<AnthropicCostRecord>> GetCostsAsync(LocalDate from, LocalDate through, CancellationToken ct);
Task<IReadOnlyList<ClaudeCodeUsageRecord>> GetClaudeCodeUsageAsync(LocalDate from, LocalDate through, CancellationToken ct);
```

Use the official Messages Usage Report, Cost Report, and Claude Code Usage endpoints. Validate every `next_page`/cursor, date range, currency, and nonnegative token/count field before returning the accumulated immutable list.

- [ ] **Step 4: Persist each semantic lane**

Messages events use `Api/ListPriceEstimate`; Cost Report rows use `Api/Billed` and retain `cost_type`, model, workspace, context window, inference geography, product surface, `amount`, and `list_amount`; Claude Code events use the upstream `customer_type` to choose `Api` or `Subscription` and use `ProviderEstimated` only when the payload supplies `estimated_cost`, otherwise `None`.

Do not merge local Claude telemetry here. Documented local support can be added to the dependency-free sweeper when a stable parser fixture exists.

- [ ] **Step 5: Register and verify all Anthropic sources**

Register Messages and Costs behind `ANTHROPIC_BILLING_KEY`. Register Claude Code only when `CLAUDE_CODE_USAGE_ENABLED=true`; startup validation states that an organization Admin API key and eligible plan are required.

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~Anthropic|FullyQualifiedName~ClaudeCode|FullyQualifiedName~IngestHostTests"
```

- [ ] **Step 6: Commit**

```powershell
git add src/AiObservatory.Ingest tests/AiObservatory.Ingest.Tests
```

```powershell
git commit -m "feat(anthropic): ingest usage costs and Claude Code reports"
```

### Task 4: Replace retired Copilot metrics with signed organization reports

**Files:**
- Create: `src/AiObservatory.Data/Entities/CopilotDailyReport.cs`
- Modify: `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- Create: `src/AiObservatory.Data/Migrations/20260824120000_AddCopilotDailyReports.cs`
- Create: `src/AiObservatory.Data/Migrations/20260824120000_AddCopilotDailyReports.Designer.cs`
- Modify: `src/AiObservatory.Data/Migrations/AiObservatoryDbContextModelSnapshot.cs`
- Replace: `src/AiObservatory.Ingest/Services/Copilot/CopilotUsageRecord.cs` with `src/AiObservatory.Ingest/Services/Copilot/CopilotDailyReportRecord.cs`
- Replace: `src/AiObservatory.Ingest/Services/Copilot/ICopilotUsageClient.cs` with `src/AiObservatory.Ingest/Services/Copilot/ICopilotReportClient.cs`
- Replace: `src/AiObservatory.Ingest/Services/Copilot/CopilotUsageClient.cs` with `src/AiObservatory.Ingest/Services/Copilot/CopilotReportClient.cs`
- Rename: `src/AiObservatory.Ingest/Services/Copilot/CopilotIngestionService.cs` to `src/AiObservatory.Ingest/Services/Copilot/CopilotReportSource.cs`
- Modify: `src/AiObservatory.Ingest/Program.cs`
- Replace: `tests/AiObservatory.Ingest.Tests/Services/CopilotIngestionServiceTests.cs` with `tests/AiObservatory.Ingest.Tests/Services/CopilotReportSourceTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Services/CopilotReportClientTests.cs`

**Interfaces:**
- Implements: `IUsageSource` with source ID `copilot-org-report`.
- Produces: `CopilotDailyReport` rows unique by `(SourceId, ReportKey)`; produces no `UsageEvent`.

- [ ] **Step 1: Write descriptor/download/no-fake-token tests**

```csharp
var records = await client.GetLatestOrganizationReportAsync(ct);
records.Should().ContainSingle(x => x.Day == new LocalDate(2026, 8, 20));
descriptorHandler.Requests.Should().ContainSingle();
downloadHandler.Requests.Should().ContainSingle();

await source.IngestAsync(new(2026, 8, 1), new(2026, 8, 23), ct);
await usageRepository.DidNotReceiveWithAnyArgs().RecordEventAsync(default!, default);
(await db.CopilotDailyReports.AsNoTracking().SingleAsync(ct)).UserInitiatedInteractionCount.Should().Be(42);
```

Add tests for failed signed download, oversized download, malformed middle NDJSON line, duplicate day, and a row outside the descriptor window; each must leave the database unchanged.

- [ ] **Step 2: Run tests and confirm current endpoint/shape fails**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~Copilot"
```

- [ ] **Step 3: Add the provider-specific entity**

Store `Day`, `SourceId`, `SourceKind = ProviderApi`, `UsageScope = Subscription`, `CostBasis = None`, `ReportKey`, nullable active-user counts, `UserInitiatedInteractionCount`, `CodeGenerationActivityCount`, `CodeAcceptanceActivityCount`, `RawPayload` jsonb, and `ObservedAt`. Keep nonnegative constraints and a filtered correction key. Do not add token or cost columns.

- [ ] **Step 4: Implement the current report flow**

Request `GET /orgs/{org}/copilot/metrics/reports/organization-28-day/latest` with GitHub's current API version. Validate `report_start_day`, `report_end_day`, and every HTTPS `download_link`. Download without forwarding the GitHub Authorization header, limit the report to 50 MiB, parse every NDJSON line, and validate all rows before returning.

The source upserts the complete validated set by stable day/report key. It records provider observation time from the descriptor when available and writes no usage or spend row. Keep local Copilot tokens as the separate `copilot-local/Subscription/Notional` lane.

- [ ] **Step 5: Generate migration and verify**

```powershell
dotnet ef migrations add AddCopilotDailyReports --project src/AiObservatory.Data --startup-project src/AiObservatory.Api
```

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~Copilot"
```

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~UsageMigrationTests"
```

- [ ] **Step 6: Commit**

```powershell
git add src/AiObservatory.Data src/AiObservatory.Ingest tests
```

```powershell
git commit -m "fix(copilot): ingest signed organization reports"
```

### Task 5: Replace nonexistent Google reports with Billing BigQuery export

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/AiObservatory.Ingest/AiObservatory.Ingest.csproj`
- Modify: `src/AiObservatory.Ingest/IngestOptions.cs`
- Replace: `src/AiObservatory.Ingest/Services/Google/IGoogleBillingClient.cs` with `src/AiObservatory.Ingest/Services/Google/IGoogleBillingExportClient.cs`
- Replace: `src/AiObservatory.Ingest/Services/Google/GoogleBillingClient.cs` with `src/AiObservatory.Ingest/Services/Google/GoogleBillingExportClient.cs`
- Modify: `src/AiObservatory.Ingest/Services/Google/GoogleBillingRecord.cs`
- Rename: `src/AiObservatory.Ingest/Services/Google/GoogleIngestionService.cs` to `src/AiObservatory.Ingest/Services/Google/GoogleBillingExportSource.cs`
- Modify: `src/AiObservatory.Ingest/Program.cs`
- Modify: `tests/AiObservatory.Ingest.Tests/IngestOptionsTests.cs`
- Create: `tests/AiObservatory.Ingest.Tests/Services/GoogleBillingExportClientTests.cs`
- Replace: `tests/AiObservatory.Ingest.Tests/Services/GoogleIngestionServiceTests.cs` with `tests/AiObservatory.Ingest.Tests/Services/GoogleBillingExportSourceTests.cs`

**Interfaces:**
- Implements: `IUsageSource` with source ID `google-cloud-billing-export`.
- Consumes: official `Google.Cloud.BigQuery.V2` client and `BillingObservationWriter`.
- Produces: Google billing observations and billed ledger rows; produces no fabricated token usage.

- [ ] **Step 1: Write exact SQL-shape and retained-facts tests**

```csharp
var rows = await client.QueryAsync(new(2026, 8, 1), new(2026, 8, 2), ct);
rows.Single().Should().BeEquivalentTo(new GoogleBillingRecord(
    Date: new(2026, 8, 1),
    BillingPeriod: "202608",
    Service: "Vertex AI",
    SkuId: "sku-123",
    Sku: "Gemini input tokens",
    Currency: "USD",
    GrossAmount: 10m,
    CreditAmount: -3m,
    NetAmount: 7m,
    RawJson: expectedRaw));
```

Assert the query parameters carry the date range, the configured table name is validated before interpolation, credits are unnested and summed without multiplying cost rows, and a query exception causes no writer calls.

- [ ] **Step 2: Add the official BigQuery package and run the failing tests**

Add `Google.Cloud.BigQuery.V2` version `3.12.0` to `Directory.Packages.props` and a package reference to the Ingest project.

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~GoogleBilling"
```

- [ ] **Step 3: Implement the normalized BigQuery query**

Bind `GOOGLE_CLOUD_PROJECT_ID` and `GOOGLE_BILLING_EXPORT_TABLE`; use Application Default Credentials. Validate the table as exactly three backtick-safe identifier segments before inserting it into SQL. Parameterize `@from` and `@throughExclusive` with `BigQueryParameter`.

Query the standard export's `usage_start_time`, `invoice.month`, `service.description`, `sku.id`, `sku.description`, `currency`, `cost`, and credits. Aggregate credits in a correlated subquery before grouping so repeated credit rows cannot multiply gross cost. Follow Google's recommendation to isolate schema drift in one query/view projection.

- [ ] **Step 4: Persist billing truth only**

Map each row to `BillingObservation` with all normalized fields plus raw JSON. Use the seeded `google` vendor and `cloud` category. The writer retains zero-net/full-credit observations while excluding them from spend totals. Delete the nonexistent `/v1/{billingAccount}/reports` route, its options, and all zero-token event creation.

- [ ] **Step 5: Run Google, composition, and complete backend tests**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~GoogleBilling|FullyQualifiedName~IngestHostTests"
```

```powershell
dotnet csharpier check .
```

```powershell
dotnet build AiObservatory.slnx --configuration Release
```

```powershell
dotnet test --solution AiObservatory.slnx --configuration Release --no-build --timeout 5m
```

- [ ] **Step 6: Commit**

```powershell
git add Directory.Packages.props src/AiObservatory.Ingest tests/AiObservatory.Ingest.Tests
```

```powershell
git commit -m "fix(google): ingest Cloud Billing BigQuery export"
```
