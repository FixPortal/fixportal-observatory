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

        public WriterResources(AiObservatoryDbContext db)
        {
            var fx = Substitute.For<FxRateProvider>(_http, _cache, NullLogger<FxRateProvider>.Instance);
            fx.GetGbpRateOnAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns(0.8m);
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
