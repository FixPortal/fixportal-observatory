using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Pricing;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Ingest.Tests.Pricing;

public sealed class GooglePricingSourceTests
{
    private const string ApiKey = "synthetic-secret-key";
    private const string ServiceId = "synthetic-service";
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 8, 25, 12, 0);
    private static readonly LocalDate UsageDate = new(2026, 8, 25);
    private static readonly GoogleSkuMapping[] Mappings =
    [
        new(
            "sku-input-us",
            "Gemini Enterprise Agent Platform",
            ["Gemini Enterprise Agent Platform"],
            "us",
            "REGIONAL",
            "text",
            "standard",
            "none",
            128_000,
            "token",
            0m
        ),
        new(
            "sku-output-global",
            "Gemini Enterprise Agent Platform",
            ["Gemini Enterprise Agent Platform"],
            "global",
            "GLOBAL",
            "text-output",
            "standard",
            "none",
            128_000,
            "token",
            0m
        ),
    ];

    [Fact]
    public async Task FetchReadsEveryPageAndRetainsOnlyExactMappedDimensions()
    {
        var handler = Pages(Page1(), Page2());
        var logger = new CapturingLogger();
        var source = Source(handler, logger);

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);

        candidate.Should().NotBeNull();
        candidate.Provider.Should().Be(Provider.Google);
        candidate.SourceId.Should().Be(PricingSourceIds.GoogleCloudCatalog);
        candidate.SourceUrl.Should().Be("https://cloudbilling.googleapis.com/v1/services/synthetic-service/skus");
        candidate.RawEvidence.Should().NotContain(ApiKey).And.NotContain("pageToken").And.NotContain("sku-unmapped");

        var catalog = PricingCatalogJson.Deserialize<GooglePriceCatalog>(candidate.NormalizedCatalog);
        ((Action)catalog.Validate).Should().NotThrow();
        catalog.Entries.Should().HaveCount(2);
        catalog
            .Resolve(
                "Gemini Enterprise Agent Platform",
                "sku-input-us",
                "us",
                "text",
                "standard",
                "none",
                128_000,
                UsageDate
            )
            .Should()
            .NotBeNull();
        catalog
            .Resolve(
                "Gemini Enterprise Agent Platform",
                "sku-input-us",
                "europe",
                "text",
                "standard",
                "none",
                128_000,
                UsageDate
            )
            .Should()
            .BeNull();
        catalog
            .Resolve(
                "Gemini Enterprise Agent Platform",
                "SKU-INPUT-US",
                "us",
                "text",
                "standard",
                "none",
                128_000,
                UsageDate
            )
            .Should()
            .BeNull();
        var global = catalog.Resolve(
            "Gemini Enterprise Agent Platform",
            "sku-output-global",
            "global",
            "text-output",
            "standard",
            "none",
            128_000,
            UsageDate
        );
        global.Should().NotBeNull();
        global.GeoTaxonomyType.Should().Be("GLOBAL");
        global.GeoTaxonomyRegions.Should().BeEmpty();

        handler.Requests.Should().HaveCount(2);
        handler
            .Requests.Should()
            .OnlyContain(request => request.Headers.GetValues("X-Goog-Api-Key").Single() == ApiKey);
        handler
            .Requests.Should()
            .OnlyContain(request => !request.RequestUri!.Query.Contains("key=", StringComparison.OrdinalIgnoreCase));
        handler.Requests[1].RequestUri!.Query.Should().Be("?pageToken=page%20two%2F%2B%3F");
        logger
            .Messages.Should()
            .ContainSingle()
            .Which.Should()
            .Be("Google catalog fetched: 2 mapped SKU(s), 1 unknown SKU(s).");
    }

    [Fact]
    public async Task FetchWithNoVerifiedProductionMappingsRejectsBeforeNetworkAccess()
    {
        var handler = Pages(Page1());
        var source = new GooglePricingSource(
            new FakeClock(RetrievedAt),
            new CapturingLogger(),
            Options.Create(
                new IngestOptions { GoogleCloudCatalogApiKey = ApiKey, GoogleCloudCatalogServiceId = ServiceId }
            ),
            handler
        );

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("all-unknown")]
    [InlineData("zero")]
    public async Task FetchRejectsIncompleteOrDuplicateConfiguredSkuCoverage(string mutation)
    {
        var pages = CoveragePages(mutation);
        var source = Source(Pages(pages));

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("REGIONAL", "us", "europe-west1")]
    [InlineData("MULTI_REGIONAL", "us", null)]
    public async Task FetchRejectsImpossibleGeographyCardinality(
        string taxonomyType,
        string firstRegion,
        string? secondRegion
    )
    {
        var page = JsonNode.Parse(Page1())!.AsObject();
        page.Remove("nextPageToken");
        var taxonomy = page["skus"]![0]!["geoTaxonomy"]!.AsObject();
        taxonomy["type"] = taxonomyType;
        taxonomy["regions"] = new JsonArray(secondRegion is null ? [firstRegion] : [firstRegion, secondRegion]);
        var mapping = Mappings[0] with { GeoTaxonomyType = taxonomyType };
        var source = Source(Pages(page.ToJsonString()), mappings: [mapping]);

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task FetchPreservesTheExactMappedExpressionAndConvertsNanosWithoutFloatingPoint()
    {
        var source = Source(Pages(Page1(), Page2()));

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);
        var entry = PricingCatalogJson
            .Deserialize<GooglePriceCatalog>(candidate!.NormalizedCatalog)
            .Entries.Single(x => x.SkuId == "sku-input-us");

        entry.Description.Should().Be("Synthetic Agent Platform text input tokens in US");
        entry
            .EffectiveTime.Should()
            .Be(Instant.FromUtc(2026, 8, 20, 12, 34, 56) + Duration.FromNanoseconds(123_456_789));
        entry.EffectiveDateIsProviderDeclared.Should().BeTrue();
        entry.GeoTaxonomyType.Should().Be("REGIONAL");
        entry.ServiceRegions.Should().Equal("us");
        entry.PricingUnit.Should().Be("token");
        entry.PricingUnitDescription.Should().Be("token");
        entry.BaseUnit.Should().Be("token");
        entry.BaseUnitConversionFactor.Should().Be(1m);
        entry.DisplayQuantity.Should().Be(1_000_000m);
        entry.TierStartUsageAmount.Should().Be(0m);
        entry.UnitPriceUnits.Should().Be(0);
        entry.UnitPriceNanos.Should().Be(1250);
        entry.AggregationLevel.Should().Be("PROJECT");
        entry.AggregationInterval.Should().Be("DAILY");
        entry.AggregationCount.Should().Be(1);
        entry.Rate.Should().Be(1.25m);
    }

    [Fact]
    public async Task FetchRejectsTheWholeCandidateWhenALaterPageFailsWithoutLeakingResponseOrRequestData()
    {
        var handler = new RecordingHandler(
            (request, call) =>
                call == 1
                    ? Json(Page1())
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent($"do not leak {ApiKey} or {request.RequestUri!.Query}"),
                    }
        );
        var source = Source(handler);

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<HttpRequestException>();
        exception.Which.Message.Should().NotContain(ApiKey).And.NotContain("pageToken").And.NotContain("do not leak");
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("ambiguous-tier")]
    [InlineData("usage-unit")]
    [InlineData("missing-pricing")]
    [InlineData("nanos-bound")]
    [InlineData("nanos-sign")]
    [InlineData("service")]
    [InlineData("taxonomy")]
    [InlineData("aggregation-enum")]
    public async Task FetchRejectsMappedSkusWithUnrecognizedPricingExpressions(string mutation)
    {
        var firstPage = Mutate(Page1(), mutation);
        var source = Source(Pages(firstPage, Page2()));

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task FetchConvertsNonzeroMoneyUnitsAndNanosExactly()
    {
        var page = Page1().Replace("\"units\": \"0\"", "\"units\": \"1\"", StringComparison.Ordinal);
        var source = Source(Pages(page, Page2()));

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);
        var entry = PricingCatalogJson
            .Deserialize<GooglePriceCatalog>(candidate!.NormalizedCatalog)
            .Entries.Single(x => x.SkuId == "sku-input-us");

        entry.Rate.Should().Be(1_000_001.25m);
    }

    [Fact]
    public async Task FetchRejectsRedirectsWithoutFollowingThem()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("https://evil.example/skus");
        var handler = new RecordingHandler((_, _) => response);
        var source = Source(handler);

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public void ProductionHandlerFactoryDisablesAutomaticRedirects()
    {
        using var handler = GooglePricingSource.CreateHttpMessageHandler();

        handler.AllowAutoRedirect.Should().BeFalse();
    }

    [Fact]
    public async Task FetchAppliesTheLinkedTimeoutToABlockedHandler()
    {
        // Deterministic: the manual timer fires the linked timeout only after the handler is
        // genuinely blocked on it — no short real delay whose scheduling could outrace the
        // assertion on a loaded machine. The 30s request timeout never elapses in real time,
        // so an unwired seam hangs the fetch until the WaitAsync bound fails the test.
        var timeProvider = new ManualTimeProvider();
        var handlerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(
            async (_, _, token) =>
            {
                handlerBlocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Json("unreachable");
            }
        );
        var source = Source(handler, requestTimeout: TimeSpan.FromSeconds(30), timeProvider: timeProvider);

        var fetch = source.FetchAsync(TestContext.Current.CancellationToken);
        await handlerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        timeProvider.FireTimers();

        var act = () => fetch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchPropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var source = Source(
            new RecordingHandler(
                async (_, _, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Json("unreachable");
                }
            )
        );

        var act = () => source.FetchAsync(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchRejectsInvalidUtf8()
    {
        var source = Source(Pages(Bytes([0xC3, 0x28])));

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DecoderFallbackException>();
    }

    [Fact]
    public async Task FetchStopsAnUndeclaredStreamOverTwoMebibytes()
    {
        var content = new StreamContent(new MemoryStream(new byte[2 * 1024 * 1024 + 1]));
        content.Headers.ContentLength = null;
        var source = Source(Pages(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task FetchAcceptsAValidPageOfExactlyTwoMebibytes()
    {
        var page = ExactLimitPage();
        var source = Source(Pages(Bytes(page)), mappings: [Mappings[0]]);

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);

        candidate.Should().NotBeNull();
    }

    [Fact]
    public async Task FetchRejectsRepeatedPaginationTokens()
    {
        var secondPage = Page2().Replace("\n}", ",\n  \"nextPageToken\": \"page two/+?\"\n}", StringComparison.Ordinal);
        var source = Source(Pages(Page1(), secondPage));

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public void BundledCatalogIsValidAndContainsNoUnverifiedRates()
    {
        var catalog = PricingCatalogJson.Deserialize<GooglePriceCatalog>(Bundle());

        ((Action)catalog.Validate).Should().NotThrow();
        catalog.Currency.Should().Be("USD");
        catalog
            .SourceUrl.Should()
            .Be("https://cloud.google.com/gemini-enterprise-agent-platform/generative-ai/pricing");
        catalog.RetrievedAt.Should().Be(Instant.FromUtc(2026, 8, 25, 0, 0));
        catalog.Entries.Should().BeEmpty();
    }

    private static GooglePricingSource Source(
        RecordingHandler handler,
        CapturingLogger? logger = null,
        IReadOnlyList<GoogleSkuMapping>? mappings = null,
        TimeSpan? requestTimeout = null,
        TimeProvider? timeProvider = null
    ) =>
        new(
            new FakeClock(RetrievedAt),
            logger ?? new CapturingLogger(),
            Options.Create(
                new IngestOptions { GoogleCloudCatalogApiKey = ApiKey, GoogleCloudCatalogServiceId = ServiceId }
            ),
            mappings ?? Mappings,
            handler,
            requestTimeout,
            timeProvider
        );

    private static RecordingHandler Pages(params string[] pages) =>
        new((_, call) => call <= pages.Length ? Json(pages[call - 1]) : throw new InvalidOperationException());

    private static RecordingHandler Pages(params HttpResponseMessage[] pages) =>
        new((_, call) => call <= pages.Length ? pages[call - 1] : throw new InvalidOperationException());

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") };

    private static string Mutate(string json, string mutation) =>
        mutation switch
        {
            "currency" => json.Replace(
                "\"currencyCode\": \"USD\"",
                "\"currencyCode\": \"EUR\"",
                StringComparison.Ordinal
            ),
            "ambiguous-tier" => json.Replace(
                "\"tieredRates\": [",
                "\"tieredRates\": [{\"startUsageAmount\": 1, \"unitPrice\": {\"currencyCode\": \"USD\", \"units\": \"0\", \"nanos\": 1}},",
                StringComparison.Ordinal
            ),
            "usage-unit" => json.Replace(
                "\"usageUnit\": \"token\"",
                "\"usageUnit\": \"character\"",
                StringComparison.Ordinal
            ),
            "missing-pricing" => json.Replace(
                "\"pricingInfo\": [",
                "\"pricingInfoMissing\": [",
                StringComparison.Ordinal
            ),
            "nanos-bound" => json.Replace("\"nanos\": 1250", "\"nanos\": 1000000000", StringComparison.Ordinal),
            "nanos-sign" => json.Replace("\"units\": \"0\"", "\"units\": \"1\"", StringComparison.Ordinal)
                .Replace("\"nanos\": 1250", "\"nanos\": -1", StringComparison.Ordinal),
            "service" => json.Replace(
                "\"serviceDisplayName\": \"Gemini Enterprise Agent Platform\"",
                "\"serviceDisplayName\": \"Changed Service\"",
                StringComparison.Ordinal
            ),
            "taxonomy" => json.Replace("\"type\": \"REGIONAL\"", "\"type\": \"GLOBAL\"", StringComparison.Ordinal),
            "aggregation-enum" => json.Replace(
                "\"aggregationLevel\": \"PROJECT\"",
                "\"aggregationLevel\": \"TEAM\"",
                StringComparison.Ordinal
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

    private static string[] CoveragePages(string mutation)
    {
        if (mutation == "zero")
        {
            return ["{\"skus\":[]}"];
        }

        var first = JsonNode.Parse(Page1())!.AsObject();
        var second = JsonNode.Parse(Page2())!.AsObject();
        if (mutation == "missing")
        {
            first.Remove("nextPageToken");
            return [first.ToJsonString()];
        }

        if (mutation == "all-unknown")
        {
            foreach (var sku in first["skus"]!.AsArray().Concat(second["skus"]!.AsArray()))
            {
                sku!["skuId"] = $"unknown-{sku["skuId"]}";
            }

            return [first.ToJsonString(), second.ToJsonString()];
        }

        if (mutation == "duplicate")
        {
            second["skus"]!.AsArray().Insert(0, first["skus"]![0]!.DeepClone());
            return [first.ToJsonString(), second.ToJsonString()];
        }

        throw new ArgumentOutOfRangeException(nameof(mutation));
    }

    private static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static byte[] ExactLimitPage()
    {
        const int limit = 2 * 1024 * 1024;
        var page = JsonNode.Parse(Page1())!.AsObject();
        page["skus"]!.AsArray().RemoveAt(1);
        page.Remove("nextPageToken");
        var body = page.ToJsonString();
        var prefix = body[..^1] + ",\"padding\":\"";
        const string suffix = "\"}";
        var paddingLength = limit - Encoding.UTF8.GetByteCount(prefix + suffix);
        var bytes = Encoding.UTF8.GetBytes(prefix + new string('a', paddingLength) + suffix);
        if (bytes.Length != limit)
        {
            throw new InvalidOperationException("The exact-limit fixture has the wrong byte length.");
        }

        return bytes;
    }

    private static string Page1() => Fixture("google-skus-page-1.json");

    private static string Page2() => Fixture("google-skus-page-2.json");

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Fixtures", name));

    private static string Bundle() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "google.json"));

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _response;
        private int _calls;

        public RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> response)
            : this((request, call, _) => Task.FromResult(response(request, call))) { }

        public RecordingHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> response) =>
            _response = response;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return await _response(request, Interlocked.Increment(ref _calls), cancellationToken);
        }
    }

    // A TimeProvider whose timers never fire on their own: Change records nothing, so the
    // only way a CancelAfter cancellation happens is the test calling FireTimers. That turns
    // "the linked timeout interrupted a blocked request" into a synchronised sequence instead
    // of a race against a short real delay.
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }

        public void FireTimers()
        {
            foreach (var timer in _timers)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Fire() => callback(state);

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger<GooglePricingSource>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));
    }
}
