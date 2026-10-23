using System.Net;
using AiObservatory.Data;
using AiObservatory.Data.Spend;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NodaTime;
using NodaTime.Testing;
using Npgsql;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// WebApplicationFactory harness for AiObservatory.Api. Boots the real Program.cs
/// composition root (migrations, seed-route mapping, rate limiter, ForwardedHeaders,
/// API-key guard) against a throwaway per-instance Postgres database — same
/// TEST_DB_CONNECTION-env-var-plus-random-database-name pattern the Data.Tests already
/// use, so no new infra dependency is introduced.
/// </summary>
/// <remarks>
/// Defaults to Development with a valid admin key, so most endpoint tests never need to
/// think about the startup guards. Set <see cref="Environment"/> / <see cref="ApiKeyOverride"/>
/// / <see cref="SetDbConnection"/> and read <see cref="Services"/> (or call
/// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>) to exercise
/// Production-only / misconfigured-startup behaviour — do that on a throwaway instance,
/// not the shared collection fixture.
/// </remarks>
public sealed class AiObservatoryApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminKey = "test-admin-key-0123456789";
    public const string ReadOnlyKey = "test-readonly-key-0123456789";
    public const string IdeKey = "test-ide-key-0123456789";

    public string Environment { get; set; } = Environments.Development;
    public bool UseMigratedTemplate { get; init; } = true;
    public string? ApiKeyOverride { get; set; } = AdminKey;
    public IClock? ClockOverride { get; init; }

    /// <summary>Sets OBSERVATORY_DEMO_MODE for the host (Production-style startup, demo routes).</summary>
    public bool DemoMode { get; set; }

    /// <summary>
    /// Owner allowlist for the Activity and GitHub tabs, as the delimited scalar the deployed
    /// app receives. Defaults to the FixPortal deployment's value so the suites asserting that a
    /// non-allowlisted owner is EXCLUDED have a list to exclude against; set it to an empty
    /// string to exercise the allow-everything default a self-hoster gets.
    /// </summary>
    public string ProjectOwnersOverride { get; set; } = "FixPortal,fix-portal";

    // Mutable test-fixture seam used by consumers even though the in-repo tests keep the default.
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string? ReadOnlyKeyOverride { get; set; } = ReadOnlyKey;
    public string? IdeKeyOverride { get; set; } = IdeKey;

    // Every test in this collection shares one fixture instance, so the whole suite's request
    // volume runs through one 120-req/min bucket -- comfortably fine locally, but the full
    // integration suite finishes in under a minute in CI and trips it (429s with no test-level
    // fault). No test here exercises the limiter itself, so raising it in tests only is safe.
    public Dictionary<string, string?> ConfigurationOverrides { get; } =
        new()
        {
            ["RateLimiting:ApiPermitLimit"] = "100000",
            // The daily suggestion worker evaluates at startup and fetches a live FX rate, which
            // would show up in tests that count outbound FX requests.
            ["Suggestions:WorkerEnabled"] = "false",
            ["Audit:WorkerEnabled"] = "false",
        };

    private string? _dbConnectionOverride;
    private bool _dbConnectionOverrideSet;
    private readonly string _connectionString;
    private bool _databasePrepared;

    public AiObservatoryApiFactory()
    {
        var baseConn =
            System.Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConn)
        {
            Database = $"aiobs_test_api_{Guid.NewGuid():N}",
        }.ConnectionString;
    }

    /// Explicitly override DB_CONNECTION (e.g. to null, to drive the missing-config guard).
    /// Leave unset to use the auto-provisioned per-instance throwaway database.
    public void SetDbConnection(string? value)
    {
        _dbConnectionOverride = value;
        _dbConnectionOverrideSet = true;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Belt-and-braces: this is the documented WebApplicationFactory mechanism for
        // environment selection. ConfigureAppConfiguration callbacks registered here were
        // separately found NOT to reliably apply before Program.cs's own top-level
        // builder.Configuration[...] reads for this minimal-hosting (top-level-statement)
        // entry point — see CreateHost below, which drives DB_CONNECTION/API-key config via
        // process environment variables instead.
        builder.UseEnvironment(Environment);
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(ConfigurationOverrides));
        // No test through this host may reach api.frankfurter.dev: an unreachable FX service is a
        // fast null (not cached), which is what a refresh sees offline. A test that needs a rate
        // layers its own handler with WithWebHostBuilder, which is registered after this and wins.
        builder.ConfigureTestServices(services =>
            services
                .AddHttpClient<FxRateProvider>()
                .ConfigurePrimaryHttpMessageHandler(() => new FxUnavailableHandler())
        );
        if (ClockOverride is { } clock)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton(clock);
            });
        }
    }

    private sealed class FxUnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (UseMigratedTemplate && !_dbConnectionOverrideSet && !_databasePrepared)
        {
            Testing
                .PostgresTestAssemblyFixture.CloneMigratedDatabaseAsync(_connectionString, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            _databasePrepared = true;
        }
        // Runs once per EnsureServer(), strictly after every test-side property override
        // (Environment/ApiKeyOverride/etc. — set via object initializer or after
        // construction) and strictly before builder.Build() triggers Program.Main.
        System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environment);
        System.Environment.SetEnvironmentVariable(
            "DB_CONNECTION",
            _dbConnectionOverrideSet ? _dbConnectionOverride : _connectionString
        );
        System.Environment.SetEnvironmentVariable("OBSERVATORY_API_KEY", ApiKeyOverride);
        System.Environment.SetEnvironmentVariable("OBSERVATORY_READONLY_API_KEY", ReadOnlyKeyOverride);
        System.Environment.SetEnvironmentVariable("OBSERVATORY_IDE_API_KEY", IdeKeyOverride);
        System.Environment.SetEnvironmentVariable("OBSERVATORY_DEMO_MODE", DemoMode ? "true" : null);
        System.Environment.SetEnvironmentVariable("SWA_ORIGIN", "https://example.test");
        // The owner allowlist is configuration now, and empty means allow everything. The
        // Activity and GitHub suites assert that a NON-allowlisted owner is excluded, so they
        // need a non-empty list or they would pass vacuously against an allow-all default.
        // This is the value the FixPortal deployment configures.
        System.Environment.SetEnvironmentVariable("Activity__ProjectOwners", ProjectOwnersOverride);
        return base.CreateHost(builder);
    }

    /// <summary>GET/read client carrying the readonly key.</summary>
    public HttpClient CreateReadOnlyClient()
    {
        var client = CreateClient();
        // Send whatever key the server was actually configured with, so the client stays
        // in sync when a test customises ReadOnlyKeyOverride (defaults to ReadOnlyKey).
        client.DefaultRequestHeaders.Add("X-Observatory-Key", ReadOnlyKeyOverride);
        return client;
    }

    /// <summary>Client carrying no API key.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient();

    /// <summary>Client carrying the admin key — required for every write.</summary>
    public HttpClient CreateAdminClient()
    {
        var client = CreateClient();
        // Mirror the server's configured admin key (defaults to AdminKey) rather than the
        // const, so an ApiKeyOverride-driven test doesn't silently send a mismatched key.
        client.DefaultRequestHeaders.Add("X-Observatory-Key", ApiKeyOverride);
        return client;
    }

    public HttpClient CreateIdeClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Observatory-IDE-Key", IdeKeyOverride);
        return client;
    }

    public async ValueTask InitializeAsync()
    {
        // Force host construction (and Program.cs's own MigrateAsync) up front so the
        // schema exists before any test seeds rows directly via a fresh DbContext.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        await db.Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(_connectionString, o => o.UseNodaTime())
                .Options;
            await using var ctx = new AiObservatoryDbContext(options);
            await ctx.Database.EnsureDeletedAsync();
        }
        catch
        {
            // Best-effort cleanup; a leaked throwaway DB doesn't fail the test.
        }
        await base.DisposeAsync();
    }
}

/// <summary>
/// Shared collection: one factory (one throwaway Postgres DB, one migrated schema) reused
/// across every endpoint-validation test class that only needs the default Development +
/// valid-admin-key configuration. Startup-guard / misconfiguration tests must NOT join this
/// collection — they need their own factory instance with a non-default Environment/key.
/// </summary>
[CollectionDefinition("ApiFactory")]
public class ApiFactoryCollection : ICollectionFixture<AiObservatoryApiFactory>;

public sealed class FixedClockApiFactory : IAsyncLifetime
{
    public static readonly Instant Now = Instant.FromUtc(2030, 1, 15, 12, 0);
    public AiObservatoryApiFactory Factory { get; } = new() { ClockOverride = new FakeClock(Now) };

    public ValueTask InitializeAsync() => Factory.InitializeAsync();

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}

[CollectionDefinition("FixedClockApiFactory")]
public class FixedClockApiFactoryCollection : ICollectionFixture<FixedClockApiFactory>;
