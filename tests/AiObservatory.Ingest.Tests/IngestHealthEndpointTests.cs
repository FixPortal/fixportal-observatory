using System.Net;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Ingest.Tests;

// Separate from IngestHostTests deliberately: that suite's IngestFactory calls
// services.RemoveAll<IHostedService>(), which leaves ExecuteTask permanently absent and
// /healthz permanently 503 -- it cannot distinguish a running worker from a stopped one.
// This factory keeps a single hosted, controllable worker registered so the route sees the
// real state transition.
[Collection("IngestHost")]
public class IngestHealthEndpointTests
{
    [Fact]
    public async Task HealthzReports200WhileRunningAnd503OnceTheWorkerStops()
    {
        await using var factory = new IngestHealthFactory();
        using var client = factory.CreateClient();

        var whileRunning = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        whileRunning.StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Worker.Complete();
        await factory.Worker.ExecuteTask!;

        var afterStopped = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        afterStopped.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // Test double for ProviderPollingWorkerService: real BackgroundService lifecycle (so
    // ExecuteTask behaves exactly as production wiring depends on), but ExecuteAsync is a
    // controllable gate instead of a real poll loop, so the test needs no provider
    // credentials or reachable database.
    private sealed class ControllableWorker(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        ILogger<ProviderPollingWorkerService> logger,
        IOptions<IngestOptions> options
    ) : ProviderPollingWorkerService(scopeFactory, clock, logger, options)
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => _gate.Task;

        public void Complete() => _gate.TrySetResult();
    }

    private sealed class IngestHealthFactory : WebApplicationFactory<Program>
    {
        private static readonly string[] SettingNames =
        [
            "DB_CONNECTION",
            "ANTHROPIC_BILLING_KEY",
            "CLAUDE_CODE_USAGE_ENABLED",
            "GITHUB_TOKEN",
            "COPILOT_ORG",
            "GOOGLE_CLOUD_PROJECT_ID",
            "GOOGLE_BILLING_EXPORT_TABLE",
            "GOOGLE_BILLING_ACCOUNT_ID",
            "OPENAI_ADMIN_KEY",
            "APPLICATIONINSIGHTS_CONNECTION_STRING",
            "GOOGLE_CLOUD_CATALOG_API_KEY",
            "GOOGLE_CLOUD_CATALOG_SERVICE_ID",
        ];

        public ControllableWorker Worker { get; private set; } = null!;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // Drop the real hosted workers (PricingRefreshWorkerService needs live
                // provider/database access this test never sets up), then re-register the
                // health-route dependency as a single controllable singleton that is also
                // the hosted service -- matching Program.cs's "singleton first, then handed
                // to AddHostedService" wiring so /healthz reads the same instance the host
                // runs.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ProviderPollingWorkerService>();
                services.AddSingleton<ProviderPollingWorkerService>(sp =>
                    Worker = ActivatorUtilities.CreateInstance<ControllableWorker>(sp)
                );
                services.AddHostedService(sp =>
                    (ControllableWorker)sp.GetRequiredService<ProviderPollingWorkerService>()
                );
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var originals = SettingNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var name in SettingNames)
                {
                    Environment.SetEnvironmentVariable(name, null);
                }
                Environment.SetEnvironmentVariable("DB_CONNECTION", "Host=unused;Database=unused");

                return base.CreateHost(builder);
            }
            finally
            {
                foreach (var original in originals)
                {
                    Environment.SetEnvironmentVariable(original.Key, original.Value);
                }
            }
        }
    }
}
