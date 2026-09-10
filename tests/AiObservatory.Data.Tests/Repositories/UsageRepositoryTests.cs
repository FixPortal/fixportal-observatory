using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Repositories;

// Requires TEST_DB_CONNECTION env var pointing at a real PostgreSQL instance.
// Example: "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres"
[Trait("Category", "Integration")]
public class UsageRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IUsageRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConn =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connStr = new NpgsqlConnectionStringBuilder(baseConn)
        {
            Database = $"aiobs_test_usage_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new UsageRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ctx is not null && _connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
            {
                await _ctx.Database.EnsureDeletedAsync();
            }
        }
        finally
        {
            if (_ctx is not null)
            {
                await _ctx.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task RecordEvent_with_same_eventKey_records_and_aggregates_once()
    {
        static UsageEvent NewEvent() =>
            new()
            {
                Provider = Provider.Copilot,
                OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                Model = "gpt-5.4",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
                EventKey = "copilot:session-abc:gpt-5.4",
            };

        var first = await _repo.RecordEventAsync(NewEvent(), TestContext.Current.CancellationToken);
        var second = await _repo.RecordEventAsync(NewEvent(), TestContext.Current.CancellationToken);

        first.Disposition.Should().Be(RecordEventDisposition.Created);
        second.Disposition.Should().Be(RecordEventDisposition.Unchanged);
        first.IsDuplicate.Should().BeFalse();
        second.IsDuplicate.Should().BeTrue();
        second.EventId.Should().Be(first.EventId);

        (await _ctx.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        var agg = await _ctx.DailyAggregates.SingleAsync(TestContext.Current.CancellationToken);
        agg.InputTokens.Should().Be(100);
        agg.RequestCount.Should().Be(1);
    }

    [Theory]
    [InlineData(nameof(UsageEvent.Provider))]
    [InlineData(nameof(UsageEvent.OccurredAt))]
    [InlineData(nameof(UsageEvent.Model))]
    [InlineData(nameof(UsageEvent.InputTokens))]
    [InlineData(nameof(UsageEvent.OutputTokens))]
    [InlineData(nameof(UsageEvent.CacheReadTokens))]
    [InlineData(nameof(UsageEvent.CacheWriteTokens))]
    [InlineData(nameof(UsageEvent.CacheWrite1hTokens))]
    [InlineData(nameof(UsageEvent.ThoughtTokens))]
    [InlineData(nameof(UsageEvent.CostUsd))]
    [InlineData(nameof(UsageEvent.CacheSavingsUsd))]
    [InlineData(nameof(UsageEvent.Runtime))]
    [InlineData(nameof(UsageEvent.SessionId))]
    [InlineData(nameof(UsageEvent.AgentId))]
    [InlineData(nameof(UsageEvent.RawPayload))]
    [InlineData(nameof(UsageEvent.SourceKind))]
    [InlineData(nameof(UsageEvent.UsageScope))]
    [InlineData(nameof(UsageEvent.CostBasis))]
    public async Task RecordEvent_changed_canonical_field_replaces_snapshot(string changedField)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = NewEvent();
        var corrected = NewEvent(changedField);

        await _repo.RecordEventAsync(first, ct);
        var correction = await _repo.RecordEventAsync(corrected, ct);
        var unchanged = await _repo.RecordEventAsync(NewEvent(changedField), ct);

        correction.Disposition.Should().Be(RecordEventDisposition.Corrected);
        unchanged.Disposition.Should().Be(RecordEventDisposition.Unchanged);
        correction.IsDuplicate.Should().BeFalse();
        unchanged.IsDuplicate.Should().BeTrue();
        correction.EventId.Should().Be(first.Id);
        unchanged.EventId.Should().Be(first.Id);

        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved
            .Should()
            .BeEquivalentTo(
                corrected,
                options => options.Excluding(e => e.Id).Excluding(e => e.IngestedAt).Excluding(e => e.RawPayload)
            );
        using var savedPayload = JsonDocument.Parse(saved.RawPayload);
        using var expectedPayload = JsonDocument.Parse(corrected.RawPayload);
        JsonElement.DeepEquals(savedPayload.RootElement, expectedPayload.RootElement).Should().BeTrue();

        var rows = await _ctx.DailyAggregates.AsNoTracking().ToListAsync(ct);
        rows.Should().ContainSingle();
        rows.Sum(x => x.InputTokens).Should().Be(corrected.InputTokens);
        rows.Sum(x => x.OutputTokens).Should().Be(corrected.OutputTokens);
        rows.Sum(x => x.CacheReadTokens).Should().Be(corrected.CacheReadTokens ?? 0);
        rows.Sum(x => x.CacheWriteTokens).Should().Be(corrected.CacheWriteTokens ?? 0);
        rows.Sum(x => x.CacheWrite1hTokens).Should().Be(corrected.CacheWrite1hTokens ?? 0);
        rows.Sum(x => x.CostUsd).Should().Be(corrected.CostUsd ?? 0);
        rows.Sum(x => x.CacheSavingsUsd).Should().Be(corrected.CacheSavingsUsd ?? 0);
        rows.Sum(x => x.RequestCount).Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_correction_moves_bucket_and_removes_zero_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = NewEvent(model: "gpt-5.4", input: 100, cost: 1m);
        var corrected = NewEvent(model: "gpt-5.5", input: 175, cost: 2m);

        (await _repo.RecordEventAsync(first, ct)).Disposition.Should().Be(RecordEventDisposition.Created);
        (await _repo.RecordEventAsync(corrected, ct)).Disposition.Should().Be(RecordEventDisposition.Corrected);
        (await _repo.RecordEventAsync(NewEvent(model: "gpt-5.5", input: 175, cost: 2m), ct))
            .Disposition.Should()
            .Be(RecordEventDisposition.Unchanged);

        var rows = await _ctx.DailyAggregates.AsNoTracking().OrderBy(x => x.Model).ToListAsync(ct);
        rows.Should().NotContain(x => x.Model == "gpt-5.4");
        rows.Single(x => x.Model == "gpt-5.5").InputTokens.Should().Be(175);
        rows.Sum(x => x.CostUsd).Should().Be(2m);
    }

    [Fact]
    public async Task RecordEvent_correction_updates_unknown_cost_and_cache_savings_counts()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repo.RecordEventAsync(NewEvent(cost: null, cacheSavings: null), ct);
        await _repo.RecordEventAsync(NewEvent(cost: 2m, cacheSavings: 0.5m), ct);

        var known = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        known.CostUsd.Should().Be(2m);
        known.UnknownCostCount.Should().Be(0);
        known.CacheSavingsUsd.Should().Be(0.5m);
        known.UnknownCacheSavingsCount.Should().Be(0);

        await _repo.RecordEventAsync(NewEvent(cost: null, cacheSavings: null), ct);

        var unknown = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        unknown.CostUsd.Should().Be(0m);
        unknown.UnknownCostCount.Should().Be(1);
        unknown.CacheSavingsUsd.Should().Be(0m);
        unknown.UnknownCacheSavingsCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_new_cache_write_preserves_negative_savings_in_new_aggregate()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repo.RecordEventAsync(NewEvent(cacheSavings: -0.75m), ct);

        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.CacheSavingsUsd.Should().Be(-0.75m);
        aggregate.UnknownCacheSavingsCount.Should().Be(0);
    }

    [Fact]
    public async Task RecordEvent_identical_replay_advances_the_observed_at_watermark()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = NewEvent();
        var reread = NewEvent(
            ingestedAt: Instant.FromUtc(2026, 8, 24, 13, 0),
            observedAt: Instant.FromUtc(2026, 8, 24, 13, 1)
        );

        await _repo.RecordEventAsync(first, ct);
        var result = await _repo.RecordEventAsync(reread, ct);

        // Values and aggregates are untouched, but the watermark moves forward so a delayed
        // stale snapshot observed between the two instants can no longer pass the ordering guard.
        result.Disposition.Should().Be(RecordEventDisposition.Unchanged);
        result.IsDuplicate.Should().BeTrue();
        result.WatermarkAdvanced.Should().BeTrue();
        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.IngestedAt.Should().Be(first.IngestedAt);
        saved.ObservedAt.Should().Be(reread.ObservedAt);
    }

    [Fact]
    public async Task RecordEvent_stale_correction_between_replays_is_rejected_by_the_watermark()
    {
        var ct = TestContext.Current.CancellationToken;
        // Explicit instants: the recorded object is the tracked row, so deriving later timestamps
        // from first.ObservedAt after the replay would observe the advanced watermark instead.
        var original = Instant.FromUtc(2026, 8, 24, 12, 2);
        var first = NewEvent(observedAt: original);
        var replay = NewEvent(observedAt: original + Duration.FromMinutes(2));

        await _repo.RecordEventAsync(first, ct);
        await _repo.RecordEventAsync(replay, ct);

        // A differing snapshot observed after the original but before the replay must not roll
        // the event (or its aggregate) back: the identical replay already advanced the watermark.
        var delayed = NewEvent(input: 175, observedAt: original + Duration.FromMinutes(1));
        var result = await _repo.RecordEventAsync(delayed, ct);

        result.Disposition.Should().Be(RecordEventDisposition.Unchanged);
        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.InputTokens.Should().Be(first.InputTokens);
        saved.ObservedAt.Should().Be(replay.ObservedAt);
        (await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct)).InputTokens.Should().Be(first.InputTokens);
    }

    [Fact]
    public async Task RecordEvent_correction_copies_the_new_observed_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var correctedObservedAt = Instant.FromUtc(2026, 8, 24, 13, 1);

        await _repo.RecordEventAsync(NewEvent(input: 100), ct);
        var result = await _repo.RecordEventAsync(NewEvent(input: 175, observedAt: correctedObservedAt), ct);

        result.Disposition.Should().Be(RecordEventDisposition.Corrected);
        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.ObservedAt.Should().Be(correctedObservedAt);
    }

    [Fact]
    public async Task RecordEvent_older_correction_does_not_replace_newer_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var newerObservedAt = Instant.FromUtc(2026, 8, 24, 13, 1);

        await _repo.RecordEventAsync(NewEvent(input: 175, observedAt: newerObservedAt), ct);
        var result = await _repo.RecordEventAsync(NewEvent(input: 100), ct);

        result.Disposition.Should().Be(RecordEventDisposition.Unchanged);
        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.InputTokens.Should().Be(175);
        (await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct)).InputTokens.Should().Be(175);
    }

    [Fact]
    public async Task RecordEvent_unchanged_local_snapshot_repairs_missing_source_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = NewEvent(sourceId: "codex-local");
        first.SourceKind = SourceKind.LocalTelemetry;
        first.UsageScope = UsageScope.Subscription;

        await _repo.RecordEventAsync(first, ct);
        await _ctx.SourceSyncStates.ExecuteDeleteAsync(ct);
        var replay = NewEvent(sourceId: "codex-local", observedAt: first.ObservedAt + Duration.FromMinutes(1));
        replay.SourceKind = SourceKind.LocalTelemetry;
        replay.UsageScope = UsageScope.Subscription;

        (await _repo.RecordEventAsync(replay, ct)).Disposition.Should().Be(RecordEventDisposition.Unchanged);
        var status = await _ctx.SourceSyncStates.AsNoTracking().SingleAsync(ct);
        status.SourceId.Should().Be("codex-local");
        status.LatestObservationAt.Should().Be(replay.ObservedAt);
    }

    [Fact]
    public async Task RecordEvent_correction_refreshes_a_stale_tracked_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;

        await _repo.RecordEventAsync(NewEvent(input: 100), ct);
        await using (var otherContext = new AiObservatoryDbContext(options))
        {
            var otherRepository = new UsageRepository(otherContext);
            (await otherRepository.RecordEventAsync(NewEvent(input: 200), ct))
                .Disposition.Should()
                .Be(RecordEventDisposition.Corrected);
            (await otherContext.DailyAggregates.AsNoTracking().SingleAsync(ct)).InputTokens.Should().Be(200);
        }

        await _repo.RecordEventAsync(NewEvent(input: 300), ct);

        _ctx.ChangeTracker.Clear();
        (await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct)).InputTokens.Should().Be(300);
        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.InputTokens.Should().Be(300);
        aggregate.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_rejects_duplicate_raw_payload_properties()
    {
        var evt = NewEvent();
        evt.RawPayload = """{"request":"first","request":"last"}""";

        var act = () => _repo.RecordEventAsync(evt, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task RecordEvent_failed_correction_rolls_back_event_and_aggregate()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = NewEvent(input: 100, cost: 1m);
        await _repo.RecordEventAsync(first, ct);

        var act = () => _repo.RecordEventAsync(NewEvent(input: -1, cost: 2m), ct);

        await act.Should().ThrowAsync<DbUpdateException>();
        _ctx.ChangeTracker.Clear();
        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.InputTokens.Should().Be(100);
        saved.CostUsd.Should().Be(1m);
        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.InputTokens.Should().Be(100);
        aggregate.CostUsd.Should().Be(1m);
        aggregate.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_with_same_eventKey_and_different_sourceIds_records_both()
    {
        static UsageEvent NewEvent(string sourceId) =>
            new()
            {
                Provider = Provider.Copilot,
                OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                Model = "gpt-5.4",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
                EventKey = "shared-session-key",
                SourceId = sourceId,
                SourceKind = SourceKind.LocalTelemetry,
                UsageScope = UsageScope.Subscription,
                CostBasis = CostBasis.Notional,
            };

        var first = await _repo.RecordEventAsync(
            NewEvent(UsageSourceIds.CopilotLocal),
            TestContext.Current.CancellationToken
        );
        var second = await _repo.RecordEventAsync(
            NewEvent(UsageSourceIds.GitHubBillingApi),
            TestContext.Current.CancellationToken
        );

        first.Disposition.Should().Be(RecordEventDisposition.Created);
        second.Disposition.Should().Be(RecordEventDisposition.Created);
        (await _ctx.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        (await _ctx.DailyAggregates.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task RecordEvent_without_eventKey_records_every_submission()
    {
        static UsageEvent NewEvent() =>
            new()
            {
                Provider = Provider.Anthropic,
                OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                Model = "claude-sonnet-4-6",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
            };

        var first = await _repo.RecordEventAsync(NewEvent(), TestContext.Current.CancellationToken);
        var second = await _repo.RecordEventAsync(NewEvent(), TestContext.Current.CancellationToken);

        first.Disposition.Should().Be(RecordEventDisposition.Created);
        second.Disposition.Should().Be(RecordEventDisposition.Created);
        (await _ctx.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        var agg = await _ctx.DailyAggregates.SingleAsync(TestContext.Current.CancellationToken);
        agg.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentRecordEventStoresAndAggregatesTheSharedKeyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        await using var firstContext = new AiObservatoryDbContext(options);
        await using var secondContext = new AiObservatoryDbContext(options);
        var firstRepository = new UsageRepository(firstContext);
        var secondRepository = new UsageRepository(secondContext);

        static UsageEvent NewEvent() =>
            new()
            {
                Provider = Provider.OpenAI,
                OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 1),
                Model = "gpt-5.4",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
                EventKey = "openai:2026-06-02:gpt-5.4",
            };

        await using var gateConnection = new NpgsqlConnection(_connStr);
        await gateConnection.OpenAsync(ct);
        await using var gateTransaction = await gateConnection.BeginTransactionAsync(ct);
        await using (var command = gateConnection.CreateCommand())
        {
            command.CommandText = """LOCK TABLE "DailyAggregates" IN SHARE MODE""";
            await command.ExecuteNonQueryAsync(ct);
        }

        var first = firstRepository.RecordEventAsync(NewEvent(), ct);
        var second = secondRepository.RecordEventAsync(NewEvent(), ct);
        await WaitForBlockedWritesAsync(gateConnection, "DailyAggregates", ct);
        await gateTransaction.CommitAsync(ct);

        var results = await Task.WhenAll(first, second);

        results.Should().ContainSingle(r => !r.IsDuplicate);
        results.Should().ContainSingle(r => r.IsDuplicate);
        results[0].EventId.Should().Be(results[1].EventId);
        (await _ctx.UsageEvents.AsNoTracking().CountAsync(ct)).Should().Be(1);
        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.InputTokens.Should().Be(100);
        aggregate.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentRecordEvent_returns_its_source_scoped_winner_when_a_sibling_source_has_the_same_key()
    {
        var ct = TestContext.Current.CancellationToken;
        const string eventKey = "openai:2026-06-02:gpt-5.4";
        const string targetSourceId = UsageSourceIds.OpenAiUsageApi;
        var sibling = new UsageEvent
        {
            Provider = Provider.OpenAI,
            OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
            IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 1),
            Model = "gpt-5.4",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.01m,
            EventKey = eventKey,
            SourceId = UsageSourceIds.OpenAiCostsApi,
        };
        await _repo.RecordEventAsync(sibling, ct);

        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        await using var firstContext = new AiObservatoryDbContext(options);
        await using var secondContext = new AiObservatoryDbContext(options);
        var firstRepository = new UsageRepository(firstContext);
        var secondRepository = new UsageRepository(secondContext);

        static UsageEvent NewTargetEvent() =>
            new()
            {
                Provider = Provider.OpenAI,
                OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 1),
                Model = "gpt-5.4",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
                EventKey = eventKey,
                SourceId = targetSourceId,
            };

        await using var gateConnection = new NpgsqlConnection(_connStr);
        await gateConnection.OpenAsync(ct);
        await using var gateTransaction = await gateConnection.BeginTransactionAsync(ct);
        await using (var command = gateConnection.CreateCommand())
        {
            command.CommandText = """LOCK TABLE "DailyAggregates" IN SHARE MODE""";
            await command.ExecuteNonQueryAsync(ct);
        }

        var first = firstRepository.RecordEventAsync(NewTargetEvent(), ct);
        var second = secondRepository.RecordEventAsync(NewTargetEvent(), ct);
        await WaitForBlockedWritesAsync(gateConnection, "DailyAggregates", ct);
        await gateTransaction.CommitAsync(ct);

        var results = await Task.WhenAll(first, second);

        var targetId = results.Single(r => !r.IsDuplicate).EventId;
        results.Single(r => r.IsDuplicate).EventId.Should().Be(targetId);
        targetId.Should().NotBe(sibling.Id);
    }

    private static async Task WaitForBlockedWritesAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken ct
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT count(*)
                FROM pg_locks locks
                JOIN pg_class tables ON tables.oid = locks.relation
                WHERE tables.relname = @tableName AND NOT locks.granted
                """;
            command.Parameters.AddWithValue("tableName", tableName);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token)) == 2)
            {
                return;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task PatchEventCost_updates_event_and_aggregate()
    {
        var ct = TestContext.Current.CancellationToken;

        // Record an event so both UsageEvents and DailyAggregates rows exist.
        var evt = new UsageEvent
        {
            Provider = Provider.Google,
            OccurredAt = Instant.FromUtc(2026, 6, 3, 9, 0),
            IngestedAt = Instant.FromUtc(2026, 6, 3, 9, 0),
            Model = "gemini-3.5-flash",
            InputTokens = 12000,
            OutputTokens = 800,
            CacheWriteTokens = 480,
            CostUsd = 0.0024m,
            EventKey = "gemini:sess-abc:gemini-3.5-flash",
        };
        await _repo.RecordEventAsync(evt, ct);

        // PATCH to the corrected cost.
        var newCost = 12000 * 1.50m / 1_000_000 + 800 * 9.00m / 1_000_000 + 480 * 3.50m / 1_000_000;
        var result = await _repo.PatchEventCostAsync(
            Provider.Google,
            UsageSourceIds.LegacyApi,
            "gemini:sess-abc:gemini-3.5-flash",
            newCost,
            ct: ct
        );

        result.Should().NotBeNull();
        result.OldCostUsd.Should().Be(0.0024m);
        result.NewCostUsd.Should().Be(newCost);

        // ExecuteUpdateAsync bypasses the EF change tracker; use AsNoTracking to read the live DB row.
        var saved = await _ctx.UsageEvents.AsNoTracking().FirstAsync(e => e.Id == evt.Id, ct);
        saved.CostUsd.Should().Be(newCost);

        var agg = await _ctx.DailyAggregates.FirstOrDefaultAsync(a => a.Model == "gemini-3.5-flash", ct);
        agg.Should().NotBeNull();
        // Aggregate delta = newCost - 0.0024m; check it's updated correctly.
        agg.CostUsd.Should().BeApproximately(newCost, precision: 0.000001m);
    }

    [Fact]
    public async Task PatchEventCost_returns_null_for_unknown_key()
    {
        var result = await _repo.PatchEventCostAsync(
            Provider.Google,
            UsageSourceIds.LegacyApi,
            "gemini:nonexistent:model",
            0.01m,
            ct: TestContext.Current.CancellationToken
        );

        result.Should().BeNull();
    }

    [Fact]
    public async Task PatchEventCost_noop_when_cost_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;

        var evt = new UsageEvent
        {
            Provider = Provider.Google,
            OccurredAt = Instant.FromUtc(2026, 6, 4, 9, 0),
            IngestedAt = Instant.FromUtc(2026, 6, 4, 9, 0),
            Model = "gemini-2.5-pro",
            InputTokens = 5000,
            OutputTokens = 200,
            CostUsd = 0.0083m,
            EventKey = "gemini:sess-xyz:gemini-2.5-pro",
        };
        await _repo.RecordEventAsync(evt, ct);

        var result = await _repo.PatchEventCostAsync(
            Provider.Google,
            UsageSourceIds.LegacyApi,
            "gemini:sess-xyz:gemini-2.5-pro",
            0.0083m,
            ct: ct
        );

        result.Should().NotBeNull();
        result.OldCostUsd.Should().Be(0.0083m);
        result.NewCostUsd.Should().Be(0.0083m);
        // CostUsd unchanged on the entity.
        (await _ctx.UsageEvents.FindAsync([evt.Id], ct))!
            .CostUsd.Should()
            .Be(0.0083m);
    }

    [Fact]
    public async Task PatchEventCost_reports_null_old_cost_for_a_previously_unpriced_event()
    {
        var ct = TestContext.Current.CancellationToken;
        const string eventKey = "unpriced-cost-key";
        await _repo.RecordEventAsync(NewEvent(cost: null, eventKey: eventKey), ct);

        var result = await _repo.PatchEventCostAsync(
            Provider.OpenAI,
            UsageSourceIds.OpenAiUsageApi,
            eventKey,
            2m,
            ct: ct
        );

        // "Was unknown" must not collapse into "was zero": the aggregate side of the same
        // operation distinguishes them (UnknownCostCount decrements), so the result does too.
        result.Should().NotBeNull();
        result.OldCostUsd.Should().BeNull();
        result.NewCostUsd.Should().Be(2m);
    }

    [Fact]
    public async Task PatchEventCost_updates_only_the_exact_source()
    {
        var ct = TestContext.Current.CancellationToken;
        const string eventKey = "shared-cost-key";
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.OpenAiUsageApi, eventKey: eventKey), ct);
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.CodexLocal, eventKey: eventKey), ct);

        var result = await _repo.PatchEventCostAsync(Provider.OpenAI, UsageSourceIds.CodexLocal, eventKey, 3m, ct: ct);

        result.Should().NotBeNull();
        var events = await _ctx.UsageEvents.AsNoTracking().OrderBy(x => x.SourceId).ToListAsync(ct);
        events.Single(x => x.SourceId == UsageSourceIds.CodexLocal).CostUsd.Should().Be(3m);
        events.Single(x => x.SourceId == UsageSourceIds.OpenAiUsageApi).CostUsd.Should().Be(1m);
        var rows = await _ctx.DailyAggregates.AsNoTracking().ToListAsync(ct);
        rows.Single(x => x.SourceId == UsageSourceIds.CodexLocal).CostUsd.Should().Be(3m);
        rows.Single(x => x.SourceId == UsageSourceIds.OpenAiUsageApi).CostUsd.Should().Be(1m);
    }

    // The by-id FOR UPDATE lookup returns the already-tracked instance when the context has one, so it
    // reloads it. Without that reload the repricing delta is calculated from the pre-correction cost and
    // the aggregate ends up 1.00 too high, which no other test covered.
    [Fact]
    public async Task UpdateEventPricing_refreshes_a_stale_tracked_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;

        var first = NewEvent(cost: 1m, cacheSavings: 0m);
        first.CostBasis = CostBasis.ListPriceEstimate;
        await _repo.RecordEventAsync(first, ct);

        await using (var other = new AiObservatoryDbContext(options))
        {
            var otherRepository = new UsageRepository(other);
            var corrected = NewEvent(cost: 2m, cacheSavings: 0m, observedAt: Instant.FromUtc(2026, 8, 24, 12, 3));
            corrected.CostBasis = CostBasis.ListPriceEstimate;
            (await otherRepository.RecordEventAsync(corrected, ct))
                .Disposition.Should()
                .Be(RecordEventDisposition.Corrected);
            (await other.DailyAggregates.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(2m);
        }

        var current = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        await _repo.UpdateEventPricingAsync(current, new UsagePriceQuote(5m, 0m), ct);

        _ctx.ChangeTracker.Clear();
        (await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(5m);
        (await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(5m);
    }

    [Fact]
    public async Task PatchEventCost_refreshes_a_stale_tracked_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        const string eventKey = "stale-cost-key";
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;

        await _repo.RecordEventAsync(NewEvent(eventKey: eventKey, cost: 1m), ct);
        await using (var otherContext = new AiObservatoryDbContext(options))
        {
            var otherRepository = new UsageRepository(otherContext);
            var otherResult = await otherRepository.PatchEventCostAsync(
                Provider.OpenAI,
                UsageSourceIds.OpenAiUsageApi,
                eventKey,
                2m,
                ct: ct
            );
            otherResult.Should().NotBeNull();
            (await otherContext.DailyAggregates.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(2m);
        }

        await _repo.PatchEventCostAsync(Provider.OpenAI, UsageSourceIds.OpenAiUsageApi, eventKey, 3m, ct: ct);

        _ctx.ChangeTracker.Clear();
        (await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(3m);
        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.CostUsd.Should().Be(3m);
        aggregate.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetEventsByProvider_round_trips_into_PatchEventCost_for_every_source()
    {
        var ct = TestContext.Current.CancellationToken;
        // legacy-api rows are stored provider-prefixed; codex-local rows are stored as-is. The
        // projection must carry enough identity (SourceId + stored EventKey) for both to be
        // patchable without the caller knowing the storage convention.
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.LegacyApi, eventKey: "round-trip-legacy"), ct);
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.CodexLocal, eventKey: "round-trip-codex"), ct);

        var events = await _repo.GetEventsByProviderAsync(Provider.OpenAI, ct: ct);

        events.Should().HaveCount(2);
        foreach (var record in events)
        {
            var patch = await _repo.PatchEventCostAsync(Provider.OpenAI, record.SourceId, record.EventKey!, 5m, ct: ct);
            patch.Should().NotBeNull($"the projected identity of {record.SourceId}/{record.EventKey} must patch");
            patch.NewCostUsd.Should().Be(5m);
        }

        var saved = await _ctx.UsageEvents.AsNoTracking().ToListAsync(ct);
        saved.Should().OnlyContain(e => e.CostUsd == 5m);
    }

    [Fact]
    public async Task PatchEventCost_accepts_an_already_prefixed_legacy_key_without_double_prefixing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.LegacyApi, eventKey: "stored-form-key"), ct);

        var viaStoredForm = await _repo.PatchEventCostAsync(
            Provider.OpenAI,
            UsageSourceIds.LegacyApi,
            "OpenAI:stored-form-key",
            2m,
            ct: ct
        );

        viaStoredForm.Should().NotBeNull("the stored key form must not be double-prefixed into a 404");
        (await _ctx.UsageEvents.AsNoTracking().SingleAsync(e => e.EventKey == "OpenAI:stored-form-key", ct))
            .CostUsd.Should()
            .Be(2m);
    }

    [Fact]
    public async Task RecordEvent_a_prefixed_raw_legacy_key_is_stored_distinct_from_the_unprefixed_key()
    {
        var ct = TestContext.Current.CancellationToken;
        // A raw legacy key that happens to start with the provider name is not the stored form
        // of "x": the shape test stored both under "OpenAI:x", merging two distinct events.
        // Recorded in this order, the canonical (always-prefixed) form keeps them apart. (The
        // reverse order resolves "OpenAI:x" to the existing row by lookup: an input that equals
        // another event's stored key is irreducibly ambiguous, and the lookup never splits a
        // row into a double-counting second one.)
        await _repo.RecordEventAsync(
            NewEvent(sourceId: UsageSourceIds.LegacyApi, eventKey: "OpenAI:x", input: 400),
            ct
        );
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.LegacyApi, eventKey: "x", input: 100), ct);

        var saved = await _ctx.UsageEvents.AsNoTracking().ToDictionaryAsync(e => e.EventKey!, ct);
        saved.Keys.Should().BeEquivalentTo("OpenAI:x", "OpenAI:OpenAI:x");
        saved["OpenAI:x"].InputTokens.Should().Be(100);
        saved["OpenAI:OpenAI:x"].InputTokens.Should().Be(400);

        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.InputTokens.Should().Be(500);
        aggregate.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task RecordEvent_a_raw_key_matching_another_providers_stored_form_stores_a_distinct_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.LegacyApi, eventKey: "x"), ct);
        // "OpenAI:x" is OpenAI's stored form for raw "x", but a Google event with that raw key
        // is its own event: the verbatim fallback is source-scoped, not provider-scoped, so
        // without the provider check this would correct OpenAI's row with Google data.
        var googleEvent = NewEvent(sourceId: UsageSourceIds.LegacyApi, eventKey: "OpenAI:x");
        googleEvent.Provider = Provider.Google;
        var result = await _repo.RecordEventAsync(googleEvent, ct);

        result.Disposition.Should().Be(RecordEventDisposition.Created);
        var saved = await _ctx.UsageEvents.AsNoTracking().ToListAsync(ct);
        saved.Should().HaveCount(2);
        saved.Should().ContainSingle(e => e.Provider == Provider.OpenAI && e.EventKey == "OpenAI:x");
        saved.Should().ContainSingle(e => e.Provider == Provider.Google && e.EventKey == "Google:OpenAI:x");
    }

    [Fact]
    public async Task RecordEvent_a_replayed_prefixed_raw_legacy_key_corrects_the_double_prefixed_row()
    {
        var ct = TestContext.Current.CancellationToken;
        // The old unconditional prefixing (and the AddObservationProvenance migration) stored a
        // raw "OpenAI:x" key as "OpenAI:OpenAI:x". The shape test resolved a replay to "OpenAI:x"
        // instead, missed the row and inserted a second one; the canonical form finds it.
        await _repo.RecordEventAsync(
            NewEvent(sourceId: UsageSourceIds.LegacyApi, eventKey: "OpenAI:x", input: 100),
            ct
        );

        var replay = NewEvent(
            sourceId: UsageSourceIds.LegacyApi,
            eventKey: "OpenAI:x",
            input: 700,
            observedAt: Instant.FromUtc(2026, 8, 24, 12, 5)
        );
        var result = await _repo.RecordEventAsync(replay, ct);

        result.Disposition.Should().Be(RecordEventDisposition.Corrected);
        var rows = await _ctx.UsageEvents.AsNoTracking().ToListAsync(ct);
        rows.Should().ContainSingle("the replay corrects the double-prefixed row, not inserts a second one");
        rows[0].EventKey.Should().Be("OpenAI:OpenAI:x");
        rows[0].InputTokens.Should().Be(700);
        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.InputTokens.Should().Be(700);
        aggregate.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task PatchEventCost_a_raw_prefixed_legacy_key_resolves_the_double_prefixed_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.LegacyApi, eventKey: "OpenAI:x"), ct);

        // The canonical form is tried first: the raw key "OpenAI:x" belongs to the row stored
        // as "OpenAI:OpenAI:x".
        var patch = await _repo.PatchEventCostAsync(Provider.OpenAI, UsageSourceIds.LegacyApi, "OpenAI:x", 4m, ct: ct);

        patch.Should().NotBeNull("the raw key prefixes to the double-prefixed row, not 404s");
        (await _ctx.UsageEvents.AsNoTracking().SingleAsync(e => e.EventKey == "OpenAI:OpenAI:x", ct))
            .CostUsd.Should()
            .Be(4m);
    }

    [Fact]
    public async Task PatchEventCost_the_stored_double_prefixed_key_round_trips_from_the_projection()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.LegacyApi, eventKey: "OpenAI:x"), ct);
        var storedKey = (await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct)).EventKey!;
        storedKey.Should().Be("OpenAI:OpenAI:x");

        // A caller echoing the projection holds the stored key itself; the canonical lookup
        // misses ("OpenAI:OpenAI:OpenAI:x") and the verbatim fallback finds the row.
        var patch = await _repo.PatchEventCostAsync(Provider.OpenAI, UsageSourceIds.LegacyApi, storedKey, 6m, ct: ct);

        patch.Should().NotBeNull("the stored key form must resolve to its own row");
        (await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(6m);
    }

    [Fact]
    public async Task PatchEventCost_rebases_an_estimated_event_and_survives_costless_replay()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = NewEvent(cost: 1m);
        original.CostBasis = CostBasis.ListPriceEstimate;
        await _repo.RecordEventAsync(original, ct);

        var patch = await _repo.PatchEventCostAsync(
            Provider.OpenAI,
            UsageSourceIds.OpenAiUsageApi,
            "day:model",
            3m,
            ct: ct
        );
        patch.Should().NotBeNull();

        var corrected = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        corrected.CostUsd.Should().Be(3m);
        corrected
            .CostBasis.Should()
            .Be(CostBasis.ProviderEstimated, "a corrected estimate leaves the repricer's scan set");
        corrected.CorrectedAt.Should().NotBeNull();
        (corrected.CorrectedAt > corrected.ObservedAt).Should().BeTrue();

        // The local sweepers' steady state: every snapshot re-posted with costUsd null and a
        // fresh ObservedAt. Before the marker this rolled the correction back to unknown.
        var replay = NewEvent(
            cost: null,
            cacheSavings: null,
            input: 175,
            observedAt: Instant.FromUtc(2026, 8, 24, 12, 5)
        );
        replay.CostBasis = CostBasis.Notional;
        replay.SourceKind = SourceKind.LocalTelemetry;
        (await _repo.RecordEventAsync(replay, ct)).Disposition.Should().Be(RecordEventDisposition.Corrected);

        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.InputTokens.Should().Be(175, "the replay still updates the usage figures");
        saved.CostUsd.Should().Be(3m, "the replay carries no cost and must not erase the correction");
        saved.CostBasis.Should().Be(CostBasis.ProviderEstimated);
        saved.CorrectedAt.Should().Be(corrected.CorrectedAt);

        var aggregates = await _ctx.DailyAggregates.AsNoTracking().ToListAsync(ct);
        var row = aggregates.Should().ContainSingle().Which;
        row.CostBasis.Should().Be(CostBasis.ProviderEstimated);
        row.CostUsd.Should().Be(3m);
        row.InputTokens.Should().Be(175);
        row.UnknownCostCount.Should().Be(0);
    }

    [Fact]
    public async Task PatchEventCost_noop_patch_still_stamps_CorrectedAt_and_survives_costless_replay()
    {
        var ct = TestContext.Current.CancellationToken;
        // The stored cost already equals the patched figure, so the snapshot apply is a
        // no-op -- but the re-applied patch is still an operator correction and must
        // (re)stamp the marker, or the next cost-less replay silently undoes it.
        await _repo.RecordEventAsync(NewEvent(cost: 3m), ct);

        var patch = await _repo.PatchEventCostAsync(
            Provider.OpenAI,
            UsageSourceIds.OpenAiUsageApi,
            "day:model",
            3m,
            ct: ct
        );
        patch.Should().NotBeNull();
        patch.OldCostUsd.Should().Be(3m);
        patch.NewCostUsd.Should().Be(3m);

        var corrected = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        corrected.CostUsd.Should().Be(3m);
        corrected.CorrectedAt.Should().NotBeNull("a no-op patch is still an operator correction");

        var replay = NewEvent(
            cost: null,
            cacheSavings: null,
            input: 175,
            observedAt: Instant.FromUtc(2026, 8, 24, 12, 5)
        );
        replay.CostBasis = CostBasis.Notional;
        replay.SourceKind = SourceKind.LocalTelemetry;
        (await _repo.RecordEventAsync(replay, ct)).Disposition.Should().Be(RecordEventDisposition.Corrected);

        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.InputTokens.Should().Be(175, "the replay still updates the usage figures");
        saved.CostUsd.Should().Be(3m, "the cost-less replay must not undo the re-applied correction");
        saved.CorrectedAt.Should().Be(corrected.CorrectedAt);
    }

    [Fact]
    public async Task Replay_with_an_explicit_cost_reasserts_source_authority_over_a_correction()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordEventAsync(NewEvent(cost: 1m), ct);
        await _repo.PatchEventCostAsync(Provider.OpenAI, UsageSourceIds.OpenAiUsageApi, "day:model", 3m, ct: ct);

        var reasserted = NewEvent(cost: 2m, observedAt: Instant.FromUtc(2026, 8, 24, 12, 5));
        (await _repo.RecordEventAsync(reasserted, ct)).Disposition.Should().Be(RecordEventDisposition.Corrected);

        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.CostUsd.Should().Be(2m);
        saved.CorrectedAt.Should().BeNull("a source post with its own cost supersedes the manual figure");
        (await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(2m);
    }

    [Fact]
    public async Task PatchEventCost_equal_value_patch_rebases_an_estimated_event_out_of_the_repricing_scan()
    {
        var ct = TestContext.Current.CancellationToken;
        // The operator confirms the figure the estimate already carries. The cost does not
        // change, but authority does: the row must leave the ListPriceEstimate bucket the
        // repricer scans, or the next catalog activation silently rewrites the confirmed figure.
        var original = NewEvent(cost: 3m);
        original.CostBasis = CostBasis.ListPriceEstimate;
        await _repo.RecordEventAsync(original, ct);

        var patch = await _repo.PatchEventCostAsync(
            Provider.OpenAI,
            UsageSourceIds.OpenAiUsageApi,
            "day:model",
            3m,
            ct: ct
        );

        patch.Should().NotBeNull();
        patch.OldCostUsd.Should().Be(3m);
        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.CostUsd.Should().Be(3m);
        saved
            .CostBasis.Should()
            .Be(CostBasis.ProviderEstimated, "an equal-value patch is still an operator correction");
        saved.CorrectedAt.Should().NotBeNull();
        saved
            .CacheSavingsUsd.Should()
            .Be(0.25m, "no savings override was supplied, so the stored figure carries through");
        var row = (await _ctx.DailyAggregates.AsNoTracking().ToListAsync(ct)).Should().ContainSingle().Which;
        row.CostBasis.Should().Be(CostBasis.ProviderEstimated);
        row.CostUsd.Should().Be(3m);
    }

    [Fact]
    public async Task PatchEventCost_applies_the_corrected_cache_savings_and_clears_the_unknown_count()
    {
        var ct = TestContext.Current.CancellationToken;
        // An event whose cost and cache savings are both unknown counts in
        // UnknownCacheSavingsCount; the correction rebases it out of the repricer's scan, so the
        // patch itself must supply the savings figure or the count could never clear.
        var original = NewEvent(cost: null, cacheSavings: null);
        original.CostBasis = CostBasis.ListPriceEstimate;
        await _repo.RecordEventAsync(original, ct);
        var before = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        before.UnknownCostCount.Should().Be(1);
        before.UnknownCacheSavingsCount.Should().Be(1);

        await _repo.PatchEventCostAsync(Provider.OpenAI, UsageSourceIds.OpenAiUsageApi, "day:model", 2m, 0.5m, ct);

        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.CostUsd.Should().Be(2m);
        saved.CacheSavingsUsd.Should().Be(0.5m);
        saved.CostBasis.Should().Be(CostBasis.ProviderEstimated);
        var row = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        row.CostUsd.Should().Be(2m);
        row.CacheSavingsUsd.Should().Be(0.5m);
        row.UnknownCostCount.Should().Be(0);
        row.UnknownCacheSavingsCount.Should().Be(0);
    }

    [Fact]
    public async Task PatchEventCost_without_a_savings_override_keeps_the_stored_savings()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordEventAsync(NewEvent(cost: 1m, cacheSavings: 0.25m), ct);

        await _repo.PatchEventCostAsync(Provider.OpenAI, UsageSourceIds.OpenAiUsageApi, "day:model", 3m, ct: ct);

        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.CostUsd.Should().Be(3m);
        saved.CacheSavingsUsd.Should().Be(0.25m, "a null override leaves the stored savings untouched");
        (await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct)).CacheSavingsUsd.Should().Be(0.25m);
    }

    [Fact]
    public async Task Replay_with_the_same_explicit_cost_clears_correction_authority()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordEventAsync(NewEvent(cost: 1m), ct);
        await _repo.PatchEventCostAsync(Provider.OpenAI, UsageSourceIds.OpenAiUsageApi, "day:model", 3m, ct: ct);
        (await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct)).CorrectedAt.Should().NotBeNull();

        // The source re-posts the identical snapshot *with* the cost the operator set: every
        // canonical value matches, so this takes the identical-replay path — which used to
        // return before the correction-authority logic and leave the stale marker set.
        var reasserted = NewEvent(cost: 3m, observedAt: Instant.FromUtc(2026, 8, 24, 12, 5));
        (await _repo.RecordEventAsync(reasserted, ct)).Disposition.Should().Be(RecordEventDisposition.Unchanged);

        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.CostUsd.Should().Be(3m);
        saved.CorrectedAt.Should().BeNull("a source post carrying the same explicit cost re-asserts source authority");
        saved.ObservedAt.Should().Be(reasserted.ObservedAt, "the watermark still advances on an identical replay");
    }

    [Fact]
    public async Task PurgeProvider_deletes_events_and_aggregates_for_only_that_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordEventAsync(NewEvent(cost: 1m, eventKey: "purge-a"), ct);
        await _repo.RecordEventAsync(NewEvent(cost: 2m, eventKey: "purge-b", model: "gpt-other"), ct);
        var google = new UsageEvent
        {
            Provider = Provider.Google,
            OccurredAt = Instant.FromUtc(2026, 6, 3, 9, 0),
            IngestedAt = Instant.FromUtc(2026, 6, 3, 9, 0),
            ObservedAt = Instant.FromUtc(2026, 6, 3, 9, 1),
            Model = "gemini-3.5-flash",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.01m,
            RawPayload = "{}",
            EventKey = "purge-google",
        };
        await _repo.RecordEventAsync(google, ct);

        var result = await _repo.PurgeProviderAsync(Provider.OpenAI, ct);

        // Two events in two distinct (date, model) buckets: both aggregate rows go with them.
        result.DeletedEvents.Should().Be(2);
        result.DeletedAggregates.Should().Be(2);
        var surviving = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        surviving.Provider.Should().Be(Provider.Google);
        var survivingAggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        survivingAggregate.Provider.Should().Be(Provider.Google);
        survivingAggregate.CostUsd.Should().Be(0.01m);
    }

    [Fact]
    public async Task PurgeProvider_with_nothing_to_delete_returns_zero_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordEventAsync(NewEvent(), ct);

        var result = await _repo.PurgeProviderAsync(Provider.Google, ct);

        result.DeletedEvents.Should().Be(0);
        result.DeletedAggregates.Should().Be(0);
        (await _ctx.UsageEvents.AsNoTracking().CountAsync(ct)).Should().Be(1);
        (await _ctx.DailyAggregates.AsNoTracking().CountAsync(ct)).Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_with_CostBasis_None_counts_as_a_known_zero_not_missing_pricing()
    {
        var ct = TestContext.Current.CancellationToken;
        var evt = NewEvent(cost: null, cacheSavings: null);
        evt.CostBasis = CostBasis.None;

        await _repo.RecordEventAsync(evt, ct);

        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.UnknownCostCount.Should().Be(0, "None declares that no price applies");
        aggregate.UnknownCacheSavingsCount.Should().Be(0);
        aggregate.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_rejects_a_positive_cost_under_CostBasis_None()
    {
        var evt = NewEvent(cost: 1m);
        evt.CostBasis = CostBasis.None;

        var act = () => _repo.RecordEventAsync(evt, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(nameof(DailyAggregate.InputTokens))]
    [InlineData(nameof(DailyAggregate.OutputTokens))]
    [InlineData(nameof(DailyAggregate.CacheReadTokens))]
    [InlineData(nameof(DailyAggregate.CacheWriteTokens))]
    [InlineData(nameof(DailyAggregate.CacheWrite1hTokens))]
    [InlineData(nameof(DailyAggregate.CostUsd))]
    [InlineData(nameof(DailyAggregate.RequestCount))]
    public async Task DailyAggregateRejectsNegativeNumericValues(string property)
    {
        var aggregate = new DailyAggregate
        {
            Date = new LocalDate(2026, 7, 30),
            Provider = Provider.Anthropic,
            Model = property,
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = 1,
            CacheWriteTokens = 1,
            CacheWrite1hTokens = 1,
            CostUsd = 1m,
            RequestCount = 1,
        };
        switch (property)
        {
            case nameof(DailyAggregate.InputTokens):
                aggregate.InputTokens = -1;
                break;
            case nameof(DailyAggregate.OutputTokens):
                aggregate.OutputTokens = -1;
                break;
            case nameof(DailyAggregate.CacheReadTokens):
                aggregate.CacheReadTokens = -1;
                break;
            case nameof(DailyAggregate.CacheWriteTokens):
                aggregate.CacheWriteTokens = -1;
                break;
            case nameof(DailyAggregate.CacheWrite1hTokens):
                aggregate.CacheWrite1hTokens = -1;
                break;
            case nameof(DailyAggregate.CostUsd):
                aggregate.CostUsd = -1m;
                break;
            case nameof(DailyAggregate.RequestCount):
                aggregate.RequestCount = -1;
                break;
        }
        _ctx.DailyAggregates.Add(aggregate);

        var act = () => _ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task DailyAggregateRejectsOneHourCacheWritesAboveTotalCacheWrites()
    {
        _ctx.DailyAggregates.Add(
            new DailyAggregate
            {
                Date = new LocalDate(2026, 7, 30),
                Provider = Provider.Anthropic,
                Model = "invalid-cache-write-split",
                InputTokens = 1,
                OutputTokens = 1,
                CacheReadTokens = 1,
                CacheWriteTokens = 1,
                CacheWrite1hTokens = 2,
                CostUsd = 1m,
                RequestCount = 1,
            }
        );

        var act = () => _ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task GetBilledSpendGbpAsync_sums_signed_in_range_entries_and_filters_by_mapped_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        var anthropic = await _ctx.SpendVendors.SingleAsync(v => v.Provider == Provider.Anthropic, ct);
        var openAi = await _ctx.SpendVendors.SingleAsync(v => v.Provider == Provider.OpenAI, ct);
        var unmapped = await _ctx.SpendVendors.SingleAsync(v => v.Key == "coderabbit", ct);
        var categoryId = await _ctx.SpendCategories.Select(c => c.Id).FirstAsync(ct);

        _ctx.SpendEntries.AddRange(
            Spend(anthropic.Id, categoryId, new LocalDate(2026, 8, 1), 20m),
            Spend(anthropic.Id, categoryId, new LocalDate(2026, 8, 2), -5m),
            Spend(openAi.Id, categoryId, new LocalDate(2026, 8, 2), 7m),
            Spend(unmapped.Id, categoryId, new LocalDate(2026, 8, 2), 3m),
            Spend(anthropic.Id, categoryId, new LocalDate(2026, 7, 31), 100m)
        );
        await _ctx.SaveChangesAsync(ct);

        (await _repo.GetBilledSpendGbpAsync(new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 2), null, ct))
            .Should()
            .Be(25m);
        (
            await _repo.GetBilledSpendGbpAsync(
                new LocalDate(2026, 8, 1),
                new LocalDate(2026, 8, 2),
                Provider.Anthropic,
                ct
            )
        )
            .Should()
            .Be(15m);

        (
            await _repo.GetDailyBilledSpendGbpAsync(
                new LocalDate(2026, 8, 1),
                new LocalDate(2026, 8, 2),
                Provider.Anthropic,
                ct
            )
        )
            .Should()
            .Equal(
                new DailyBilledSpend(new LocalDate(2026, 8, 1), 20m),
                new DailyBilledSpend(new LocalDate(2026, 8, 2), -5m)
            );
    }

    [Fact]
    public async Task Budget_alert_claim_converges_concurrent_and_replayed_calls_on_one_durable_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = new BudgetRule { Period = BillingPeriod.Daily, ThresholdGbp = 10m };
        _ctx.BudgetRules.Add(rule);
        await _ctx.SaveChangesAsync(ct);

        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        await using var firstContext = new AiObservatoryDbContext(options);
        await using var secondContext = new AiObservatoryDbContext(options);
        var firstRepository = new UsageRepository(firstContext);
        var secondRepository = new UsageRepository(secondContext);
        var period = new LocalDate(2026, 8, 1);
        var triggeredAt = Instant.FromUtc(2026, 8, 2, 0, 5);

        var results = await Task.WhenAll(
            firstRepository.GetOrCreateBudgetAlertAsync(
                rule.Id,
                period,
                period,
                10m,
                15m,
                AlertInsight(period, triggeredAt),
                triggeredAt,
                ct
            ),
            secondRepository.GetOrCreateBudgetAlertAsync(
                rule.Id,
                period,
                period,
                10m,
                15m,
                AlertInsight(period, triggeredAt),
                triggeredAt,
                ct
            )
        );

        results.Should().ContainSingle(result => result.Created);
        results.Should().ContainSingle(result => !result.Created);
        results.Select(result => result.ClaimId).Distinct().Should().ContainSingle();
        (await _ctx.BudgetAlertClaims.AsNoTracking().CountAsync(ct)).Should().Be(1);
        (await _ctx.Insights.AsNoTracking().CountAsync(ct)).Should().Be(1);
        (await _ctx.BudgetRules.AsNoTracking().SingleAsync(candidate => candidate.Id == rule.Id, ct))
            .LastTriggeredAt.Should()
            .Be(triggeredAt);

        BudgetAlertClaimResult replay;
        await using (var readOnlyTx = await firstContext.Database.BeginTransactionAsync(ct))
        {
            await firstContext.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            replay = await firstRepository.GetOrCreateBudgetAlertAsync(
                rule.Id,
                period,
                period,
                99m,
                100m,
                AlertInsight(period, triggeredAt),
                triggeredAt,
                ct
            );
            await readOnlyTx.RollbackAsync(ct);
        }
        replay.Created.Should().BeFalse();
        replay.ThresholdGbp.Should().Be(10m);
        replay.ActualSpendGbp.Should().Be(15m);
        (await _ctx.BudgetAlertClaims.AsNoTracking().CountAsync(ct)).Should().Be(1);
        (await _ctx.Insights.AsNoTracking().CountAsync(ct)).Should().Be(1);

        var leaseDuration = Duration.FromMinutes(15);
        var pending = await firstRepository.GetDeliverableBudgetAlertEmailsAsync(
            triggeredAt.Minus(leaseDuration),
            triggeredAt.Minus(leaseDuration),
            ct
        );
        pending.Should().ContainSingle();
        pending[0].ClaimId.Should().Be(replay.ClaimId);
        pending[0].RuleId.Should().Be(rule.Id);
        pending[0].PeriodStart.Should().Be(period);
        pending[0].ActualSpendGbp.Should().Be(15m);

        var firstLeaseId = Guid.NewGuid();
        var secondLeaseId = Guid.NewGuid();
        var emailClaims = await Task.WhenAll(
            firstRepository.TryAcquireBudgetAlertEmailLeaseAsync(
                replay.ClaimId,
                firstLeaseId,
                triggeredAt,
                triggeredAt.Minus(leaseDuration),
                ct
            ),
            secondRepository.TryAcquireBudgetAlertEmailLeaseAsync(
                replay.ClaimId,
                secondLeaseId,
                triggeredAt,
                triggeredAt.Minus(leaseDuration),
                ct
            )
        );
        emailClaims.Should().ContainSingle(claimed => claimed);
        emailClaims.Should().ContainSingle(claimed => !claimed);

        var activeLeaseId = emailClaims[0] ? firstLeaseId : secondLeaseId;
        var freshLeaseCheckAt = triggeredAt.Plus(Duration.FromMinutes(5));
        (
            await firstRepository.GetDeliverableBudgetAlertEmailsAsync(
                freshLeaseCheckAt.Minus(leaseDuration),
                triggeredAt.Minus(leaseDuration),
                ct
            )
        )
            .Should()
            .BeEmpty("a fresh delivery lease suppresses concurrent/restart attempts");

        await firstRepository.ReleaseBudgetAlertEmailLeaseAsync(replay.ClaimId, activeLeaseId, ct);
        (
            await firstRepository.GetDeliverableBudgetAlertEmailsAsync(
                freshLeaseCheckAt.Minus(leaseDuration),
                triggeredAt.Minus(leaseDuration),
                ct
            )
        )
            .Should()
            .ContainSingle("a definitive failure releases its lease for immediate retry");

        var retryLeaseId = Guid.NewGuid();
        (
            await firstRepository.TryAcquireBudgetAlertEmailLeaseAsync(
                replay.ClaimId,
                retryLeaseId,
                freshLeaseCheckAt,
                freshLeaseCheckAt.Minus(leaseDuration),
                ct
            )
        )
            .Should()
            .BeTrue();

        var staleLeaseCheckAt = freshLeaseCheckAt.Plus(Duration.FromMinutes(16));
        var stalePending = await firstRepository.GetDeliverableBudgetAlertEmailsAsync(
            staleLeaseCheckAt.Minus(leaseDuration),
            triggeredAt.Minus(leaseDuration),
            ct
        );
        stalePending.Should().ContainSingle("an abandoned delivery lease must recover");

        var recoveryLeaseId = Guid.NewGuid();
        (
            await firstRepository.TryAcquireBudgetAlertEmailLeaseAsync(
                replay.ClaimId,
                recoveryLeaseId,
                staleLeaseCheckAt,
                staleLeaseCheckAt.Minus(leaseDuration),
                ct
            )
        )
            .Should()
            .BeTrue();
        await firstRepository.MarkBudgetAlertEmailSentAsync(replay.ClaimId, recoveryLeaseId, staleLeaseCheckAt, ct);
        (
            await firstRepository.GetDeliverableBudgetAlertEmailsAsync(
                staleLeaseCheckAt.Plus(Duration.FromHours(1)),
                triggeredAt.Minus(leaseDuration),
                ct
            )
        )
            .Should()
            .BeEmpty("EmailSentAt is terminal and reader-filtered");
    }

    [Fact]
    public async Task Budget_alert_slack_sent_fence_starts_false_and_is_set_by_marking()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = new BudgetRule { Period = BillingPeriod.Daily, ThresholdGbp = 10m };
        _ctx.BudgetRules.Add(rule);
        await _ctx.SaveChangesAsync(ct);

        var period = new LocalDate(2026, 8, 1);
        var triggeredAt = Instant.FromUtc(2026, 8, 2, 0, 5);
        var claimResult = await _repo.GetOrCreateBudgetAlertAsync(
            rule.Id,
            period,
            period,
            10m,
            15m,
            AlertInsight(period, triggeredAt),
            triggeredAt,
            ct
        );

        (await _repo.GetBudgetAlertSlackSentAsync(claimResult.ClaimId, ct)).Should().BeFalse();

        var sentAt = triggeredAt.Plus(Duration.FromMinutes(1));
        await _repo.MarkBudgetAlertSlackSentAsync(claimResult.ClaimId, sentAt, ct);

        (await _repo.GetBudgetAlertSlackSentAsync(claimResult.ClaimId, ct)).Should().BeTrue();
        (await _ctx.BudgetAlertClaims.AsNoTracking().SingleAsync(c => c.Id == claimResult.ClaimId, ct))
            .SlackSentAt.Should()
            .Be(sentAt);
    }

    [Fact]
    public async Task Budget_alert_delivery_index_excludes_sent_history_and_covers_lease_filtering()
    {
        var definition = await _ctx
            .Database.SqlQueryRaw<string>(
                """
                SELECT indexdef AS "Value"
                FROM pg_indexes
                WHERE schemaname = 'public'
                  AND indexname = 'IX_BudgetAlertClaims_Deliverable'
                """
            )
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

        definition.Should().NotBeNull();
        definition.Should().Contain("(\"CreatedAt\", \"Id\")");
        definition.Should().Contain("INCLUDE (\"EmailLeaseAcquiredAt\")");
        definition.Should().Contain("WHERE (\"EmailSentAt\" IS NULL)");
    }

    [Fact]
    public async Task Deliverable_budget_alert_emails_are_deterministically_bounded_and_exclude_terminal_claims()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseTime = Instant.FromUtc(2026, 1, 1, 0, 0);
        var rule = new BudgetRule
        {
            Period = BillingPeriod.Daily,
            ThresholdGbp = 10m,
            EvaluationStartsOn = new LocalDate(2026, 1, 1),
        };
        _ctx.BudgetRules.Add(rule);
        var deliverable = Enumerable
            .Range(0, 52)
            .Select(index =>
            {
                var period = new LocalDate(2026, 1, 1).PlusDays(index);
                var insight = AlertInsight(period, baseTime.Plus(Duration.FromMinutes(index)));
                _ctx.Insights.Add(insight);
                return new BudgetAlertClaim
                {
                    Id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"),
                    BudgetRuleId = rule.Id,
                    PeriodStart = period,
                    PeriodEnd = period,
                    InsightId = insight.Id,
                    ThresholdGbp = 10m,
                    ActualSpendGbp = 15m,
                    CreatedAt = index < 2 ? baseTime : baseTime.Plus(Duration.FromMinutes(index - 1)),
                };
            })
            .ToList();
        _ctx.BudgetAlertClaims.AddRange(deliverable);

        var sentInsight = AlertInsight(new LocalDate(2026, 3, 1), baseTime.Minus(Duration.FromHours(2)));
        var leasedInsight = AlertInsight(new LocalDate(2026, 3, 2), baseTime.Minus(Duration.FromHours(1)));
        _ctx.Insights.AddRange(sentInsight, leasedInsight);
        _ctx.BudgetAlertClaims.AddRange(
            new BudgetAlertClaim
            {
                BudgetRuleId = rule.Id,
                PeriodStart = sentInsight.PeriodStart,
                PeriodEnd = sentInsight.PeriodEnd,
                InsightId = sentInsight.Id,
                ThresholdGbp = 10m,
                ActualSpendGbp = 15m,
                CreatedAt = sentInsight.GeneratedAt,
                EmailSentAt = baseTime,
            },
            new BudgetAlertClaim
            {
                BudgetRuleId = rule.Id,
                PeriodStart = leasedInsight.PeriodStart,
                PeriodEnd = leasedInsight.PeriodEnd,
                InsightId = leasedInsight.Id,
                ThresholdGbp = 10m,
                ActualSpendGbp = 15m,
                CreatedAt = leasedInsight.GeneratedAt,
                EmailLeaseId = Guid.NewGuid(),
                EmailLeaseAcquiredAt = baseTime.Plus(Duration.FromDays(2)),
            }
        );
        await _ctx.SaveChangesAsync(ct);

        var result = await _repo.GetDeliverableBudgetAlertEmailsAsync(
            baseTime.Plus(Duration.FromDays(1)),
            baseTime.Minus(Duration.FromDays(3)),
            ct
        );

        result.Select(email => email.ClaimId).Should().Equal(deliverable.Take(50).Select(claim => claim.Id));
    }

    private static Insight AlertInsight(LocalDate period, Instant generatedAt) =>
        new()
        {
            GeneratedAt = generatedAt,
            PeriodStart = period,
            PeriodEnd = period,
            InsightType = InsightType.BudgetAlert,
            Title = "Budget alert",
            Body = "Billed spend exceeded the threshold.",
        };

    private static SpendEntry Spend(Guid vendorId, Guid categoryId, LocalDate occurredOn, decimal amountGbp) =>
        new()
        {
            VendorId = vendorId,
            CategoryId = categoryId,
            OccurredOn = occurredOn,
            Amount = amountGbp,
            AmountGbp = amountGbp,
            Currency = "GBP",
            FxRate = 1m,
            Source = SpendSource.Manual,
            RecordedAt = Instant.FromUtc(2026, 8, 2, 12, 0),
            ObservedAt = Instant.FromUtc(2026, 8, 2, 12, 0),
            CostBasis = CostBasis.Billed,
        };

    private static UsageEvent NewEvent(
        string? changedField = null,
        string sourceId = UsageSourceIds.OpenAiUsageApi,
        string? eventKey = "day:model",
        string model = "gpt-5.4",
        long input = 100,
        decimal? cost = 1m,
        decimal? cacheSavings = 0.25m,
        Instant? ingestedAt = null,
        Instant? observedAt = null
    )
    {
        var evt = new UsageEvent
        {
            Provider = Provider.OpenAI,
            OccurredAt = Instant.FromUtc(2026, 8, 24, 12, 0),
            IngestedAt = ingestedAt ?? Instant.FromUtc(2026, 8, 24, 12, 1),
            Model = model,
            InputTokens = input,
            OutputTokens = 50,
            CacheReadTokens = 20,
            CacheWriteTokens = 20,
            CacheWrite1hTokens = 5,
            ThoughtTokens = 4,
            CostUsd = cost,
            CacheSavingsUsd = cacheSavings,
            Runtime = "api",
            SessionId = "session-1",
            AgentId = "agent-1",
            RawPayload = "{\"request\":\"stable\"}",
            SourceId = sourceId,
            SourceKind = SourceKind.ProviderApi,
            UsageScope = UsageScope.Api,
            CostBasis = CostBasis.ProviderEstimated,
            ObservedAt = observedAt ?? Instant.FromUtc(2026, 8, 24, 12, 2),
            EventKey = eventKey,
        };

        ChangeCanonicalField(evt, changedField);
        return evt;
    }

    private static void ChangeCanonicalField(UsageEvent evt, string? changedField)
    {
        switch (changedField)
        {
            case nameof(UsageEvent.Provider):
                evt.Provider = Provider.Anthropic;
                break;
            case nameof(UsageEvent.OccurredAt):
                evt.OccurredAt = Instant.FromUtc(2026, 8, 25, 12, 0);
                break;
            case nameof(UsageEvent.Model):
                evt.Model = "gpt-5.5";
                break;
            case nameof(UsageEvent.InputTokens):
                evt.InputTokens = 175;
                break;
            case nameof(UsageEvent.OutputTokens):
                evt.OutputTokens = 75;
                break;
            case nameof(UsageEvent.CacheReadTokens):
                evt.CacheReadTokens = 30;
                break;
            case nameof(UsageEvent.CacheWriteTokens):
                evt.CacheWriteTokens = 30;
                break;
            case nameof(UsageEvent.CacheWrite1hTokens):
                evt.CacheWrite1hTokens = 10;
                break;
            case nameof(UsageEvent.ThoughtTokens):
                evt.ThoughtTokens = 6;
                break;
            default:
                ChangeCanonicalEvidence(evt, changedField);
                break;
        }
    }

    private static void ChangeCanonicalEvidence(UsageEvent evt, string? changedField)
    {
        switch (changedField)
        {
            case nameof(UsageEvent.CostUsd):
                evt.CostUsd = 2m;
                break;
            case nameof(UsageEvent.CacheSavingsUsd):
                evt.CacheSavingsUsd = 0.5m;
                break;
            case nameof(UsageEvent.Runtime):
                evt.Runtime = "cloud";
                break;
            case nameof(UsageEvent.SessionId):
                evt.SessionId = "session-2";
                break;
            case nameof(UsageEvent.AgentId):
                evt.AgentId = "agent-2";
                break;
            case nameof(UsageEvent.RawPayload):
                evt.RawPayload = "{\"request\":\"corrected\"}";
                break;
            case nameof(UsageEvent.SourceKind):
                evt.SourceKind = SourceKind.LocalTelemetry;
                break;
            case nameof(UsageEvent.UsageScope):
                evt.UsageScope = UsageScope.Subscription;
                break;
            case nameof(UsageEvent.CostBasis):
                evt.CostBasis = CostBasis.Notional;
                break;
        }
    }
}
