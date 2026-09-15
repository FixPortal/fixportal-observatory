using AiObservatory.Api.Services;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using NSubstitute;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// Covers what the pure <c>SourceHealthDigest.Compose</c> tests cannot reach: the day claim,
/// its dedup, and what happens to the claim when delivery throws.
/// <para>
/// Needs a REAL database rather than the in-memory provider, because the claim is an
/// <c>ExecuteUpdateAsync</c> — relational-only, and it throws on the in-memory provider. A
/// test reaching for in-memory here would be exercising nothing.
/// </para>
/// <para>
/// Owns a throwaway database directly (the Data.Tests pattern) instead of booting
/// <see cref="AiObservatoryApiFactory"/>: the factory starts the real
/// <c>IntelligenceWorkerService</c>, whose digest arm would race these tests for the very day
/// claim they assert on.
/// </para>
/// </summary>
public sealed class SourceHealthDigestServiceTests : IAsyncLifetime
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 15, 12, 0);
    private static readonly LocalDate Today = Now.InUtc().Date;

    private string _connectionString = null!;
    private AiObservatoryDbContext _db = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConnection =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_digest_{Guid.NewGuid():N}",
        }.ConnectionString;
        _db = new AiObservatoryDbContext(
            new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(_connectionString, options => options.UseNodaTime())
                .Options
        );
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.Database.EnsureDeletedAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Claims_a_row_that_has_never_been_claimed_and_sends()
    {
        // The null arm of the claim predicate is what this proves: in SQL
        // `NULL <> DATE '...'` evaluates to NULL rather than true, so a fresh settings row
        // would never be claimed and the digest would silently never fire.
        await SeedAsync(lastDigestOn: null);
        var smtp = Smtp();

        var sent = await Service(smtp).SendIfDueAsync(TestContext.Current.CancellationToken);

        sent.Should().BeTrue();
        await smtp.Received(1).SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>());
        (await ClaimedOnAsync()).Should().Be(Today);
    }

    [Fact]
    public async Task Sends_nothing_on_a_second_call_the_same_day()
    {
        await SeedAsync(lastDigestOn: null);
        var smtp = Smtp();

        await Service(smtp).SendIfDueAsync(TestContext.Current.CancellationToken);
        var second = await Service(smtp).SendIfDueAsync(TestContext.Current.CancellationToken);

        second.Should().BeFalse();
        await smtp.Received(1).SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keeps_the_day_claimed_when_delivery_throws()
    {
        // The deliberate inversion of BudgetAlertClaim: a digest lost to an SMTP outage costs
        // a day's notice and heals tomorrow, whereas releasing the claim would re-send on
        // every worker pass and restart for the remainder of the day.
        await SeedAsync(lastDigestOn: null);
        var smtp = Smtp();
        smtp.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("smtp is down"));

        var sent = await Service(smtp).SendIfDueAsync(TestContext.Current.CancellationToken);

        sent.Should().BeFalse();
        (await ClaimedOnAsync()).Should().Be(Today, "a failed send must not release the day");
    }

    [Fact]
    public async Task Sends_nothing_and_consumes_no_claim_when_no_source_is_degraded()
    {
        await SeedAsync(lastDigestOn: null, degraded: false);
        var smtp = Smtp();

        var sent = await Service(smtp).SendIfDueAsync(TestContext.Current.CancellationToken);

        sent.Should().BeFalse();
        await smtp.DidNotReceive().SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>());
        (await ClaimedOnAsync()).Should().BeNull("a quiet day must not spend the claim");
    }

    [Fact]
    public async Task Sends_nothing_when_no_recipient_is_configured()
    {
        await SeedAsync(lastDigestOn: null, alertEmailTo: null);
        var smtp = Smtp();

        var sent = await Service(smtp).SendIfDueAsync(TestContext.Current.CancellationToken);

        sent.Should().BeFalse();
        await smtp.DidNotReceive().SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>());
        (await ClaimedOnAsync()).Should().BeNull();
    }

    private static ISmtpClient Smtp() => Substitute.For<ISmtpClient>();

    private SourceHealthDigestService Service(ISmtpClient smtp)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["BUDGET_ALERT_EMAIL_FROM"] = "observatory@example.test" }
            )
            .Build();
        return new SourceHealthDigestService(
            _db,
            new SmtpMailSender(smtp, config),
            config,
            new FakeClock(Now),
            NullLogger<SourceHealthDigestService>.Instance
        );
    }

    private async Task<LocalDate?> ClaimedOnAsync()
    {
        // The service claims via ExecuteUpdateAsync, which bypasses the change tracker, so a
        // tracked instance would still report the pre-claim value.
        _db.ChangeTracker.Clear();
        var settings = await _db
            .NotificationSettings.AsNoTracking()
            .FirstAsync(s => s.Id == NotificationSettings.SingletonId, TestContext.Current.CancellationToken);
        return settings.LastSourceHealthDigestOn;
    }

    private async Task SeedAsync(
        LocalDate? lastDigestOn,
        bool degraded = true,
        string? alertEmailTo = "alerts@example.test"
    )
    {
        _db.NotificationSettings.Add(
            new NotificationSettings
            {
                AlertEmailTo = alertEmailTo,
                LastSourceHealthDigestOn = lastDigestOn,
                UpdatedAt = Now,
            }
        );
        _db.SourceSyncStates.Add(
            new SourceSyncState
            {
                SourceId = "test-source",
                IsConfigured = true,
                IsAvailable = !degraded,
                ConsecutiveFailureCount = degraded ? 306 : 0,
                LastSuccessAt = Now.Minus(Duration.FromDays(12)),
                LastAttemptAt = Now,
                LastError = degraded ? "27 of 27 configured GitHub repos failed to ingest this cycle" : null,
                ExpectedRefreshIntervalSeconds = 3600,
            }
        );
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
    }
}
