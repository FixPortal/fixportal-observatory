using System.Threading.RateLimiting;
using AiObservatory.Api;
using AiObservatory.Api.Endpoints;
using AiObservatory.Api.Routing;
using AiObservatory.Api.Services;
using AiObservatory.Api.Services.GitHub;
using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Spend;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

Program.AddApplicationInsightsIfConfigured(builder);
builder.Services.AddOpenApi();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase,
            allowIntegerValues: false
        )
    );
});

var dbConnection =
    builder.Configuration["DB_CONNECTION"]
    ?? throw new InvalidOperationException("DB_CONNECTION configuration is missing.");
builder.Services.AddDataLayer(dbConnection);

Program.ValidateApiKeys(builder);
builder.Services.AddSingleton<IClock>(SystemClock.Instance);

// Owner allowlist for the Activity and GitHub tabs. Resolved once here, so every consumer sees
// the same value, and bound from either the array or the delimited-scalar shape. Absent means
// absent: an unset allowlist filters nothing, rather than emptying both tabs.
builder.Services.Configure<ActivityOptions>(builder.Configuration.GetSection(ActivityOptions.SectionName));
var projectOwners = ActivityOptions.ResolveProjectOwners(builder.Configuration);
builder.Services.PostConfigure<ActivityOptions>(o => o.ProjectOwners = projectOwners);
builder.Services.AddSingleton(
    RoutingCatalogService.Load(Path.Combine(AppContext.BaseDirectory, "Routing", "routing-catalog.json"))
);

builder.Services.AddTransient<MailKit.Net.Smtp.ISmtpClient, MailKit.Net.Smtp.SmtpClient>();
builder.Services.AddKeyedTransient<IAlertNotifier, EmailAlertNotifier>("email");

// The webhook URL is the credential (its path is the whole auth), so this client's
// HttpClientFactory logging handlers are removed -- otherwise every budget alert writes the
// full URL to the logs and App Insights. Matches the Ingest secret-carrying registrations.
builder
    .Services.AddHttpClient<SlackAlertNotifier>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
    .RemoveAllLoggers();
builder.Services.AddKeyedTransient<IAlertNotifier>("slack", (sp, _) => sp.GetRequiredService<SlackAlertNotifier>());
builder.Services.AddTransient<IAlertNotifier, CompositeAlertNotifier>();
builder.Services.AddScoped<BudgetAlertService>();
builder.Services.AddScoped<AdversarialReviewService>();
builder.Services.AddSingleton<AnthropicIntelligenceClient>();
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<InsightResponseParser>();
builder.Services.AddScoped<IInsightGenerator, InsightGenerator>();
builder.Services.AddMemoryCache();

// FxRateProvider is a transient typed client; only consume from scoped/transient services.
// Timeout bounded well below the 100s default: a slow Frankfurter must not stall a ledger
// write for that long, and a large batch pays this per row (see FetchGbpRateAsync's
// cancellation-vs-timeout handling for why a timeout here does not abort the whole batch).
builder.Services.AddHttpClient<FxRateProvider>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<BillingObservationWriter>();

// GitHub billing — the org bill is not paid from the account the spend CSV exports, so
// without this arm the ledger silently omits every penny of GitHub spend. No-ops unless
// GITHUB_TOKEN and GITHUB_BILLING_ORG are both configured; see AddGitHubBilling for the
// token scope that arm needs and why an unresolved Key Vault reference counts as unset.
builder.Services.AddGitHubBilling(builder.Configuration);

builder.Services.AddHostedService<IntelligenceWorkerService>();

// Fixed-window rate limit per client IP on the /api group, so an unauthenticated GET or a
// hot loop can't hammer the B1 plan. Partitioning on the (real, post-UseForwardedHeaders)
// remote IP rather than the X-Observatory-Key header is deliberate: the limiter runs before
// ApiKeyEndpointFilter, so the header is unvalidated at this point — keying on it let an
// anonymous caller mint a fresh 120/min bucket per request just by rotating a random header,
// bypassing the limit entirely.
// Validate the permit limit at boot: FixedWindowRateLimiterOptions is only validated when a
// partition's limiter is first constructed, so a zero or negative value would otherwise turn
// every /api request into a 500 instead of refusing to start. The factory below keeps its own
// read so a config reload still applies to newly-seen client partitions.
Program.ValidateRateLimitPermitLimit(builder);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(
        "api",
        ctx =>
        {
            var partitionKey = ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = builder.Configuration.GetValue("RateLimiting:ApiPermitLimit", 120),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }
            );
        }
    );
});

// Honour X-Forwarded-For from a trusted reverse proxy (the nginx sidecar in the
// Docker self-host topology) so the rate limiter partitions by the real client IP
// rather than collapsing every proxied caller onto the bridge IP. Only proxies on
// private networks are trusted, so a public client cannot spoof the header.
builder.Services.Configure<ForwardedHeadersOptions>(Program.ConfigureForwardedHeaders);

builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p =>
        p.WithOrigins(builder.Configuration["SWA_ORIGIN"] ?? "https://fpaiobs-swa.azurestaticapps.net")
            .AllowAnyMethod()
            .AllowAnyHeader()
    )
);

// Entra (Azure AD) JWT bearer auth for human callers. Only wired when configured —
// local dev runs without AzureAd settings, so the app starts and the API-key path
// (or no key, in dev) governs access. In production an authenticated user bypasses
// the API key entirely (see ApiKeyEndpointFilter); machine callers keep using keys.
var authEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"]);
if (authEnabled)
{
    builder
        .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
    await db.Database.MigrateAsync();

    // One-time backfill: an existing deployment's BUDGET_ALERT_EMAIL_TO env var predates the
    // NotificationSettings table, so without this a pre-existing deployment would silently stop
    // emailing budget alerts on upgrade until someone visited the settings UI. Self-retiring --
    // once a row exists (created here or via the UI) this is a permanent no-op.
    if (!await db.NotificationSettings.AnyAsync(s => s.Id == NotificationSettings.SingletonId))
    {
        await Program.SeedLegacyAlertEmailAsync(db, scope.ServiceProvider, builder.Configuration);
    }
}

app.UseForwardedHeaders();
app.UseCors();
app.UseRateLimiter();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

var api = app.MapGroup("/api").AddEndpointFilter<ApiKeyEndpointFilter>().RequireRateLimiting("api");
var ide = app.MapGroup("/api/ide/v1").AddEndpointFilter<IdeApiKeyEndpointFilter>().RequireRateLimiting("api");
ide.MapIdeEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    api.MapPost(
        "/dev/seed",
        async (AiObservatoryDbContext db, IClock clock, CancellationToken ct) =>
        {
            // Idempotent: only seed a genuinely empty database. The Docker compose seed
            // service calls this on every `up`; the guard must cover EVERY table the seed
            // writes, not just DailyAggregates — a self-hoster who created a Subscription or
            // BudgetRule (neither of which writes a DailyAggregate) before any aggregation
            // would otherwise have that config silently destroyed on the next `up`.
            if (
                await db.DailyAggregates.AnyAsync(ct)
                || await db.Subscriptions.AnyAsync(ct)
                || await db.Insights.AnyAsync(ct)
                || await db.BudgetRules.AnyAsync(ct)
                || await db.UsageEvents.AnyAsync(ct)
                || await db.SpendEntries.AnyAsync(ct)
            )
            {
                return Results.Ok("Already seeded — skipping (data present).");
            }

            // No TRUNCATE: the guard above proves all seeded tables are empty, so the previous
            // destructive reset is unnecessary (and was the data-loss vector it guarded against).
            var today = clock.GetCurrentInstant().InUtc().Date;

            for (int i = 0; i < 14; i++)
            {
                var date = today.PlusDays(-i);

                db.DailyAggregates.Add(
                    new DailyAggregate
                    {
                        Date = date,
                        Provider = Provider.Anthropic,
                        Model = "claude-3-5-sonnet",
                        SourceId = UsageSourceIds.DemoSeed,
                        SourceKind = SourceKind.Synthetic,
                        UsageScope = UsageScope.Api,
                        CostBasis = CostBasis.ListPriceEstimate,
                        InputTokens = 150000 + i * 1000,
                        OutputTokens = 80000 + i * 500,
                        CacheReadTokens = 30000 + i * 200,
                        CacheWriteTokens = 12000 + i * 100,
                        CostUsd = 2.50m + i * 0.15m,
                        RequestCount = 50 + i,
                    }
                );

                db.DailyAggregates.Add(
                    new DailyAggregate
                    {
                        Date = date,
                        Provider = Provider.Anthropic,
                        Model = "claude-3-5-haiku",
                        SourceId = UsageSourceIds.DemoSeed,
                        SourceKind = SourceKind.Synthetic,
                        UsageScope = UsageScope.Api,
                        CostBasis = CostBasis.ListPriceEstimate,
                        InputTokens = 300000 + i * 2000,
                        OutputTokens = 150000 + i * 1000,
                        CacheReadTokens = 80000 + i * 500,
                        CacheWriteTokens = 25000 + i * 150,
                        CostUsd = 0.80m + i * 0.05m,
                        RequestCount = 120 + i,
                    }
                );

                db.DailyAggregates.Add(
                    new DailyAggregate
                    {
                        Date = date,
                        Provider = Provider.Google,
                        Model = "gemini-1.5-pro",
                        SourceId = UsageSourceIds.DemoSeed,
                        SourceKind = SourceKind.Synthetic,
                        UsageScope = UsageScope.Api,
                        CostBasis = CostBasis.ListPriceEstimate,
                        InputTokens = 80000 + i * 500,
                        OutputTokens = 40000 + i * 200,
                        CacheReadTokens = 15000 + i * 100,
                        CacheWriteTokens = 5000 + i * 50,
                        CostUsd = 1.20m + i * 0.08m,
                        RequestCount = 30 + i,
                    }
                );

                db.DailyAggregates.Add(
                    new DailyAggregate
                    {
                        Date = date,
                        Provider = Provider.Google,
                        Model = "gemini-1.5-flash",
                        SourceId = UsageSourceIds.DemoSeed,
                        SourceKind = SourceKind.Synthetic,
                        UsageScope = UsageScope.Api,
                        CostBasis = CostBasis.ListPriceEstimate,
                        InputTokens = 500000 + i * 5000,
                        OutputTokens = 250000 + i * 2000,
                        CacheReadTokens = 120000 + i * 1000,
                        CacheWriteTokens = 45000 + i * 400,
                        CostUsd = 0.40m + i * 0.02m,
                        RequestCount = 200 + i,
                    }
                );

                db.DailyAggregates.Add(
                    new DailyAggregate
                    {
                        Date = date,
                        Provider = Provider.Copilot,
                        Model = "copilot-chat",
                        SourceId = UsageSourceIds.DemoSeed,
                        SourceKind = SourceKind.Synthetic,
                        UsageScope = UsageScope.Subscription,
                        CostBasis = CostBasis.Notional,
                        InputTokens = 40000 + i * 200,
                        OutputTokens = 20000 + i * 100,
                        CacheReadTokens = 0,
                        CacheWriteTokens = 0,
                        CostUsd = 0.30m + i * 0.01m,
                        RequestCount = 15 + i,
                    }
                );
            }

            db.Subscriptions.Add(
                new Subscription
                {
                    Provider = Provider.Copilot,
                    Name = "GitHub Copilot Business",
                    CostAmount = 19.00m,
                    Currency = "USD",
                    BillingDay = 1,
                    ActiveFrom = today.PlusDays(-60),
                    ActiveTo = null,
                }
            );

            db.Subscriptions.Add(
                new Subscription
                {
                    Provider = Provider.Anthropic,
                    Name = "Claude Pro",
                    CostAmount = 18.00m,
                    Currency = "GBP",
                    BillingDay = 15,
                    ActiveFrom = today.PlusDays(-30),
                    ActiveTo = null,
                    ExtraUsageCost = 5.50m,
                }
            );

            db.BudgetRules.Add(
                new BudgetRule
                {
                    Provider = null,
                    Period = BillingPeriod.Daily,
                    ThresholdGbp = 5.00m,
                    EvaluationStartsOn = today,
                }
            );

            db.BudgetRules.Add(
                new BudgetRule
                {
                    Provider = Provider.Anthropic,
                    Period = BillingPeriod.Monthly,
                    ThresholdGbp = 150.00m,
                    EvaluationStartsOn = today,
                }
            );

            db.Insights.Add(
                new Insight
                {
                    GeneratedAt = clock.GetCurrentInstant(),
                    PeriodStart = today.PlusDays(-7),
                    PeriodEnd = today,
                    InsightType = InsightType.Anomaly,
                    Title = "Spend Spike on Claude 3.5 Sonnet",
                    Body =
                        "Your Anthropic API cost spiked by 45% yesterday compared to the previous 7-day average. This was driven by a large batch code generation task.",
                    Data = "{\"spikePercent\":45}",
                }
            );

            db.Insights.Add(
                new Insight
                {
                    GeneratedAt = clock.GetCurrentInstant().Minus(Duration.FromHours(2)),
                    PeriodStart = today.PlusDays(-7),
                    PeriodEnd = today,
                    InsightType = InsightType.Efficiency,
                    Title = "Gemini Flash Cache Hits High",
                    Body =
                        "Google Gemini 1.5 Flash query cache hit rate reached 82%, saving approximately $12.40 in input token costs over the past 3 days.",
                    Data = "{\"savingsUsd\":12.4}",
                }
            );

            db.Insights.Add(
                new Insight
                {
                    GeneratedAt = clock.GetCurrentInstant().Minus(Duration.FromHours(5)),
                    PeriodStart = today.PlusDays(-7),
                    PeriodEnd = today,
                    InsightType = InsightType.Recommendation,
                    Title = "Switch simple chat completions to Haiku",
                    Body =
                        "43% of your Claude 3.5 Sonnet requests contain prompts under 200 tokens with low complexity. Switching these to Claude 3.5 Haiku could reduce your Anthropic spend by $15.50/month.",
                    Data = "{\"potentialSavingsUsd\":15.5}",
                }
            );

            await SeedBilledLedgerAsync(db, today, clock.GetCurrentInstant(), ct);

            await db.SaveChangesAsync(ct);
            return Results.Ok("Seed successful");
        }
    );
}

api.MapEventsEndpoints();
api.MapCavemanEndpoints();
api.MapActivityEndpoints();
api.MapGitHubActivityEndpoints();
api.MapAdversarialReviewEndpoints();
api.MapAggregatesEndpoints();
api.MapInsightsEndpoints();
api.MapSubscriptionsEndpoints();
api.MapBudgetRulesEndpoints();
api.MapNotificationSettingsEndpoints();
api.MapSpendCatalogEndpoints();
api.MapSpendEntriesEndpoints();
api.MapSourceStatusEndpoints();

await app.RunAsync();

// Test-enabling: `public partial class Program` exposes the top-level-statement entry
// point so WebApplicationFactory<Program> can host it; ConfigureForwardedHeaders is
// pulled out to a named, independently testable method (same body as before, no
// behaviour change) so a spoofed-XFF test can build the exact same ForwardedHeadersOptions
// without spinning up the whole app.
// Required as the WebApplicationFactory<TEntryPoint> marker for integration tests.
// ReSharper disable once ClassNeverInstantiated.Global
public partial class Program
{
    protected Program() { }

    /// <summary>
    /// Vendor keys, and the monthly GBP charge, used to seed billed evidence in Development.
    /// Keys match the <c>SpendVendors</c> catalog migrations; a key absent from the catalog is
    /// skipped rather than failing the seed.
    /// </summary>
    private static readonly (string VendorKey, decimal MonthlyGbp)[] SeedBilledMonthlyGbp =
    [
        ("anthropic", 18.00m),
        ("coderabbit", 12.00m),
        ("github-actions", 7.50m),
    ];

    private static readonly string[] SeedBilledVendorKeys = [.. SeedBilledMonthlyGbp.Select(v => v.VendorKey)];

    /// <summary>
    /// Seeds billed evidence for the Reporting tab, which reads the spend ledger rather than
    /// <c>DailyAggregates</c>. Without it every Reporting tile reads "Not reported" on a fresh
    /// install and the tab cannot show what it is for.
    /// <para>
    /// Vendors are resolved by their stable catalog key rather than by hardcoded GUID, and a
    /// vendor that is missing — or that carries no default category — is skipped rather than
    /// failing the whole seed.
    /// </para>
    /// </summary>
    private static async Task SeedBilledLedgerAsync(
        AiObservatoryDbContext db,
        LocalDate today,
        Instant seededAt,
        CancellationToken ct
    )
    {
        var vendors = await db
            .SpendVendors.Where(v => SeedBilledVendorKeys.Contains(v.Key))
            .Select(v => new
            {
                v.Key,
                v.Id,
                v.DefaultCategoryId,
            })
            .ToListAsync(ct);

        var firstOfThisMonth = today.PlusDays(1 - today.Day);

        foreach (var (vendorKey, monthlyGbp) in SeedBilledMonthlyGbp)
        {
            var vendor = vendors.Find(v => v.Key == vendorKey);
            if (vendor?.DefaultCategoryId is not { } categoryId)
            {
                continue;
            }

            // One charge per month over three months so the Reporting comparison
            // ("previous period") has billed evidence on both sides of the boundary.
            for (int monthsBack = 0; monthsBack < 3; monthsBack++)
            {
                var occurredOn = firstOfThisMonth.PlusMonths(-monthsBack);
                db.SpendEntries.Add(
                    new SpendEntry
                    {
                        OccurredOn = occurredOn,
                        VendorId = vendor.Id,
                        CategoryId = categoryId,
                        Amount = monthlyGbp,
                        Currency = "GBP",
                        AmountGbp = monthlyGbp,
                        FxRate = 1m,
                        Description = "Demo seed — synthetic billed charge",
                        Source = SpendSource.Api,
                        EntryKey = $"demo-seed:{vendorKey}:{occurredOn.Year:D4}-{occurredOn.Month:D2}",
                        RecordedAt = seededAt,
                        SourceId = UsageSourceIds.DemoSeed,
                        SourceKind = SourceKind.Synthetic,
                        UsageScope = UsageScope.Subscription,
                        CostBasis = CostBasis.Billed,
                        ObservedAt = seededAt,
                    }
                );
            }
        }
    }

    internal static void ValidateApiKeys(WebApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
        {
            return;
        }

        var adminKey = builder.Configuration["OBSERVATORY_API_KEY"];
        var readOnlyKey = builder.Configuration["OBSERVATORY_READONLY_API_KEY"];
        var ideKey = builder.Configuration["OBSERVATORY_IDE_API_KEY"];
        ValidateApiKey(adminKey, "OBSERVATORY_API_KEY");
        ValidateApiKey(readOnlyKey, "OBSERVATORY_READONLY_API_KEY");
        ValidateIdeApiKey(ideKey);

        // The null-forgiving operators below are safe only because the validators throw on a
        // bad value rather than returning it — keep that throw-or-continue contract.
        if (ApiKeyComparer.FixedTimeEquals(adminKey!, readOnlyKey!))
        {
            throw new InvalidOperationException(
                "OBSERVATORY_API_KEY and OBSERVATORY_READONLY_API_KEY must be different outside Development."
            );
        }
        if (ApiKeyComparer.FixedTimeEquals(ideKey!, adminKey!) || ApiKeyComparer.FixedTimeEquals(ideKey!, readOnlyKey!))
        {
            throw new InvalidOperationException(
                "OBSERVATORY_IDE_API_KEY must be different from the existing API keys."
            );
        }
    }

    internal static void ValidateRateLimitPermitLimit(WebApplicationBuilder builder)
    {
        var permitLimit = builder.Configuration.GetValue("RateLimiting:ApiPermitLimit", 120);
        if (permitLimit <= 0)
        {
            throw new InvalidOperationException("RateLimiting:ApiPermitLimit must be a positive integer.");
        }
    }

    private static void ValidateIdeApiKey(string? value)
    {
        ValidateApiKey(value, "OBSERVATORY_IDE_API_KEY");
        if (value != value!.Trim())
        {
            throw new InvalidOperationException(
                "OBSERVATORY_IDE_API_KEY must be set to a non-default, unpadded value outside Development."
            );
        }
    }

    private static void ValidateApiKey(string? value, string name)
    {
        // The length floor is what makes this an anti-guessability check rather than a
        // presence check: "x" passed every gate below yet would stand as a bearer credential.
        const int minimumLength = 16;
        if (
            string.IsNullOrWhiteSpace(value)
            || value.Length < minimumLength
            || value == "change-me"
            || value.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException(
                $"{name} must be set to a non-default value of at least {minimumLength} characters outside Development."
            );
        }
    }

    internal static void AddApplicationInsightsIfConfigured(WebApplicationBuilder builder)
    {
        if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            builder.Services.AddApplicationInsightsTelemetry();
        }
    }

    /// <summary>
    /// True when the legacy BUDGET_ALERT_EMAIL_TO value is safe to seed into
    /// NotificationSettings. Routed through the same IsValidEmail gate the settings endpoint
    /// applies: an invalid value stored here would throw in EmailAlertNotifier at send time and
    /// wedge the alert claim into an endless retry loop (S4). The Key Vault prefix check is the
    /// same gate every other configuration value in this composition root carries — an
    /// unresolved reference arrives as the literal "@Microsoft.KeyVault(...)" string.
    /// </summary>
    internal static bool IsSeedableLegacyAlertEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase)
        && NotificationSettingsEndpoints.IsValidEmail(value);

    internal static async Task SeedLegacyAlertEmailAsync(
        AiObservatoryDbContext db,
        IServiceProvider services,
        IConfiguration configuration
    )
    {
        var legacyEmail = configuration["BUDGET_ALERT_EMAIL_TO"];
        if (!IsSeedableLegacyAlertEmail(legacyEmail))
        {
            if (!string.IsNullOrWhiteSpace(legacyEmail))
            {
                services
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("AiObservatory.Api.Startup")
                    .LogWarning(
                        "BUDGET_ALERT_EMAIL_TO is not a valid email address; skipping the legacy alert-email seed."
                    );
            }
            return;
        }

        var clock = services.GetRequiredService<IClock>();
        db.NotificationSettings.Add(
            new NotificationSettings { AlertEmailTo = legacyEmail, UpdatedAt = clock.GetCurrentInstant() }
        );
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost the insert race to another instance starting concurrently (overlapping
            // old/new containers during a deploy). Best-effort, one-time backfill -- someone
            // else already won, which is the outcome this backfill wants anyway.
        }
    }

    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions o)
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        o.ForwardLimit = 1;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.Parse("10.0.0.0"), 8));
        o.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.Parse("172.16.0.0"), 12));
        o.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.Parse("192.168.0.0"), 16));
    }
}
