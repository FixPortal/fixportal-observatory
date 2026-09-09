using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest.Pricing;
using AiObservatory.Ingest.Services.Anthropic;
using AiObservatory.Ingest.Services.Copilot;
using AiObservatory.Ingest.Services.GitHub;
using AiObservatory.Ingest.Services.Google;
using AiObservatory.Ingest.Services.OpenAi;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Ingest.Tests;

[CollectionDefinition("IngestHost", DisableParallelization = true)]
public class IngestHostCollection;

[Collection("IngestHost")]
public class IngestHostTests
{
    private const string KeyVaultReference = "@Microsoft.KeyVault(VaultName=fpaiobs-kv;SecretName=test)";

    [Fact]
    public async Task StartupFailsWhenTheDatabaseConnectionIsMissing()
    {
        await using var factory = new IngestFactory { DatabaseConnection = null };

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown, "DB_CONNECTION").Should().BeTrue();
    }

    [Theory]
    [InlineData("ANTHROPIC_BILLING_KEY", UsageSourceIds.AnthropicUsageApi)]
    [InlineData("ANTHROPIC_BILLING_KEY", UsageSourceIds.AnthropicCostReport)]
    [InlineData("COPILOT_ORG", UsageSourceIds.CopilotOrgReport)]
    [InlineData("OPENAI_ADMIN_KEY", UsageSourceIds.OpenAiUsageApi)]
    [InlineData("OPENAI_ADMIN_KEY", UsageSourceIds.OpenAiCostsApi)]
    [InlineData("GITHUB_TOKEN", UsageSourceIds.GitHubActivityApi)]
    public async Task UnresolvedKeyVaultReferenceDoesNotEnableAProvider(string setting, string sourceId)
    {
        await using var factory = new IngestFactory();
        factory.Settings[setting] = KeyVaultReference;
        if (setting == "COPILOT_ORG")
        {
            factory.Settings["GITHUB_TOKEN"] = "configured-token";
        }
        if (setting == "GITHUB_TOKEN")
        {
            factory.ConfigurationOverrides["Ingest:GitHubRepoAllowlist:0"] = "fix-portal/example";
        }

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetServices<IUsageSource>().Should().NotContain(source => source.SourceId == sourceId);
        scope
            .ServiceProvider.GetServices<SourceDefinition>()
            .Should()
            .ContainSingle(definition => definition.SourceId == sourceId)
            .Which.IsConfigured.Should()
            .BeFalse();
    }

    [Fact]
    public async Task RegistersExactlyOneDefinitionForEveryKnownSourceWhenUnconfigured()
    {
        await using var factory = new IngestFactory();

        var definitions = factory.Services.GetServices<SourceDefinition>().ToList();

        definitions
            .Select(x => x.SourceId)
            .Should()
            .BeEquivalentTo(
                UsageSourceIds.AnthropicUsageApi,
                UsageSourceIds.AnthropicCostReport,
                UsageSourceIds.ClaudeCodeUsageApi,
                UsageSourceIds.CopilotOrgReport,
                UsageSourceIds.GoogleCloudBillingExport,
                UsageSourceIds.OpenAiUsageApi,
                UsageSourceIds.OpenAiCostsApi,
                UsageSourceIds.GitHubActivityApi
            );
        definitions.Should().OnlyContain(x => !x.IsConfigured);
        definitions.Select(x => x.SourceId).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("COPILOT_ORG", typeof(CopilotReportSource), UsageSourceIds.CopilotOrgReport)]
    [InlineData("GITHUB_TOKEN", typeof(GitHubIngestionService), UsageSourceIds.GitHubActivityApi)]
    public async Task ConfiguredCredentialRegistersTheMatchingUsageSource(
        string setting,
        Type implementationType,
        string sourceId
    )
    {
        await using var factory = new IngestFactory();
        factory.Settings[setting] = "configured";
        if (setting == "COPILOT_ORG")
        {
            factory.Settings["GITHUB_TOKEN"] = "configured-token";
        }
        if (setting == "GITHUB_TOKEN")
        {
            factory.ConfigurationOverrides["Ingest:GitHubRepoAllowlist:0"] = "fix-portal/example";
        }

        using var scope = factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetServices<IUsageSource>().ToList();
        var definitions = scope.ServiceProvider.GetServices<SourceDefinition>().ToList();

        sources.Should().ContainSingle(x => x.GetType() == implementationType).Which.SourceId.Should().Be(sourceId);
        definitions.Should().ContainSingle(x => x.SourceId == sourceId).Which.IsConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("GOOGLE_CLOUD_PROJECT_ID", "configured-project")]
    [InlineData("GOOGLE_BILLING_EXPORT_TABLE", "configured_project.billing_export.gcp_billing_export_v1")]
    public async Task GoogleBillingExport_requires_both_project_and_safe_table(string setting, string value)
    {
        await using var factory = new IngestFactory();
        factory.Settings[setting] = value;

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown, "GOOGLE_CLOUD_PROJECT_ID and GOOGLE_BILLING_EXPORT_TABLE").Should().BeTrue();
    }

    [Fact]
    public async Task GoogleBillingExport_registers_lazily_with_shared_billing_services_when_project_and_table_are_valid()
    {
        await using var factory = new IngestFactory();
        factory.Settings["GOOGLE_CLOUD_PROJECT_ID"] = "configured-project";
        factory.Settings["GOOGLE_BILLING_EXPORT_TABLE"] = "configured_project.billing_export.gcp_billing_export_v1";

        using var scope = factory.Services.CreateScope();
        scope
            .ServiceProvider.GetServices<IUsageSource>()
            .Should()
            .ContainSingle(source => source is GoogleBillingExportSource);
        scope
            .ServiceProvider.GetServices<SourceDefinition>()
            .Single(x => x.SourceId == UsageSourceIds.GoogleCloudBillingExport)
            .IsConfigured.Should()
            .BeTrue();
        scope.ServiceProvider.GetRequiredService<BillingObservationWriter>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<FxRateProvider>().Should().NotBeNull();
    }

    [Fact]
    public async Task GoogleBillingExport_reuses_one_lazy_client_registration_across_scopes()
    {
        await using var factory = new IngestFactory();
        factory.Settings["GOOGLE_CLOUD_PROJECT_ID"] = "configured-project";
        factory.Settings["GOOGLE_BILLING_EXPORT_TABLE"] = "configured_project.billing_export.gcp_billing_export_v1";

        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();

        first
            .ServiceProvider.GetRequiredService<IGoogleBillingExportClient>()
            .Should()
            .BeSameAs(second.ServiceProvider.GetRequiredService<IGoogleBillingExportClient>());
    }

    [Fact]
    public async Task LegacyBillingAccountSetting_does_not_enable_the_removed_reports_route()
    {
        await using var factory = new IngestFactory();
        factory.Settings["GOOGLE_BILLING_ACCOUNT_ID"] = "billingAccounts/123456-123456-123456";

        using var scope = factory.Services.CreateScope();

        scope
            .ServiceProvider.GetServices<IUsageSource>()
            .Should()
            .NotContain(source => source.SourceId == UsageSourceIds.GoogleCloudBillingExport);
    }

    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("COPILOT_ORG")]
    public async Task CopilotReportRequiresBothConfiguredValues(string configuredSetting)
    {
        await using var factory = new IngestFactory();
        factory.Settings[configuredSetting] = "configured";

        using var scope = factory.Services.CreateScope();

        scope
            .ServiceProvider.GetServices<IUsageSource>()
            .Should()
            .NotContain(source => source.SourceId == UsageSourceIds.CopilotOrgReport);
        scope
            .ServiceProvider.GetServices<SourceDefinition>()
            .Should()
            .ContainSingle(definition => definition.SourceId == UsageSourceIds.CopilotOrgReport)
            .Which.IsConfigured.Should()
            .BeFalse();
    }

    [Theory]
    [InlineData("GITHUB_TOKEN", " ")]
    [InlineData("COPILOT_ORG", "\t")]
    [InlineData("GITHUB_TOKEN", KeyVaultReference)]
    [InlineData("COPILOT_ORG", KeyVaultReference)]
    public async Task InvalidCopilotConfigurationFailsClosed(string invalidSetting, string invalidValue)
    {
        await using var factory = new IngestFactory();
        factory.Settings["GITHUB_TOKEN"] = "configured-token";
        factory.Settings["COPILOT_ORG"] = "FixPortal";
        factory.Settings[invalidSetting] = invalidValue;

        using var scope = factory.Services.CreateScope();

        scope
            .ServiceProvider.GetServices<IUsageSource>()
            .Should()
            .NotContain(source => source.SourceId == UsageSourceIds.CopilotOrgReport);
    }

    [Fact]
    public async Task CopilotAndGitHubActivityKeepIndependentConfigurationGates()
    {
        await using var activityFactory = new IngestFactory();
        activityFactory.Settings["GITHUB_TOKEN"] = "configured-token";
        activityFactory.ConfigurationOverrides["Ingest:GitHubRepoAllowlist:0"] = "fix-portal/example";
        using var activityScope = activityFactory.Services.CreateScope();
        activityScope
            .ServiceProvider.GetServices<IUsageSource>()
            .Should()
            .ContainSingle(source => source is GitHubIngestionService);
        activityScope
            .ServiceProvider.GetServices<IUsageSource>()
            .Should()
            .NotContain(source => source.SourceId == UsageSourceIds.CopilotOrgReport);

        await using var copilotFactory = new IngestFactory();
        copilotFactory.Settings["GITHUB_TOKEN"] = "configured-token";
        copilotFactory.Settings["COPILOT_ORG"] = "FixPortal";
        using var copilotScope = copilotFactory.Services.CreateScope();
        copilotScope
            .ServiceProvider.GetServices<IUsageSource>()
            .Should()
            .ContainSingle(source => source is CopilotReportSource);
        copilotScope
            .ServiceProvider.GetServices<IUsageSource>()
            .Should()
            .NotContain(source => source is GitHubIngestionService);
    }

    [Fact]
    public async Task CopilotUsesAuthorizedDescriptorAndUnauthenticatedDownloadClients()
    {
        await using var factory = new IngestFactory();
        factory.Settings["GITHUB_TOKEN"] = "github-secret";
        factory.Settings["COPILOT_ORG"] = "FixPortal";

        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();
        using var descriptor = clients.CreateClient(nameof(ICopilotReportClient));
        using var download = clients.CreateClient("CopilotSignedDownloads");

        descriptor.BaseAddress.Should().Be(new Uri("https://api.github.com"));
        descriptor.DefaultRequestHeaders.Authorization.Should().NotBeNull();
        descriptor.DefaultRequestHeaders.Authorization.ToString().Should().Be("Bearer github-secret");
        descriptor.DefaultRequestHeaders.Accept.Single().MediaType.Should().Be("application/vnd.github+json");
        descriptor
            .DefaultRequestHeaders.GetValues("X-GitHub-Api-Version")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("2026-03-10");
        download.DefaultRequestHeaders.Authorization.Should().BeNull();
        download.DefaultRequestHeaders.Contains("X-GitHub-Api-Version").Should().BeFalse();
        download.DefaultRequestHeaders.Accept.Should().BeEmpty();
    }

    [Fact]
    public async Task CopilotSignedDownloadClientHasNoLoggingHandlers()
    {
        await using var factory = new IngestFactory();
        factory.Settings["GITHUB_TOKEN"] = "github-secret";
        factory.Settings["COPILOT_ORG"] = "FixPortal";

        var handlers = HandlerChain(
                factory
                    .Services.GetRequiredService<IHttpMessageHandlerFactory>()
                    .CreateHandler("CopilotSignedDownloads")
            )
            .Select(handler => handler.GetType().FullName ?? handler.GetType().Name)
            .ToList();

        handlers.Should().NotContain(name => name.Contains("Logging", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(nameof(IOpenAiAdminClient), "OPENAI_ADMIN_KEY")]
    [InlineData(nameof(IAnthropicAdminClient), "ANTHROPIC_BILLING_KEY")]
    public async Task ProviderAdminClientsHaveNoLoggingHandlers(string clientName, string setting)
    {
        await using var factory = new IngestFactory();
        factory.Settings[setting] = "configured-secret";

        var handlers = HandlerChain(
                factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName)
            )
            .Select(handler => handler.GetType().FullName ?? handler.GetType().Name)
            .ToList();

        handlers.Should().NotContain(name => name.Contains("Logging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenAiAdminKeyRegistersTwoIndependentSourcesWithOneClientAndTheBillingWriter()
    {
        await using var factory = new IngestFactory();
        factory.Settings["OPENAI_ADMIN_KEY"] = "configured-secret";

        using var scope = factory.Services.CreateScope();
        var openAiSources = scope
            .ServiceProvider.GetServices<IUsageSource>()
            .Where(source => source.SourceId is UsageSourceIds.OpenAiUsageApi or UsageSourceIds.OpenAiCostsApi)
            .ToList();
        var definitions = scope
            .ServiceProvider.GetServices<SourceDefinition>()
            .Where(definition => definition.SourceId is UsageSourceIds.OpenAiUsageApi or UsageSourceIds.OpenAiCostsApi)
            .ToList();

        openAiSources
            .Select(source => source.GetType())
            .Should()
            .BeEquivalentTo([typeof(OpenAiUsageSource), typeof(OpenAiCostsSource)]);
        definitions.Should().HaveCount(2).And.OnlyContain(definition => definition.IsConfigured);
        scope.ServiceProvider.GetServices<IOpenAiAdminClient>().Should().ContainSingle();
        scope.ServiceProvider.GetRequiredService<FxRateProvider>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<BillingObservationWriter>().Should().NotBeNull();
        definitions
            .Select(definition => definition.ToString())
            .Should()
            .OnlyContain(value => !value.Contains("configured-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnthropicAdminKeyRegistersUsageAndCostsWithOneClientAndSharedBillingServices()
    {
        await using var factory = new IngestFactory();
        factory.Settings["ANTHROPIC_BILLING_KEY"] = "configured-secret";

        using var scope = factory.Services.CreateScope();
        var sources = scope
            .ServiceProvider.GetServices<IUsageSource>()
            .Where(source =>
                source.SourceId
                    is UsageSourceIds.AnthropicUsageApi
                        or UsageSourceIds.AnthropicCostReport
                        or UsageSourceIds.ClaudeCodeUsageApi
            )
            .ToList();
        var definitions = scope
            .ServiceProvider.GetServices<SourceDefinition>()
            .Where(definition =>
                definition.SourceId
                    is UsageSourceIds.AnthropicUsageApi
                        or UsageSourceIds.AnthropicCostReport
                        or UsageSourceIds.ClaudeCodeUsageApi
            )
            .ToList();

        sources
            .Select(source => source.GetType())
            .Should()
            .BeEquivalentTo([typeof(AnthropicUsageSource), typeof(AnthropicCostsSource)]);
        definitions.Should().HaveCount(3);
        definitions.Single(row => row.SourceId == UsageSourceIds.ClaudeCodeUsageApi).IsConfigured.Should().BeFalse();
        definitions
            .Where(row => row.SourceId != UsageSourceIds.ClaudeCodeUsageApi)
            .Should()
            .OnlyContain(row => row.IsConfigured);
        scope.ServiceProvider.GetServices<IAnthropicAdminClient>().Should().ContainSingle();
        scope.ServiceProvider.GetServices<FxRateProvider>().Should().ContainSingle();
        scope.ServiceProvider.GetServices<BillingObservationWriter>().Should().ContainSingle();
        definitions
            .Select(definition => definition.ToString())
            .Should()
            .OnlyContain(value => !value.Contains("configured-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClaudeCodeOptInRegistersTheThirdAnthropicSource()
    {
        await using var factory = new IngestFactory();
        factory.Settings["ANTHROPIC_BILLING_KEY"] = "configured-secret";
        factory.Settings["CLAUDE_CODE_USAGE_ENABLED"] = "true";

        using var scope = factory.Services.CreateScope();

        scope
            .ServiceProvider.GetServices<IUsageSource>()
            .Should()
            .ContainSingle(source => source is ClaudeCodeUsageSource)
            .Which.SourceId.Should()
            .Be(UsageSourceIds.ClaudeCodeUsageApi);
        scope
            .ServiceProvider.GetServices<SourceDefinition>()
            .Should()
            .ContainSingle(definition => definition.SourceId == UsageSourceIds.ClaudeCodeUsageApi)
            .Which.IsConfigured.Should()
            .BeTrue();
    }

    [Fact]
    public async Task ClaudeCodeOptInWithoutAnAdminKeyFailsStartupClearly()
    {
        await using var factory = new IngestFactory();
        factory.Settings["CLAUDE_CODE_USAGE_ENABLED"] = "true";

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown, "CLAUDE_CODE_USAGE_ENABLED requires ANTHROPIC_BILLING_KEY").Should().BeTrue();
    }

    [Fact]
    public async Task WhitespaceOpenAiAdminKeyRegistersNeitherSource()
    {
        await using var factory = new IngestFactory();
        factory.Settings["OPENAI_ADMIN_KEY"] = " \t ";

        using var scope = factory.Services.CreateScope();
        var sourceIds = new[] { UsageSourceIds.OpenAiUsageApi, UsageSourceIds.OpenAiCostsApi };

        scope
            .ServiceProvider.GetServices<IUsageSource>()
            .Should()
            .NotContain(source => sourceIds.Contains(source.SourceId));
        scope
            .ServiceProvider.GetServices<SourceDefinition>()
            .Where(definition => sourceIds.Contains(definition.SourceId))
            .Should()
            .HaveCount(2)
            .And.OnlyContain(definition => !definition.IsConfigured);
    }

    [Fact]
    public async Task AnthropicAdminHttpClientUsesVersionFastModeAndIntegrationIdentity()
    {
        await using var factory = new IngestFactory();
        factory.Settings["ANTHROPIC_BILLING_KEY"] = "configured";

        using var client = factory
            .Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IAnthropicAdminClient));

        client.DefaultRequestHeaders.TryGetValues("anthropic-beta", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("fast-mode-2026-02-01");
        client.DefaultRequestHeaders.TryGetValues("anthropic-version", out var versions).Should().BeTrue();
        versions.Should().ContainSingle().Which.Should().Be("2023-06-01");
        client.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("AiObservatory.Ingest/");
    }

    [Fact]
    public async Task RegistersExactlyOneDailyDefinitionAndSourceForEachPublicPricingDocument()
    {
        await using var factory = new IngestFactory();
        using var scope = factory.Services.CreateScope();

        var sources = scope.ServiceProvider.GetServices<IPricingSource>().ToList();
        var definitions = scope.ServiceProvider.GetServices<PricingSourceDefinition>().ToList();

        sources
            .Select(source => source.SourceId)
            .Should()
            .BeEquivalentTo(PricingSourceIds.OpenAi, PricingSourceIds.Claude, PricingSourceIds.Kimi);
        sources.Select(source => source.SourceId).Should().OnlyHaveUniqueItems();
        definitions
            .Select(definition => definition.SourceId)
            .Should()
            .BeEquivalentTo(
                PricingSourceIds.OpenAi,
                PricingSourceIds.Claude,
                PricingSourceIds.Kimi,
                PricingSourceIds.GoogleCloudCatalog
            );
        definitions.Select(definition => definition.SourceId).Should().OnlyHaveUniqueItems();
        definitions
            .Should()
            .OnlyContain(definition => definition.ExpectedRefreshInterval == NodaTime.Duration.FromDays(1));
        definitions
            .Single(definition => definition.SourceId == PricingSourceIds.GoogleCloudCatalog)
            .IsConfigured.Should()
            .BeFalse();
    }

    [Fact]
    public async Task DoesNotRegisterGooglePricingWithoutVerifiedMappingsEvenWhenCredentialsExist()
    {
        await using var factory = new IngestFactory();
        factory.Settings["GOOGLE_CLOUD_CATALOG_API_KEY"] = "configured-key";
        factory.Settings["GOOGLE_CLOUD_CATALOG_SERVICE_ID"] = "configured-service";
        using var scope = factory.Services.CreateScope();

        scope
            .ServiceProvider.GetServices<IPricingSource>()
            .Should()
            .NotContain(source => source is GooglePricingSource);
        scope
            .ServiceProvider.GetServices<PricingSourceDefinition>()
            .Should()
            .ContainSingle(definition => definition.SourceId == PricingSourceIds.GoogleCloudCatalog)
            .Which.IsConfigured.Should()
            .BeFalse();
    }

    [Fact]
    public async Task GooglePricingWithVerifiedMappingsResolvesThroughTheTypedClientRegistration()
    {
        // VerifiedMappings is empty until the SKU list is verified, so the host-build path that
        // registers GooglePricingSource cannot be reached through configuration alone — flip the
        // internal gate for the duration of this test.
        var original = GooglePricingSource.HasVerifiedMappings;
        GooglePricingSource.HasVerifiedMappings = true;
        try
        {
            await using var factory = new IngestFactory();
            factory.Settings["GOOGLE_CLOUD_CATALOG_API_KEY"] = "configured-key";
            factory.Settings["GOOGLE_CLOUD_CATALOG_SERVICE_ID"] = "configured-service";

            using var scope = factory.Services.CreateScope();

            scope
                .ServiceProvider.GetServices<IPricingSource>()
                .Should()
                .ContainSingle(source => source is GooglePricingSource);
            scope
                .ServiceProvider.GetServices<PricingSourceDefinition>()
                .Should()
                .ContainSingle(definition => definition.SourceId == PricingSourceIds.GoogleCloudCatalog)
                .Which.IsConfigured.Should()
                .BeTrue();
        }
        finally
        {
            GooglePricingSource.HasVerifiedMappings = original;
        }
    }

    private static bool ExceptionChainContains(Exception ex, string fragment)
    {
        if (ex.Message.Contains(fragment, StringComparison.Ordinal))
        {
            return true;
        }
        if (ex is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(e => ExceptionChainContains(e, fragment));
        }
        return ex.InnerException is not null && ExceptionChainContains(ex.InnerException, fragment);
    }

    private static IEnumerable<HttpMessageHandler> HandlerChain(HttpMessageHandler handler)
    {
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            yield return current;
        }
    }

    private static Exception? CaptureServicesException(IngestFactory factory) =>
        Record.Exception(() => factory.Services);

    private sealed class IngestFactory : WebApplicationFactory<Program>
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

        public string? DatabaseConnection { get; init; } = "Host=unused;Database=unused";
        public Dictionary<string, string?> Settings { get; } = [];
        public Dictionary<string, string?> ConfigurationOverrides { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(ConfigurationOverrides));
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
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
                Environment.SetEnvironmentVariable("DB_CONNECTION", DatabaseConnection);
                foreach (var setting in Settings)
                {
                    Environment.SetEnvironmentVariable(setting.Key, setting.Value);
                }

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
