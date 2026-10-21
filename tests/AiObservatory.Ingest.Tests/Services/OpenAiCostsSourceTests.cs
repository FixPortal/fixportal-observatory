using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest.Services.OpenAi;
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
public sealed class OpenAiCostsSourceTests(ProviderPollingDatabase database)
{
    private static readonly Instant Start = Instant.FromUtc(2026, 8, 1, 0, 0);
    private static readonly Instant End = Instant.FromUtc(2026, 8, 2, 0, 0);

    [Fact]
    public async Task IngestAsync_WritesBilledObservationsAndSpendWithoutMutatingUsageAggregates()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var line = $"input-{suffix}";
        var project = $"project-{suffix}";
        var client = Substitute.For<IOpenAiAdminClient>();
        client
            .GetCostsAsync(Start.InUtc().Date, Start.InUtc().Date, Arg.Any<CancellationToken>())
            .Returns([Cost(12.34m, line, project)]);
        await using var db = CreateDb();
        var aggregatesBefore = await db.DailyAggregates.CountAsync(TestContext.Current.CancellationToken);
        using var resources = new WriterResources(db);
        var sut = new OpenAiCostsSource(
            client,
            resources.Writer,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<OpenAiCostsSource>.Instance
        );

        var result = await sut.IngestAsync(
            Start.InUtc().Date,
            Start.InUtc().Date,
            TestContext.Current.CancellationToken
        );

        var observation = await db
            .BillingObservations.AsNoTracking()
            .SingleAsync(row => row.Sku == line, TestContext.Current.CancellationToken);
        observation
            .Should()
            .BeEquivalentTo(
                new
                {
                    ProviderKey = "openai",
                    SourceId = UsageSourceIds.OpenAiCostsApi,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.Billed,
                    OccurredOn = Start.InUtc().Date,
                    BillingPeriod = "2026-08",
                    Service = "OpenAI API",
                    Sku = line,
                    Currency = "USD",
                    GrossAmount = 12.34m,
                    CreditAmount = 0m,
                    NetAmount = 12.34m,
                    ObservedAt = Instant.FromUtc(2026, 8, 3, 9, 0),
                }
            );
        using (var raw = JsonDocument.Parse(observation.RawPayload))
        {
            raw.RootElement.GetProperty("result").GetProperty("project_id").GetString().Should().Be(project);
            raw.RootElement.GetProperty("result").GetProperty("quantity").GetDecimal().Should().Be(2.5m);
        }
        var spend = await db
            .SpendEntries.AsNoTracking()
            .SingleAsync(
                row => row.SourceId == UsageSourceIds.OpenAiCostsApi && row.Description == line,
                TestContext.Current.CancellationToken
            );
        spend.Amount.Should().Be(12.34m);
        spend.CostBasis.Should().Be(CostBasis.Billed);
        (await db.DailyAggregates.CountAsync(TestContext.Current.CancellationToken)).Should().Be(aggregatesBefore);
        result.LatestObservationAt.Should().Be(End);
    }

    [Fact]
    public async Task IngestAsync_RetainsZeroAndCorrectsOneStableFinancialIdentity()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var correctedLine = $"output-{suffix}";
        var zeroLine = $"zero-{suffix}";
        var project = $"project-{suffix}";
        var client = Substitute.For<IOpenAiAdminClient>();
        client
            .GetCostsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                Costs(1m, correctedLine, zeroLine, project, quantityUnit: null),
                Costs(2m, correctedLine, zeroLine, project),
                Costs(2m, correctedLine, zeroLine, project)
            );
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = new OpenAiCostsSource(
            client,
            resources.Writer,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<OpenAiCostsSource>.Instance
        );

        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);
        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);
        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        (await db.BillingObservations.AsNoTracking().CountAsync(row => row.Sku == correctedLine, ct)).Should().Be(1);
        (await db.BillingObservations.AsNoTracking().SingleAsync(row => row.Sku == correctedLine, ct))
            .NetAmount.Should()
            .Be(2m);
        (
            await db
                .SpendEntries.AsNoTracking()
                .CountAsync(
                    row => row.SourceId == UsageSourceIds.OpenAiCostsApi && row.Description == correctedLine,
                    ct
                )
        )
            .Should()
            .Be(1);
        (
            await db
                .SpendEntries.AsNoTracking()
                .SingleAsync(
                    row => row.SourceId == UsageSourceIds.OpenAiCostsApi && row.Description == correctedLine,
                    ct
                )
        )
            .Amount.Should()
            .Be(2m);
        (await db.BillingObservations.AsNoTracking().CountAsync(row => row.Sku == zeroLine, ct)).Should().Be(1);
        (
            await db
                .SpendEntries.AsNoTracking()
                .CountAsync(row => row.SourceId == UsageSourceIds.OpenAiCostsApi && row.Description == zeroLine, ct)
        )
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task IngestAsync_DistinguishesAbsentProjectIdFromLiteralNull()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var line = $"identity-{suffix}";
        var client = Substitute.For<IOpenAiAdminClient>();
        client
            .GetCostsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([Cost(12m, line, null), Cost(12m, line, "null")]);
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = new OpenAiCostsSource(
            client,
            resources.Writer,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<OpenAiCostsSource>.Instance
        );

        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var observations = await db
            .BillingObservations.AsNoTracking()
            .Where(row => row.SourceId == UsageSourceIds.OpenAiCostsApi && row.Sku == line)
            .ToListAsync(ct);
        observations.Should().HaveCount(2);
        observations.Select(row => row.ObservationKey).Should().OnlyHaveUniqueItems();
        (
            await db
                .SpendEntries.AsNoTracking()
                .CountAsync(row => row.SourceId == UsageSourceIds.OpenAiCostsApi && row.Description == line, ct)
        )
            .Should()
            .Be(2);
    }

    [Fact]
    public async Task IngestAsync_WhenAnyUpstreamPageFails_WritesNothing()
    {
        var client = Substitute.For<IOpenAiAdminClient>();
        client
            .GetCostsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<OpenAiCostRecord>>(new InvalidDataException("page two")));
        await using var db = CreateDb();
        var before = await db.BillingObservations.CountAsync(TestContext.Current.CancellationToken);
        using var resources = new WriterResources(db);
        var sut = new OpenAiCostsSource(
            client,
            resources.Writer,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<OpenAiCostsSource>.Instance
        );

        var act = () => sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        (await db.BillingObservations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(before);
    }

    private AiObservatoryDbContext CreateDb() =>
        new(
            new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(database.ConnectionString, options => options.UseNodaTime())
                .Options
        );

    private static OpenAiCostRecord Cost(
        decimal amount,
        string? lineItem,
        string? projectId,
        string? quantityUnit = "tokens"
    ) =>
        new(
            Start,
            End,
            amount,
            "USD",
            lineItem,
            projectId,
            2.5m,
            quantityUnit,
            JsonSerializer.Serialize(
                new
                {
                    bucket = new { start_time = Start.ToUnixTimeSeconds(), end_time = End.ToUnixTimeSeconds() },
                    result = new
                    {
                        amount = new { value = amount, currency = "usd" },
                        line_item = lineItem,
                        project_id = projectId,
                        quantity = 2.5m,
                        quantity_unit = quantityUnit,
                    },
                }
            )
        );

    private static IReadOnlyList<OpenAiCostRecord> Costs(
        decimal correctedAmount,
        string correctedLine,
        string zeroLine,
        string project,
        string? quantityUnit = "tokens"
    ) => [Cost(correctedAmount, correctedLine, project, quantityUnit), Cost(0m, zeroLine, project, quantityUnit)];

    private sealed class WriterResources : IDisposable
    {
        private readonly HttpClient _http = new();
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());

        public WriterResources(AiObservatoryDbContext db)
        {
            var fx = Substitute.For<FxRateProvider>(_http, _cache, NullLogger<FxRateProvider>.Instance);
            fx.GetGbpRateOnAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns(0.8m);
            Writer = new BillingObservationWriter(db, fx, new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)));
        }

        public BillingObservationWriter Writer { get; }

        public void Dispose()
        {
            _cache.Dispose();
            _http.Dispose();
        }
    }
}
