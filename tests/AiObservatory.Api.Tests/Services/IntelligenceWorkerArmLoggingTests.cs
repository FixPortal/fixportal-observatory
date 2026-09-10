using AiObservatory.Api.Services.GitHub;
using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data;
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Api.Tests.Services;

/// <summary>
/// An optional arm that was never registered produces no output at all, which is
/// indistinguishable from an arm that ran and found nothing. The startup line is what makes
/// the two tellable apart — so it is worth a test that fails if it stops naming an arm's state.
/// </summary>
public sealed class IntelligenceWorkerArmLoggingTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }
    }

    private (IntelligenceWorkerService Worker, CapturingLogger Log) Create(bool registerGitHubBilling)
    {
        var services = new ServiceCollection();
        if (registerGitHubBilling)
        {
            var billingHttp = new HttpClient();
            var fxHttp = new HttpClient();
            var fxCache = new MemoryCache(new MemoryCacheOptions());
            var db = new AiObservatoryDbContext(
                new DbContextOptionsBuilder<AiObservatoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );
            _owned.AddRange([billingHttp, fxHttp, fxCache, db]);
            services.AddSingleton(
                new GitHubBillingSyncService(
                    new GitHubBillingClient(billingHttp, "FixPortal", NullLogger<GitHubBillingClient>.Instance),
                    new BillingObservationWriter(
                        db,
                        new FxRateProvider(fxHttp, fxCache, NullLogger<FxRateProvider>.Instance),
                        new FakeClock(Instant.FromUtc(2026, 7, 30, 9, 0))
                    ),
                    new FakeClock(Instant.FromUtc(2026, 7, 30, 9, 0)),
                    NullLogger<GitHubBillingSyncService>.Instance
                )
            );
        }

        var provider = services.BuildServiceProvider();
        _owned.Add(provider);
        var log = new CapturingLogger();
        var worker = new IntelligenceWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(Instant.FromUtc(2026, 7, 30, 9, 0)),
            log
        );
        return (worker, log);
    }

    [Fact]
    public void SaysSoWhenTheGitHubBillingArmIsRegistered()
    {
        var (worker, log) = Create(registerGitHubBilling: true);

        worker.LogEnabledArms();

        log.Messages.Should().ContainSingle().Which.Should().Contain("GitHub billing sync: enabled");
    }

    /// <summary>
    /// The case that actually bit: the arm was unregistered for weeks while the logs looked
    /// no different from a healthy cycle. "NOT CONFIGURED" has to be stated, not implied by
    /// an absence.
    /// </summary>
    [Fact]
    public void SaysSoLoudlyWhenTheGitHubBillingArmIsNotConfigured()
    {
        var (worker, log) = Create(registerGitHubBilling: false);

        worker.LogEnabledArms();

        var message = log.Messages.Should().ContainSingle().Subject;
        message.Should().Contain("GitHub billing sync: NOT CONFIGURED");
        message
            .Should()
            .Contain(
                "no entries will be written",
                "the line has to say what the consequence is, or it reads as a harmless notice"
            );
    }

    [Fact]
    public void NamesTheArmsThatAreAlwaysOnSoASilentCycleCanBeRuledOut()
    {
        var (worker, log) = Create(registerGitHubBilling: true);

        worker.LogEnabledArms();

        var message = log.Messages.Should().ContainSingle().Subject;
        message.Should().Contain("analysis catchup").And.Contain("budget check");
    }

    [Fact]
    public void LogsAtInformationSoItSurvivesTheDefaultProductionFilter()
    {
        var (worker, log) = Create(registerGitHubBilling: true);

        worker.LogEnabledArms();

        log.Levels.Should().AllSatisfy(level => level.Should().Be(LogLevel.Information));
    }

    private sealed class CapturingLogger : ILogger<IntelligenceWorkerService>
    {
        public List<string> Messages { get; } = [];
        public List<LogLevel> Levels { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
