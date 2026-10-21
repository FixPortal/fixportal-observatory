using AiObservatory.Api.Services;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using NSubstitute;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// Covers what the pure <c>SourceHealthDigest.Compose</c> tests cannot reach: the day claim,
/// its dedup, and what happens to the claim on each delivery outcome.
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
        var notifier = Notifier(AlertDeliveryResult.Sent);

        var sent = await Service(notifier).SendIfDueAsync(TestContext.Current.CancellationToken);

        sent.Should().BeTrue();
        await notifier.Received(1).NotifyAsync(Arg.Any<AlertMessage>(), Arg.Any<CancellationToken>());
        (await ClaimedOnAsync()).Should().Be(Today);
    }

    [Fact]
    public async Task Sends_the_digest_without_a_message_id_so_a_later_day_is_never_collapsed_into_today()
    {
        await SeedAsync(lastDigestOn: null);
        var notifier = Notifier(AlertDeliveryResult.Sent);

        await Service(notifier).SendIfDueAsync(TestContext.Current.CancellationToken);

        await notifier
            .Received(1)
            .NotifyAsync(
                Arg.Is<AlertMessage>(m =>
                    m.MessageId == null && m.SlackFenceClaimId == null && m.Subject.Contains("degraded")
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Sends_nothing_on_a_second_call_the_same_day()
    {
        await SeedAsync(lastDigestOn: null);
        var notifier = Notifier(AlertDeliveryResult.Sent);

        await Service(notifier).SendIfDueAsync(TestContext.Current.CancellationToken);
        var second = await Service(notifier).SendIfDueAsync(TestContext.Current.CancellationToken);

        second.Should().BeFalse();
        await notifier.Received(1).NotifyAsync(Arg.Any<AlertMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keeps_the_day_claimed_when_delivery_throws()
    {
        // The deliberate inversion of BudgetAlertClaim: a digest lost to an SMTP outage costs
        // a day's notice and heals tomorrow, whereas releasing the claim would re-send on
        // every worker pass and restart for the remainder of the day.
        await SeedAsync(lastDigestOn: null);
        var notifier = Substitute.For<IAlertNotifier>();
        notifier
            .NotifyAsync(Arg.Any<AlertMessage>(), Arg.Any<CancellationToken>())
            .Returns<Task<AlertDeliveryResult>>(_ => throw new InvalidOperationException("smtp is down"));

        var sent = await Service(notifier).SendIfDueAsync(TestContext.Current.CancellationToken);

        sent.Should().BeFalse();
        (await ClaimedOnAsync()).Should().Be(Today, "a failed send must not release the day");
    }

    [Fact]
    public async Task Keeps_the_day_claimed_when_a_channel_reports_a_transient_failure()
    {
        await SeedAsync(lastDigestOn: null);
        var notifier = Notifier(AlertDeliveryResult.Failed);

        var sent = await Service(notifier).SendIfDueAsync(TestContext.Current.CancellationToken);

        sent.Should().BeFalse();
        (await ClaimedOnAsync()).Should().Be(Today, "a channel that reached the network may have partially delivered");
    }

    [Fact]
    public async Task Sends_nothing_and_consumes_no_claim_when_no_source_is_degraded()
    {
        await SeedAsync(lastDigestOn: null, degraded: false);
        var notifier = Notifier(AlertDeliveryResult.Sent);

        var sent = await Service(notifier).SendIfDueAsync(TestContext.Current.CancellationToken);

        sent.Should().BeFalse();
        await notifier.DidNotReceive().NotifyAsync(Arg.Any<AlertMessage>(), Arg.Any<CancellationToken>());
        (await ClaimedOnAsync()).Should().BeNull("a quiet day must not spend the claim");
    }

    [Fact]
    public async Task Restores_the_claim_when_no_channel_is_configured()
    {
        // Whether anything can deliver is only known once a channel has been asked, so the
        // claim is taken first and given back here. Without the restore, configuring a channel
        // an hour after the worker ran would cost a day's notice for no reason — and the
        // digest's whole purpose is not losing notice.
        await SeedAsync(lastDigestOn: null);
        var notifier = Notifier(AlertDeliveryResult.NoRecipientConfigured);

        var sent = await Service(notifier).SendIfDueAsync(TestContext.Current.CancellationToken);

        sent.Should().BeFalse();
        (await ClaimedOnAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Restores_the_previous_claim_date_rather_than_clearing_it()
    {
        // Restoring to null unconditionally would re-arm a row that had legitimately sent
        // yesterday, which the dedup predicate reads as "never sent".
        var yesterday = Today.PlusDays(-1);
        await SeedAsync(lastDigestOn: yesterday);
        var notifier = Notifier(AlertDeliveryResult.NoRecipientConfigured);

        await Service(notifier).SendIfDueAsync(TestContext.Current.CancellationToken);

        (await ClaimedOnAsync()).Should().Be(yesterday);
    }

    [Fact]
    public async Task Does_not_overwrite_a_competing_writer_that_moved_the_claim_mid_delivery()
    {
        // The restore reads previousDigestOn before taking the claim, so on paper it could
        // write a stale value back. It cannot, because the restore is conditional on the
        // column still holding THIS instance's claim — and no other writer can set it to
        // today without having taken the claim itself. Proven rather than argued: the
        // notifier callback runs between the claim and the restore, which is the only window
        // where an interleaved write is possible at all.
        var yesterday = Today.PlusDays(-1);
        await SeedAsync(lastDigestOn: yesterday);
        var intruderDate = Today.PlusDays(3);

        var notifier = Substitute.For<IAlertNotifier>();
        notifier
            .NotifyAsync(Arg.Any<AlertMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await using var competing = new AiObservatoryDbContext(
                    new DbContextOptionsBuilder<AiObservatoryDbContext>()
                        .UseNpgsql(_connectionString, options => options.UseNodaTime())
                        .Options
                );
                await competing
                    .NotificationSettings.Where(s => s.Id == NotificationSettings.SingletonId)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastSourceHealthDigestOn, intruderDate));
                return AlertDeliveryResult.NoRecipientConfigured;
            });

        await Service(notifier).SendIfDueAsync(TestContext.Current.CancellationToken);

        (await ClaimedOnAsync())
            .Should()
            .Be(intruderDate, "the restore must leave a value it did not write, not stamp yesterday over it");
    }

    private static IAlertNotifier Notifier(AlertDeliveryResult result)
    {
        var notifier = Substitute.For<IAlertNotifier>();
        notifier.NotifyAsync(Arg.Any<AlertMessage>(), Arg.Any<CancellationToken>()).Returns(result);
        return notifier;
    }

    private SourceHealthDigestService Service(IAlertNotifier notifier) =>
        new(_db, notifier, new FakeClock(Now), NullLogger<SourceHealthDigestService>.Instance);

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
