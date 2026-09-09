using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest.Services.Google;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

[Trait("Category", "Integration")]
public sealed class GoogleBillingExportSourcePostgresTests : IAsyncLifetime
{
    private readonly HttpClient _http = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private AiObservatoryDbContext _db = null!;

    public async ValueTask InitializeAsync()
    {
        var database = $"aiobs_google_export_{Guid.NewGuid():N}";
        var baseConnection = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")!;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(
                new NpgsqlConnectionStringBuilder(baseConnection) { Database = database }.ConnectionString,
                x => x.UseNodaTime()
            )
            .Options;
        _db = new AiObservatoryDbContext(options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.Database.EnsureDeletedAsync();
        await _db.DisposeAsync();
        _cache.Dispose();
        _http.Dispose();
    }

    [Fact]
    public async Task IngestAsync_creates_google_cloud_billed_spend_without_usage_or_aggregates()
    {
        var source = Source([Record(net: 8m, currency: "EUR")], 0.85m);

        var result = await source.IngestAsync(
            new LocalDate(2026, 8, 1),
            new LocalDate(2026, 8, 1),
            TestContext.Current.CancellationToken
        );

        result.LatestObservationAt.Should().Be(Instant.FromUtc(2026, 8, 5, 0, 0));
        var observation = await _db.BillingObservations.SingleAsync(TestContext.Current.CancellationToken);
        observation.ProviderKey.Should().Be("google");
        observation.Currency.Should().Be("EUR");
        observation.NetAmount.Should().Be(8m);
        var spend = await _db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        spend.AmountGbp.Should().Be(6.8m);
        (await _db.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await _db.DailyAggregates.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task IngestAsync_replay_correction_and_full_credit_converge_the_same_google_identity()
    {
        var first = Source([Record(net: 8m)], 1m);
        await first.IngestAsync(
            new LocalDate(2026, 8, 1),
            new LocalDate(2026, 8, 1),
            TestContext.Current.CancellationToken
        );
        var replay = Source([Record(net: 8m)], 1m);
        await replay.IngestAsync(
            new LocalDate(2026, 8, 1),
            new LocalDate(2026, 8, 1),
            TestContext.Current.CancellationToken
        );
        (await _db.BillingObservations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await _db.SpendEntries.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        var correction = Source([Record(gross: 10m, credit: -10m, net: 0m)], 1m);
        await correction.IngestAsync(
            new LocalDate(2026, 8, 1),
            new LocalDate(2026, 8, 1),
            TestContext.Current.CancellationToken
        );

        (await _db.BillingObservations.SingleAsync(TestContext.Current.CancellationToken)).NetAmount.Should().Be(0m);
        (await _db.SpendEntries.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task IngestAsync_identity_uses_stable_ids_not_mutable_descriptions_or_money()
    {
        var first = Record();
        var second = first with
        {
            ServiceDescription = "Renamed service",
            SkuDescription = "Renamed SKU",
            GrossAmount = 12m,
            CreditAmount = -4m,
            NetAmount = 8m,
        };
        await Source([first, second], 1m)
            .IngestAsync(new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 1), TestContext.Current.CancellationToken);

        (await _db.BillingObservations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Theory]
    [InlineData("usage_date")]
    [InlineData("billing_period")]
    [InlineData("service_id")]
    [InlineData("sku_id")]
    [InlineData("currency")]
    public async Task IngestAsync_each_stable_dimension_distinguishes_observation_and_spend_identity(string dimension)
    {
        var first = Record();
        var second = dimension switch
        {
            "usage_date" => first with { UsageDate = new LocalDate(2026, 8, 2) },
            "billing_period" => first with { BillingPeriod = "202609" },
            "service_id" => first with { ServiceId = "other-service" },
            "sku_id" => first with { SkuId = "other-sku" },
            "currency" => first with { Currency = "EUR" },
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };

        await Source([first, second], 1m)
            .IngestAsync(new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 2), TestContext.Current.CancellationToken);

        var observationKeys = await _db
            .BillingObservations.Select(observation => observation.ObservationKey)
            .ToListAsync(TestContext.Current.CancellationToken);
        observationKeys.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        var spendKeys = await _db
            .SpendEntries.Select(entry => entry.EntryKey)
            .ToListAsync(TestContext.Current.CancellationToken);
        spendKeys.Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task IngestAsync_uses_stored_watermark_to_correct_an_existing_identity_outside_requested_range()
    {
        var watermark = Instant.FromUtc(2026, 8, 10, 12, 0);
        var original = Record() with
        {
            UsageDate = new LocalDate(2026, 7, 1),
            ObservedAt = Instant.FromUtc(2026, 8, 9, 12, 0),
            RawJson = "{\"version\":1}",
        };
        await Source([original], 1m)
            .IngestAsync(new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);
        _db.SourceSyncStates.Add(
            new SourceSyncState { SourceId = UsageSourceIds.GoogleCloudBillingExport, LatestObservationAt = watermark }
        );
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var client = Substitute.For<IGoogleBillingExportClient>();
        var correction = original with
        {
            GrossAmount = 15m,
            CreditAmount = -3m,
            NetAmount = 12m,
            ObservedAt = watermark.Plus(Duration.FromHours(1)),
            RawJson = "{\"version\":2}",
        };
        client
            .GetBillingRecordsAsync(Arg.Any<Instant>(), Arg.Any<Instant>(), watermark, Arg.Any<CancellationToken>())
            .Returns(new GoogleBillingExportResult([correction], 0));

        await Source(client, 1m)
            .IngestAsync(new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 1), TestContext.Current.CancellationToken);

        await client
            .Received(1)
            .GetBillingRecordsAsync(
                Instant.FromUtc(2026, 8, 1, 0, 0),
                Instant.FromUtc(2026, 8, 2, 0, 0),
                watermark,
                TestContext.Current.CancellationToken
            );
        var observation = await _db.BillingObservations.SingleAsync(TestContext.Current.CancellationToken);
        observation.OccurredOn.Should().Be(new LocalDate(2026, 7, 1));
        observation.NetAmount.Should().Be(12m);
        observation.ObservedAt.Should().Be(Instant.FromUtc(2026, 8, 10, 13, 0));
        var spend = await _db.SpendEntries.SingleAsync(TestContext.Current.CancellationToken);
        spend.Amount.Should().Be(12m);
        spend.AmountGbp.Should().Be(12m);
        spend.ObservedAt.Should().Be(Instant.FromUtc(2026, 8, 10, 13, 0));
        (await _db.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await _db.DailyAggregates.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task IngestAsync_without_a_watermark_uses_the_requested_UTC_from_as_changes_since()
    {
        var client = Substitute.For<IGoogleBillingExportClient>();
        client
            .GetBillingRecordsAsync(
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GoogleBillingExportResult([], 0));

        await Source(client, 1m)
            .IngestAsync(new LocalDate(2026, 8, 3), new LocalDate(2026, 8, 5), TestContext.Current.CancellationToken);

        await client
            .Received(1)
            .GetBillingRecordsAsync(
                Instant.FromUtc(2026, 8, 3, 0, 0),
                Instant.FromUtc(2026, 8, 6, 0, 0),
                Instant.FromUtc(2026, 8, 3, 0, 0),
                TestContext.Current.CancellationToken
            );
    }

    [Fact]
    public async Task IngestAsync_when_the_client_throws_writes_no_provider_data()
    {
        var client = Substitute.For<IGoogleBillingExportClient>();
        client
            .GetBillingRecordsAsync(
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromException<GoogleBillingExportResult>(new InvalidOperationException("query failed")));

        var act = () =>
            Source(client, 1m)
                .IngestAsync(
                    new LocalDate(2026, 8, 1),
                    new LocalDate(2026, 8, 1),
                    TestContext.Current.CancellationToken
                );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("query failed");
        (await _db.BillingObservations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await _db.SpendEntries.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await _db.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await _db.DailyAggregates.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task IngestAsync_when_corrections_fall_outside_the_scan_floor_logs_a_warning()
    {
        // The companion count reports affected keys the pruned line_items scan can never
        // return; without the warning those late corrections would go silently stale.
        var client = Substitute.For<IGoogleBillingExportClient>();
        client
            .GetBillingRecordsAsync(
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GoogleBillingExportResult([Record()], 2));
        var logger = new CapturingLogger();

        await Source(client, 1m, logger)
            .IngestAsync(new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 1), TestContext.Current.CancellationToken);

        logger
            .Warnings.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("2 billing correction key(s)")
            .And.Contain("31-day scan floor");
    }

    [Fact]
    public async Task IngestAsync_when_every_affected_key_is_covered_logs_no_warning()
    {
        var client = Substitute.For<IGoogleBillingExportClient>();
        client
            .GetBillingRecordsAsync(
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GoogleBillingExportResult([Record()], 0));
        var logger = new CapturingLogger();

        await Source(client, 1m, logger)
            .IngestAsync(new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 1), TestContext.Current.CancellationToken);

        logger.Warnings.Should().BeEmpty();
    }

    private GoogleBillingExportSource Source(IReadOnlyList<GoogleBillingRecord> records, decimal fxRate)
    {
        var client = Substitute.For<IGoogleBillingExportClient>();
        client
            .GetBillingRecordsAsync(
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GoogleBillingExportResult(records, 0));
        return Source(client, fxRate);
    }

    private GoogleBillingExportSource Source(
        IGoogleBillingExportClient client,
        decimal fxRate,
        ILogger<GoogleBillingExportSource>? logger = null
    )
    {
        var fx = Substitute.For<FxRateProvider>(_http, _cache, NullLogger<FxRateProvider>.Instance);
        fx.GetGbpRateOnAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns(fxRate);
        return new GoogleBillingExportSource(
            client,
            new SourceSyncStateStore(_db),
            new BillingObservationWriter(_db, fx, new FakeClock(Instant.FromUtc(2026, 8, 5, 1, 0))),
            logger ?? NullLogger<GoogleBillingExportSource>.Instance
        );
    }

    private sealed class CapturingLogger : ILogger<GoogleBillingExportSource>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private static GoogleBillingRecord Record(
        decimal gross = 10m,
        decimal credit = -2m,
        decimal net = 8m,
        string currency = "USD"
    ) =>
        new(
            new LocalDate(2026, 8, 1),
            "202608",
            "6F81",
            "Vertex AI",
            "9A1B",
            "Gemini",
            currency,
            gross,
            credit,
            net,
            Instant.FromUtc(2026, 8, 5, 0, 0),
            "{}"
        );
}
