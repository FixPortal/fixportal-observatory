using System.Text.Json;
using AiObservatory.Api.Services;
using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Npgsql;

namespace AiObservatory.Api.IntegrationTests.Services;

[Trait("Category", "Integration")]
public class InsightGeneratorConcurrencyTests : IAsyncLifetime
{
    private readonly AiObservatoryApiFactory _factory = new() { ClockOverride = new FakeClock(Now) };
    private static readonly Instant Now = Instant.FromUtc(2030, 3, 5, 9, 17);
    private static readonly LocalDate AnalysisDate = new(2030, 3, 4);

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Overlapping_generation_waits_for_the_winner_and_rereads_its_committed_insight()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var scope = _factory.Services.CreateAsyncScope();
        var monitor = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        monitor.Subscriptions.Add(
            new Subscription
            {
                Provider = Provider.Anthropic,
                Name = "Anthropic test subscription",
                CostAmount = 20,
                BillingDay = 1,
                ActiveFrom = AnalysisDate.PlusDays(-60),
            }
        );
        await monitor.SaveChangesAsync(ct);
        var connection =
            monitor.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Factory database has no connection string.");
        var harness =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? throw new InvalidOperationException("TEST_DB_CONNECTION is required.");
        var ownedConnection = new NpgsqlConnectionStringBuilder(harness)
        {
            Database = new NpgsqlConnectionStringBuilder(connection).Database,
        }.ConnectionString;
        var gate = new InsertGate();
        var winnerOptions = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(ownedConnection, o => o.UseNodaTime())
            .AddInterceptors(gate)
            .Options;
        var loserOptions = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(ownedConnection, o => o.UseNodaTime())
            .Options;
        await using var winnerDb = new AiObservatoryDbContext(winnerOptions);
        await using var loserDb = new AiObservatoryDbContext(loserOptions);
        await winnerDb.Database.OpenConnectionAsync(ct);
        await loserDb.Database.OpenConnectionAsync(ct);
        var winnerPid = ((NpgsqlConnection)winnerDb.Database.GetDbConnection()).ProcessID;
        var loserPid = ((NpgsqlConnection)loserDb.Database.GetDbConnection()).ProcessID;
        winnerPid.Should().NotBe(loserPid);
        using var http = new HttpClient(new RejectNetworkHandler());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("fx:usd-gbp", 0.8m);
        var fx = new FxRateProvider(http, cache, NullLogger<FxRateProvider>.Instance);
        await using var winnerScope = _factory.Services.CreateAsyncScope();
        await using var loserScope = _factory.Services.CreateAsyncScope();
        var winner = Generator(winnerDb, fx, winnerScope.ServiceProvider.GetRequiredService<BudgetAlertService>());
        var loser = Generator(loserDb, fx, loserScope.ServiceProvider.GetRequiredService<BudgetAlertService>());
        Task<InsightGenerationResult>? winnerTask = null;
        Task<InsightGenerationResult>? loserTask = null;
        try
        {
            winnerTask = winner.GenerateForDateAsync(AnalysisDate, ct);
            await gate.Inserted.Task.WaitAsync(ct);
            // The INSERT really happened, but the production transaction has not committed.
            (await monitor.Insights.AsNoTracking().CountAsync(ct))
                .Should()
                .Be(0);
            loserTask = loser.GenerateForDateAsync(AnalysisDate, ct);
            var blocked = await WaitForAdvisoryWaitOrCompletionAsync(monitor, winnerPid, loserPid, loserTask, ct);
            gate.Release.TrySetResult();
            var winnerResult = await winnerTask;
            var loserResult = await loserTask;
            var rows = await monitor.Insights.AsNoTracking().ToListAsync(ct);
            using var assertions = new AssertionScope();
            winnerResult.Should().Be(new InsightGenerationResult(1, 1));
            loserResult
                .Should()
                .Be(
                    new InsightGenerationResult(1, 0),
                    "the waiting generator must re-read the winner's newly committed story"
                );
            rows.Should().ContainSingle();
            var row = rows[0];
            row.Id.Should().NotBeEmpty();
            row.InsightType.Should().Be(InsightType.Efficiency);
            row.Title.Should().Be("Anthropic repeated spend opportunity");
            row.Body.Should().Be("Anthropic has a recurring billed efficiency opportunity.");
            row.GeneratedAt.Should().Be(Now);
            row.PeriodStart.Should().Be(new LocalDate(2030, 2, 3));
            row.PeriodEnd.Should().Be(AnalysisDate);
            row.AcknowledgedAt.Should().BeNull();
            using var data = JsonDocument.Parse(row.Data);
            data.RootElement.GetProperty("costBasis").GetString().Should().Be("billed");
            data.RootElement.GetProperty("estimatedWeeklySaving").GetDecimal().Should().Be(12.34m);
            data.RootElement.EnumerateObject().Should().HaveCount(2);
            blocked.Should().BeTrue("the actual loser must wait on the winner's transaction-scoped advisory lock");
        }
        finally
        {
            gate.Release.TrySetResult();
            await deadline.CancelAsync();
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cleanup.CancelAfter(TimeSpan.FromSeconds(30));
            Task<InsightGenerationResult>?[] drained = [winnerTask, loserTask];
            foreach (var pending in drained.OfType<Task<InsightGenerationResult>>())
            {
                try
                {
                    await pending.WaitAsync(cleanup.Token);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"Drained generator: {exception.GetType().Name}");
                }
            }
        }
    }

    private static InsightGenerator Generator(
        AiObservatoryDbContext db,
        FxRateProvider fx,
        BudgetAlertService budget
    ) =>
        new(
            new UsageRepository(db),
            db,
            new ModelResponse(),
            new PromptBuilder(),
            new InsightResponseParser(),
            fx,
            budget,
            new FakeClock(Now),
            NullLogger<InsightGenerator>.Instance
        );

    private static async Task<bool> WaitForAdvisoryWaitOrCompletionAsync(
        AiObservatoryDbContext monitor,
        int winnerPid,
        int loserPid,
        Task loser,
        CancellationToken ct
    )
    {
        while (!loser.IsCompleted)
        {
            await using var command = monitor.Database.GetDbConnection().CreateCommand();
            if (command.Connection!.State != System.Data.ConnectionState.Open)
            {
                await command.Connection.OpenAsync(ct);
            }
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity a
                    WHERE a.pid = @loser AND a.datname = current_database()
                      AND a.wait_event_type = 'Lock' AND a.wait_event = 'advisory'
                      AND a.query LIKE '%pg_advisory_xact_lock%'
                      AND @winner = ANY(pg_blocking_pids(a.pid))
                      AND EXISTS (SELECT 1 FROM pg_locks l WHERE l.pid = a.pid
                                  AND l.locktype = 'advisory' AND NOT l.granted))
                """;
            var loserParameter = command.CreateParameter();
            loserParameter.ParameterName = "loser";
            loserParameter.Value = loserPid;
            command.Parameters.Add(loserParameter);
            var winnerParameter = command.CreateParameter();
            winnerParameter.ParameterName = "winner";
            winnerParameter.Value = winnerPid;
            command.Parameters.Add(winnerParameter);
            if ((bool)(await command.ExecuteScalarAsync(ct))!)
            {
                Console.WriteLine($"Positive advisory lock witness: winner={winnerPid}, loser={loserPid}");
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        }
        return false;
    }

    // Existing EF interception seam: the real repository's SaveChanges has inserted the row,
    // while the generator still owns the transaction and has not reached CommitAsync.
    private sealed class InsertGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Inserted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            Inserted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class ModelResponse()
        : AnthropicIntelligenceClient(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = Guid.NewGuid().ToString("N") }
                )
                .Build(),
            NullLogger<AnthropicIntelligenceClient>.Instance
        )
    {
        public override Task<string> GenerateInsightsJsonAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult(
                """
                [{"type":"efficiency","title":"Anthropic repeated spend opportunity",
                "body":"Anthropic has a recurring billed efficiency opportunity.",
                "data":{"costBasis":"billed","estimatedWeeklySaving":12.34}}]
                """
            );
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("No FX request is permitted in this test.");
    }
}
