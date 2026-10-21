# Source-Aware Usage Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every usage and spend observation honest about its source, make stable snapshots correctable without aggregate drift, and harden the shipped Codex, Claude, Copilot, and Kimi local collectors.

**Architecture:** Add provenance to the existing entities and keep the existing raw-event-plus-daily-aggregate shape. Replace duplicate suppression with one transactional insert/no-op/correct algorithm, then make the poller enumerate registered sources and persist freshness independently of `/healthz`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10/Npgsql, PostgreSQL, NodaTime, xUnit v3, NSubstitute, AwesomeAssertions, Node.js 18+.

**Spec:** `docs/superpowers/specs/2026-08-24-source-aware-observability-design.md`

## Global Constraints

- Preserve all historical observations without inventing precision.
- Existing requests without provenance remain accepted as `legacy-api`, `Legacy`, `Unknown`, and `Unknown`.
- `EventKey` uniqueness is scoped to `SourceId`; null keys remain append-only.
- Event replacement and both aggregate movements happen in one database transaction.
- `ObservedAt` uses injected `IClock`; BCL timestamps remain only at HTTP/JSON boundaries.
- `/healthz` remains process liveness; source freshness is a separate API concern.
- Do not add a runtime plugin loader, universal source framework, or new package.
- Keep PostgreSQL tests in the existing integration lane and assertions in AwesomeAssertions.

## File Structure

- `Entities/ObservationProvenance.cs` owns the three persisted enums and stable source IDs.
- `UsageEvent.cs`, `DailyAggregate.cs`, and `SpendEntry.cs` carry truth metadata; no parallel metadata table.
- `UsageRepository.cs` remains the single transactional event/aggregate writer.
- `Sources/IUsageSource.cs` is the compile-time polling boundary; existing ingestion services implement it directly.
- `SourceSyncStateStore.cs` owns persisted attempt/success/failure updates.
- The local sweeper continues to be dependency-free and sends stable cumulative snapshots.

---

### Task 1: Add provenance and preserve legacy rows

**Files:**
- Create: `src/AiObservatory.Data/Entities/ObservationProvenance.cs`
- Modify: `src/AiObservatory.Data/Entities/UsageEvent.cs`
- Modify: `src/AiObservatory.Data/Entities/DailyAggregate.cs`
- Modify: `src/AiObservatory.Data/Entities/SpendEntry.cs`
- Modify: `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- Create: `src/AiObservatory.Data/Migrations/20260824090000_AddObservationProvenance.cs`
- Create: `src/AiObservatory.Data/Migrations/20260824090000_AddObservationProvenance.Designer.cs`
- Modify: `src/AiObservatory.Data/Migrations/AiObservatoryDbContextModelSnapshot.cs`
- Modify: `tests/AiObservatory.Data.Tests/Repositories/UsageMigrationTests.cs`
- Modify: `tests/AiObservatory.Data.Tests/Repositories/UsageRepositoryTests.cs`

**Interfaces:**
- Produces: `SourceKind`, `UsageScope`, `CostBasis`, and `UsageSourceIds` in `AiObservatory.Data.Entities`.
- Produces: provenance properties on `UsageEvent`, `DailyAggregate`, and `SpendEntry`.
- Produces: aggregate primary key `(Date, Provider, Model, SourceId, SourceKind, UsageScope, CostBasis)`.

- [ ] **Step 1: Write the failing entity and migration tests**

Add this classification assertion to `UsageMigrationTests` after migrating representative pre-change rows:

```csharp
var usage = await after.UsageEvents.AsNoTracking().SingleAsync(e => e.EventKey == "legacy-null-cost-a", ct);
usage.SourceId.Should().Be(UsageSourceIds.LegacyApi);
usage.SourceKind.Should().Be(SourceKind.Legacy);
usage.UsageScope.Should().Be(UsageScope.Unknown);
usage.CostBasis.Should().Be(CostBasis.Unknown);
usage.ObservedAt.Should().Be(usage.IngestedAt);

var aggregate = await after.DailyAggregates.AsNoTracking().SingleAsync(ct);
aggregate.SourceId.Should().Be(UsageSourceIds.LegacyApi);
aggregate.SourceKind.Should().Be(SourceKind.Legacy);
aggregate.UsageScope.Should().Be(UsageScope.Unknown);
aggregate.CostBasis.Should().Be(CostBasis.Unknown);
```

Add a model test proving two events may reuse a key when `SourceId` differs.

- [ ] **Step 2: Run the focused tests and confirm the missing members fail the build**

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~UsageMigrationTests|FullyQualifiedName~UsageRepositoryTests"
```

Expected: compile failure because the provenance types and properties do not exist.

- [ ] **Step 3: Add the exact truth types and fields**

```csharp
namespace AiObservatory.Data.Entities;

public enum SourceKind { ProviderApi, LocalTelemetry, Manual, Legacy }
public enum UsageScope { Api, Subscription, Mixed, Unknown }
public enum CostBasis { Billed, ProviderEstimated, ListPriceEstimate, Notional, None, Unknown }

public static class UsageSourceIds
{
    public const string LegacyApi = "legacy-api";
    public const string LegacySpend = "legacy-spend";
    public const string ManualLedger = "manual-ledger";
    public const string GitHubBillingApi = "github-billing-api";
    public const string OpenAiUsageApi = "openai-usage-api";
    public const string OpenAiCostsApi = "openai-costs-api";
    public const string CodexLocal = "codex-local";
    public const string AnthropicUsageApi = "anthropic-usage-api";
    public const string AnthropicCostReport = "anthropic-cost-report";
    public const string ClaudeCodeUsageApi = "claude-code-usage-api";
    public const string ClaudeLocal = "claude-local";
    public const string CopilotOrgReport = "copilot-org-report";
    public const string CopilotLocal = "copilot-local";
    public const string GoogleCloudBillingExport = "google-cloud-billing-export";
    public const string KimiLocal = "kimi-local";
}
```

Add these properties to `UsageEvent` and `SpendEntry`; add the first four to `DailyAggregate`:

```csharp
public string SourceId { get; set; } = UsageSourceIds.LegacyApi;
public SourceKind SourceKind { get; set; } = SourceKind.Legacy;
public UsageScope UsageScope { get; set; } = UsageScope.Unknown;
public CostBasis CostBasis { get; set; } = CostBasis.Unknown;
public Instant ObservedAt { get; set; }
```

Also add `public string RawPayload { get; set; } = "{}";` to `SpendEntry` and map it as `jsonb`, so provider financial imports can retain auditable evidence without provider-specific nullable columns. Backfill existing spend rows with `{}`.

Add nullable `CacheSavingsUsd` to `UsageEvent`, plus `CacheSavingsUsd` and `UnknownCacheSavingsCount` to `DailyAggregate`. Legacy rows default to unknown savings rather than preserving the frontend's hand-maintained rate guess.

Use string conversions for all three enums, `HasMaxLength(100)` for `SourceId`, and replace the usage-event index with:

```csharp
b.HasIndex(e => new { e.SourceId, e.EventKey })
    .IsUnique()
    .HasFilter("\"EventKey\" IS NOT NULL");
```

- [ ] **Step 4: Generate and inspect the migration**

```powershell
dotnet ef migrations add AddObservationProvenance --project src/AiObservatory.Data --startup-project src/AiObservatory.Api
```

The migration must default existing usage rows and aggregates to `legacy-api/Legacy/Unknown/Unknown`, set `ObservedAt = IngestedAt`, and retain every existing aggregate row. Existing spend rows become `legacy-spend/Legacy/Unknown/Billed` with `ObservedAt = RecordedAt`; this preserves known financial meaning without guessing acquisition origin. Remove the old `(Provider, EventKey)` index only after the new columns are populated.

- [ ] **Step 5: Verify migration and model**

```powershell
dotnet build AiObservatory.slnx --configuration Release
```

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~UsageMigrationTests|FullyQualifiedName~UsageRepositoryTests"
```

Expected: build succeeds and the real-PostgreSQL migration tests pass when `TEST_DB_CONNECTION` is available.

- [ ] **Step 6: Commit**

```powershell
git add src/AiObservatory.Data tests/AiObservatory.Data.Tests
```

```powershell
git commit -m "feat(data): add observation provenance"
```

### Task 2: Replace duplicate suppression with correctable snapshots

**Files:**
- Modify: `src/AiObservatory.Data/Entities/UsageEvent.cs`
- Modify: `src/AiObservatory.Data/Repositories/IUsageRepository.cs`
- Modify: `src/AiObservatory.Data/Repositories/UsageRepository.cs`
- Modify: `tests/AiObservatory.Data.Tests/Repositories/UsageRepositoryTests.cs`

**Interfaces:**
- Consumes: the expanded aggregate key from Task 1.
- Produces: `RecordEventDisposition { Created, Unchanged, Corrected }` and `RecordEventResult(Guid EventId, RecordEventDisposition Disposition)`.
- Preserves: computed `RecordEventResult.IsDuplicate`, true only for `Unchanged`.
- Produces: source-exact `PatchEventCostAsync(Provider, string sourceId, string eventKey, decimal, CancellationToken)`.

- [ ] **Step 1: Write the correction tests**

Add one parameterized theory for changed canonical fields and explicit tests for bucket movement, null-key append, same key/different source, and rollback. The core correction assertion is:

```csharp
var first = NewEvent(sourceId: UsageSourceIds.OpenAiUsageApi, eventKey: "day:model", model: "gpt-5.4", input: 100, cost: 1m);
(await repository.RecordEventAsync(first)).Disposition.Should().Be(RecordEventDisposition.Created);

var corrected = NewEvent(sourceId: UsageSourceIds.OpenAiUsageApi, eventKey: "day:model", model: "gpt-5.5", input: 175, cost: 2m);
(await repository.RecordEventAsync(corrected)).Disposition.Should().Be(RecordEventDisposition.Corrected);
(await repository.RecordEventAsync(corrected)).Disposition.Should().Be(RecordEventDisposition.Unchanged);

var rows = await db.DailyAggregates.AsNoTracking().OrderBy(x => x.Model).ToListAsync();
rows.Should().NotContain(x => x.Model == "gpt-5.4");
rows.Single(x => x.Model == "gpt-5.5").InputTokens.Should().Be(175);
rows.Sum(x => x.CostUsd).Should().Be(2m);
```

`NewEvent` is a private test helper returning a fully populated `UsageEvent`, including all provenance and `ObservedAt`.

- [ ] **Step 2: Run the repository tests and confirm corrections still report duplicate**

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~UsageRepositoryTests"
```

Expected: the new correction assertions fail because the repository returns before comparing the stored snapshot.

- [ ] **Step 3: Implement one transactional state machine**

Make canonical event properties mutable, then implement exactly three outcomes:

```csharp
public enum RecordEventDisposition { Created, Unchanged, Corrected }

public sealed record RecordEventResult(Guid EventId, RecordEventDisposition Disposition)
{
    public bool IsDuplicate => Disposition == RecordEventDisposition.Unchanged;
}
```

In `RecordEventAsync`, begin a transaction, look up by `SourceId + EventKey`, compare every aggregate-affecting and persisted fact/provenance field, and:

```csharp
if (existing is null)
{
    db.UsageEvents.Add(evt);
    await ApplyAggregateDeltaAsync(evt, +1, ct);
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return new(evt.Id, RecordEventDisposition.Created);
}

if (CanonicalEquals(existing, evt))
{
    await tx.RollbackAsync(ct);
    return new(existing.Id, RecordEventDisposition.Unchanged);
}

await ApplyAggregateDeltaAsync(existing, -1, ct);
CopyCanonicalValues(existing, evt);
await ApplyAggregateDeltaAsync(existing, +1, ct);
await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
return new(existing.Id, RecordEventDisposition.Corrected);
```

`ApplyAggregateDeltaAsync` must include all aggregate-key columns and signed token, cost, cache-savings, unknown-cost, unknown-cache-savings, and request deltas in its PostgreSQL UPSERT. Delete aggregate rows whose request count becomes zero. Pass nullable CLR values directly to `ExecuteSqlInterpolatedAsync`; never interpolate `DBNull.Value`.

`CanonicalEquals` excludes `Id`, `IngestedAt`, and `ObservedAt`: a successful re-read of an unchanged fact belongs in `SourceSyncState`, not as a write to every event. `CopyCanonicalValues` applies the new `ObservedAt` only when the fact actually changes. Provider clients must omit volatile request IDs from canonical raw evidence.

For concurrent first inserts, catch only PostgreSQL unique violation `23505`, roll back, clear the change tracker, and retry the lookup once. Do not add a process-wide lock.

- [ ] **Step 4: Remove superseded special paths**

Delete `AddUsageEventAsync` and the unused `UpsertDailyAggregateAsync` if `rg` confirms no production callers. Change the repository correction lookup to `PatchEventCostAsync(Provider provider, string sourceId, string eventKey, decimal newCostUsd, CancellationToken ct)` and route it through the same locked correction path so it cannot disagree with `RecordEventAsync`.

- [ ] **Step 5: Run repository and migration tests**

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~UsageRepositoryTests|FullyQualifiedName~UsageMigrationTests"
```

Expected: created, unchanged, corrected, moved-bucket, concurrent insert, null-key, and rollback cases pass.

- [ ] **Step 6: Commit**

```powershell
git add src/AiObservatory.Data tests/AiObservatory.Data.Tests
```

```powershell
git commit -m "fix(data): apply corrected usage snapshots"
```

### Task 3: Preserve old API clients and make the shipped sweeper cumulative

**Files:**
- Modify: `src/AiObservatory.Api/Endpoints/EventsEndpoints.cs`
- Modify: `src/AiObservatory.Api/Endpoints/AggregatesEndpoints.cs`
- Modify: `tests/AiObservatory.Api.Tests/EventsEndpointsTests.cs`
- Modify: `tests/AiObservatory.Api.IntegrationTests/EventsEndpointsWafTests.cs`
- Modify: `tests/AiObservatory.Api.IntegrationTests/AggregatesEndpointsWafTests.cs`
- Modify: `clients/observatory-sweep.mjs`
- Modify: `clients/observatory-sweep.test.mjs`
- Modify: `clients/README.md`

**Interfaces:**
- Consumes: `RecordEventDisposition` from Task 2.
- Produces: optional request fields `sourceId`, `sourceKind`, `usageScope`, `costBasis`, and `observedAtUtc`; cache savings remains server-derived.
- Produces: aggregate response provenance fields without removing existing fields.

- [ ] **Step 1: Write compatibility and cumulative-snapshot tests**

Add an API test proving omitted fields map exactly to legacy defaults, and explicit values round-trip. Add Node tests proving a changed transcript replaces the same key:

```javascript
const snapshots = buildDailySnapshots([
  { tool: 'codex', sessionId: 'a', date: '2026-08-24', model: 'gpt-5.4', cum: { input: 10, output: 2, cacheRead: 1, cacheWrite: 0 } },
  { tool: 'codex', sessionId: 'b', date: '2026-08-24', model: 'gpt-5.4', cum: { input: 20, output: 3, cacheRead: 2, cacheWrite: 0 } },
])
assert.equal(snapshots[0].eventKey, 'codex:2026-08-24:gpt-5.4')
assert.equal(snapshots[0].inputTokens, 30)
assert.equal(snapshots[0].sourceId, 'codex-local')
assert.equal(snapshots[0].usageScope, 'subscription')
assert.equal(snapshots[0].costBasis, 'notional')
```

Add synthetic local-shape tests that contain telemetry only:

```javascript
const claude = parseClaude([
  JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-1', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_read_input_tokens: 20, cache_creation_input_tokens: 30, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 }, service_tier: 'standard', speed: 'standard', inference_geo: 'not_available' } } }),
  JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:01Z', message: { id: 'msg-1', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_read_input_tokens: 20, cache_creation_input_tokens: 30 } } }),
].join('\n'))
assert.equal(claude.length, 1)

const kimi = parseKimi(JSON.stringify({ type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding', usage: { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 } }))
assert.equal(kimi[0].inputTokens, 10)
assert.equal(kimi[0].cacheReadTokens, 20)
```

The Claude assertion proves repeated transcript copies of one `message.id` count once. Add a Kimi `step.end` row with identical usage and prove it is ignored.

- [ ] **Step 2: Run tests and confirm the new wire fields fail**

```powershell
dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~EventsEndpointsTests"
```

```powershell
node --test clients/observatory-sweep.test.mjs
```

- [ ] **Step 3: Extend validation without breaking old payloads**

Add optional strings to `UsageEventRequest`, parse them case-insensitively, reject unknown enum values and source IDs longer than 100 characters, and use:

```csharp
var sourceId = string.IsNullOrWhiteSpace(req.SourceId) ? UsageSourceIds.LegacyApi : req.SourceId.Trim().ToLowerInvariant();
var sourceKind = ParseOrDefault(req.SourceKind, SourceKind.Legacy);
var usageScope = ParseOrDefault(req.UsageScope, UsageScope.Unknown);
var costBasis = ParseOrDefault(req.CostBasis, CostBasis.Unknown);
var observedAt = req.ObservedAtUtc is { } suppliedObserved ? Instant.FromDateTimeOffset(suppliedObserved) : now;
```

Reject `ObservedAtUtc` more than five minutes in the future. Return `{ id, duplicate, corrected }` for 200 responses; retain 201 for a newly created row. Add optional `sourceId` to the cost-patch query and default it to `legacy-api`, so installed maintenance clients keep reaching migrated rows while new callers address the exact `(SourceId, EventKey)` identity.

- [ ] **Step 4: Replace delta keys with cumulative day/model snapshots**

Export `buildDailySnapshots`, `parseClaude`, and `parseKimi`. Cache parsed per-file records by path and mtime only to avoid rescanning unchanged files; rebuild each day/model/pricing-dimension total from that cache every run. Emit stable keys `codex:<date>:<model>`, `claude:<date>:<model>:<tier>:<speed>:<geo>`, `copilot:<date>:<model>`, and `kimi:<date>:<model>`, explicit provenance, and the latest contributing timestamp as `occurredAtUtc`. Losing the state file must only cause re-reading and harmless resubmission.

Claude scans `~/.claude/projects/**/*.jsonl`, selects `type == "assistant"` rows with `message.usage`, and deduplicates repeated transcript copies by `message.id` before summing. Preserve service tier, speed, inference geography, thinking tokens, and the 5-minute/1-hour cache-creation split. Kimi scans `~/.kimi-code/sessions/**/wire.jsonl`, selects only `type == "usage.record"` so the mirrored `step.end.usage` is not counted twice, and maps `inputOther`, `output`, `inputCacheRead`, and `inputCacheCreation`. Its generic `kimi-for-coding` model remains unpriced unless a first-party-observed model dimension is present. Bind `OBSERVATORY_LOCAL_SOURCES` as a comma-separated allowlist with `codex,copilot,claude,kimi` as the default; users with Claude Code Usage API coverage exclude `claude` to prevent overlapping subscription observations.

Keep the current Codex/Copilot notional calculation until the pricing plan replaces it, but calculate it from the cumulative snapshot rather than a delta. Claude uses the existing server-side Anthropic calculation during this transition; Kimi sends null cost. Retain the existing `ponytail:` mixed-model attribution comment.

- [ ] **Step 5: Run API, Node, and architecture checks**

```powershell
dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~EventsEndpointsTests"
```

```powershell
dotnet test tests/AiObservatory.Api.IntegrationTests/AiObservatory.Api.IntegrationTests.csproj --filter "FullyQualifiedName~EventsEndpointsWafTests|FullyQualifiedName~AggregatesEndpointsWafTests"
```

```powershell
node --test clients/observatory-sweep.test.mjs
```

- [ ] **Step 6: Commit**

```powershell
git add src/AiObservatory.Api tests/AiObservatory.Api.Tests tests/AiObservatory.Api.IntegrationTests clients
```

```powershell
git commit -m "feat(api): accept source-aware usage snapshots"
```

### Task 4: Poll registered sources and persist freshness

**Files:**
- Create: `src/AiObservatory.Data/Entities/SourceSyncState.cs`
- Create: `src/AiObservatory.Data/Repositories/SourceSyncStateStore.cs`
- Modify: `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- Modify: `src/AiObservatory.Data/ServiceCollectionExtensions.cs`
- Create: `src/AiObservatory.Data/Migrations/20260824091000_AddSourceSyncStates.cs`
- Create: `src/AiObservatory.Data/Migrations/20260824091000_AddSourceSyncStates.Designer.cs`
- Modify: `src/AiObservatory.Data/Migrations/AiObservatoryDbContextModelSnapshot.cs`
- Create: `src/AiObservatory.Ingest/Sources/IUsageSource.cs`
- Modify: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicIngestionService.cs`
- Modify: `src/AiObservatory.Ingest/Services/Copilot/CopilotIngestionService.cs`
- Modify: `src/AiObservatory.Ingest/Services/Google/GoogleIngestionService.cs`
- Modify: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiIngestionService.cs`
- Modify: `src/AiObservatory.Ingest/Services/GitHub/GitHubIngestionService.cs`
- Modify: `src/AiObservatory.Ingest/ProviderPollingWorkerService.cs`
- Modify: `src/AiObservatory.Ingest/Program.cs`
- Modify: `src/AiObservatory.Api/Endpoints/EventsEndpoints.cs`
- Modify: `tests/AiObservatory.Api.IntegrationTests/EventsEndpointsWafTests.cs`
- Modify: `tests/AiObservatory.Ingest.Tests/Services/ProviderPollingWorkerServiceTests.cs`
- Modify: `tests/AiObservatory.Ingest.Tests/IngestHostTests.cs`

**Interfaces:**
- Produces: `IUsageSource.SourceId` and `IngestAsync(LocalDate from, LocalDate through, CancellationToken) -> SourceIngestionResult`.
- Produces: `SourceDefinition(string SourceId, bool IsConfigured, Duration ExpectedRefreshInterval)` registered once per known source.
- Produces: persistent `SourceSyncState` keyed by `SourceId`.

- [ ] **Step 1: Write worker tests that reject the hard-coded provider list**

```csharp
var first = Substitute.For<IUsageSource>();
first.SourceId.Returns("first-source");
first.IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
    .Returns(new SourceIngestionResult(Instant.FromUtc(2026, 8, 23, 23, 0)));
var second = Substitute.For<IUsageSource>();
second.SourceId.Returns("second-source");
second.IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
    .Returns(new SourceIngestionResult(null));

await worker.RunPollAsync(new LocalDate(2026, 8, 20), new LocalDate(2026, 8, 23), CancellationToken.None);

await first.Received(1).IngestAsync(new(2026, 8, 20), new(2026, 8, 23), Arg.Any<CancellationToken>());
await second.Received(1).IngestAsync(new(2026, 8, 20), new(2026, 8, 23), Arg.Any<CancellationToken>());
```

Add assertions that the second source still runs after the first fails, failures are sanitized and persisted, success resets the counter, an explicit `SourceUnavailableException` records unavailable state, and an absent configured implementation becomes `IsConfigured = false` rather than a failure.

- [ ] **Step 2: Run focused tests and confirm the interface is missing**

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~ProviderPollingWorkerServiceTests|FullyQualifiedName~IngestHostTests"
```

- [ ] **Step 3: Add the minimum source contract and state entity**

```csharp
public interface IUsageSource
{
    string SourceId { get; }
    Task<SourceIngestionResult> IngestAsync(LocalDate from, LocalDate through, CancellationToken cancellationToken);
}

public sealed record SourceIngestionResult(Instant? LatestObservationAt);
public sealed record SourceDefinition(string SourceId, bool IsConfigured, Duration ExpectedRefreshInterval);

public sealed class SourceUnavailableException(string message) : Exception(message);
```

`SourceSyncState` contains `SourceId`, `IsConfigured`, nullable `IsAvailable`, `ExpectedRefreshIntervalSeconds`, nullable `LastAttemptAt`, `LastSuccessAt`, `LatestObservationAt`, `ConsecutiveFailureCount`, and a nullable 500-character `LastError`. `SourceSyncStateStore` exposes `MarkUnconfiguredAsync`, `MarkAttemptAsync`, `MarkSuccessAsync`, `MarkUnavailableAsync`, and `MarkFailureAsync`; every method receives the current `Instant` rather than reading time statically.

- [ ] **Step 4: Replace concrete worker calls with collection enumeration**

Resolve `IEnumerable<IUsageSource>` and `IEnumerable<SourceDefinition>` from one async scope. First persist unconfigured definitions, then call each registered source independently and pass `SourceIngestionResult.LatestObservationAt` to `MarkSuccessAsync`. Catch `SourceUnavailableException` separately and persist `IsAvailable = false`; ordinary failures leave availability unknown/unchanged. Sanitize failure text by removing CR/LF and URI query strings before truncating to 500 characters. Preserve cancellation and the existing three-failure log escalation.

Each existing ingestion service implements the range contract directly and returns the greatest upstream observation/usage instant it actually handled, or null when the source supplies none. Services that currently accept a day iterate the inclusive range internally; GitHub uses `from` once. Register with `TryAddEnumerable(ServiceDescriptor.Scoped<IUsageSource, ...>())` only inside the existing credential gate, and always register its `SourceDefinition`.

After `/events` accepts a `LocalTelemetry` observation, call `MarkSuccessAsync` for its source ID with `ExpectedRefreshInterval = Duration.FromDays(1)` and the event's `ObservedAt`. Do this for created, corrected, and unchanged submissions: an unchanged cumulative snapshot is still proof that the local collector ran. Add a WAF assertion that posting `codex-local` creates/refreshes its state.

- [ ] **Step 5: Generate the state migration and run tests**

```powershell
dotnet ef migrations add AddSourceSyncStates --project src/AiObservatory.Data --startup-project src/AiObservatory.Api
```

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --filter "FullyQualifiedName~ProviderPollingWorkerServiceTests|FullyQualifiedName~IngestHostTests"
```

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~UsageMigrationTests"
```

- [ ] **Step 6: Verify the complete foundation**

```powershell
dotnet csharpier check .
```

```powershell
dotnet build AiObservatory.slnx --configuration Release
```

```powershell
dotnet test --solution AiObservatory.slnx --configuration Release --no-build --timeout 5m
```

```powershell
node --test clients/observatory-sweep.test.mjs
```

- [ ] **Step 7: Commit**

```powershell
git add src/AiObservatory.Data src/AiObservatory.Ingest tests
```

```powershell
git commit -m "feat(ingest): poll registered sources with persistent status"
```
