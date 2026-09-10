using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AiObservatory.Data.Tests.Spend;

[Trait("Category", "Integration")]
public sealed class BillingObservationWriterTests : IAsyncLifetime
{
    private static readonly Instant ObservedAt = Instant.FromUtc(2026, 8, 24, 12, 0);
    private static readonly Instant RecordedAt = Instant.FromUtc(2026, 8, 25, 8, 30);

    private readonly HttpClient _http = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private string _connectionString = null!;
    private DbContextOptions<AiObservatoryDbContext> _options = null!;
    private AiObservatoryDbContext _db = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConnection =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_billing_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connectionString, options => options.UseNodaTime())
            .Options;
        _db = new AiObservatoryDbContext(_options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _db.Database.EnsureDeletedAsync();
        }
        finally
        {
            await _db.DisposeAsync();
            _cache.Dispose();
            _http.Dispose();
        }
    }

    [Fact]
    public async Task CreateRetainsExactEvidenceAndBilledProvenanceThenExactReplayIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = Writer(_db, 0.75m);
        var observation = Observation(raw: """{"line_item":"input","project":"proj-a"}""");

        (await writer.RecordAsync(observation, "openai", "api-usage", ct)).Should().Be(BillingWriteDisposition.Created);
        var replay = Observation(raw: observation.RawPayload, observedAt: ObservedAt.Plus(Duration.FromHours(1)));
        (await writer.RecordAsync(replay, "openai", "api-usage", ct))
            .Should()
            .Be(BillingWriteDisposition.Unchanged, "a later poll of unchanged provider facts is not a correction");

        var stored = await _db.BillingObservations.AsNoTracking().SingleAsync(ct);
        stored.Should().BeEquivalentTo(observation, options => options.Excluding(row => row.RawPayload));
        JsonEquals(stored.RawPayload, observation.RawPayload).Should().BeTrue();
        var spend = await _db.SpendEntries.AsNoTracking().SingleAsync(ct);
        spend.Amount.Should().Be(8m);
        spend.AmountGbp.Should().Be(6m);
        spend.FxRate.Should().Be(0.75m);
        spend.Source.Should().Be(SpendSource.Api);
        spend.EntryKey.Should().Be("billing:openai-costs-api:2026-08:sku-a");
        spend.SourceId.Should().Be(UsageSourceIds.OpenAiCostsApi);
        spend.SourceKind.Should().Be(SourceKind.ProviderApi);
        spend.UsageScope.Should().Be(UsageScope.Api);
        spend.CostBasis.Should().Be(CostBasis.Billed);
        JsonEquals(spend.RawPayload, observation.RawPayload).Should().BeTrue();
        spend.ObservedAt.Should().Be(ObservedAt);
        spend.RecordedAt.Should().Be(RecordedAt);
    }

    [Fact]
    public async Task ExactReplayRetainsFrozenFxAndRecordedAtWhenCurrentFxDiffers()
    {
        var ct = TestContext.Current.CancellationToken;
        await Writer(_db, 0.75m).RecordAsync(Observation(), "openai", "api-usage", ct);
        var replay = Observation(observedAt: ObservedAt.Plus(Duration.FromHours(1)));

        var disposition = await Writer(_db, 0.79m, RecordedAt.Plus(Duration.FromDays(1)))
            .RecordAsync(replay, "openai", "api-usage", ct);

        disposition.Should().Be(BillingWriteDisposition.Unchanged);
        var spend = await _db.SpendEntries.AsNoTracking().SingleAsync(ct);
        spend.FxRate.Should().Be(0.75m);
        spend.AmountGbp.Should().Be(6m);
        spend.RecordedAt.Should().Be(RecordedAt);
    }

    [Fact]
    public async Task CorrectionUpdatesTheFactsAndFrozenFxButPreservesManualCategorisation()
    {
        var ct = TestContext.Current.CancellationToken;
        await Writer(_db, 0.75m).RecordAsync(Observation(), "openai", "api-usage", ct);
        var spend = await _db.SpendEntries.SingleAsync(ct);
        var manualVendor = await _db.SpendVendors.SingleAsync(vendor => vendor.Key == "google", ct);
        var manualCategory = await _db.SpendCategories.SingleAsync(category => category.Key == "cloud", ct);
        spend.VendorId = manualVendor.Id;
        spend.CategoryId = manualCategory.Id;
        await _db.SaveChangesAsync(ct);
        var corrected = Observation(
            gross: 12m,
            credit: -3m,
            net: 9m,
            raw: """{"line_item":"corrected"}""",
            observedAt: ObservedAt.Plus(Duration.FromDays(1))
        );

        (await Writer(_db, 0.8m).RecordAsync(corrected, "openai", "api-usage", ct))
            .Should()
            .Be(BillingWriteDisposition.Corrected);

        var stored = await _db.BillingObservations.AsNoTracking().SingleAsync(ct);
        stored.GrossAmount.Should().Be(12m);
        stored.CreditAmount.Should().Be(-3m);
        stored.NetAmount.Should().Be(9m);
        JsonEquals(stored.RawPayload, corrected.RawPayload).Should().BeTrue();
        stored.ObservedAt.Should().Be(corrected.ObservedAt);
        var updated = await _db.SpendEntries.AsNoTracking().SingleAsync(ct);
        updated.Amount.Should().Be(9m);
        updated.AmountGbp.Should().Be(7.2m);
        updated.FxRate.Should().Be(0.8m);
        updated.VendorId.Should().Be(manualVendor.Id);
        updated.CategoryId.Should().Be(manualCategory.Id);
    }

    [Fact]
    public async Task ZeroNetIsRetainedWithoutSpendAndCorrectionToZeroRemovesOnlyTheKeyedApiRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await Writer(_db, 1m).RecordAsync(Observation(), "openai", "api-usage", ct);
        var existing = await _db.SpendEntries.AsNoTracking().SingleAsync(ct);
        _db.SpendEntries.Add(
            new SpendEntry
            {
                OccurredOn = existing.OccurredOn,
                VendorId = existing.VendorId,
                CategoryId = existing.CategoryId,
                Amount = 99m,
                Currency = "GBP",
                AmountGbp = 99m,
                FxRate = 1m,
                Source = SpendSource.Manual,
                EntryKey = existing.EntryKey,
                RecordedAt = RecordedAt,
            }
        );
        await _db.SaveChangesAsync(ct);
        var zero = Observation(gross: 10m, credit: -10m, net: 0m, raw: """{"fully_credited":true}""");

        (await WriterThrowingFx(_db).RecordAsync(zero, "openai", "api-usage", ct))
            .Should()
            .Be(BillingWriteDisposition.Corrected, "a correction to zero removes the prior provider spend");

        var stored = await _db.BillingObservations.AsNoTracking().SingleAsync(ct);
        stored.NetAmount.Should().Be(0m);
        JsonEquals(stored.RawPayload, zero.RawPayload).Should().BeTrue();
        var remaining = await _db.SpendEntries.AsNoTracking().SingleAsync(ct);
        remaining.Source.Should().Be(SpendSource.Manual);
        remaining.Amount.Should().Be(99m);

        var secondZero = Observation(
            key: "2026-08:sku-free",
            gross: 2m,
            credit: -2m,
            net: 0m,
            raw: """{"included":true}"""
        );
        (await WriterThrowingFx(_db).RecordAsync(secondZero, "missing-vendor", "missing-category", ct))
            .Should()
            .Be(BillingWriteDisposition.Created, "zero-net evidence needs neither FX nor a ledger catalog row");
        (await _db.BillingObservations.AsNoTracking().CountAsync(ct)).Should().Be(2);
        (await _db.SpendEntries.AsNoTracking().CountAsync(ct)).Should().Be(1);
    }

    [Fact]
    public async Task RefundKeepsItsSignThroughHistoricalFx()
    {
        var ct = TestContext.Current.CancellationToken;
        var refund = Observation(gross: 0m, credit: -4m, net: -4m, raw: """{"kind":"refund"}""");

        await Writer(_db, 0.5m).RecordAsync(refund, "openai", "api-usage", ct);

        var spend = await _db.SpendEntries.AsNoTracking().SingleAsync(ct);
        spend.Amount.Should().Be(-4m);
        spend.AmountGbp.Should().Be(-2m);
        spend.FxRate.Should().Be(0.5m);
    }

    [Fact]
    public async Task AmountThatRoundsToZeroGbpLeavesNoPartialObservation()
    {
        var ct = TestContext.Current.CancellationToken;
        var crumb = Observation(gross: 0.00001m, credit: 0m, net: 0.00001m);

        var act = () => Writer(_db, 0.8m).RecordAsync(crumb, "openai", "api-usage", ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rounds to zero GBP*");
        (await _db.BillingObservations.AsNoTracking().CountAsync(ct)).Should().Be(0);
        (await _db.SpendEntries.AsNoTracking().CountAsync(ct)).Should().Be(0);
    }

    [Theory]
    [InlineData("vendor")]
    [InlineData("category")]
    [InlineData("fx")]
    public async Task MissingMoneyDependencyLeavesNeitherHalfOfTheWrite(string missing)
    {
        var ct = TestContext.Current.CancellationToken;
        var observation = Observation(key: $"missing-{missing}");
        var writer = missing == "fx" ? WriterThrowingFx(_db) : Writer(_db, 1m);
        var vendor = missing == "vendor" ? "absent-vendor" : "openai";
        var category = missing == "category" ? "absent-category" : "api-usage";

        var act = () => writer.RecordAsync(observation, vendor, category, ct);

        await act.Should().ThrowAsync<Exception>();
        _db.ChangeTracker.Entries().Should().BeEmpty();
        (
            await _db
                .BillingObservations.AsNoTracking()
                .AnyAsync(row => row.ObservationKey == observation.ObservationKey, ct)
        )
            .Should()
            .BeFalse();
        (await _db.SpendEntries.AsNoTracking().AnyAsync(row => row.SourceId == observation.SourceId, ct))
            .Should()
            .BeFalse();
    }

    [Theory]
    [InlineData("provider-case")]
    [InlineData("source-kind")]
    [InlineData("cost-basis")]
    [InlineData("currency")]
    [InlineData("arithmetic")]
    [InlineData("json")]
    [InlineData("blank-key")]
    [InlineData("long-source")]
    public async Task RejectsInvalidTrustBoundaryDataWithoutWriting(string invalid)
    {
        var ct = TestContext.Current.CancellationToken;
        var observation = Observation();
        switch (invalid)
        {
            case "provider-case":
                observation.ProviderKey = "OpenAI";
                break;
            case "source-kind":
                observation.SourceKind = SourceKind.LocalTelemetry;
                break;
            case "cost-basis":
                observation.CostBasis = CostBasis.ListPriceEstimate;
                break;
            case "currency":
                observation.Currency = "usd";
                break;
            case "arithmetic":
                observation.NetAmount = 7m;
                break;
            case "json":
                observation.RawPayload = "not-json";
                break;
            case "blank-key":
                observation.ObservationKey = " ";
                break;
            case "long-source":
                observation.SourceId = new string('s', 201);
                break;
        }

        var act = () => Writer(_db, 1m).RecordAsync(observation, "openai", "api-usage", ct);

        await act.Should().ThrowAsync<ArgumentException>();
        (await _db.BillingObservations.AsNoTracking().CountAsync(ct)).Should().Be(0);
        (await _db.SpendEntries.AsNoTracking().CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentDuplicateIdentityCreatesOneObservationAndOneSpendRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var firstDb = new AiObservatoryDbContext(_options);
        await using var secondDb = new AiObservatoryDbContext(_options);

        // Force the overlap the assertions depend on: a SHARE gate on BillingObservations lets
        // both writers begin and pass their existence reads, then blocks whichever holds the
        // identity advisory lock at its INSERT — so both are provably inside RecordAsync before
        // either can finish. Bare Task.WhenAll lets one writer commit before the other starts,
        // which passes whether or not the advisory lock serialises them.
        await using var gate = new NpgsqlConnection(_connectionString);
        await gate.OpenAsync(ct);
        await using var gateTransaction = await gate.BeginTransactionAsync(ct);
        await using (var gateCommand = gate.CreateCommand())
        {
            gateCommand.Transaction = gateTransaction;
            gateCommand.CommandText = """LOCK TABLE "BillingObservations" IN SHARE MODE""";
            await gateCommand.ExecuteNonQueryAsync(ct);
        }

        var first = Writer(firstDb, 1m).RecordAsync(Observation(), "openai", "api-usage", ct);
        var second = Writer(secondDb, 1m).RecordAsync(Observation(), "openai", "api-usage", ct);
        try
        {
            // The gate's victim is mid-INSERT; the sibling is mid-advisory-lock — both overlapped.
            await WaitForBlockedStatementAsync("INSERT INTO \"BillingObservations\"", ct);
            await WaitForBlockedStatementAsync("pg_advisory_xact_lock", ct);
        }
        finally
        {
            // Release the gate BEFORE the writers are awaited, on every path: they are blocked
            // on it, so a timed-out wait that skipped the release stranded them — their faults
            // went unobserved and DisposeAsync deleted the database under live connections,
            // reporting a single timeout as a teardown failure instead.
            await gateTransaction.RollbackAsync(CancellationToken.None);
            try
            {
                await Task.WhenAll(first, second);
            }
            catch
            {
                // Observation only on the failure path; the happy-path await below surfaces a
                // genuine writer failure.
            }
        }

        var results = await Task.WhenAll(first, second);

        results.Should().ContainSingle(result => result == BillingWriteDisposition.Created);
        results.Should().ContainSingle(result => result == BillingWriteDisposition.Unchanged);
        (await _db.BillingObservations.AsNoTracking().CountAsync(ct)).Should().Be(1);
        (await _db.SpendEntries.AsNoTracking().CountAsync(ct)).Should().Be(1);
    }

    /// <summary>
    /// Waits until another session is in a PostgreSQL heavyweight-lock wait while executing a
    /// statement whose text contains <paramref name="statementFragment"/> — the deterministic
    /// replacement for "delay N ms and assert the task had not finished". The fragment must be
    /// a literal (no LIKE wildcards, no apostrophes).
    /// </summary>
    private async Task WaitForBlockedStatementAsync(string statementFragment, CancellationToken ct)
    {
        // 600 x 50 ms = 30 s, the same budget the other contention helpers in this suite use.
        for (var attempt = 0; attempt < 600; attempt++)
        {
            var blocked = await _db
                .Database.SqlQuery<bool>(
                    $"""
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_stat_activity
                        WHERE datname = current_database()
                            AND pid <> pg_backend_pid()
                            AND wait_event_type = 'Lock'
                            AND query LIKE '%' || {statementFragment} || '%'
                    ) AS "Value"
                    """
                )
                .SingleAsync(ct);
            if (blocked)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        throw new TimeoutException($"No session reached the expected PostgreSQL lock wait for '{statementFragment}'.");
    }

    [Fact]
    public async Task OverlongReadableKeysUseCollisionSafeHashesWithoutTruncation()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new string('s', 200);
        var firstKey = new string('k', 199) + "a";
        var secondKey = new string('k', 199) + "b";
        var writer = Writer(_db, 1m);

        await writer.RecordAsync(Observation(source: source, key: firstKey), "openai", "api-usage", ct);
        await writer.RecordAsync(Observation(source: source, key: secondKey), "openai", "api-usage", ct);

        var keys = await _db.SpendEntries.AsNoTracking().Select(entry => entry.EntryKey!).ToListAsync(ct);
        keys.Should().OnlyContain(key => key.StartsWith("billing:", StringComparison.Ordinal) && key.Length == 72);
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task CallerCancellationPropagatesBeforeFxOrPersistence()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancellationToken = cancellation.Token;

        var act = () => WriterThrowingFx(_db).RecordAsync(Observation(), "openai", "api-usage", cancellationToken);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cancellationToken);
        (await _db.BillingObservations.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task FirstWriteAdoptsAndRekeysTheMigratedLegacyGitHubApiRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var observation = Observation(
            provider: "github",
            source: UsageSourceIds.GitHubBillingApi,
            key: "github:2026-08:actions:linux",
            gross: 8m,
            credit: 0m,
            net: 8m,
            service: "actions",
            sku: "linux"
        );
        var vendor = await _db.SpendVendors.SingleAsync(candidate => candidate.Key == "github-actions", ct);
        var category = await _db.SpendCategories.SingleAsync(candidate => candidate.Key == "ci", ct);
        _db.SpendEntries.Add(
            new SpendEntry
            {
                OccurredOn = observation.OccurredOn,
                VendorId = vendor.Id,
                CategoryId = category.Id,
                Amount = 8m,
                Currency = "USD",
                AmountGbp = 6m,
                FxRate = 0.75m,
                Description = "linux",
                Source = SpendSource.Api,
                EntryKey = observation.ObservationKey,
                RecordedAt = RecordedAt.Minus(Duration.FromDays(1)),
                SourceId = UsageSourceIds.LegacySpend,
                SourceKind = SourceKind.Legacy,
                UsageScope = UsageScope.Unknown,
                CostBasis = CostBasis.Billed,
                ObservedAt = ObservedAt.Minus(Duration.FromDays(1)),
            }
        );
        await _db.SaveChangesAsync(ct);

        await Writer(_db, 0.75m).RecordAsync(observation, "github-actions", "ci", ct);

        var spend = await _db.SpendEntries.AsNoTracking().SingleAsync(ct);
        spend.EntryKey.Should().Be("billing:github-billing-api:github:2026-08:actions:linux");
        spend.SourceId.Should().Be(UsageSourceIds.GitHubBillingApi);
        spend.SourceKind.Should().Be(SourceKind.ProviderApi);
        spend.UsageScope.Should().Be(UsageScope.Api);
        (await _db.BillingObservations.AsNoTracking().CountAsync(ct)).Should().Be(1);

        await using var replayDb = new AiObservatoryDbContext(_options);
        var disposition = await Writer(replayDb, 0.75m)
            .RecordAsync(
                Observation(
                    provider: "github",
                    source: UsageSourceIds.GitHubBillingApi,
                    key: "github:2026-08:actions:linux",
                    gross: 8m,
                    credit: 0m,
                    net: 8m,
                    service: "actions",
                    sku: "linux"
                ),
                "github-actions",
                "ci",
                ct
            );

        disposition.Should().Be(BillingWriteDisposition.Unchanged);
        (await replayDb.SpendEntries.AsNoTracking().CountAsync(ct)).Should().Be(1);
    }

    [Fact]
    public async Task GitHubWriteDoesNotAdoptANonGitHubLegacyKeyCollision()
    {
        var ct = TestContext.Current.CancellationToken;
        const string collisionKey = "portal:collision";
        var vendor = await _db.SpendVendors.SingleAsync(candidate => candidate.Key == "github-actions", ct);
        var category = await _db.SpendCategories.SingleAsync(candidate => candidate.Key == "ci", ct);
        _db.SpendEntries.Add(
            new SpendEntry
            {
                OccurredOn = new LocalDate(2026, 8, 1),
                VendorId = vendor.Id,
                CategoryId = category.Id,
                Amount = 8m,
                Currency = "USD",
                AmountGbp = 6m,
                FxRate = 0.75m,
                Description = "unrelated legacy row",
                Source = SpendSource.Api,
                EntryKey = collisionKey,
                RecordedAt = RecordedAt.Minus(Duration.FromDays(1)),
                SourceId = UsageSourceIds.LegacySpend,
                SourceKind = SourceKind.Legacy,
                UsageScope = UsageScope.Unknown,
                CostBasis = CostBasis.Billed,
                ObservedAt = ObservedAt.Minus(Duration.FromDays(1)),
            }
        );
        await _db.SaveChangesAsync(ct);

        await Writer(_db, 0.75m)
            .RecordAsync(
                Observation(
                    provider: "github",
                    source: UsageSourceIds.GitHubBillingApi,
                    key: collisionKey,
                    gross: 8m,
                    credit: 0m,
                    net: 8m,
                    service: "actions",
                    sku: "linux"
                ),
                "github-actions",
                "ci",
                ct
            );

        var spend = await _db.SpendEntries.AsNoTracking().ToListAsync(ct);
        spend.Should().ContainSingle(entry => entry.SourceId == UsageSourceIds.LegacySpend);
        spend.Should().ContainSingle(entry => entry.SourceId == UsageSourceIds.GitHubBillingApi);
    }

    [Fact]
    public async Task PostgreSqlEnforcesJsonAndProviderBillingConstraints()
    {
        var ct = TestContext.Current.CancellationToken;
        var invalid = Observation();
        invalid.RawPayload = "not-json";
        _db.BillingObservations.Add(invalid);

        var invalidJson = () => _db.SaveChangesAsync(ct);

        await invalidJson.Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Clear();
        var invalidBasis = Observation(key: "wrong-basis");
        invalidBasis.CostBasis = CostBasis.Notional;
        _db.BillingObservations.Add(invalidBasis);

        var wrongBasis = () => _db.SaveChangesAsync(ct);

        await wrongBasis.Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Clear();
        var rawType = await _db
            .Database.SqlQueryRaw<string>(
                """
                SELECT data_type AS "Value"
                FROM information_schema.columns
                WHERE table_name = 'BillingObservations' AND column_name = 'RawPayload'
                """
            )
            .SingleAsync(ct);
        rawType.Should().Be("jsonb");
    }

    private BillingObservationWriter Writer(AiObservatoryDbContext db, decimal rate, Instant? recordedAt = null)
    {
        var fx = Substitute.For<FxRateProvider>(_http, _cache, NullLogger<FxRateProvider>.Instance);
        fx.GetGbpRateOnAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns(rate);
        return new BillingObservationWriter(db, fx, new FakeClock(recordedAt ?? RecordedAt));
    }

    private static bool JsonEquals(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }

    private BillingObservationWriter WriterThrowingFx(AiObservatoryDbContext db)
    {
        var fx = Substitute.For<FxRateProvider>(_http, _cache, NullLogger<FxRateProvider>.Instance);
        fx.GetGbpRateOnAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new FxUnavailableException("USD", new LocalDate(2026, 8, 1)));
        return new BillingObservationWriter(db, fx, new FakeClock(RecordedAt));
    }

    private static BillingObservation Observation(
        string provider = "openai",
        string source = UsageSourceIds.OpenAiCostsApi,
        string key = "2026-08:sku-a",
        decimal gross = 10m,
        decimal credit = -2m,
        decimal net = 8m,
        string raw = """{"line_item":"input"}""",
        string service = "responses",
        string sku = "sku-a",
        Instant? observedAt = null
    ) =>
        new()
        {
            ProviderKey = provider,
            SourceId = source,
            SourceKind = SourceKind.ProviderApi,
            UsageScope = UsageScope.Api,
            CostBasis = CostBasis.Billed,
            ObservationKey = key,
            OccurredOn = new LocalDate(2026, 8, 1),
            BillingPeriod = "2026-08",
            Service = service,
            Sku = sku,
            Currency = "USD",
            GrossAmount = gross,
            CreditAmount = credit,
            NetAmount = net,
            RawPayload = raw,
            ObservedAt = observedAt ?? ObservedAt,
        };
}
