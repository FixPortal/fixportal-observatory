using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.OpenAi;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

[Collection("ProviderPollingWorker")]
public sealed class OpenAiUsageSourceTests(ProviderPollingDatabase database)
{
    private static readonly Instant Start = Instant.FromUtc(2026, 8, 1, 0, 0);
    private static readonly Instant End = Instant.FromUtc(2026, 8, 2, 0, 0);

    [Fact]
    public async Task IngestAsync_KeepsPriceBearingLanesSeparateAndUsesStableCorrectionKeys()
    {
        var client = Substitute.For<IOpenAiAdminClient>();
        client
            .GetUsageAsync(Start.InUtc().Date, Start.InUtc().Date, Arg.Any<CancellationToken>())
            .Returns([
                Usage(false, "default", "standard", uncached: 10, cached: 2, cacheWrite: 3, output: 5, 1),
                Usage(false, "default", "standard", uncached: 20, cached: 4, cacheWrite: 6, output: 7, 2),
                Usage(true, "default", "batch", uncached: 1, cached: 1, cacheWrite: 0, output: 2, 3),
                Usage(false, "priority", "fast", uncached: 4, cached: 0, cacheWrite: 0, output: 3, 4),
            ]);
        var recorded = new List<UsageEvent>();
        var repository = Substitute.For<IUsageRepository>();
        repository
            .RecordEstimatedEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var usage = call.Arg<UsageEvent>();
                recorded.Add(usage);
                return new RecordEventResult(usage.Id, RecordEventDisposition.Created);
            });
        var sut = new OpenAiUsageSource(
            client,
            repository,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<OpenAiUsageSource>.Instance
        );

        var result = await sut.IngestAsync(
            Start.InUtc().Date,
            Start.InUtc().Date,
            TestContext.Current.CancellationToken
        );

        recorded.Should().HaveCount(3);
        var standard = recorded.Single(row => Evidence(row).GetProperty("processing").GetString() == "standard");
        standard
            .Should()
            .BeEquivalentTo(
                new
                {
                    Provider = Provider.OpenAI,
                    OccurredAt = Start,
                    Model = "gpt-5.4",
                    InputTokens = 30L,
                    OutputTokens = 12L,
                    CacheReadTokens = (long?)6,
                    CacheWriteTokens = (long?)9,
                    CostUsd = (decimal?)null,
                    SourceId = UsageSourceIds.OpenAiUsageApi,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.ListPriceEstimate,
                    ObservedAt = Instant.FromUtc(2026, 8, 3, 9, 0),
                }
            );
        Evidence(standard).GetProperty("service_tier").GetString().Should().Be("default");
        Evidence(standard).TryGetProperty("context", out _).Should().BeFalse();
        Evidence(standard).TryGetProperty("region", out _).Should().BeFalse();
        recorded.Select(row => row.EventKey).Should().OnlyHaveUniqueItems();
        result.LatestObservationAt.Should().Be(End);

        var catalog = new OpenAiPriceCatalog(
            "USD",
            "https://developers.openai.com/api/docs/pricing",
            Instant.FromUtc(2026, 8, 1, 0, 0),
            [new("gpt-5.4", ["gpt-5.4"], Start.InUtc().Date, false, "standard", "short", "global", 1m, 0.1m, 2m, 1m)]
        );
        new OpenAiPriceCalculator()
            .Calculate(standard, PricingCatalogJson.Serialize(catalog))
            .Should()
            .BeNull("the provider report supplies neither context nor region");
    }

    [Fact]
    public async Task IngestAsync_ReusesTheSameKeyForAProviderCorrection()
    {
        var client = Substitute.For<IOpenAiAdminClient>();
        client
            .GetUsageAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                List(Usage(false, "flex", "flex", uncached: 10)),
                List(Usage(false, "flex", "flex", uncached: 20))
            );
        var keys = new List<string?>();
        var repository = Substitute.For<IUsageRepository>();
        repository
            .RecordEstimatedEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                keys.Add(call.Arg<UsageEvent>().EventKey);
                return new RecordEventResult(Guid.NewGuid(), RecordEventDisposition.Corrected);
            });
        var sut = new OpenAiUsageSource(
            client,
            repository,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<OpenAiUsageSource>.Instance
        );

        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);
        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);

        keys.Should().HaveCount(2).And.OnlyContain(key => key != null);
        keys[1].Should().Be(keys[0]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IngestAsync_RetainsUnknownModelUsageWithNullCostAndDistinctIdentity()
    {
        var client = Substitute.For<IOpenAiAdminClient>();
        client
            .GetUsageAsync(Start.InUtc().Date, Start.InUtc().Date, Arg.Any<CancellationToken>())
            .Returns([
                Usage(false, null, null, uncached: 500, cached: 400, cacheWrite: 100, output: 500) with
                {
                    Model = null,
                },
                Usage(false, null, null, evidence: 2) with
                {
                    Model = "null",
                },
            ]);
        await using var db = CreateDb();
        var store = new PricingSnapshotStore(db);
        var repository = new UsageRepository(
            db,
            store,
            new UsagePriceResolver(store, [new OpenAiPriceCalculator()], NullLogger<UsagePriceResolver>.Instance)
        );
        var sut = new OpenAiUsageSource(
            client,
            repository,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<OpenAiUsageSource>.Instance
        );

        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);

        var events = await db
            .UsageEvents.AsNoTracking()
            .Where(row => row.SourceId == UsageSourceIds.OpenAiUsageApi && row.OccurredAt == Start)
            .ToListAsync(TestContext.Current.CancellationToken);
        events.Should().HaveCount(2);
        events.Select(row => row.EventKey).Should().OnlyHaveUniqueItems();
        events
            .Single(row => row.Model == null)
            .Should()
            .BeEquivalentTo(
                new
                {
                    InputTokens = 500L,
                    CacheReadTokens = (long?)400,
                    CacheWriteTokens = (long?)100,
                    OutputTokens = 500L,
                    CostUsd = (decimal?)null,
                }
            );
    }

    [Fact]
    public async Task IngestAsync_WhenAnyUpstreamPageFails_WritesNothing()
    {
        var client = Substitute.For<IOpenAiAdminClient>();
        client
            .GetUsageAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<OpenAiUsageRecord>>(new InvalidDataException("page two")));
        var repository = Substitute.For<IUsageRepository>();
        var sut = new OpenAiUsageSource(
            client,
            repository,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<OpenAiUsageSource>.Instance
        );

        var act = () => sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        await repository.DidNotReceive().RecordEstimatedEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>());
    }

    private static JsonElement Evidence(UsageEvent usage) => JsonSerializer.Deserialize<JsonElement>(usage.RawPayload);

    private AiObservatoryDbContext CreateDb() =>
        new(
            new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(database.ConnectionString, options => options.UseNodaTime())
                .Options
        );

    private static IReadOnlyList<OpenAiUsageRecord> List(OpenAiUsageRecord record) => [record];

    private static OpenAiUsageRecord Usage(
        bool? batch,
        string? tier,
        string? processing,
        long uncached = 1,
        long cached = 0,
        long cacheWrite = 0,
        long output = 1,
        int evidence = 1
    ) =>
        new(
            Start,
            End,
            "gpt-5.4",
            batch,
            tier,
            processing,
            uncached,
            cached,
            cacheWrite,
            output,
            1,
            $$"""{"provider_row":{{evidence}}}"""
        );
}
