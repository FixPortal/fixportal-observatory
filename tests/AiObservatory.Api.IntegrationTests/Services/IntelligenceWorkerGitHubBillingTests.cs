using AiObservatory.Api.Services;
using AiObservatory.Api.Services.GitHub;
using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using NSubstitute;

namespace AiObservatory.Api.IntegrationTests.Services;

/// <summary>
/// The regression this guards: a token without the org billing scope used to come back as
/// an empty usage list, the sync returned 0, and the worker recorded a healthy success —
/// the status surface said "fine" while the entire GitHub org bill went missing. These run
/// the real worker against the real <see cref="SourceSyncStateStore"/> so the recorded
/// state itself is what is asserted, not a mocked call.
/// </summary>
[Trait("Category", "Integration")]
public class IntelligenceWorkerGitHubBillingTests : IAsyncLifetime
{
    // Each scenario starts without the other scenario's success/failure state.
    private readonly AiObservatoryApiFactory _factory = new();
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 30, 9, 0);

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task<ServiceProvider> BuildWorkerHostAsync(GitHubBillingClient client)
    {
        var clock = new FakeClock(Now);
        var repository = Substitute.For<IUsageRepository>();
        // Yesterday already analysed, so the catchup loop is empty and no IInsightGenerator
        // is ever resolved.
        repository.GetLatestInsightPeriodEndAsync(Arg.Any<CancellationToken>()).Returns(new LocalDate(2026, 7, 29));

        var budget = Substitute.For<BudgetAlertService>(
            repository,
            clock,
            Substitute.For<IAlertNotifier>(),
            NullLogger<BudgetAlertService>.Instance,
            new ConfigurationBuilder().Build()
        );
        budget.CheckAndAlertAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var connectionString = await ConnectionStringAsync();
        var dbOptions = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connectionString, o => o.UseNodaTime())
            .Options;

        // The sync throws (auth failure) or returns empty before the writer is ever used,
        // but the constructor still needs one.
        using var fxClient = new HttpClient();
        using var fxCache = new MemoryCache(new MemoryCacheOptions());
        var writer = new BillingObservationWriter(
            new AiObservatoryDbContext(dbOptions),
            new FxRateProvider(fxClient, fxCache, NullLogger<FxRateProvider>.Instance),
            clock
        );

        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(budget);
        services.AddSingleton(
            new GitHubBillingSyncService(client, writer, clock, NullLogger<GitHubBillingSyncService>.Instance)
        );
        services.AddScoped(_ => new AiObservatoryDbContext(dbOptions));
        services.AddScoped<SourceSyncStateStore>();
        return services.BuildServiceProvider();
    }

    private async Task<string> ConnectionStringAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        // GetConnectionString strips the password (Persist Security Info defaults off), so
        // rebuild from the harness's own env var and swap in the factory's throwaway
        // database name — the credentials the container actually requires stay intact.
        var harness =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? throw new InvalidOperationException("TEST_DB_CONNECTION is not set.");
        var database =
            db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Factory database has no connection string.");
        return new NpgsqlConnectionStringBuilder(harness)
        {
            Database = new NpgsqlConnectionStringBuilder(database).Database,
        }.ConnectionString;
    }

    private async Task<SourceSyncState> WaitForStateAsync(
        Func<SourceSyncState, bool> predicate,
        string because,
        CapturingLogger log
    )
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        SourceSyncState? state = null;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            state = await db
                .SourceSyncStates.AsNoTracking()
                .SingleOrDefaultAsync(
                    s => s.SourceId == UsageSourceIds.GitHubBillingApi,
                    TestContext.Current.CancellationToken
                );
            if (state is not null && predicate(state))
            {
                return state;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var snapshot = state is null
            ? "no row"
            : $"IsConfigured={state.IsConfigured} IsAvailable={state.IsAvailable} "
                + $"LastAttemptAt={state.LastAttemptAt} LastSuccessAt={state.LastSuccessAt} "
                + $"Failures={state.ConsecutiveFailureCount} LastError={state.LastError}";
        predicate(state!)
            .Should()
            .BeTrue($"{because}. Row: {snapshot}. Worker log: {string.Join(" | ", log.Messages)}");
        return state!;
    }

    [Fact]
    public async Task AnUnscopedTokenIsRecordedAsASourceFailureNotAnEmptySuccess()
    {
        var provider = await BuildWorkerHostAsync(new ThrowingBillingClient());
        await using var _ = provider;
        var log = new CapturingLogger();
        var worker = new IntelligenceWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(Now),
            log
        );

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var state = await WaitForStateAsync(
                s => s.LastError is not null,
                "a 403/404 from GitHub must reach MarkFailureAsync, not read as a quiet month",
                log
            );

            state.IsAvailable.Should().NotBeTrue();
            state.LastSuccessAt.Should().BeNull();
            state.ConsecutiveFailureCount.Should().BeGreaterThan(0);
            state.LastError.Should().Contain("billing read scope");
            worker.ExecuteTask.Should().NotBeNull();
            worker
                .ExecuteTask.IsCompleted.Should()
                .BeFalse("the failure must be recorded without taking the worker's daily cycle down");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AGenuinelyEmptyYearIsStillRecordedAsASuccess()
    {
        var provider = await BuildWorkerHostAsync(new EmptyBillingClient());
        await using var _ = provider;
        var log = new CapturingLogger();
        var worker = new IntelligenceWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(Now),
            log
        );

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var state = await WaitForStateAsync(
                s => s.LastSuccessAt is not null,
                "zero spend is a real result, not a failure — it stays a successful sync",
                log
            );

            state.IsAvailable.Should().BeTrue();
            state.ConsecutiveFailureCount.Should().Be(0);
            state.LastError.Should().BeNull();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The 403/404 case: the token cannot see the org's billing at all.</summary>
    private sealed class ThrowingBillingClient()
        : GitHubBillingClient(Unused, "test-org", NullLogger<GitHubBillingClient>.Instance)
    {
        public override Task<IReadOnlyList<GitHubBillingUsageItem>> GetUsageAsync(
            int year,
            CancellationToken ct = default
        ) =>
            Task.FromException<IReadOnlyList<GitHubBillingUsageItem>>(
                new GitHubBillingUnavailableException(
                    "GitHub billing usage unavailable for org 'test-org' (403) — the token likely lacks billing read scope"
                )
            );
    }

    /// <summary>The zero-spend case: the API answers fine, there is simply nothing billed.</summary>
    private sealed class EmptyBillingClient()
        : GitHubBillingClient(Unused, "test-org", NullLogger<GitHubBillingClient>.Instance)
    {
        public override Task<IReadOnlyList<GitHubBillingUsageItem>> GetUsageAsync(
            int year,
            CancellationToken ct = default
        ) => Task.FromResult<IReadOnlyList<GitHubBillingUsageItem>>([]);
    }

    /// <summary>One shared instance; the overrides never issue a request.</summary>
    private static readonly HttpClient Unused = new();

    /// <summary>Surfaces the worker's log lines in assertion output when a wait times out.</summary>
    private sealed class CapturingLogger : ILogger<IntelligenceWorkerService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add($"{logLevel}: {formatter(state, exception)} {exception}");

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
