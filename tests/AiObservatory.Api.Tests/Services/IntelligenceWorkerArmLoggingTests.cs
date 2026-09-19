using AiObservatory.Api.Services.GitHub;
using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Security;
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

    private (IntelligenceWorkerService Worker, CapturingLogger Log) Create(
        bool registerGitHubBilling,
        string? alertEmailTo = null,
        string? slackWebhookUrl = null,
        bool smtpConfigured = false
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    smtpConfigured
                        ? new Dictionary<string, string?> { ["BUDGET_ALERT_SMTP_USER"] = "obs@example.test" }
                        : []
                )
                .Build()
        );

        if (alertEmailTo is not null || slackWebhookUrl is not null)
        {
            var settingsDb = new AiObservatoryDbContext(
                new DbContextOptionsBuilder<AiObservatoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );
            _owned.Add(settingsDb);
            settingsDb.NotificationSettings.Add(
                new NotificationSettings
                {
                    AlertEmailTo = alertEmailTo,
                    SlackWebhookUrl = slackWebhookUrl,
                    UpdatedAt = Instant.FromUtc(2026, 7, 30, 9, 0),
                }
            );
            settingsDb.SaveChanges();
            services.AddSingleton(settingsDb);
        }

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
    public async Task SaysSoWhenTheGitHubBillingArmIsRegistered()
    {
        var (worker, log) = Create(registerGitHubBilling: true);

        await worker.LogEnabledArmsAsync(TestContext.Current.CancellationToken);

        log.Messages.Should().ContainSingle().Which.Should().Contain("GitHub billing sync: enabled");
    }

    /// <summary>
    /// The case that actually bit: the arm was unregistered for weeks while the logs looked
    /// no different from a healthy cycle. "NOT CONFIGURED" has to be stated, not implied by
    /// an absence.
    /// </summary>
    [Fact]
    public async Task SaysSoLoudlyWhenTheGitHubBillingArmIsNotConfigured()
    {
        var (worker, log) = Create(registerGitHubBilling: false);

        await worker.LogEnabledArmsAsync(TestContext.Current.CancellationToken);

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
    public async Task NamesTheArmsThatAreAlwaysOnSoASilentCycleCanBeRuledOut()
    {
        var (worker, log) = Create(registerGitHubBilling: true);

        await worker.LogEnabledArmsAsync(TestContext.Current.CancellationToken);

        var message = log.Messages.Should().ContainSingle().Subject;
        message.Should().Contain("analysis catchup").And.Contain("budget check");
    }

    /// <summary>
    /// The alerting arms are always registered, so registration alone says nothing about
    /// whether they can send. Claiming "enabled" with no deliverable channel would reproduce,
    /// in the startup line itself, the exact failure the digest exists to prevent — which is
    /// what happened: production carried a configured recipient and no SMTP transport at all,
    /// so the old line said "enabled" while every send died on an empty sender.
    /// </summary>
    [Fact]
    public async Task SaysSoWhenNoAlertChannelCanActuallyDeliver()
    {
        var (worker, log) = Create(registerGitHubBilling: true);

        await worker.LogEnabledArmsAsync(TestContext.Current.CancellationToken);

        var message = log.Messages.Should().ContainSingle().Subject;
        message.Should().Contain("alert channels: NO DELIVERABLE CHANNEL");
        message
            .Should()
            .Contain(
                "nothing will be sent",
                "the line has to say what the consequence is, or it reads as a harmless notice"
            );
    }

    /// <summary>
    /// The production shape, verbatim: a recipient stored in the database and no SMTP
    /// transport anywhere in configuration. A recipient is not a channel, and a line that
    /// counted one as the other is what let two ingest sources fail unannounced for weeks.
    /// </summary>
    [Fact]
    public async Task ReportsNoChannelWhenARecipientIsStoredButNoTransportIsConfigured()
    {
        var (worker, log) = Create(registerGitHubBilling: true, alertEmailTo: "alerts@example.test");

        await worker.LogEnabledArmsAsync(TestContext.Current.CancellationToken);

        log.Messages.Should().ContainSingle().Which.Should().Contain("alert channels: NO DELIVERABLE CHANNEL");
    }

    [Theory]
    [InlineData("alerts@example.test", null, true, "alert channels: email")]
    [InlineData(null, "https://hooks.slack.com/services/T0/B0/xyz", false, "alert channels: Slack")]
    [InlineData(
        "alerts@example.test",
        "https://hooks.slack.com/services/T0/B0/xyz",
        true,
        "alert channels: email + Slack"
    )]
    public async Task NamesEveryChannelThatCanActuallyDeliver(
        string? alertEmailTo,
        string? slackWebhookUrl,
        bool smtpConfigured,
        string expected
    )
    {
        var (worker, log) = Create(
            registerGitHubBilling: true,
            alertEmailTo: alertEmailTo,
            slackWebhookUrl: slackWebhookUrl,
            smtpConfigured: smtpConfigured
        );

        await worker.LogEnabledArmsAsync(TestContext.Current.CancellationToken);

        log.Messages.Should().ContainSingle().Which.Should().Contain(expected);
    }

    /// <summary>
    /// A webhook the read path could not decrypt is stored as a non-empty sentinel, so a
    /// naive emptiness check would report a live Slack channel that cannot post.
    /// </summary>
    [Fact]
    public async Task DoesNotCountAnUndecryptableWebhookAsAChannel()
    {
        var (worker, log) = Create(
            registerGitHubBilling: true,
            slackWebhookUrl: SlackWebhookProtector.UndecryptableSentinel
        );

        await worker.LogEnabledArmsAsync(TestContext.Current.CancellationToken);

        log.Messages.Should().ContainSingle().Which.Should().Contain("alert channels: NO DELIVERABLE CHANNEL");
    }

    [Fact]
    public async Task LogsAtInformationSoItSurvivesTheDefaultProductionFilter()
    {
        var (worker, log) = Create(registerGitHubBilling: true);

        await worker.LogEnabledArmsAsync(TestContext.Current.CancellationToken);

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
