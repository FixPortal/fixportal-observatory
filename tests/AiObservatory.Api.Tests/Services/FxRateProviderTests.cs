using System.Net;
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace AiObservatory.Api.Tests.Services;

/// <summary>
/// The ledger converts once, at write, using the rate on the CHARGE date — not "now".
/// Converting at render would make a historical charge show a different figure every day.
/// </summary>
public sealed class FxRateProviderTests : IDisposable
{
    private readonly List<HttpClient> _httpClients = [];
    private readonly List<MemoryCache> _memoryCaches = [];

    public void Dispose()
    {
        _httpClients.ForEach(client => client.Dispose());
        _memoryCaches.ForEach(cache => cache.Dispose());
    }

    private FxRateProvider Create(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var cache = new MemoryCache(new MemoryCacheOptions());
        _httpClients.Add(http);
        _memoryCaches.Add(cache);
        return new FxRateProvider(http, cache, NullLogger<FxRateProvider>.Instance);
    }

    [Fact]
    public async Task GbpShortCircuitsToOneAndMakesNoRequest()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync(
            "GBP",
            new LocalDate(2026, 3, 15),
            TestContext.Current.CancellationToken
        );

        rate.Should().Be(1m);
        handler.Requested.Should().BeEmpty("GBP needs no conversion, so it must not cost a network call");
    }

    [Fact]
    public async Task UsesTheDatedEndpointForTheChargeDate()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.7412}}""");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync(
            "USD",
            new LocalDate(2026, 3, 15),
            TestContext.Current.CancellationToken
        );

        rate.Should().Be(0.7412m);
        handler.Requested.Should().ContainSingle().Which.Should().Contain("/v1/2026-03-15").And.Contain("from=USD");
    }

    [Fact]
    public async Task CachesPerDateSoTheSameDayIsFetchedOnce()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.7412}}""");
        var sut = Create(handler);
        var date = new LocalDate(2026, 3, 15);

        await sut.GetGbpRateOnAsync("USD", date, TestContext.Current.CancellationToken);
        await sut.GetGbpRateOnAsync("USD", date, TestContext.Current.CancellationToken);

        handler.Requested.Should().ContainSingle("a historical rate is immutable, so it caches indefinitely");
    }

    [Fact]
    public async Task FallsBackRatherThanFailingTheWrite()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync(
            "USD",
            new LocalDate(2026, 3, 15),
            TestContext.Current.CancellationToken
        );

        // 0.79m is FxRateProvider's private Fallback constant, documented as a recent
        // USD->GBP rate. USD is the only currency phase-1 entries offer besides GBP, so it
        // is the only one with a static fallback -- see ThrowsForANonUsdCurrencyOnOutage.
        rate.Should().Be(0.79m, "an FX outage must not block recording a real USD charge");
    }

    [Fact]
    public async Task ThrowsForANonUsdCurrencyOnOutage()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        var sut = Create(handler);
        var date = new LocalDate(2026, 3, 15);

        var act = () => sut.GetGbpRateOnAsync("EUR", date, TestContext.Current.CancellationToken);

        var ex = await act.Should()
            .ThrowAsync<FxUnavailableException>(
                "freezing a USD-shaped fallback rate against a EUR charge would be undetectably wrong"
            );
        ex.Which.Message.Should().Contain("EUR").And.Contain("2026-03-15");
    }

    /// <summary>
    /// A lower-case currency must resolve to the same rate and the same cache entry as its
    /// upper-case form. The ledger accepts "usd" (validation upper-cases it), but a caller
    /// reaching this directly must not get a second cache slot or a miss on the GBP shortcut.
    /// </summary>
    [Theory]
    [InlineData("gbp")]
    [InlineData("Gbp")]
    public async Task TreatsTheCurrencyCodeCaseInsensitively(string currency)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var sut = Create(handler);

        var rate = await sut.GetGbpRateOnAsync(
            currency,
            new LocalDate(2026, 3, 15),
            TestContext.Current.CancellationToken
        );

        rate.Should().Be(1m);
        handler.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task DoesNotCacheTheFallbackSoALaterCallCanStillSucceed()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        var sut = Create(handler);
        var date = new LocalDate(2026, 3, 15);

        await sut.GetGbpRateOnAsync("USD", date, TestContext.Current.CancellationToken);
        await sut.GetGbpRateOnAsync("USD", date, TestContext.Current.CancellationToken);

        handler
            .Requested.Should()
            .HaveCount(2, "caching 0.79 would freeze the fallback for every later write of that date");
    }

    /// <summary>
    /// A 200 whose body carries no GBP rate is a distinct failure from an outage and reaches
    /// the same "no rate" branch — the fetch collapses both into 0m deliberately.
    /// </summary>
    [Fact]
    public async Task TreatsAResponseWithoutAGbpRateAsUnavailable()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"EUR":1.17}}""");
        var sut = Create(handler);

        var act = () => sut.GetGbpRateOnAsync("EUR", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<FxUnavailableException>();
    }

    [Fact]
    public async Task TreatsAnAbsentRatesObjectAsUnavailable()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var sut = Create(handler);

        var act = () => sut.GetGbpRateOnAsync("EUR", new LocalDate(2026, 3, 15), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<FxUnavailableException>();
    }

    /// <summary>
    /// A caller-cancelled request must abort rather than land on the fallback: the ledger is
    /// the first caller where a wrong rate is frozen permanently rather than just mis-rendered.
    /// </summary>
    [Fact]
    public async Task PropagatesTheCallersCancellation()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.74}}""");
        var sut = Create(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancellationToken = cts.Token;

        var act = () => sut.GetGbpRateOnAsync("USD", new LocalDate(2026, 3, 15), cancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LatestRateReadsTheUndatedEndpoint()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.7412}}""");
        var sut = Create(handler);

        var rate = await sut.GetUsdToGbpAsync(TestContext.Current.CancellationToken);

        rate.Should().Be(0.7412m);
        handler.Requested.Should().ContainSingle().Which.Should().Contain("/v1/latest").And.Contain("from=USD");
    }

    [Fact]
    public async Task LatestRateIsCachedSoRepeatedRendersCostOneCall()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.7412}}""");
        var sut = Create(handler);

        await sut.GetUsdToGbpAsync(TestContext.Current.CancellationToken);
        await sut.GetUsdToGbpAsync(TestContext.Current.CancellationToken);

        handler.Requested.Should().ContainSingle();
    }

    /// <summary>
    /// Unlike the dated path, this one never throws: it renders an estimate, so an FX outage
    /// must degrade the figure rather than break insight generation.
    /// </summary>
    [Fact]
    public async Task LatestRateFallsBackOnAnOutageRatherThanThrowing()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        var sut = Create(handler);

        var rate = await sut.GetUsdToGbpAsync(TestContext.Current.CancellationToken);

        rate.Should().Be(0.79m);
    }

    [Fact]
    public async Task LatestRateDoesNotCacheTheFallback()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        var sut = Create(handler);

        await sut.GetUsdToGbpAsync(TestContext.Current.CancellationToken);
        await sut.GetUsdToGbpAsync(TestContext.Current.CancellationToken);

        handler.Requested.Should().HaveCount(2, "the fallback is not cached, so the next call retries the real rate");
    }
}
