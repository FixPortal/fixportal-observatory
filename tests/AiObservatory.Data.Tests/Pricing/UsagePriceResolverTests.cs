using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Npgsql;

namespace AiObservatory.Data.Tests.Pricing;

[Trait("Category", "Integration")]
public sealed class UsagePriceResolverTests : IAsyncLifetime
{
    private static readonly LocalDate EffectiveFrom = new(2026, 8, 24);
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 8, 24, 12, 0);
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(
        JsonSerializerDefaults.Web
    ).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    private AiObservatoryDbContext _db = null!;
    private PricingSnapshotStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConnection =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        var connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_price_resolver_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseNodaTime())
            .Options;
        _db = new AiObservatoryDbContext(options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        _store = new PricingSnapshotStore(_db);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _db.Database.EnsureDeletedAsync();
        }
        finally
        {
            await _db.DisposeAsync();
        }
    }

    [Fact]
    public void OpenAiCalculatorPricesEveryObservedLaneAndComputesCounterfactualCacheSavings()
    {
        var usage = Event(
            Provider.OpenAI,
            "gpt-5.4-2026-08-24",
            """{"processing":"standard","context":"short","region":"global"}""",
            input: 1_000_000,
            output: 1_000_000,
            cacheRead: 1_000_000,
            cacheWrite: 1_000_000
        );
        var catalog = OpenAiCatalog(
            new OpenAiPriceEntry(
                "gpt-5.4",
                ["gpt-5.4"],
                EffectiveFrom,
                false,
                "standard",
                "short",
                "global",
                2m,
                0.5m,
                10m,
                3m
            )
        );

        var quote = new OpenAiPriceCalculator().Calculate(usage, Json(catalog));

        quote.Should().Be(new UsagePriceQuote(15.5m, 0.5m));
    }

    [Theory]
    [InlineData("processing", "batch")]
    [InlineData("context", "long")]
    [InlineData("region", "us")]
    public void OpenAiCalculatorRequiresAnExactObservedDimensionMatch(string dimension, string wrongValue)
    {
        var dimensions = new Dictionary<string, string>
        {
            ["processing"] = "standard",
            ["context"] = "short",
            ["region"] = "global",
        };
        dimensions[dimension] = wrongValue;
        var usage = Event(Provider.OpenAI, "gpt-5.4", Json(dimensions));

        new OpenAiPriceCalculator().Calculate(usage, Json(OpenAiCatalog())).Should().BeNull();
    }

    [Fact]
    public void OpenAiCalculatorReturnsNullForUnknownModelWithoutFallback()
    {
        var usage = Event(
            Provider.OpenAI,
            "unknown-model",
            """{"processing":"standard","context":"short","region":"global"}"""
        );

        new OpenAiPriceCalculator().Calculate(usage, Json(OpenAiCatalog())).Should().BeNull();
    }

    [Fact]
    public void OpenAiCalculatorReturnsNullWhenAnObservedCacheLaneHasNoRate()
    {
        var usage = Event(
            Provider.OpenAI,
            "gpt-5.4",
            """{"processing":"standard","context":"short","region":"global"}""",
            cacheWrite: 1
        );
        var entry = OpenAiCatalog().Entries.Single() with { CacheWrite = null };

        new OpenAiPriceCalculator().Calculate(usage, Json(OpenAiCatalog(entry))).Should().BeNull();
    }

    [Fact]
    public void AnthropicCalculatorPricesCacheDurationsExactly()
    {
        var usage = Event(
            Provider.Anthropic,
            "claude-sonnet-5",
            """{"service_tier":"standard","speed":"standard","inference_geo":"global","cache_creation":{"ephemeral_5m_input_tokens":1000000,"ephemeral_1h_input_tokens":1000000}}""",
            input: 1_000_000,
            output: 1_000_000,
            cacheRead: 1_000_000,
            cacheWrite: 2_000_000,
            cacheWrite1h: 1_000_000
        );

        var quote = new AnthropicPriceCalculator().Calculate(usage, Json(AnthropicCatalog()));

        quote.Should().Be(new UsagePriceQuote(18.7m, -0.7m));
    }

    [Theory]
    [InlineData("batch", "standard", "global", 6)]
    [InlineData("standard", "fast", "global", 60)]
    [InlineData("standard", "standard", "us", 13.2)]
    public void AnthropicCalculatorAppliesOnlyObservedBatchFastAndGeography(
        string tier,
        string speed,
        string geography,
        double expected
    )
    {
        var usage = Event(
            Provider.Anthropic,
            "claude-sonnet-5",
            $$"""{"service_tier":"{{tier}}","speed":"{{speed}}","inference_geo":"{{geography}}"}""",
            input: 1_000_000,
            output: 1_000_000
        );

        var quote = new AnthropicPriceCalculator().Calculate(usage, Json(AnthropicCatalog()));

        quote!.CostUsd.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("batch", "standard", 1.35, 0.65)]
    [InlineData("standard", "fast", 13.5, 6.5)]
    public void AnthropicCalculatorStacksBatchAndFastWithCacheMultipliers(
        string tier,
        string speed,
        double expectedCost,
        double expectedSavings
    )
    {
        var usage = Event(
            Provider.Anthropic,
            "claude-sonnet-5",
            Json(
                new
                {
                    service_tier = tier,
                    speed,
                    inference_geo = "global",
                    cache_creation = new { ephemeral_5m_input_tokens = 1_000_000, ephemeral_1h_input_tokens = 0 },
                }
            ),
            input: 0,
            output: 0,
            cacheRead: 1_000_000,
            cacheWrite: 1_000_000
        );

        var quote = new AnthropicPriceCalculator().Calculate(usage, Json(AnthropicCatalog()));

        quote.Should().Be(new UsagePriceQuote((decimal)expectedCost, (decimal)expectedSavings));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"service_tier\":\"standard\",\"speed\":\"standard\"}")]
    [InlineData("{\"service_tier\":\"standard\",\"speed\":\"standard\",\"inference_geo\":\"not_available\"}")]
    [InlineData(
        "{\"service_tier\":\"standard\",\"speed\":\"fast\",\"inference_geo\":\"global\",\"cache_creation\":{\"ephemeral_5m_input_tokens\":1,\"ephemeral_1h_input_tokens\":0}}"
    )]
    public void AnthropicCalculatorReturnsNullWhenRequiredObservedDimensionsAreMissingOrInconsistent(string raw)
    {
        var usage = Event(
            Provider.Anthropic,
            "claude-sonnet-5",
            raw,
            cacheWrite: raw.Contains("cache_creation", StringComparison.Ordinal) ? 2 : 0
        );

        new AnthropicPriceCalculator().Calculate(usage, Json(AnthropicCatalog())).Should().BeNull();
    }

    [Fact]
    public void AnthropicCalculatorUsesStandardPublicRatesForSparseSubscriptionTelemetry()
    {
        var usage = Event(
            Provider.Anthropic,
            "claude-sonnet-5",
            "{}",
            cacheWrite: 1_000_000,
            costBasis: CostBasis.Notional
        );

        var quote = new AnthropicPriceCalculator().Calculate(usage, Json(AnthropicCatalog()));

        quote.Should().Be(new UsagePriceQuote(14.5m, -0.5m));
    }

    [Theory]
    [InlineData(false, false, 5.14, 0.76)]
    [InlineData(false, true, 3.084, 0.456)]
    [InlineData(true, false, 10.28, 1.52)]
    public void KimiCalculatorKeepsHighSpeedAndEligibleBatchDistinct(
        bool highSpeed,
        bool batch,
        double expectedCost,
        double expectedSavings
    )
    {
        var model = highSpeed ? "kimi-k2.7-code-highspeed" : "kimi-k2.7-code";
        var usage = Event(
            Provider.Moonshot,
            model,
            $$"""{"high_speed":{{highSpeed.ToString().ToLowerInvariant()}},"batch":{{batch.ToString().ToLowerInvariant()}}}""",
            input: 1_000_000,
            output: 1_000_000,
            cacheRead: 1_000_000
        );

        var quote = new KimiPriceCalculator().Calculate(usage, Json(KimiCatalog()));

        quote.Should().Be(new UsagePriceQuote((decimal)expectedCost, (decimal)expectedSavings));
    }

    [Theory]
    [InlineData("kimi-for-coding", "{\"high_speed\":false,\"batch\":false}")]
    [InlineData("kimi-k2.7-code", "{}")]
    [InlineData("kimi-k3", "{\"high_speed\":false,\"batch\":true}")]
    public void KimiCalculatorReturnsNullForGenericModelMissingDimensionsOrIneligibleBatch(string model, string raw)
    {
        new KimiPriceCalculator()
            .Calculate(Event(Provider.Moonshot, model, raw), Json(KimiCatalog()))
            .Should()
            .BeNull();
    }

    [Theory]
    [InlineData("kimi-code/kimi-for-coding", 4.95)]
    [InlineData("kimi-code/k3", 18)]
    [InlineData("kimi-code/k3-256k", 18)]
    public void KimiCalculatorMapsSubscriptionModelIdsToPublishedApiRates(string model, double expectedCost)
    {
        var usage = Event(Provider.Moonshot, model, "{}", costBasis: CostBasis.Notional);

        var quote = new KimiPriceCalculator().Calculate(usage, Json(KimiCatalog()));

        quote!.CostUsd.Should().Be((decimal)expectedCost);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("service", "Changed service")]
    [InlineData("sku_id", "SKU-input-us")]
    [InlineData("region", "europe")]
    [InlineData("modality", "image")]
    [InlineData("tier", "batch")]
    [InlineData("cache_lane", "read")]
    [InlineData("context_threshold", 1L)]
    public void GoogleCalculatorRequiresEveryExactSkuDimension(string? changedDimension, object? wrongValue)
    {
        var dimensions = new Dictionary<string, object?>
        {
            ["service"] = "Gemini Enterprise Agent Platform",
            ["sku_id"] = "sku-input-us",
            ["region"] = "us",
            ["modality"] = "text",
            ["tier"] = "standard",
            ["cache_lane"] = "none",
            ["context_threshold"] = 128_000L,
        };
        if (changedDimension is not null)
        {
            dimensions[changedDimension] = wrongValue;
        }
        var usage = Event(Provider.Google, "gemini-enterprise", Json(dimensions), input: 2_000_000);

        var quote = new GooglePriceCalculator().Calculate(usage, Json(GoogleCatalog()));

        if (changedDimension is null)
        {
            quote.Should().Be(new UsagePriceQuote(2.5m, 0m));
        }
        else
        {
            quote.Should().BeNull();
        }
    }

    [Fact]
    public async Task ResolverValuesGeminiDeveloperApiInputCacheOutputAndThoughtTokens()
    {
        var usage = Event(
            Provider.Google,
            "gemini-3.1-pro-preview",
            """{"service":"Gemini Developer API","tier":"standard","context":"short"}""",
            input: 1_000_000,
            output: 1_000_000,
            cacheRead: 1_000_000,
            thought: 1_000_000
        );
        var catalog = new GeminiDeveloperPriceCatalog(
            "USD",
            "https://ai.google.dev/gemini-api/docs/pricing",
            RetrievedAt,
            [
                new(
                    "gemini-3.1-pro-preview",
                    ["gemini-3.1-pro-preview"],
                    EffectiveFrom,
                    false,
                    "standard",
                    "short",
                    2m,
                    0.2m,
                    12m
                ),
            ]
        );
        const string evidence = "Gemini Developer API pricing evidence";
        await _store.ActivateAsync(
            new PricingSnapshotCandidate(
                Provider.Google,
                PricingSourceIds.GeminiDeveloperApi,
                RetrievedAt,
                catalog.SourceUrl,
                PricingSnapshotCandidate.ComputeContentHash(evidence, Json(catalog)),
                evidence,
                Json(catalog)
            ),
            TestContext.Current.CancellationToken
        );

        (await Resolver().ResolveAsync(usage, TestContext.Current.CancellationToken))
            .Should()
            .Be(new UsagePriceQuote(26.2m, 1.8m));
    }

    [Fact]
    public async Task ResolverValuesGoogleSubscriptionUsageAtStandardGeminiDeveloperApiRates()
    {
        var usage = Event(
            Provider.Google,
            "gemini-3.1-pro-high",
            "{}",
            input: 1_000_000,
            output: 1_000_000,
            thought: 1_000_000,
            costBasis: CostBasis.Notional
        );
        var catalog = new GeminiDeveloperPriceCatalog(
            "USD",
            "https://ai.google.dev/gemini-api/docs/pricing",
            RetrievedAt,
            [
                new(
                    "gemini-3.1-pro-preview",
                    ["gemini-3.1-pro-preview", "gemini-3.1-pro"],
                    EffectiveFrom,
                    false,
                    "standard",
                    "short",
                    2m,
                    0.2m,
                    12m
                ),
            ]
        );
        const string evidence = "Gemini Developer API pricing evidence";
        await _store.ActivateAsync(
            new PricingSnapshotCandidate(
                Provider.Google,
                PricingSourceIds.GeminiDeveloperApi,
                RetrievedAt,
                catalog.SourceUrl,
                PricingSnapshotCandidate.ComputeContentHash(evidence, Json(catalog)),
                evidence,
                Json(catalog)
            ),
            TestContext.Current.CancellationToken
        );

        var quote = await Resolver().ResolveAsync(usage, TestContext.Current.CancellationToken);

        quote.Should().Be(new UsagePriceQuote(26m, 0m));
    }

    [Fact]
    public async Task ResolverPricesHistoryBeforeTheEarliestAssumedEffectiveWindow()
    {
        // Bundled catalogs stamp effectiveFrom with the fetch date (assumed, not
        // provider-declared), so usage predating the first fetch resolves against the
        // earliest known window instead of becoming unpriceable.
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate(OpenAiCatalog()), ct);
        var resolver = Resolver();
        var dimensions = """{"processing":"standard","context":"short","region":"global"}""";

        var before = await resolver.ResolveAsync(
            Event(Provider.OpenAI, "gpt-5.4", dimensions, occurredOn: EffectiveFrom.PlusDays(-1)),
            ct
        );
        var effective = await resolver.ResolveAsync(
            Event(Provider.OpenAI, "gpt-5.4", dimensions, occurredOn: EffectiveFrom),
            ct
        );

        before!.CostUsd.Should().Be(12m);
        effective!.CostUsd.Should().Be(12m);
    }

    [Fact]
    public async Task ResolverFallsThroughToAnOlderSnapshotWhenTheNewestCannotPriceTheModel()
    {
        // A catalog refresh that retires a model must not make its history unpriceable: the
        // retained snapshot that still carries the model is consulted next.
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate(OpenAiCatalog()), ct);
        var refreshed = new OpenAiPriceCatalog(
            "USD",
            "https://developers.openai.com/api/docs/pricing.md",
            RetrievedAt.Plus(Duration.FromDays(1)),
            [
                new OpenAiPriceEntry(
                    "gpt-6",
                    ["gpt-6"],
                    EffectiveFrom,
                    false,
                    "standard",
                    "short",
                    "global",
                    4m,
                    1m,
                    20m,
                    6m
                ),
            ]
        );
        await _store.ActivateAsync(Candidate(refreshed), ct);
        var usage = Event(
            Provider.OpenAI,
            "gpt-5.4",
            """{"processing":"standard","context":"short","region":"global"}"""
        );

        var quote = await Resolver().ResolveAsync(usage, ct);

        quote!.CostUsd.Should().Be(12m);
    }

    [Fact]
    public async Task ResolverFallsThroughToAnOlderSnapshotForANotionalModelRetiredFromTheActiveCatalog()
    {
        // Notional events get the same retained-snapshot fall-through as dated pricing: the
        // active catalog has dropped the model, so the older snapshot that still carries it
        // prices the event instead of leaving it permanently unpriced.
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate(OpenAiCatalog()), ct);
        var refreshed = new OpenAiPriceCatalog(
            "USD",
            "https://developers.openai.com/api/docs/pricing.md",
            RetrievedAt.Plus(Duration.FromDays(1)),
            [
                new OpenAiPriceEntry(
                    "gpt-6",
                    ["gpt-6"],
                    EffectiveFrom,
                    false,
                    "standard",
                    "short",
                    "global",
                    4m,
                    1m,
                    20m,
                    6m
                ),
            ]
        );
        await _store.ActivateAsync(Candidate(refreshed), ct);
        var usage = Event(Provider.OpenAI, "gpt-5.4", "{}", costBasis: CostBasis.Notional);

        var quote = await Resolver().ResolveAsync(usage, ct);

        quote!.CostUsd.Should().Be(12m);
    }

    [Fact]
    public async Task ResolverPricesNotionalUsageFromTheActiveSnapshotBeforeRetainedOnes()
    {
        // The active catalog prices notional usage; older retained snapshots are only a
        // fall-through for models the active one can no longer price.
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate(OpenAiCatalog()), ct);
        var refreshed = new OpenAiPriceCatalog(
            "USD",
            "https://developers.openai.com/api/docs/pricing.md",
            RetrievedAt.Plus(Duration.FromDays(1)),
            [
                new OpenAiPriceEntry(
                    "gpt-5.4",
                    ["gpt-5.4"],
                    EffectiveFrom,
                    false,
                    "standard",
                    "short",
                    "global",
                    4m,
                    1m,
                    20m,
                    6m
                ),
            ]
        );
        await _store.ActivateAsync(Candidate(refreshed), ct);
        var usage = Event(Provider.OpenAI, "gpt-5.4", "{}", costBasis: CostBasis.Notional);

        var quote = await Resolver().ResolveAsync(usage, ct);

        quote!.CostUsd.Should().Be(24m);
    }

    [Fact]
    public void OpenAiCalculatorDefaultsToTheStandardPublicTierForSparseNotionalTelemetry()
    {
        var usage = Event(Provider.OpenAI, "gpt-5.4", "{}", costBasis: CostBasis.Notional);

        var quote = new OpenAiPriceCalculator().Calculate(usage, Json(OpenAiCatalog()));

        quote.Should().Be(new UsagePriceQuote(12m, 0m));
    }

    [Fact]
    public void KimiCalculatorReturnsNullWhenTheModelSuffixAndHighSpeedFlagConflict()
    {
        // "…-highspeed" in the model id but high_speed:false in the payload: either reading
        // prices the wrong lane, so the event is surfaced as unpriceable instead.
        var usage = Event(Provider.Moonshot, "kimi-k2.7-code-highspeed", """{"high_speed":false,"batch":false}""");

        new KimiPriceCalculator().Calculate(usage, Json(KimiCatalog())).Should().BeNull();
    }

    [Fact]
    public async Task ResolverAppliesCurrentPublishedRatesToEarlierNotionalUsage()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate(OpenAiCatalog()), ct);
        var usage = Event(
            Provider.OpenAI,
            "gpt-5.4",
            """{"processing":"standard","context":"short","region":"global"}""",
            occurredOn: EffectiveFrom.PlusDays(-1),
            costBasis: CostBasis.Notional
        );

        var quote = await Resolver().ResolveAsync(usage, ct);

        quote!.CostUsd.Should().Be(12m);
    }

    [Fact]
    public async Task ResolverRateLimitsTheSameUnknownDimensionDiagnostic()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate(OpenAiCatalog()), ct);
        var logger = new CapturingLogger<UsagePriceResolver>();
        var usage = Event(Provider.OpenAI, "gpt-5.4", "{}");

        (await Resolver(logger).ResolveAsync(usage, ct)).Should().BeNull();
        (await Resolver(logger).ResolveAsync(usage, ct)).Should().BeNull();

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("context,processing,region");
    }

    [Fact]
    public async Task ResolverReturnsAnExactZeroQuoteWithoutCatalogOrPricingDimensions()
    {
        var usage = Event(Provider.OpenAI, "removed-local-snapshot", "{}", input: 0, output: 0);

        var quote = await Resolver().ResolveAsync(usage, TestContext.Current.CancellationToken);

        quote.Should().Be(new UsagePriceQuote(0m, 0m));
    }

    [Fact]
    public async Task ResolverBoundsTheDiagnosticForAnUntrustedLongModelName()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate(OpenAiCatalog()), ct);
        var logger = new CapturingLogger<UsagePriceResolver>();
        var model = $"diagnostic-{Guid.NewGuid():N}-" + new string('x', 10_000) + "\r\nsecret-tail";

        (await Resolver(logger).ResolveAsync(Event(Provider.OpenAI, model, "{}"), ct)).Should().BeNull();

        logger.Warnings.Should().ContainSingle();
        logger.Warnings.Single().Should().Contain(model[..48]);
        logger.Warnings.Single().Should().NotContain("secret-tail");
        logger.Warnings.Single().Length.Should().BeLessThan(512);
    }

    private UsagePriceResolver Resolver(ILogger<UsagePriceResolver>? logger = null) =>
        new(
            _store,
            [
                new OpenAiPriceCalculator(),
                new AnthropicPriceCalculator(),
                new KimiPriceCalculator(),
                new GooglePriceCalculator(),
            ],
            logger ?? new CapturingLogger<UsagePriceResolver>()
        );

    private static UsageEvent Event(
        Provider provider,
        string model,
        string raw,
        long input = 1_000_000,
        long output = 1_000_000,
        long cacheRead = 0,
        long cacheWrite = 0,
        long? cacheWrite1h = 0,
        long thought = 0,
        LocalDate? occurredOn = null,
        CostBasis costBasis = CostBasis.ListPriceEstimate
    ) =>
        new()
        {
            Provider = provider,
            Model = model,
            OccurredAt = (occurredOn ?? EffectiveFrom).AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant(),
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            CacheWrite1hTokens = cacheWrite1h,
            ThoughtTokens = thought,
            RawPayload = raw,
            CostBasis = costBasis,
        };

    private static OpenAiPriceCatalog OpenAiCatalog(OpenAiPriceEntry? entry = null) =>
        new(
            "USD",
            "https://developers.openai.com/api/docs/pricing.md",
            RetrievedAt,
            [
                entry
                    ?? new OpenAiPriceEntry(
                        "gpt-5.4",
                        ["gpt-5.4"],
                        EffectiveFrom,
                        false,
                        "standard",
                        "short",
                        "global",
                        2m,
                        0.5m,
                        10m,
                        3m
                    ),
            ]
        );

    private static AnthropicPriceCatalog AnthropicCatalog() =>
        new(
            "USD",
            "https://platform.claude.com/docs/en/about-claude/pricing.md",
            RetrievedAt,
            [
                new AnthropicPriceEntry(
                    "claude-sonnet-5",
                    ["claude-sonnet-5"],
                    EffectiveFrom,
                    false,
                    2m,
                    10m,
                    0.2m,
                    2.5m,
                    4m,
                    1m,
                    5m,
                    10m,
                    50m,
                    1.1m
                ),
            ]
        );

    private static KimiPriceCatalog KimiCatalog() =>
        new(
            "USD",
            "https://platform.kimi.ai/docs/llms.txt",
            RetrievedAt,
            [
                new KimiPriceEntry(
                    "kimi-k2.7-code",
                    ["kimi-k2.7-code"],
                    EffectiveFrom,
                    false,
                    0.19m,
                    0.95m,
                    4m,
                    false,
                    0.6m
                ),
                new KimiPriceEntry(
                    "kimi-k2.7-code-highspeed",
                    ["kimi-k2.7-code-highspeed"],
                    EffectiveFrom,
                    false,
                    0.38m,
                    1.9m,
                    8m,
                    true,
                    null
                ),
                new KimiPriceEntry("kimi-k3", ["kimi-k3"], EffectiveFrom, false, 0.3m, 3m, 15m, false, null),
            ]
        );

    private static GooglePriceCatalog GoogleCatalog() =>
        new(
            "USD",
            "https://cloud.google.com/billing/catalog",
            RetrievedAt,
            [
                new GooglePriceEntry(
                    "Gemini Enterprise Agent Platform",
                    "sku-input-us",
                    "services/test/skus/sku-input-us",
                    "Synthetic input price",
                    ["Gemini Enterprise Agent Platform"],
                    EffectiveFrom,
                    true,
                    EffectiveFrom.AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant(),
                    "us",
                    "REGIONAL",
                    ["us"],
                    ["us"],
                    "text",
                    "standard",
                    "none",
                    128_000,
                    "token",
                    "token",
                    "token",
                    "token",
                    1m,
                    1_000_000m,
                    0m,
                    "USD",
                    0,
                    1250,
                    "PROJECT",
                    "DAILY",
                    1,
                    1m,
                    1.25m
                ),
            ]
        );

    private static PricingSnapshotCandidate Candidate(OpenAiPriceCatalog catalog)
    {
        const string raw = "resolver catalog evidence";
        return new PricingSnapshotCandidate(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            catalog.RetrievedAt,
            catalog.SourceUrl,
            PricingSnapshotCandidate.ComputeContentHash(raw, Json(catalog)),
            raw,
            Json(catalog)
        );
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
