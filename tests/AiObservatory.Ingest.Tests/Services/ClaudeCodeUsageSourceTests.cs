using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.Anthropic;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

[Collection("ProviderPollingWorker")]
[Trait("Category", "Integration")]
public sealed class ClaudeCodeUsageSourceTests(ProviderPollingDatabase database)
{
    private static readonly LocalDate Day = new(2026, 8, 1);
    private static readonly Instant DayStart = Instant.FromUtc(2026, 8, 1, 0, 0);

    [Fact]
    public async Task IngestAsync_SeparatesApiAndSubscriptionLanesWithOptionalProviderEstimatedCost()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var client = Substitute.For<IAnthropicAdminClient>();
        client
            .GetClaudeCodeUsageAsync(Day, Day, Arg.Any<CancellationToken>())
            .Returns([
                Usage($"api-{suffix}", "api", false, "vscode", "claude-sonnet-5", 1234m, "enterprise"),
                Usage($"sub-{suffix}", "subscription", true, "remote", "claude-sonnet-5", null),
            ]);
        await using var db = CreateDb();
        var repository = new UsageRepository(db);
        var spendBefore = await db.SpendEntries.CountAsync(TestContext.Current.CancellationToken);
        var sut = new ClaudeCodeUsageSource(
            client,
            repository,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<ClaudeCodeUsageSource>.Instance
        );

        var result = await sut.IngestAsync(Day, Day, TestContext.Current.CancellationToken);

        var rows = await db
            .UsageEvents.AsNoTracking()
            .Where(row => row.SourceId == UsageSourceIds.ClaudeCodeUsageApi && row.OccurredAt == DayStart)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows = rows.Where(row => row.RawPayload.Contains(suffix, StringComparison.Ordinal)).ToList();
        rows.Should().HaveCount(2);
        var apiRow = rows.Single(row => row.UsageScope == UsageScope.Api);
        apiRow
            .Should()
            .BeEquivalentTo(
                new
                {
                    Provider = Provider.Anthropic,
                    Model = "claude-sonnet-5",
                    InputTokens = 100L,
                    OutputTokens = 20L,
                    CacheReadTokens = (long?)30,
                    CacheWriteTokens = (long?)10,
                    CostUsd = (decimal?)12.34m,
                    CostBasis = CostBasis.ProviderEstimated,
                    SourceKind = SourceKind.ProviderApi,
                }
            );
        using var apiEvidence = JsonDocument.Parse(apiRow.RawPayload);
        apiEvidence.RootElement.GetProperty("subscription_type").GetString().Should().Be("enterprise");
        rows.Single(row => row.UsageScope == UsageScope.Subscription)
            .Should()
            .BeEquivalentTo(new { CostUsd = (decimal?)null, CostBasis = CostBasis.None });
        rows.Select(row => row.EventKey).Should().OnlyHaveUniqueItems();
        (await db.SpendEntries.CountAsync(TestContext.Current.CancellationToken)).Should().Be(spendBefore);
        result.LatestObservationAt.Should().Be(Instant.FromUtc(2026, 8, 2, 0, 0));
    }

    [Fact]
    public async Task IngestAsync_GroupsDuplicateModelLanesAndCorrectsWithoutDuplicating()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var actor = $"dev-{suffix}@example.com";
        var client = Substitute.For<IAnthropicAdminClient>();
        IReadOnlyList<ClaudeCodeUsageRecord> initial =
        [
            Usage(actor, "subscription", false, "vscode", "claude-sonnet-5", 100m),
            Usage(actor, "subscription", false, "vscode", "claude-sonnet-5", 200m),
        ];
        IReadOnlyList<ClaudeCodeUsageRecord> corrected =
        [
            Usage(actor, "subscription", false, "vscode", "claude-sonnet-5", 400m),
        ];
        client
            .GetClaudeCodeUsageAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(initial, corrected);
        await using var db = CreateDb();
        var sut = new ClaudeCodeUsageSource(
            client,
            new UsageRepository(db),
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<ClaudeCodeUsageSource>.Instance
        );

        await sut.IngestAsync(Day, Day, TestContext.Current.CancellationToken);
        await sut.IngestAsync(Day, Day, TestContext.Current.CancellationToken);

        var rows = await db
            .UsageEvents.AsNoTracking()
            .Where(row => row.SourceId == UsageSourceIds.ClaudeCodeUsageApi && row.OccurredAt == DayStart)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows = rows.Where(row => row.RawPayload.Contains(actor, StringComparison.Ordinal)).ToList();
        rows.Should().ContainSingle();
        rows[0].InputTokens.Should().Be(100);
        rows[0].CostUsd.Should().Be(4m);
    }

    [Fact]
    public async Task IngestAsync_WhenAnyUpstreamDayFails_WritesNothing()
    {
        var client = Substitute.For<IAnthropicAdminClient>();
        client
            .GetClaudeCodeUsageAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ClaudeCodeUsageRecord>>(new InvalidDataException("day two")));
        var repository = Substitute.For<IUsageRepository>();
        var sut = new ClaudeCodeUsageSource(
            client,
            repository,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<ClaudeCodeUsageSource>.Instance
        );

        var act = () => sut.IngestAsync(Day, Day.PlusDays(1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        await repository.DidNotReceive().RecordEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WhenSubscriptionTypeDiffersWithinAGroup_KeepsTheFirst()
    {
        // A mid-day resubscription can put two distinct values in one group; previously
        // Distinct().SingleOrDefault() threw out of the payload build and failed the day.
        var client = Substitute.For<IAnthropicAdminClient>();
        client
            .GetClaudeCodeUsageAsync(Day, Day, Arg.Any<CancellationToken>())
            .Returns([
                Usage("dev@example.com", "subscription", false, "vscode", "claude-sonnet-5", null, "team"),
                Usage("dev@example.com", "subscription", false, "vscode", "claude-sonnet-5", null, "pro"),
            ]);
        UsageEvent? captured = null;
        var repository = Substitute.For<IUsageRepository>();
        repository
            .RecordEventAsync(Arg.Do<UsageEvent>(value => captured = value), Arg.Any<CancellationToken>())
            .Returns(new RecordEventResult(Guid.NewGuid(), RecordEventDisposition.Created));
        var sut = new ClaudeCodeUsageSource(
            client,
            repository,
            new FakeClock(Instant.FromUtc(2026, 8, 3, 9, 0)),
            NullLogger<ClaudeCodeUsageSource>.Instance
        );

        var act = () => sut.IngestAsync(Day, Day, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        captured.Should().NotBeNull();
        using var payload = JsonDocument.Parse(captured.RawPayload);
        payload.RootElement.GetProperty("subscription_type").GetString().Should().Be("team");
    }

    private AiObservatoryDbContext CreateDb() =>
        new(
            new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(database.ConnectionString, options => options.UseNodaTime())
                .Options
        );

    private static ClaudeCodeUsageRecord Usage(
        string actor,
        string customerType,
        bool isRemote,
        string terminal,
        string model,
        decimal? estimatedMinor,
        string? subscriptionType = null
    )
    {
        subscriptionType ??= customerType == "subscription" ? "team" : null;
        return new ClaudeCodeUsageRecord(
            Day,
            "user_actor",
            actor,
            "org-test",
            customerType,
            subscriptionType,
            isRemote,
            terminal,
            model,
            100,
            20,
            30,
            10,
            estimatedMinor,
            estimatedMinor is null ? null : "USD",
            JsonSerializer.Serialize(
                new
                {
                    date = $"{Day:yyyy-MM-dd}T00:00:00Z",
                    actor = new { type = "user_actor", email_address = actor },
                    organization_id = "org-test",
                    customer_type = customerType,
                    subscription_type = subscriptionType,
                    is_remote = isRemote,
                    terminal_type = terminal,
                    model_breakdown = new
                    {
                        model,
                        tokens = new
                        {
                            input = 100,
                            output = 20,
                            cache_read = 30,
                            cache_creation = 10,
                        },
                        estimated_cost = estimatedMinor is null
                            ? null
                            : new { amount = estimatedMinor, currency = "USD" },
                    },
                }
            )
        );
    }
}
