using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Data.Security;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Repositories;

[Trait("Category", "Integration")]
[Collection("SlackWebhookProtectionKey")]
public class NotificationSettingsRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IUsageRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConn =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connStr = new NpgsqlConnectionStringBuilder(baseConn)
        {
            Database = $"aiobs_test_notification_settings_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new UsageRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ctx is not null && _connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
            {
                await _ctx.Database.EnsureDeletedAsync();
            }
        }
        finally
        {
            if (_ctx is not null)
            {
                await _ctx.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task GetNotificationSettings_returns_null_when_no_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _repo.GetNotificationSettingsAsync(ct)).Should().BeNull();
    }

    [Fact]
    public async Task GetNotificationSettings_returns_the_singleton_row()
    {
        var ct = TestContext.Current.CancellationToken;
        _ctx.NotificationSettings.Add(
            new NotificationSettings
            {
                AlertEmailTo = "alerts@example.com",
                SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
            }
        );
        await _ctx.SaveChangesAsync(ct);

        var settings = await _repo.GetNotificationSettingsAsync(ct);

        settings.Should().NotBeNull();
        settings.AlertEmailTo.Should().Be("alerts@example.com");
        settings.SlackWebhookUrl.Should().Be("https://hooks.slack.com/services/T0/B0/xyz");
    }

    [Fact]
    public async Task SlackWebhookUrl_is_encrypted_at_rest_when_a_protection_key_is_set()
    {
        var ct = TestContext.Current.CancellationToken;
        Environment.SetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable, "integration-test-key");
        try
        {
            const string webhookUrl = "https://hooks.slack.com/services/T0/B0/secret";
            _ctx.NotificationSettings.Add(
                new NotificationSettings
                {
                    SlackWebhookUrl = webhookUrl,
                    UpdatedAt = Instant.FromUtc(2026, 8, 31, 0, 0),
                }
            );
            await _ctx.SaveChangesAsync(ct);

            await using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT \"SlackWebhookUrl\" FROM \"NotificationSettings\"", conn);
            var stored = (string?)await cmd.ExecuteScalarAsync(ct);

            stored.Should().StartWith(SlackWebhookProtector.EncryptedPrefix);
            stored.Should().NotContain("hooks.slack.com");

            _ctx.ChangeTracker.Clear();
            (await _repo.GetNotificationSettingsAsync(ct))!.SlackWebhookUrl.Should().Be(webhookUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task GetNotificationSettings_materialises_with_a_sentinel_when_the_protection_key_is_lost()
    {
        // M13: reading an encrypted row back without the key used to throw inside EF
        // materialisation, taking the whole singleton row (and email alerting with it) down
        // and leaving "clear the value" unreachable through the API. The row must still
        // materialise -- email intact, the webhook degraded to the sentinel the Slack
        // notifier refuses -- and the stored ciphertext must survive so a restored key
        // recovers the URL.
        var ct = TestContext.Current.CancellationToken;
        const string webhookUrl = "https://hooks.slack.com/services/T0/B0/secret";
        Environment.SetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable, "integration-test-key");
        try
        {
            _ctx.NotificationSettings.Add(
                new NotificationSettings
                {
                    AlertEmailTo = "alerts@example.com",
                    SlackWebhookUrl = webhookUrl,
                    UpdatedAt = Instant.FromUtc(2026, 9, 8, 0, 0),
                }
            );
            await _ctx.SaveChangesAsync(ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable, null);
        }

        _ctx.ChangeTracker.Clear();

        var settings = await _repo.GetNotificationSettingsAsync(ct);

        settings.Should().NotBeNull();
        settings.AlertEmailTo.Should().Be("alerts@example.com");
        // Had the value been stored as plaintext the read would return the URL itself, so
        // reaching the sentinel also proves the row really was encrypted at rest.
        settings.SlackWebhookUrl.Should().Be(SlackWebhookProtector.UndecryptableSentinel);
    }

    [Fact]
    public async Task NotificationSettings_rejects_a_row_with_a_non_singleton_id()
    {
        var ct = TestContext.Current.CancellationToken;
        _ctx.NotificationSettings.Add(
            new NotificationSettings
            {
                Id = Guid.NewGuid(),
                AlertEmailTo = "stray@example.com",
                UpdatedAt = Instant.FromUtc(2026, 8, 31, 0, 0),
            }
        );

        var save = () => _ctx.SaveChangesAsync(ct);

        await save.Should().ThrowAsync<DbUpdateException>();
    }
}

// SLACK_WEBHOOK_PROTECTION_KEY is process-wide, and these two test classes are on opposite
// sides of it: this one sets the variable to prove at-rest encryption, while
// SlackWebhookProtectorTests asserts no-key behaviour. Serialised into one collection that
// never runs in parallel with any other, so a test holding the key set can never overlap
// one asserting it is unset (same house idiom as Ingest.Tests' ProviderPollingWorker).
[CollectionDefinition("SlackWebhookProtectionKey", DisableParallelization = true)]
public class SlackWebhookProtectionKeyCollection;
