using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Security;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class NotificationSettingsEndpointsWafTests(AiObservatoryApiFactory factory)
{
    [Fact]
    public async Task Get_WhenNothingConfigured_ReturnsAllUnconfigured()
    {
        var ct = TestContext.Current.CancellationToken;

        // Singleton row, shared DB across every test in this class: guarantee a clean slate
        // rather than relying on execution order (xUnit does not promise declaration order).
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await db.NotificationSettings.ExecuteDeleteAsync(ct);
        }

        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetFromJsonAsync<JsonElement>("/api/notification-settings", ct);

        response.GetProperty("emailConfigured").GetBoolean().Should().BeFalse();
        response.GetProperty("emailMasked").ValueKind.Should().Be(JsonValueKind.Null);
        response.GetProperty("slackConfigured").GetBoolean().Should().BeFalse();
        response.GetProperty("slackMasked").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Put_SetsEmailWithoutTouchingSlack_AndMasksTheResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { slackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz" },
            ct
        );

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = "chris@fixportal.org" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("emailConfigured").GetBoolean().Should().BeTrue();
        body.GetProperty("emailMasked").GetString().Should().Be("ch***@fixportal.org");
        // The earlier PUT's Slack value must survive an edit that only touched email.
        body.GetProperty("slackConfigured").GetBoolean().Should().BeTrue();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var row = await db.NotificationSettings.SingleAsync(ct);
        row.AlertEmailTo.Should().Be("chris@fixportal.org");
        row.SlackWebhookUrl.Should().Be("https://hooks.slack.com/services/T0/B0/xyz");
    }

    [Fact]
    public async Task Put_WithNullEmail_ClearsItWithoutTouchingSlack()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        await client.PutAsJsonAsync(
            "/api/notification-settings",
            new
            {
                alertEmailTo = "chris@fixportal.org",
                slackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
            },
            ct
        );

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = (string?)null },
            ct
        );

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("emailConfigured").GetBoolean().Should().BeFalse();
        body.GetProperty("slackConfigured").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Put_WithNullSlackWebhook_ClearsAnUndecryptableValue()
    {
        // M13: a row whose stored webhook was encrypted under a since-lost protection key used
        // to throw inside EF materialisation on EVERY read -- the GET, and the PUT's load that
        // must happen before ApplyFields could clear the value -- so the documented remedy
        // needed hand-run SQL. The read path now degrades to a sentinel, so the row loads and
        // PUT clears it. Safe to mutate the process-wide key here: this whole assembly is
        // serialised (AssemblyInfo carries Parallelization(Mode = None)).
        var ct = TestContext.Current.CancellationToken;
        Environment.SetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable, "waf-test-key");
        try
        {
            await using var seedScope = factory.Services.CreateAsyncScope();
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await seedDb.NotificationSettings.ExecuteDeleteAsync(ct);
            seedDb.NotificationSettings.Add(
                new NotificationSettings
                {
                    AlertEmailTo = "alerts@example.com",
                    SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/secret",
                    UpdatedAt = Instant.FromUtc(2026, 9, 8, 0, 0),
                }
            );
            await seedDb.SaveChangesAsync(ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable, null);
        }

        using var client = factory.CreateAdminClient();

        // The undecryptable row must materialise for the GET too: still reported as
        // configured (a webhook IS stored, just unreadable), email side intact.
        var get = await client.GetFromJsonAsync<JsonElement>("/api/notification-settings", ct);
        get.GetProperty("slackConfigured").GetBoolean().Should().BeTrue();
        get.GetProperty("emailConfigured").GetBoolean().Should().BeTrue();

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { slackWebhookUrl = (string?)null },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var row = await db.NotificationSettings.SingleAsync(ct);
        row.SlackWebhookUrl.Should().BeNull();
        row.AlertEmailTo.Should().Be("alerts@example.com");
    }

    [Fact]
    public async Task Put_WithMalformedEmail_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = "not-an-email" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_WithNonSlackWebhookUrl_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { slackWebhookUrl = "https://evil.example.com/steal" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("https://hooks.slack.com@attacker.example/services/x")] // userinfo trick: real host is attacker.example
    [InlineData("https://attacker.example/services/x?redirect=hooks.slack.com")]
    [InlineData("https://hooks.slack.com.attacker.example/services/x")] // subdomain-suffix trick
    [InlineData("http://hooks.slack.com/services/x")] // not https
    [InlineData("https://hooks.slack.com:8443/services/x")] // not the default port
    public async Task Put_WithSpoofedSlackHost_ReturnsBadRequestAndIsNotPersisted(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/notification-settings", ct);

        var response = await client.PutAsJsonAsync("/api/notification-settings", new { slackWebhookUrl = url }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/notification-settings", ct);
        after
            .GetProperty("slackConfigured")
            .GetBoolean()
            .Should()
            .Be(before.GetProperty("slackConfigured").GetBoolean());
        after.GetProperty("slackMasked").ToString().Should().Be(before.GetProperty("slackMasked").ToString());
    }

    [Fact]
    public async Task Put_WithNonStringAlertEmailTo_ReturnsBadRequestNotServerError()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync("/api/notification-settings", new { alertEmailTo = 12345 }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task Put_WithNonObjectBody_ReturnsBadRequestNotServerError(string json)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PutAsync("/api/notification-settings", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_ConcurrentFirstWritesToAnEmptyTable_BothSucceedAndBothEditsSurvive()
    {
        var ct = TestContext.Current.CancellationToken;

        // Empty table, like the two other WAF tests that manage their own slate: this test
        // targets the insert race specifically, so it must start from no row rather than
        // relying on ordering against sibling tests sharing the same singleton row.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await db.NotificationSettings.ExecuteDeleteAsync(ct);
        }

        using var emailClient = factory.CreateAdminClient();
        using var slackClient = factory.CreateAdminClient();

        // Force the insert race rather than hope Task.WhenAll overlaps (it usually does not,
        // which is how this test used to pass with the reload-and-reapply removed). A SHARE
        // table lock lets both PUTs' SELECTs through — both see no row and decide to insert —
        // while blocking both INSERTs, so both requests are parked past the read-before-insert
        // point before either can write. Releasing the lock then serializes the two INSERTs:
        // the winner commits, the loser unique-violates and must reload-and-reapply (Finding
        // 4), or this test sees its 500. Different fields keep the final-state assertion
        // deterministic regardless of which write wins.
        HttpResponseMessage[] responses;
        await using (var lockScope = factory.Services.CreateAsyncScope())
        {
            var lockDb = lockScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await using var tableLock = await lockDb.Database.BeginTransactionAsync(ct);
            await lockDb.Database.ExecuteSqlRawAsync("""LOCK TABLE "NotificationSettings" IN SHARE MODE""", ct);

            var emailTask = emailClient.PutAsJsonAsync(
                "/api/notification-settings",
                new { alertEmailTo = "chris@fixportal.org" },
                ct
            );
            var slackTask = slackClient.PutAsJsonAsync(
                "/api/notification-settings",
                new { slackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz" },
                ct
            );

            await WaitForBothInsertsBlockedAsync(ct);
            await tableLock.RollbackAsync(ct);

            responses = await Task.WhenAll(emailTask, slackTask);
        }

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);

        await using var scope2 = factory.Services.CreateAsyncScope();
        var verifyDb = scope2.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var rows = await verifyDb.NotificationSettings.ToListAsync(ct);
        rows.Should().ContainSingle("the insert race must converge on one row, not two");
        rows[0].AlertEmailTo.Should().Be("chris@fixportal.org");
        rows[0].SlackWebhookUrl.Should().Be("https://hooks.slack.com/services/T0/B0/xyz");
    }

    /// <summary>
    /// Waits until BOTH first-write PUTs are parked in a PostgreSQL lock wait on their INSERT —
    /// the deterministic proof that both passed the read-before-insert point against an empty
    /// table, replacing a bare Task.WhenAll that let the two requests run sequentially.
    /// </summary>
    private async Task WaitForBothInsertsBlockedAsync(CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var blocked = await db
                .Database.SqlQueryRaw<int>(
                    """
                    SELECT COUNT(*)::int AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                        AND pid <> pg_backend_pid()
                        AND wait_event_type = 'Lock'
                        AND query LIKE '%INSERT INTO "NotificationSettings"%'
                    """
                )
                .SingleAsync(ct);
            if (blocked >= 2)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        throw new TimeoutException("Both first-write PUTs did not reach the expected INSERT lock wait.");
    }

    [Fact]
    public async Task Put_WithoutAdminKey_ReturnsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateReadOnlyClient();

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = "chris@fixportal.org" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
