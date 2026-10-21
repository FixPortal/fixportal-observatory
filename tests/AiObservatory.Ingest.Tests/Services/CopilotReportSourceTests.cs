using System.Net;
using System.Text;
using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Ingest.Services.Copilot;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NodaTime.Text;
using Npgsql;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

[Trait("Category", "Integration")]
public sealed class CopilotReportSourceTests : IAsyncLifetime
{
    private static readonly Instant FirstObservedAt = Instant.FromUtc(2026, 8, 22, 10, 15, 30);
    private static readonly Instant AcquisitionAt = Instant.FromUtc(2026, 8, 22, 12, 0);
    private readonly ICopilotReportClient _client = Substitute.For<ICopilotReportClient>();
    private string _connectionString = null!;
    private DbContextOptions<AiObservatoryDbContext> _options = null!;
    private AiObservatoryDbContext _db = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConnection =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_copilot_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connectionString, options => options.UseNodaTime())
            .Options;
        _db = new AiObservatoryDbContext(_options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _db.Database.EnsureDeletedAsync();
        }
        finally
        {
            await _db.DisposeAsync();
        }
    }

    [Fact]
    public async Task IngestAsync_PersistsOnlyRequestedDaysWithoutFabricatingUsageOrSpend()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.CopilotDailyReports.Add(Entity(new LocalDate(2026, 7, 1), "history", 9));
        await _db.SaveChangesAsync(ct);
        _client
            .GetLatestOrganizationReportAsync(Arg.Any<CancellationToken>())
            .Returns(
                new[]
                {
                    Record(new LocalDate(2026, 8, 20), 20, FirstObservedAt),
                    Record(new LocalDate(2026, 8, 21), 42, null),
                }
            );
        var sut = Source(new FakeClock(AcquisitionAt));

        var result = await sut.IngestAsync(new LocalDate(2026, 8, 21), new LocalDate(2026, 8, 21), ct);

        var rows = await _db.CopilotDailyReports.AsNoTracking().OrderBy(row => row.Day).ToListAsync(ct);
        rows.Should().HaveCount(2);
        rows[0].ReportKey.Should().Be("history");
        rows[1]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Day = new LocalDate(2026, 8, 21),
                    SourceId = UsageSourceIds.CopilotOrgReport,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Subscription,
                    CostBasis = CostBasis.None,
                    DailyActiveUsers = (int?)2,
                    WeeklyActiveUsers = (int?)7,
                    MonthlyActiveUsers = (int?)19,
                    UserInitiatedInteractionCount = 42L,
                    CodeGenerationActivityCount = 36L,
                    CodeAcceptanceActivityCount = 24L,
                    ObservedAt = AcquisitionAt,
                }
            );
        JsonEquals(rows[1].RawPayload, "{\"evidence\":42}").Should().BeTrue();
        result.LatestObservationAt.Should().Be(AcquisitionAt);
        (await _db.UsageEvents.AsNoTracking().CountAsync(ct)).Should().Be(0);
        (await _db.DailyAggregates.AsNoTracking().CountAsync(ct)).Should().Be(0);
        (await _db.BillingObservations.AsNoTracking().CountAsync(ct)).Should().Be(0);
        (await _db.SpendEntries.AsNoTracking().CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task IngestAsync_AtomicallyCorrectsStableIdentityAndTreatsExactReplayAsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var day = new LocalDate(2026, 8, 21);
        _client.GetLatestOrganizationReportAsync(Arg.Any<CancellationToken>()).Returns([Record(day, 20, null)]);
        await Source(new FakeClock(AcquisitionAt)).IngestAsync(day, day, ct);
        var original = await _db.CopilotDailyReports.AsNoTracking().SingleAsync(ct);

        var replayResult = await Source(new FakeClock(AcquisitionAt.Plus(Duration.FromDays(1))))
            .IngestAsync(day, day, ct);

        var replay = await _db.CopilotDailyReports.AsNoTracking().SingleAsync(ct);
        replay.Should().BeEquivalentTo(original);
        replayResult.LatestObservationAt.Should().Be(original.ObservedAt);
        var correctedAt = FirstObservedAt.Plus(Duration.FromDays(1));
        _client.GetLatestOrganizationReportAsync(Arg.Any<CancellationToken>()).Returns([Record(day, 99, correctedAt)]);

        var result = await Source(new FakeClock(AcquisitionAt.Plus(Duration.FromDays(2)))).IngestAsync(day, day, ct);

        var corrected = await _db.CopilotDailyReports.AsNoTracking().SingleAsync(ct);
        corrected.Id.Should().Be(original.Id);
        corrected.ReportKey.Should().Be(original.ReportKey);
        corrected.UserInitiatedInteractionCount.Should().Be(99);
        JsonEquals(corrected.RawPayload, "{\"evidence\":99}").Should().BeTrue();
        corrected.ObservedAt.Should().Be(correctedAt);
        result.LatestObservationAt.Should().Be(correctedAt);
    }

    [Fact]
    public async Task IngestAsync_ReturnsGreatestObservationAmongThePersistedRange()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new LocalDate(2026, 8, 20);
        var second = first.PlusDays(1);
        _client
            .GetLatestOrganizationReportAsync(Arg.Any<CancellationToken>())
            .Returns([
                Record(first, 1, FirstObservedAt),
                Record(second, 2, FirstObservedAt.Plus(Duration.FromHours(2))),
            ]);

        var result = await Source(new FakeClock(AcquisitionAt)).IngestAsync(first, second, ct);

        result.LatestObservationAt.Should().Be(FirstObservedAt.Plus(Duration.FromHours(2)));
    }

    [Fact]
    public async Task IngestAsync_UsesOneAcquisitionInstantForEveryTimestampLessRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new LocalDate(2026, 8, 20);
        var second = first.PlusDays(1);
        _client
            .GetLatestOrganizationReportAsync(Arg.Any<CancellationToken>())
            .Returns([Record(first, 1, null), Record(second, 2, null)]);
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(AcquisitionAt, AcquisitionAt.Plus(Duration.FromHours(1)));

        await Source(clock).IngestAsync(first, second, ct);

        var observed = await _db.CopilotDailyReports.AsNoTracking().Select(row => row.ObservedAt).ToListAsync(ct);
        observed.Should().HaveCount(2).And.OnlyContain(value => value == AcquisitionAt);
    }

    [Fact]
    public async Task IngestAsync_TwoDayCorrectionUpdatesOnlyTheChangedDaysEvidenceAndObservation()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstDay = new LocalDate(2026, 8, 20);
        var secondDay = firstDay.PlusDays(1);
        var firstReport = await ParseAsync(TwoDayWrapper(10, 20), ct);
        var correctedReport = await ParseAsync(TwoDayWrapper(99, 20), ct);
        _client.GetLatestOrganizationReportAsync(Arg.Any<CancellationToken>()).Returns(firstReport, correctedReport);
        await Source(new FakeClock(AcquisitionAt)).IngestAsync(firstDay, secondDay, ct);

        await Source(new FakeClock(AcquisitionAt.Plus(Duration.FromDays(1)))).IngestAsync(firstDay, secondDay, ct);

        var rows = await _db.CopilotDailyReports.AsNoTracking().OrderBy(row => row.Day).ToListAsync(ct);
        rows[0].UserInitiatedInteractionCount.Should().Be(99);
        rows[0].ObservedAt.Should().Be(AcquisitionAt.Plus(Duration.FromDays(1)));
        rows[1].UserInitiatedInteractionCount.Should().Be(20);
        rows[1].ObservedAt.Should().Be(AcquisitionAt);
        EvidenceDay(rows[0].RawPayload).Should().Be(firstDay);
        EvidenceDay(rows[1].RawPayload).Should().Be(secondDay);
    }

    [Fact]
    public async Task IngestAsync_ClientFailureLeavesDatabaseUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var existing = Entity(new LocalDate(2026, 7, 1), "history", 9);
        _db.CopilotDailyReports.Add(existing);
        await _db.SaveChangesAsync(ct);
        _client
            .GetLatestOrganizationReportAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CopilotDailyReportRecord>>>(_ => throw new InvalidDataException("bad report"));

        var act = () =>
            Source(new FakeClock(AcquisitionAt)).IngestAsync(new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 21), ct);

        await act.Should().ThrowAsync<InvalidDataException>();
        (await _db.CopilotDailyReports.AsNoTracking().SingleAsync(ct)).Id.Should().Be(existing.Id);
    }

    [Fact]
    public async Task IngestAsync_DatabaseFailureRollsBackTheCompleteSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new LocalDate(2026, 8, 20);
        var invalid = Record(first.PlusDays(1), 2, FirstObservedAt) with { RawJson = "not-json" };
        _client
            .GetLatestOrganizationReportAsync(Arg.Any<CancellationToken>())
            .Returns([Record(first, 1, FirstObservedAt), invalid]);

        var act = () => Source(new FakeClock(AcquisitionAt)).IngestAsync(first, first.PlusDays(1), ct);

        await act.Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Entries().Should().BeEmpty();
        (await _db.CopilotDailyReports.AsNoTracking().CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task IngestAsync_PropagatesCancellationBeforePersistence()
    {
        using var cancellation = new CancellationTokenSource();
        _client
            .GetLatestOrganizationReportAsync(cancellation.Token)
            .Returns(_ =>
            {
                cancellation.Cancel();
                return new[] { Record(new LocalDate(2026, 8, 21), 1, null) };
            });

        var act = () =>
            Source(new FakeClock(AcquisitionAt))
                .IngestAsync(new LocalDate(2026, 8, 21), new LocalDate(2026, 8, 21), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await _db.CopilotDailyReports.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task PostgreSqlEnforcesStableIdentityJsonAndNonnegativeFactsWhileAllowingNullableActiveCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var nullable = Entity(new LocalDate(2026, 8, 20), "nullable", 1);
        nullable.DailyActiveUsers = null;
        nullable.WeeklyActiveUsers = null;
        nullable.MonthlyActiveUsers = null;
        _db.CopilotDailyReports.Add(nullable);
        await _db.SaveChangesAsync(ct);
        var duplicate = Entity(nullable.Day, nullable.ReportKey, 2);
        _db.CopilotDailyReports.Add(duplicate);

        var duplicateWrite = () => _db.SaveChangesAsync(ct);

        await duplicateWrite.Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Clear();
        var negative = Entity(new LocalDate(2026, 8, 21), "negative", -1);
        _db.CopilotDailyReports.Add(negative);

        var negativeWrite = () => _db.SaveChangesAsync(ct);

        await negativeWrite.Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Clear();
        var rawType = await _db
            .Database.SqlQueryRaw<string>(
                """
                SELECT data_type AS "Value"
                FROM information_schema.columns
                WHERE table_name = 'CopilotDailyReports' AND column_name = 'RawPayload'
                """
            )
            .SingleAsync(ct);
        rawType.Should().Be("jsonb");
    }

    private CopilotReportSource Source(IClock clock) =>
        new(_client, _db, clock, NullLogger<CopilotReportSource>.Instance);

    private static bool JsonEquals(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }

    private static LocalDate EvidenceDay(string rawPayload)
    {
        using var document = JsonDocument.Parse(rawPayload);
        var totals = document.RootElement.GetProperty("day_totals");
        totals.GetArrayLength().Should().Be(1);
        return LocalDatePattern.Iso.Parse(totals[0].GetProperty("day").GetString()!).Value;
    }

    private static async Task<IReadOnlyList<CopilotDailyReportRecord>> ParseAsync(
        string report,
        CancellationToken cancellationToken
    )
    {
        using var descriptorHttp = new HttpClient(
            new StaticHandler(
                """
                {"download_links":["https://reports.example/report.ndjson"],"report_start_day":"2026-07-25","report_end_day":"2026-08-21"}
                """,
                "application/json"
            )
        )
        {
            BaseAddress = new Uri("https://api.github.com"),
        };
        using var downloadHttp = new HttpClient(new StaticHandler(report + "\n", "application/x-ndjson"));
        return await new CopilotReportClient(
            descriptorHttp,
            downloadHttp,
            "FixPortal"
        ).GetLatestOrganizationReportAsync(cancellationToken);
    }

    private static string TwoDayWrapper(long firstInteractions, long secondInteractions) =>
        JsonSerializer.Serialize(
            new
            {
                report_start_day = "2026-07-25",
                report_end_day = "2026-08-21",
                enterprise_id = "123456",
                organization_id = "987654",
                etl_id = "green",
                day_partition = "2026-08-21",
                entity_id_partition = 987654,
                day_totals = new[]
                {
                    DayTotal(new LocalDate(2026, 8, 20), firstInteractions),
                    DayTotal(new LocalDate(2026, 8, 21), secondInteractions),
                },
            }
        );

    private static object DayTotal(LocalDate day, long interactions) =>
        new
        {
            day = $"{day:yyyy-MM-dd}",
            enterprise_id = "123456",
            organization_id = "987654",
            daily_active_users = 2,
            weekly_active_users = 7,
            monthly_active_users = 19,
            user_initiated_interaction_count = interactions,
            code_generation_activity_count = 36,
            code_acceptance_activity_count = 24,
        };

    private static CopilotDailyReportRecord Record(LocalDate day, long interactions, Instant? observedAt) =>
        new(day, "987654", 2, 7, 19, interactions, 36, 24, $"{{\"evidence\":{interactions}}}", observedAt);

    private static CopilotDailyReport Entity(LocalDate day, string reportKey, long interactions) =>
        new()
        {
            Day = day,
            ReportKey = reportKey,
            DailyActiveUsers = 1,
            WeeklyActiveUsers = 1,
            MonthlyActiveUsers = 1,
            UserInitiatedInteractionCount = interactions,
            CodeGenerationActivityCount = 1,
            CodeAcceptanceActivityCount = 1,
            RawPayload = "{}",
            ObservedAt = FirstObservedAt,
        };

    private sealed class StaticHandler(string content, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, mediaType),
                }
            );
    }
}
