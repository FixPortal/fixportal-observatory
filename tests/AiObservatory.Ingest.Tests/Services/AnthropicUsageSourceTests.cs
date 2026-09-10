using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.Anthropic;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

public sealed class AnthropicUsageSourceTests
{
    private static readonly Instant Start = Instant.FromUtc(2026, 8, 1, 0, 0);
    private static readonly Instant End = Instant.FromUtc(2026, 8, 2, 0, 0);

    [Fact]
    public async Task IngestAsync_PreservesExactPricingLanesAndStableCorrectionIdentity()
    {
        var client = Substitute.For<IAnthropicAdminClient>();
        client
            .GetMessageUsageAsync(Start.InUtc().Date, Start.InUtc().Date, Arg.Any<CancellationToken>())
            .Returns([
                Usage("claude-sonnet-5", "batch", "us", "standard", 10, 2, 3, 4, 5, 1),
                Usage("claude-sonnet-5", "batch", "us", "standard", 20, 4, 6, 8, 10, 2),
                Usage("claude-sonnet-5", "standard", "global", "fast", 1, 2, 0, 0, 0, 3),
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
        var sut = new AnthropicUsageSource(
            client,
            repository,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<AnthropicUsageSource>.Instance
        );

        var result = await sut.IngestAsync(
            Start.InUtc().Date,
            Start.InUtc().Date,
            TestContext.Current.CancellationToken
        );

        recorded.Should().HaveCount(2);
        var batch = recorded.Single(row => Evidence(row).GetProperty("service_tier").GetString() == "batch");
        batch
            .Should()
            .BeEquivalentTo(
                new
                {
                    Provider = Provider.Anthropic,
                    OccurredAt = Start,
                    Model = "claude-sonnet-5",
                    InputTokens = 30L,
                    OutputTokens = 6L,
                    CacheReadTokens = (long?)9,
                    CacheWriteTokens = (long?)27,
                    CacheWrite1hTokens = (long?)15,
                    CostUsd = (decimal?)null,
                    SourceId = UsageSourceIds.AnthropicUsageApi,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.ListPriceEstimate,
                    ObservedAt = Instant.FromUtc(2026, 8, 3, 9, 0),
                }
            );
        Evidence(batch)
            .GetProperty("cache_creation")
            .GetProperty("ephemeral_5m_input_tokens")
            .GetInt64()
            .Should()
            .Be(12);
        recorded.Select(row => row.EventKey).Should().OnlyHaveUniqueItems();
        result.LatestObservationAt.Should().Be(End);

        var firstKey = batch.EventKey;
        client
            .GetMessageUsageAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([Usage("claude-sonnet-5", "batch", "us", "standard", 99, 2, 3, 4, 5, 9)]);
        recorded.Clear();
        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);
        recorded.Should().ContainSingle().Which.EventKey.Should().Be(firstKey);
    }

    [Fact]
    public async Task IngestAsync_RetainsNullableDimensionsWithCollisionSafeIdentityAndNoFallbackPrice()
    {
        var client = Substitute.For<IAnthropicAdminClient>();
        client
            .GetMessageUsageAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                Usage(null, null, null, null, 1, 1, 0, 0, 0, 1),
                Usage("null", null, null, null, 1, 1, 0, 0, 0, 2),
            ]);
        var recorded = new List<UsageEvent>();
        var repository = Substitute.For<IUsageRepository>();
        repository
            .RecordEstimatedEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recorded.Add(call.Arg<UsageEvent>());
                return new RecordEventResult(Guid.NewGuid(), RecordEventDisposition.Created);
            });
        var sut = new AnthropicUsageSource(
            client,
            repository,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<AnthropicUsageSource>.Instance
        );

        await sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);

        recorded.Should().HaveCount(2);
        recorded.Select(row => row.EventKey).Should().OnlyHaveUniqueItems();
        recorded.Should().OnlyContain(row => row.CostUsd == null);
    }

    [Fact]
    public async Task IngestAsync_WhenAnyUpstreamPageFails_WritesNothing()
    {
        var client = Substitute.For<IAnthropicAdminClient>();
        client
            .GetMessageUsageAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AnthropicUsageRecord>>(new InvalidDataException("page two")));
        var repository = Substitute.For<IUsageRepository>();
        var sut = new AnthropicUsageSource(
            client,
            repository,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<AnthropicUsageSource>.Instance
        );

        var act = () => sut.IngestAsync(Start.InUtc().Date, Start.InUtc().Date, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        await repository.DidNotReceive().RecordEstimatedEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>());
    }

    private static JsonElement Evidence(UsageEvent usage) => JsonSerializer.Deserialize<JsonElement>(usage.RawPayload);

    private static AnthropicUsageRecord Usage(
        string? model,
        string? tier,
        string? geo,
        string? speed,
        long input,
        long output,
        long cacheRead,
        long cache5m,
        long cache1h,
        int evidence
    ) =>
        new(
            Start,
            End,
            model,
            tier,
            geo,
            speed,
            input,
            output,
            cacheRead,
            cache5m,
            cache1h,
            $$"""{"row":{{evidence}}}"""
        );
}
