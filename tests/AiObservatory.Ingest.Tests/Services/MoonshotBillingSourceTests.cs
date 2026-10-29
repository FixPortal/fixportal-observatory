using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest.Services.Moonshot;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

[Collection("ProviderPollingWorker")]
[Trait("Category", "Integration")]
public sealed class MoonshotBillingSourceTests(ProviderPollingDatabase database)
{
    private static readonly LocalDate Day = new(2026, 10, 1);

    [Fact]
    public async Task IngestAsync_WritesBilledObservationsWithTheVoucherSplitAsACredit()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = $"kimi-{suffix}";
        var client = Substitute.For<IMoonshotAdminClient>();
        client
            .GetDailyBillsAsync(Day, Day, Arg.Any<CancellationToken>())
            .Returns([Line(Day, model, "prompt", total: 2.5m, cash: 2m, voucher: 0.5m)]);
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = CreateSut(client, resources);

        var result = await sut.IngestAsync(Day, Day, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var observation = await db
            .BillingObservations.AsNoTracking()
            .SingleAsync(row => row.Sku == $"{model}/prompt", ct);
        observation
            .Should()
            .BeEquivalentTo(
                new
                {
                    ProviderKey = "moonshot",
                    SourceId = UsageSourceIds.MoonshotBillingApi,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.Billed,
                    OccurredOn = Day,
                    BillingPeriod = "2026-10",
                    Service = "Moonshot API",
                    Currency = "USD",
                    GrossAmount = 2.5m,
                    CreditAmount = -0.5m,
                    NetAmount = 2m,
                    ObservedAt = Instant.FromUtc(2026, 10, 3, 9, 0),
                }
            );
        using (var raw = JsonDocument.Parse(observation.RawPayload))
        {
            raw.RootElement.GetProperty("detail").GetProperty("model_id").GetString().Should().Be(model);
        }
        var spend = await db
            .SpendEntries.AsNoTracking()
            .SingleAsync(
                row => row.SourceId == UsageSourceIds.MoonshotBillingApi && row.Description == $"{model}/prompt",
                ct
            );
        spend.Amount.Should().Be(2m);
        spend.CostBasis.Should().Be(CostBasis.Billed);
        // The 2026-10-01 bill ends at 00:00 China time on the 2nd, which is 16:00 UTC on the 1st.
        result.LatestObservationAt.Should().Be(Instant.FromUtc(2026, 10, 1, 16, 0));
    }

    [Fact]
    public async Task IngestAsync_ReportsNoLatestObservationWhenThereAreNoBills()
    {
        var client = Substitute.For<IMoonshotAdminClient>();
        client.GetDailyBillsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = CreateSut(client, resources);

        var result = await sut.IngestAsync(Day, Day, TestContext.Current.CancellationToken);

        result.LatestObservationAt.Should().BeNull();
    }

    [Fact]
    public async Task IngestAsync_RetainsAVoucherOnlyLineWithoutSpendAndCorrectsOneStableIdentity()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var correctedModel = $"corrected-{suffix}";
        var voucherModel = $"voucher-{suffix}";
        var client = Substitute.For<IMoonshotAdminClient>();
        client
            .GetDailyBillsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                Lines(1m, correctedModel, voucherModel),
                Lines(2m, correctedModel, voucherModel),
                Lines(2m, correctedModel, voucherModel)
            );
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = CreateSut(client, resources);

        await sut.IngestAsync(Day, Day, TestContext.Current.CancellationToken);
        await sut.IngestAsync(Day, Day, TestContext.Current.CancellationToken);
        await sut.IngestAsync(Day, Day, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var corrected = await db
            .BillingObservations.AsNoTracking()
            .SingleAsync(row => row.Sku == $"{correctedModel}/completion", ct);
        corrected.NetAmount.Should().Be(2m);
        var correctedSpend = await db
            .SpendEntries.AsNoTracking()
            .SingleAsync(
                row =>
                    row.SourceId == UsageSourceIds.MoonshotBillingApi
                    && row.Description == $"{correctedModel}/completion",
                ct
            );
        correctedSpend.Amount.Should().Be(2m);
        (await db.BillingObservations.AsNoTracking().CountAsync(row => row.Sku == $"{voucherModel}/completion", ct))
            .Should()
            .Be(1);
        (
            await db
                .SpendEntries.AsNoTracking()
                .CountAsync(
                    row =>
                        row.SourceId == UsageSourceIds.MoonshotBillingApi
                        && row.Description == $"{voucherModel}/completion",
                    ct
                )
        )
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task IngestAsync_KeepsTheResidualLineDistinctFromModelLines()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var day = new LocalDate(2026, 9, 15);
        var model = $"model-{suffix}";
        var client = Substitute.For<IMoonshotAdminClient>();
        client
            .GetDailyBillsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                Line(day, model, "prompt", 1m, 1m, 0m),
                new MoonshotBillRecord(day, null, null, null, null, 0.25m, 0.25m, 0m, "{\"residual\":{}}"),
            ]);
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = CreateSut(client, resources);

        await sut.IngestAsync(day, day, TestContext.Current.CancellationToken);

        var rows = await db
            .BillingObservations.AsNoTracking()
            .Where(row => row.SourceId == UsageSourceIds.MoonshotBillingApi && row.OccurredOn == day)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Select(row => row.Sku).Should().Contain([$"{model}/prompt", MoonshotBillRecord.ResidualSku]);
        rows.Select(row => row.ObservationKey).Should().OnlyHaveUniqueItems();
    }

    // The writer refuses a net amount that rounds to zero GBP at the ledger's 4-decimal precision (the test FX rate
    // is 0.8, so 0.00004 USD is 0.000032 GBP). Real Moonshot lines reach that size: a few hundred cache-hit tokens
    // bill about 4 milli-cent. Left alone, one such line aborts the whole ingest on every poll.
    [Fact]
    public async Task IngestAsync_FoldsLinesTooSmallForTheLedgerIntoOneLine_AndKeepsTheRest()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var day = new LocalDate(2026, 9, 16);
        var model = $"model-{suffix}";
        var client = Substitute.For<IMoonshotAdminClient>();
        client
            .GetDailyBillsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                Line(day, model, "completion", 1m, 1m, 0m),
                Line(day, model, "auto-caching", 0.00004m, 0.00004m, 0m),
                Line(day, model, "cache-write-5m", 0.00004m, 0.00004m, 0m),
                Line(day, model, "cache-write-1h", 0.00004m, 0.00004m, 0m),
            ]);
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = CreateSut(client, resources);

        var result = await sut.IngestAsync(day, day, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var rows = await db
            .BillingObservations.AsNoTracking()
            .Where(row => row.SourceId == UsageSourceIds.MoonshotBillingApi && row.OccurredOn == day)
            .ToListAsync(ct);
        rows.Select(row => row.Sku).Should().BeEquivalentTo($"{model}/completion", MoonshotBillingSource.SubLedgerSku);
        var folded = rows.Single(row => row.Sku == MoonshotBillingSource.SubLedgerSku);
        folded.GrossAmount.Should().Be(0.00012m);
        folded.NetAmount.Should().Be(0.00012m);
        folded.CreditAmount.Should().Be(0m);
        using (var raw = JsonDocument.Parse(folded.RawPayload))
        {
            raw.RootElement.GetProperty("folded").GetArrayLength().Should().Be(3);
        }
        result.LatestObservationAt.Should().Be(Instant.FromUtc(2026, 9, 16, 16, 0));
    }

    [Fact]
    public async Task IngestAsync_DropsTooSmallLinesWhoseSumStaysBelowTheLedgerPrecision_WithoutFailing()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var day = new LocalDate(2026, 9, 17);
        var model = $"model-{suffix}";
        var client = Substitute.For<IMoonshotAdminClient>();
        client
            .GetDailyBillsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                Line(day, model, "auto-caching", 0.00004m, 0.00004m, 0m),
                Line(day, model, "completion", 1m, 1m, 0m),
            ]);
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = CreateSut(client, resources);

        var act = () => sut.IngestAsync(day, day, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        var ct = TestContext.Current.CancellationToken;
        (await db.BillingObservations.AsNoTracking().CountAsync(row => row.Sku == $"{model}/auto-caching", ct))
            .Should()
            .Be(0);
        (await db.BillingObservations.AsNoTracking().CountAsync(row => row.Sku == $"{model}/completion", ct))
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task IngestAsync_IsIdempotentForTheFoldedLine()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var day = new LocalDate(2026, 9, 19);
        var model = $"model-{suffix}";
        var client = Substitute.For<IMoonshotAdminClient>();
        client
            .GetDailyBillsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                Line(day, model, "auto-caching", 0.00004m, 0.00004m, 0m),
                Line(day, model, "cache-write-5m", 0.00004m, 0.00004m, 0m),
                Line(day, model, "cache-write-1h", 0.00004m, 0.00004m, 0m),
            ]);
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = CreateSut(client, resources);

        await sut.IngestAsync(day, day, TestContext.Current.CancellationToken);
        await sut.IngestAsync(day, day, TestContext.Current.CancellationToken);
        await sut.IngestAsync(day, day, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var folded = await db
            .BillingObservations.AsNoTracking()
            .Where(row => row.SourceId == UsageSourceIds.MoonshotBillingApi && row.OccurredOn == day)
            .ToListAsync(ct);
        folded.Should().ContainSingle().Which.Sku.Should().Be(MoonshotBillingSource.SubLedgerSku);
        (
            await db
                .SpendEntries.AsNoTracking()
                .CountAsync(
                    row =>
                        row.SourceId == UsageSourceIds.MoonshotBillingApi
                        && row.Description == MoonshotBillingSource.SubLedgerSku
                        && row.OccurredOn == day,
                    ct
                )
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task IngestAsync_KeepsALineWhoseCashIsExactlyTheFloorOnItsOwn()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var day = new LocalDate(2026, 9, 20);
        var model = $"model-{suffix}";
        var client = Substitute.For<IMoonshotAdminClient>();
        client
            .GetDailyBillsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([Line(day, model, "auto-caching", 0.0001m, 0.0001m, 0m)]);
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = CreateSut(client, resources);

        await sut.IngestAsync(day, day, TestContext.Current.CancellationToken);

        var rows = await db
            .BillingObservations.AsNoTracking()
            .Where(row => row.SourceId == UsageSourceIds.MoonshotBillingApi && row.OccurredOn == day)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().ContainSingle().Which.Sku.Should().Be($"{model}/auto-caching");
    }

    // The 0.0001 USD floor assumes a USD to GBP rate above 0.5. At a lower rate a line just over the floor still
    // rounds to zero GBP and the writer refuses it; the source must skip that one observation, not abort the poll.
    [Fact]
    public async Task IngestAsync_WhenTheRateMakesALineRoundToZero_SkipsItAndKeepsTheRest()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var day = new LocalDate(2026, 9, 21);
        var model = $"model-{suffix}";
        var client = Substitute.For<IMoonshotAdminClient>();
        client
            .GetDailyBillsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                Line(day, model, "auto-caching", 0.0002m, 0.0002m, 0m),
                Line(day, model, "completion", 1m, 1m, 0m),
            ]);
        await using var db = CreateDb();
        using var resources = new WriterResources(db, gbpRate: 0.1m);
        var sut = CreateSut(client, resources);

        var act = () => sut.IngestAsync(day, day, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        var rows = await db
            .BillingObservations.AsNoTracking()
            .Where(row => row.SourceId == UsageSourceIds.MoonshotBillingApi && row.OccurredOn == day)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().ContainSingle().Which.Sku.Should().Be($"{model}/completion");
    }

    [Fact]
    public async Task IngestAsync_KeepsAVoucherCoveredLineWhateverItsSize()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var day = new LocalDate(2026, 9, 18);
        var model = $"model-{suffix}";
        var client = Substitute.For<IMoonshotAdminClient>();
        client
            .GetDailyBillsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([Line(day, model, "auto-caching", 0.00004m, 0m, 0.00004m)]);
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = CreateSut(client, resources);

        await sut.IngestAsync(day, day, TestContext.Current.CancellationToken);

        var row = await db
            .BillingObservations.AsNoTracking()
            .SingleAsync(row => row.Sku == $"{model}/auto-caching", TestContext.Current.CancellationToken);
        row.NetAmount.Should().Be(0m);
        row.GrossAmount.Should().Be(0.00004m);
        row.CreditAmount.Should().Be(-0.00004m);
    }

    [Fact]
    public async Task IngestAsync_WhenTheClientFails_WritesNothing()
    {
        var client = Substitute.For<IMoonshotAdminClient>();
        client
            .GetDailyBillsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<MoonshotBillRecord>>(new InvalidDataException("page two")));
        await using var db = CreateDb();
        var before = await db.BillingObservations.CountAsync(TestContext.Current.CancellationToken);
        using var resources = new WriterResources(db);
        var sut = CreateSut(client, resources);

        var act = () => sut.IngestAsync(Day, Day, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        (await db.BillingObservations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(before);
    }

    private static MoonshotBillingSource CreateSut(IMoonshotAdminClient client, WriterResources resources) =>
        new(
            client,
            resources.Writer,
            new FakeClock(Instant.FromUtc(2026, 10, 3, 9, 0)),
            NullLogger<MoonshotBillingSource>.Instance
        );

    private AiObservatoryDbContext CreateDb() =>
        new(
            new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(database.ConnectionString, options => options.UseNodaTime())
                .Options
        );

    private static MoonshotBillRecord Line(
        LocalDate date,
        string model,
        string tokenType,
        decimal total,
        decimal cash,
        decimal voucher
    ) =>
        new(
            date,
            model,
            tokenType,
            "key-1",
            1_000,
            total,
            cash,
            voucher,
            JsonSerializer.Serialize(
                new
                {
                    date = date.ToString("yyyy-MM-dd", null),
                    detail = new
                    {
                        model_id = model,
                        token_type = tokenType,
                        tokens = 1_000,
                        key = "key-1",
                    },
                }
            )
        );

    private static IReadOnlyList<MoonshotBillRecord> Lines(
        decimal correctedCash,
        string correctedModel,
        string voucherModel
    ) =>
        [
            Line(Day, correctedModel, "completion", correctedCash, correctedCash, 0m),
            Line(Day, voucherModel, "completion", 3m, 0m, 3m),
        ];

    private sealed class WriterResources : IDisposable
    {
        private readonly HttpClient _http = new();
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());

        public WriterResources(AiObservatoryDbContext db, decimal gbpRate = 0.8m)
        {
            var fx = Substitute.For<FxRateProvider>(_http, _cache, NullLogger<FxRateProvider>.Instance);
            fx.GetGbpRateOnAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
                .Returns(gbpRate);
            Writer = new BillingObservationWriter(db, fx, new FakeClock(Instant.FromUtc(2026, 10, 3, 9, 0)));
        }

        public BillingObservationWriter Writer { get; }

        public void Dispose()
        {
            _cache.Dispose();
            _http.Dispose();
        }
    }
}
