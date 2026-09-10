// Explicit: the Worker SDK's implicit usings do not include ASP.NET Core's, which arrive
// here via a FrameworkReference rather than the Web SDK (see the csproj for why).
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest;
using AiObservatory.Ingest.Pricing;
using AiObservatory.Ingest.Services.Anthropic;
using AiObservatory.Ingest.Services.Copilot;
using AiObservatory.Ingest.Services.GitHub;
using AiObservatory.Ingest.Services.Google;
using AiObservatory.Ingest.Services.OpenAi;
using AiObservatory.Ingest.Sources;
using Google.Cloud.BigQuery.V2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(
        (ctx, services) =>
        {
            var cfg = ctx.Configuration;

            // A Key Vault reference that fails to resolve (secret absent, or the app has no
            // access) is left by App Service as the literal "@Microsoft.KeyVault(...)" string.
            // That is non-empty, so a plain IsNullOrEmpty gate would enable the provider with a
            // garbage credential and 401 hourly forever. Treat such a value as unset.
            var connectionString =
                cfg["DB_CONNECTION"] ?? throw new InvalidOperationException("DB_CONNECTION is required");
            services.AddDataLayer(connectionString);
            services.AddSingleton<IClock>(SystemClock.Instance);

            services.Configure<IngestOptions>(cfg.GetSection(IngestOptions.SectionName));
            services.PostConfigure<IngestOptions>(options => IngestOptions.BindGoogleCloudCatalog(cfg, options));
            var expectedRefreshInterval = Duration.FromMinutes(
                Math.Max(1, cfg.GetValue<int?>($"{IngestOptions.SectionName}:PollingIntervalMinutes") ?? 60)
            );

            // The allowlist may arrive as a delimited scalar (deployed Key Vault reference)
            // rather than an array, which plain section binding cannot handle. Resolve it
            // once here and post-configure it in, so the registration gate below and every
            // consumer of IngestOptions see the same value.
            var githubRepoAllowlist = IngestOptions.ResolveGitHubRepoAllowlist(cfg);
            services.PostConfigure<IngestOptions>(o => o.GitHubRepoAllowlist = githubRepoAllowlist);

            var anthropicConfigured = RegisterAnthropicSources(services, cfg, expectedRefreshInterval);

            // Copilot — enabled when GITHUB_TOKEN and COPILOT_ORG are both set.
            // Classic tokens require read:org; fine-grained tokens require Organization
            // Copilot metrics (read).
            var githubToken = cfg["GITHUB_TOKEN"];
            var copilotOrg = cfg["COPILOT_ORG"];
            var copilotConfigured = IsConfigured(githubToken) && IsConfigured(copilotOrg);
            services.AddSingleton(
                new SourceDefinition(UsageSourceIds.CopilotOrgReport, copilotConfigured, expectedRefreshInterval)
            );
            if (copilotConfigured)
            {
                services.AddHttpClient(
                    nameof(ICopilotReportClient),
                    c =>
                    {
                        c.BaseAddress = new Uri("https://api.github.com");
                        c.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubToken}");
                        c.DefaultRequestHeaders.Add("User-Agent", "fpaiobs-ingest");
                        c.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
                    }
                );
                services.AddHttpClient("CopilotSignedDownloads").RemoveAllLoggers();
                services.AddScoped<ICopilotReportClient>(sp =>
                {
                    var factory = sp.GetRequiredService<IHttpClientFactory>();
                    return new CopilotReportClient(
                        factory.CreateClient(nameof(ICopilotReportClient)),
                        factory.CreateClient("CopilotSignedDownloads"),
                        copilotOrg!
                    );
                });
                services.TryAddEnumerable(ServiceDescriptor.Scoped<IUsageSource, CopilotReportSource>());
            }

            var googleConfigured = RegisterGoogleBillingExport(services, cfg, expectedRefreshInterval);

            // OpenAI — enabled when OPENAI_ADMIN_KEY is set.
            // Requires an admin API key with the openai.usage.read permission.
            // Create one at platform.openai.com/api-keys (type: Admin key).
            var openAiAdminKey = cfg["OPENAI_ADMIN_KEY"];
            var openAiConfigured = IsConfigured(openAiAdminKey);
            services.AddSingleton(
                new SourceDefinition(UsageSourceIds.OpenAiUsageApi, openAiConfigured, expectedRefreshInterval)
            );
            services.AddSingleton(
                new SourceDefinition(UsageSourceIds.OpenAiCostsApi, openAiConfigured, expectedRefreshInterval)
            );
            if (openAiConfigured)
            {
                services
                    .AddHttpClient<IOpenAiAdminClient, OpenAiAdminClient>(c =>
                    {
                        c.BaseAddress = new Uri("https://api.openai.com");
                        c.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAiAdminKey}");
                    })
                    .RemoveAllLoggers();
                services.TryAddEnumerable(ServiceDescriptor.Scoped<IUsageSource, OpenAiUsageSource>());
                services.TryAddEnumerable(ServiceDescriptor.Scoped<IUsageSource, OpenAiCostsSource>());
            }

            if (anthropicConfigured || openAiConfigured || googleConfigured)
            {
                services.AddMemoryCache();
                services.AddHttpClient<FxRateProvider>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
                services.AddScoped<BillingObservationWriter>();
            }

            // GitHub Activity — enabled when GITHUB_TOKEN is set AND at least one repo is
            // allowlisted. Reuses the same GITHUB_TOKEN as Copilot reports; this PAT also
            // needs contents:read, pull-requests:read, and actions:read for activity.
            var githubConfigured = IsConfigured(githubToken) && githubRepoAllowlist.Length > 0;
            services.AddSingleton(
                new SourceDefinition(UsageSourceIds.GitHubActivityApi, githubConfigured, expectedRefreshInterval)
            );
            if (githubConfigured)
            {
                services.AddHttpClient<IGitHubActivityClient, GitHubActivityClient>(c =>
                {
                    c.BaseAddress = new Uri("https://api.github.com");
                    c.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubToken}");
                    c.DefaultRequestHeaders.Add("User-Agent", "fpaiobs-ingest");
                    c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                });
                services.TryAddEnumerable(ServiceDescriptor.Scoped<IUsageSource, GitHubIngestionService>());
            }

            RegisterPricingSources(services, cfg);

            // Telemetry, gated on the connection string exactly as the API gates it. Worth
            // having for its own sake, but specifically: this worker spent its entire deployed
            // life failing to start, and the absence of telemetry made that read as a quiet
            // worker rather than a dead one. Without this, the next failure is just as silent.
            if (!string.IsNullOrEmpty(cfg["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
            {
                services.AddApplicationInsightsTelemetry();
            }

            // Singleton first, then handed to the hosted-service registration, so /healthz can
            // read the SAME instance the host is running. AddHostedService<T>() alone would
            // construct a second, unrelated one.
            services.AddSingleton<ProviderPollingWorkerService>();
            services.AddHostedService(sp => sp.GetRequiredService<ProviderPollingWorkerService>());
            services.AddSingleton<PricingRefreshWorkerService>();
            services.AddHostedService(sp => sp.GetRequiredService<PricingRefreshWorkerService>());
        }
    )
    // Minimal web host so the process answers Linux App Service's startup probe. The
    // probe's port comes from the environment (App Service sets ASPNETCORE_URLS; locally
    // launchSettings.json picks 5040) and is deliberately NOT hardcoded here -- pinning
    // 8080 would collide with whatever else a developer happens to be running.
    //
    // Endpoints are health only. This worker must never grow an API surface: it holds
    // provider credentials that the public-facing API does not, and every route added
    // here is one more thing exposed on a host that exists purely to keep the container
    // alive.
    .ConfigureWebHostDefaults(web => web.Configure(MapHealthEndpoint))
    .Build();

await host.RunAsync();

// Split out of the builder chain rather than inlined as nested lambdas: three levels of
// nesting inside ConfigureWebHostDefaults pushed this file's cognitive complexity from
// under the limit to 25.
static void MapHealthEndpoint(IApplicationBuilder app)
{
    app.UseRouting();
    app.UseEndpoints(endpoints => endpoints.MapGet("/healthz", ReportHealth));
}

static IResult ReportHealth(ProviderPollingWorkerService worker)
{
    // 503 on exactly one condition: the poll loop is no longer running while the host still
    // is. ExecuteTask completing -- faulted, cancelled, or simply returning -- is
    // unambiguous silent death, and is precisely the failure this whole change exists to
    // stop going unnoticed. App Service replacing the instance is the right response.
    var running = worker.ExecuteTask is { IsCompleted: false };

    // Deliberately NOT unhealthy on a stale LastCycleCompletedAt. The poll interval is
    // configurable, so a long legitimate gap would make App Service recycle a perfectly
    // healthy container -- recreating the very restart loop this change removes. Staleness
    // is reported for a human to judge, not acted on automatically.
    var body = new
    {
        status = running ? "healthy" : "unhealthy",
        service = "AiObservatory.Ingest",
        workerRunning = running,
        cyclesCompleted = worker.CyclesCompleted,
        lastCycleCompletedAt = worker.LastCycleCompletedAt?.ToString(),
    };

    return running ? Results.Ok(body) : Results.Json(body, statusCode: 503);
}

static bool IsConfigured(string? value) =>
    !string.IsNullOrWhiteSpace(value) && !value.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase);

static bool RegisterAnthropicSources(
    IServiceCollection services,
    IConfiguration configuration,
    Duration expectedRefreshInterval
)
{
    var key = configuration["ANTHROPIC_BILLING_KEY"];
    var configured = IsConfigured(key);
    var claudeCodeEnabled = string.Equals(
        configuration["CLAUDE_CODE_USAGE_ENABLED"],
        "true",
        StringComparison.OrdinalIgnoreCase
    );
    if (claudeCodeEnabled && !configured)
    {
        throw new InvalidOperationException(
            "CLAUDE_CODE_USAGE_ENABLED requires ANTHROPIC_BILLING_KEY with an organization Admin API key."
        );
    }

    services.AddSingleton(new SourceDefinition(UsageSourceIds.AnthropicUsageApi, configured, expectedRefreshInterval));
    services.AddSingleton(
        new SourceDefinition(UsageSourceIds.AnthropicCostReport, configured, expectedRefreshInterval)
    );
    services.AddSingleton(
        new SourceDefinition(
            UsageSourceIds.ClaudeCodeUsageApi,
            configured && claudeCodeEnabled,
            expectedRefreshInterval
        )
    );
    if (!configured)
    {
        return false;
    }

    services
        .AddHttpClient<IAnthropicAdminClient, AnthropicAdminClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.anthropic.com");
            client.DefaultRequestHeaders.Add("x-api-key", key);
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            client.DefaultRequestHeaders.Add("anthropic-beta", "fast-mode-2026-02-01");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "AiObservatory.Ingest/1.0 (+https://github.com/FixPortal/fixportal-observatory)"
            );
        })
        .RemoveAllLoggers();
    services.TryAddEnumerable(ServiceDescriptor.Scoped<IUsageSource, AnthropicUsageSource>());
    services.TryAddEnumerable(ServiceDescriptor.Scoped<IUsageSource, AnthropicCostsSource>());
    if (claudeCodeEnabled)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IUsageSource, ClaudeCodeUsageSource>());
    }
    return true;
}

static bool RegisterGoogleBillingExport(
    IServiceCollection services,
    IConfiguration configuration,
    Duration expectedRefreshInterval
)
{
    var projectId = configuration["GOOGLE_CLOUD_PROJECT_ID"];
    var table = configuration["GOOGLE_BILLING_EXPORT_TABLE"];
    var projectConfigured = IsConfigured(projectId);
    var tableConfigured = IsConfigured(table);
    if (projectConfigured != tableConfigured)
    {
        throw new InvalidOperationException(
            "GOOGLE_CLOUD_PROJECT_ID and GOOGLE_BILLING_EXPORT_TABLE must be configured together."
        );
    }

    services.AddSingleton(
        new SourceDefinition(UsageSourceIds.GoogleCloudBillingExport, projectConfigured, expectedRefreshInterval)
    );
    if (!projectConfigured)
    {
        return false;
    }

    GoogleBillingExportClient.ValidateExportTable(table!);
    services.AddSingleton<IGoogleBillingExportClient>(_ => new GoogleBillingExportClient(
        new Lazy<BigQueryClient>(() => BigQueryClient.Create(projectId!)),
        table!
    ));
    services.TryAddEnumerable(ServiceDescriptor.Scoped<IUsageSource, GoogleBillingExportSource>());
    return true;
}

static void RegisterPricingSources(IServiceCollection services, IConfiguration configuration)
{
    var refreshInterval = Duration.FromDays(1);
    services.AddSingleton(new PricingSourceDefinition(PricingSourceIds.OpenAi, true, refreshInterval));
    services.AddSingleton(new PricingSourceDefinition(PricingSourceIds.Claude, true, refreshInterval));
    services.AddSingleton(new PricingSourceDefinition(PricingSourceIds.Kimi, true, refreshInterval));
    services.TryAddEnumerable(ServiceDescriptor.Scoped<IPricingSource, OpenAiPricingSource>());
    services.TryAddEnumerable(ServiceDescriptor.Scoped<IPricingSource, ClaudePricingSource>());
    services.TryAddEnumerable(ServiceDescriptor.Scoped<IPricingSource, KimiPricingSource>());

    var googleConfigured =
        IsConfigured(configuration["GOOGLE_CLOUD_CATALOG_API_KEY"])
        && IsConfigured(configuration["GOOGLE_CLOUD_CATALOG_SERVICE_ID"])
        && GooglePricingSource.HasVerifiedMappings;
    services.AddSingleton(
        new PricingSourceDefinition(PricingSourceIds.GoogleCloudCatalog, googleConfigured, refreshInterval)
    );

    // The pricing sources are scoped and rebuilt once per refresh pass; their HTTP handlers
    // come from the factory so connection pools survive across passes (P1).
    services
        .AddHttpClient(FirstPartyDocumentFetcher.HttpClientName)
        .ConfigurePrimaryHttpMessageHandler(_ => new HttpClientHandler { AllowAutoRedirect = false })
        .RemoveAllLoggers();
    if (googleConfigured)
    {
        services
            .AddHttpClient<GooglePricingSource>()
            .ConfigurePrimaryHttpMessageHandler(_ => GooglePricingSource.CreateHttpMessageHandler())
            .RemoveAllLoggers();
        // Resolve through the typed-client registration: a direct Scoped<IPricingSource, GooglePricingSource>
        // would activate the class by constructor and bind the DEFAULT unnamed HttpClient, so
        // the hardened primary handler (AllowAutoRedirect = false) and RemoveAllLoggers would
        // not apply — re-sending X-Goog-Api-Key across any cross-host redirect.
        // TryAddEnumerable cannot de-duplicate this shape either: on a factory descriptor it reads
        // the implementation type from the factory's generic arguments, gets IPricingSource itself,
        // and throws ArgumentException at registration — so a plain guarded AddScoped is the only
        // safe factory shape here.
        if (
            !services.Any(descriptor =>
                descriptor.ServiceType == typeof(IPricingSource) && descriptor.ImplementationFactory is not null
            )
        )
        {
            services.AddScoped<IPricingSource>(sp => sp.GetRequiredService<GooglePricingSource>());
        }
    }

    services.AddScoped<BundledPricingCatalogLoader>();
}

// Required as the WebApplicationFactory<TEntryPoint> marker for composition-root tests.
// ReSharper disable once ClassNeverInstantiated.Global
public partial class Program
{
    protected Program() { }
}
