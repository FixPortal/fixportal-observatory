using System.Net;
using System.Text.Json;
using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public class SlackAlertNotifierTests
{
    // ponytail: StubHttpMessageHandler (shared with FxRateProviderTests/GitHubBillingClientTests)
    // takes a fixed (status, body) pair and only records request URIs, not bodies or
    // per-call responses -- doesn't fit needing both a captured JSON body and dynamic status
    // per test, so this is a small local double instead of extending the shared one.
    private sealed class CapturingHandler(HttpStatusCode status, string? responseBody = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Content is not null)
            {
                // Force the content to be buffered before the request object is handed back,
                // since HttpClient may dispose/consume the stream after SendAsync returns.
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new("application/json");
            }

            Requests.Add(request);
            return new HttpResponseMessage(status)
            {
                Content = responseBody is null ? null : new StringContent(responseBody),
            };
        }
    }

    private sealed class CapturingLogger : ILogger<SlackAlertNotifier>
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
        ) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }

    private static readonly Guid ClaimId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static BudgetAlertPayload MakePayload() =>
        new(
            "Anthropic",
            "Daily",
            10m,
            15m,
            DateTimeOffset.UtcNow,
            "budget-alert-10000000000000000000000000000001@observatory.fixportal.com",
            ClaimId
        );

    [Fact]
    public async Task NotifyAsync_is_noop_when_webhook_not_configured()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>()).Returns((NotificationSettings?)null);
        var clock = new FakeClock(Instant.FromUtc(2026, 8, 30, 0, 0));

        var sut = new SlackAlertNotifier(http, repo, clock, NullLogger<SlackAlertNotifier>.Instance);
        var result = await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        result.Should().Be(AlertDeliveryResult.NoRecipientConfigured);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_posts_a_text_payload_to_the_configured_webhook()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );
        repo.GetBudgetAlertSlackSentAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(false);
        var clock = new FakeClock(Instant.FromUtc(2026, 8, 30, 0, 0));

        var sut = new SlackAlertNotifier(http, repo, clock, NullLogger<SlackAlertNotifier>.Instance);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.RequestUri.Should().Be(new Uri("https://hooks.slack.com/services/T0/B0/xyz"));
        request.Content.Should().NotBeNull();
        var body = await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        json.GetProperty("text").GetString().Should().Contain("Anthropic").And.Contain("£10.00");
    }

    [Fact]
    public async Task NotifyAsync_does_not_throw_when_the_webhook_call_fails()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );
        repo.GetBudgetAlertSlackSentAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(false);
        var clock = new FakeClock(Instant.FromUtc(2026, 8, 30, 0, 0));

        var sut = new SlackAlertNotifier(http, repo, clock, NullLogger<SlackAlertNotifier>.Instance);
        var result = await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        result.Should().Be(AlertDeliveryResult.Failed);
        await repo.DidNotReceive()
            .MarkBudgetAlertSlackSentAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_does_not_post_when_slack_already_sent_for_this_claim()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );
        repo.GetBudgetAlertSlackSentAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(true);
        var clock = new FakeClock(Instant.FromUtc(2026, 8, 30, 0, 0));

        var sut = new SlackAlertNotifier(http, repo, clock, NullLogger<SlackAlertNotifier>.Instance);
        var result = await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        // Fenced by an earlier pass: the alert already reached Slack, so this channel reports
        // delivery even though this call posts nothing.
        result.Should().Be(AlertDeliveryResult.Sent);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_logs_the_response_body_when_the_webhook_call_fails()
    {
        // Slack puts the actionable reason (invalid_payload, channel_not_found, ...) in a
        // plain-text body; the status code alone cannot tell a rotated webhook from a bad payload.
        var handler = new CapturingHandler(HttpStatusCode.BadRequest, "invalid_payload");
        using var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );
        repo.GetBudgetAlertSlackSentAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(false);
        var clock = new FakeClock(Instant.FromUtc(2026, 8, 30, 0, 0));
        var logger = new CapturingLogger();

        var sut = new SlackAlertNotifier(http, repo, clock, logger);
        var result = await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        result.Should().Be(AlertDeliveryResult.Failed);
        logger.Messages.Should().ContainSingle(m => m.Contains("invalid_payload") && m.Contains("BadRequest"));
    }

    [Fact]
    public async Task NotifyAsync_marks_slack_sent_after_a_successful_post()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );
        repo.GetBudgetAlertSlackSentAsync(ClaimId, Arg.Any<CancellationToken>()).Returns(false);
        var now = Instant.FromUtc(2026, 8, 30, 0, 0);
        var clock = new FakeClock(now);

        var sut = new SlackAlertNotifier(http, repo, clock, NullLogger<SlackAlertNotifier>.Instance);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await repo.Received(1).MarkBudgetAlertSlackSentAsync(ClaimId, now, Arg.Any<CancellationToken>());
    }
}
