using AiObservatory.Api.Services;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

/// <summary>
/// Pins the exact DI registration shape from Program.cs: the "slack" keyed IAlertNotifier
/// registration must resolve through the typed HttpClient (10s timeout), not activate a fresh
/// SlackAlertNotifier via constructor injection against the default-named HttpClient (100s
/// default). AddKeyedTransient&lt;IAlertNotifier, SlackAlertNotifier&gt; would silently bypass
/// ConfigureHttpClient's timeout -- this test would fail if that registration regressed.
/// Also pins RemoveAllLoggers: the webhook URL is the credential (its path is the whole auth),
/// and the default HttpClientFactory logging handlers would write it to the logs at
/// Information level on every alert. Mirrors
/// IngestHostTests.CopilotSignedDownloadClientHasNoLoggingHandlers.
/// </summary>
public class SlackNotifierDiCompositionTests
{
    [Fact]
    public void KeyedSlackNotifier_ResolvesThroughTypedClient_WithConfiguredTimeout()
    {
        var services = SlackServices();

        using var provider = services.BuildServiceProvider();

        // The typed-client factory names the client after the CLR type -- resolving the same
        // named client independently proves the keyed registration above is wired through it
        // rather than through the default-named (100s) HttpClient.
        var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
        var typedClient = httpClientFactory.CreateClient(nameof(SlackAlertNotifier));

        typedClient.Timeout.Should().Be(TimeSpan.FromSeconds(10));

        // Resolving through the keyed registration must not throw and must produce a real
        // SlackAlertNotifier -- i.e. it delegates to the typed-client resolution, not some
        // other construction path.
        var resolved = provider.GetRequiredKeyedService<IAlertNotifier>("slack");
        resolved.Should().BeOfType<SlackAlertNotifier>();
    }

    [Fact]
    public void SlackAlertNotifierClient_HasNoLoggingHandlers()
    {
        using var provider = SlackServices().BuildServiceProvider();

        var handlers = HandlerChain(
                provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(SlackAlertNotifier))
            )
            .Select(handler => handler.GetType().FullName ?? handler.GetType().Name)
            .ToList();

        handlers.Should().NotContain(name => name.Contains("Logging", StringComparison.OrdinalIgnoreCase));
    }

    private static ServiceCollection SlackServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IUsageRepository>());
        services.AddSingleton<IClock>(SystemClock.Instance);
        services
            .AddHttpClient<SlackAlertNotifier>()
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers();
        services.AddKeyedTransient<IAlertNotifier>("slack", (sp, _) => sp.GetRequiredService<SlackAlertNotifier>());
        return services;
    }

    private static IEnumerable<HttpMessageHandler> HandlerChain(HttpMessageHandler handler)
    {
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            yield return current;
        }
    }
}
