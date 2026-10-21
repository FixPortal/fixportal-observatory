using AiObservatory.Api.Endpoints;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Api.IntegrationTests;

// Requires TEST_DB_CONNECTION env var pointing at a real PostgreSQL instance.
[Trait("Category", "Integration")]
public class ActivityEndpointDataTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConn =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connStr = new NpgsqlConnectionStringBuilder(baseConn)
        {
            Database = "aiobs_test_activity_api",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null && _connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
        {
            await _ctx.Database.EnsureDeletedAsync();
        }
        if (_ctx is not null)
        {
            await _ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeleteDisallowedProjectSessionsAsync_DeletesOnlyNonAllowlistedProjects()
    {
        var ct = TestContext.Current.CancellationToken;
        _ctx.ClaudeActivitySessions.AddRange(
            Session("allowed-legacy-owner", "fix-portal"),
            Session("allowed-legacy-repo", "fix-portal/example"),
            // The live org — the SQL predicate must match it, and must match it case-sensitively.
            Session("allowed-org-repo", "FixPortal/example"),
            Session("wrong-prefix", "fix-portal-other/example"),
            Session("wrong-case", "FIX-PORTAL/example"),
            Session("retired-personal-owner", "chris-fixportal/tooling")
        );
        await _ctx.SaveChangesAsync(ct);

        var deleted = await ActivityEndpoints.DeleteDisallowedProjectSessionsAsync(
            _ctx,
            ["FixPortal", "fix-portal"],
            ct
        );

        deleted.Should().Be(3);
        var remaining = await _ctx
            .ClaudeActivitySessions.OrderBy(s => s.SessionId)
            .Select(s => s.SessionId)
            .ToListAsync(ct);
        remaining.Should().Equal("allowed-legacy-owner", "allowed-legacy-repo", "allowed-org-repo");
    }

    private static ClaudeActivitySession Session(string id, string project) =>
        new()
        {
            SessionId = id,
            Project = project,
            StartedAt = Instant.FromUtc(2026, 7, 1, 9, 0),
            LastSeenAt = Instant.FromUtc(2026, 7, 1, 10, 0),
            ActiveSeconds = 3600,
            IngestedAt = Instant.FromUtc(2026, 7, 1, 10, 0),
        };
}
