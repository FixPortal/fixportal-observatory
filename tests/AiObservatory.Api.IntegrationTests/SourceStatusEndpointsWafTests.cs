using System.Net;
using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class SourceStatusEndpointsWafTests(AiObservatoryApiFactory factory)
{
    [Fact]
    public async Task GetSourceStatus_WhenUnauthenticated_ReturnsUnauthorized()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/api/sources/status", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSourceStatus_ReturnsOrderedWireContractWithStoredErrorAndNullTimestamps()
    {
        var prefix = $"source-status-{Guid.NewGuid():N}";
        var sourceIds = new
        {
            NotConfigured = $"{prefix}-a-not-configured",
            Configured = $"{prefix}-b-configured",
            Fresh = $"{prefix}-c-fresh",
            Stale = $"{prefix}-d-stale",
            Failing = $"{prefix}-e-failing",
            Unavailable = $"{prefix}-f-unavailable",
        };
        var storedError = "Request timed out after credentials were removed";

        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var now = services.GetRequiredService<IClock>().GetCurrentInstant();
            var db = services.GetRequiredService<AiObservatoryDbContext>();
            db.SourceSyncStates.AddRange(
                State(sourceIds.Unavailable, configured: true, available: false, failures: 1, lastSuccessAt: now),
                State(
                    sourceIds.Failing,
                    configured: true,
                    available: null,
                    failures: 1,
                    lastSuccessAt: now,
                    lastError: storedError
                ),
                State(
                    sourceIds.Stale,
                    configured: true,
                    available: true,
                    failures: 0,
                    lastSuccessAt: now.Minus(Duration.FromSeconds(121))
                ),
                State(sourceIds.Fresh, configured: true, available: true, failures: 0, lastSuccessAt: now),
                State(sourceIds.Configured, configured: true, available: true, failures: 0, lastSuccessAt: null),
                State(sourceIds.NotConfigured, configured: false, available: false, failures: 1, lastSuccessAt: now)
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateReadOnlyClient();
        var response = await client.GetAsync("/api/sources/status", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var rows = document.RootElement.EnumerateArray().ToArray();
        var addedRows = rows.Where(row =>
                row.GetProperty("sourceId").GetString()!.StartsWith(prefix, StringComparison.Ordinal)
            )
            .ToArray();

        addedRows.Select(row => row.GetProperty("sourceId").GetString()).Should().BeInAscendingOrder();
        addedRows
            .Select(row => row.GetProperty("status").GetString())
            .Should()
            .Equal("notConfigured", "configured", "fresh", "stale", "failing", "unavailable");

        var configured = addedRows.Single(row => row.GetProperty("sourceId").GetString() == sourceIds.Configured);
        configured
            .EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .Equal(
                "sourceId",
                "status",
                "isConfigured",
                "lastAttemptAt",
                "lastSuccessAt",
                "latestObservationAt",
                "consecutiveFailureCount",
                "lastError"
            );
        configured.GetProperty("isConfigured").GetBoolean().Should().BeTrue();
        configured.GetProperty("lastAttemptAt").ValueKind.Should().Be(JsonValueKind.Null);
        configured.GetProperty("lastSuccessAt").ValueKind.Should().Be(JsonValueKind.Null);
        configured.GetProperty("latestObservationAt").ValueKind.Should().Be(JsonValueKind.Null);
        configured.GetProperty("consecutiveFailureCount").GetInt32().Should().Be(0);
        configured.GetProperty("lastError").ValueKind.Should().Be(JsonValueKind.Null);

        var failing = addedRows.Single(row => row.GetProperty("sourceId").GetString() == sourceIds.Failing);
        failing.GetProperty("lastError").GetString().Should().Be(storedError);
    }

    private static SourceSyncState State(
        string sourceId,
        bool configured,
        bool? available,
        int failures,
        Instant? lastSuccessAt,
        string? lastError = null
    ) =>
        new()
        {
            SourceId = sourceId,
            IsConfigured = configured,
            IsAvailable = available,
            ExpectedRefreshIntervalSeconds = 60,
            LastAttemptAt = lastSuccessAt,
            LastSuccessAt = lastSuccessAt,
            LatestObservationAt = lastSuccessAt,
            ConsecutiveFailureCount = failures,
            LastError = lastError,
        };
}
