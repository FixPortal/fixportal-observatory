using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest.Services.Anthropic;
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
public sealed class AnthropicCostsSourceTests(ProviderPollingDatabase database)
{
    private static readonly Instant Start = Instant.FromUtc(2026, 8, 1, 0, 0);
    private static readonly Instant End = Instant.FromUtc(2026, 8, 2, 0, 0);

    [Fact]
    public async Task IngestAsync_ConvertsFractionalCentsAndRetainsPlatformFactsWithoutUsageCost()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var description = $"Code Execution Usage {suffix}";
        var workspace = $"wrkspc-{suffix}";
        var client = Substitute.For<IAnthropicAdminClient>();
        client
            .GetCostsAsync(Start.InUtc().Date, Start.InUtc().Date, Arg.Any<CancellationToken>())
            .Returns([Cost(123.78912m, description, workspace)]);
        await using var db = CreateDb();
        var aggregatesBefore = await db.DailyAggregates.CountAsync(TestContext.Current.CancellationToken);
        using var resources = new WriterResources(db);
        var sut = new AnthropicCostsSource(
            client,
            resources.Writer,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<AnthropicCostsSource>.Instance
        );

        var result = await sut.IngestAsync(
            Start.InUtc().Date,
            Start.InUtc().Date,
            TestContext.Current.CancellationToken
        );

        var observation = await db
            .BillingObservations.AsNoTracking()
            .SingleAsync(row => row.Sku == description, TestContext.Current.CancellationToken);
        observation
            .Should()
            .BeEquivalentTo(
                new
                {
                    ProviderKey = "anthropic",
                    SourceId = UsageSourceIds.AnthropicCostReport,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.Billed,
                    OccurredOn = Start.InUtc().Date,
                    BillingPeriod = "2026-08",
                    Service = "Anthropic API",
                    Sku = description,
                    Currency = "USD",
                    GrossAmount = 1.2378912m,
                    CreditAmount = 0m,
                    NetAmount = 1.2378912m,
                    ObservedAt = Instant.FromUtc(2026, 8, 3, 9, 0),
                }
            );
        using (var raw = JsonDocument.Parse(observation.RawPayload))
        {
            raw.RootElement.GetProperty("result").GetProperty("workspace_id").GetString().Should().Be(workspace);
            raw.RootElement.GetProperty("result").GetProperty("cost_type").GetString().Should().Be("code_execution");
            raw.RootElement.GetProperty("result").GetProperty("model").GetString().Should().Be("claude-sonnet-5");
        }
        var spend = await db
            .SpendEntries.AsNoTracking()
            .SingleAsync(
                row => row.SourceId == UsageSourceIds.AnthropicCostReport && row.Description == description,
                TestContext.Current.CancellationToken
            );
        spend.Amount.Should().Be(1.2378912m);
        spend.CostBasis.Should().Be(CostBasis.Billed);
        (await db.DailyAggregates.CountAsync(TestContext.Current.CancellationToken)).Should().Be(aggregatesBefore);
        result.LatestObservationAt.Should().Be(End);
    }

    [Fact]
    public async Task IngestAsync_RetainsZeroAndCorrectsOneStableDescriptionWorkspaceIdentity()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var correctedDescription = $"Tokens {suffix}";
        var zeroDescription = $"Web Search {suffix}";
        var workspace = $"wrkspc-{suffix}";
        var client = Substitute.For<IAnthropicAdminClient>();
        client
            .GetCostsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                Costs(100m, correctedDescription, zeroDescription, workspace),
                Costs(250m, correctedDescription, zeroDescription, workspace),
                Costs(250m, correctedDescription, zeroDescription, workspace)
            );
        await using var db = CreateDb();
        using var resources = new WriterResources(db);
        var sut = new AnthropicCostsSource(
            client,
            resources.Writer,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<AnthropicCostsSource>.Instance
        );

        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);
        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);
        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        (await db.BillingObservations.AsNoTracking().CountAsync(row => row.Sku == correctedDescription, ct))
            .Should()
            .Be(1);
        (await db.BillingObservations.AsNoTracking().SingleAsync(row => row.Sku == correctedDescription, ct))
            .NetAmount.Should()
            .Be(2.5m);
        (
            await db
                .SpendEntries.AsNoTracking()
                .CountAsync(
                    row =>
                        row.SourceId == UsageSourceIds.AnthropicCostReport && row.Description == correctedDescription,
                    ct
                )
        )
            .Should()
            .Be(1);
        (await db.BillingObservations.AsNoTracking().CountAsync(row => row.Sku == zeroDescription, ct)).Should().Be(1);
        (
            await db
                .SpendEntries.AsNoTracking()
                .CountAsync(
                    row => row.SourceId == UsageSourceIds.AnthropicCostReport && row.Description == zeroDescription,
                    ct
                )
        )
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task IngestAsync_WhenAnyUpstreamPageFails_WritesNothing()
    {
        var client = Substitute.For<IAnthropicAdminClient>();
        client
            .GetCostsAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AnthropicCostRecord>>(new InvalidDataException("page two")));
        await using var db = CreateDb();
        var before = await db.BillingObservations.CountAsync(TestContext.Current.CancellationToken);
        using var resources = new WriterResources(db);
        var sut = new AnthropicCostsSource(
            client,
            resources.Writer,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<AnthropicCostsSource>.Instance
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

    private static AnthropicCostRecord Cost(decimal amount, string description, string workspace) =>
        new(
            Start,
            End,
            amount,
            "USD",
            workspace,
            description,
            "code_execution",
            "claude-sonnet-5",
            "0-200k",
            "global",
            "standard",
            "uncached_input_tokens",
            JsonSerializer.Serialize(
                new
                {
                    bucket = new { starting_at = Start.ToString(), ending_at = End.ToString() },
                    result = new
                    {
                        amount = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        currency = "USD",
                        workspace_id = workspace,
                        description,
                        cost_type = "code_execution",
                        model = "claude-sonnet-5",
                    },
                }
            )
        );

    private static IReadOnlyList<AnthropicCostRecord> Costs(
        decimal correctedAmount,
        string correctedDescription,
        string zeroDescription,
        string workspace
    ) => [Cost(correctedAmount, correctedDescription, workspace), Cost(0m, zeroDescription, workspace)];

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
