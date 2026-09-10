using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace AiObservatory.Data.Spend;

/// <summary>
/// USD-&gt;GBP rate from frankfurter.dev (ECB reference rates, free, no key), cached ~12h.
/// Costs are stored USD-native; this converts them for GBP presentation. An FX outage
/// must never break insight generation, so failures fall back to a static rate.
/// </summary>
public class FxRateProvider(HttpClient http, IMemoryCache cache, ILogger<FxRateProvider> logger)
{
    // Static fallback (~ recent USD->GBP) used only when the FX service is unreachable.
    private const decimal Fallback = 0.79m;
    private const string CacheKey = "fx:usd-gbp";
    private static readonly Uri FrankfurterBaseUri = new("https://api.frankfurter.dev");

    public virtual async Task<decimal> GetUsdToGbpAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out decimal cached))
        {
            return cached;
        }

        var rate = await FetchGbpRateAsync(
            new Uri(FrankfurterBaseUri, "/v1/latest?from=USD&to=GBP").AbsoluteUri,
            "USD->GBP",
            ct
        );

        if (rate <= 0m)
        {
            logger.LogWarning("FX USD->GBP unavailable; using fallback {Fallback}", Fallback);
            return Fallback; // not cached — allow a retry on the next call
        }

        cache.Set(CacheKey, rate, TimeSpan.FromHours(12));
        return rate;
    }

    /// <summary>
    /// Rate converting one unit of <paramref name="currency"/> into GBP on
    /// <paramref name="on"/>. The ledger freezes this at write, so a historical total never
    /// drifts with the market. Historical rates are immutable and therefore cached without
    /// expiry, unlike the 12-hour cache on the latest rate.
    /// </summary>
    /// <exception cref="FxUnavailableException">
    /// The rate could not be resolved for a currency other than USD or GBP. USD has a static
    /// fallback because it is the other currency phase-1 entries actually use; every other
    /// currency must fail the write rather than freeze an undetectably wrong rate.
    /// </exception>
    public virtual async Task<decimal> GetGbpRateOnAsync(string currency, LocalDate on, CancellationToken ct = default)
    {
        var code = currency.ToUpperInvariant();
        if (code == "GBP")
        {
            return 1m;
        }

        // Bound to the invariant culture explicitly, matching SpendEntryKey.Derive: the
        // implicit NodaTime formatter otherwise resolves through CultureInfo.CurrentCulture,
        // which would vary the cache key, the outbound URL and the log label by machine locale.
        var dateStr = on.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var key = $"fx:{code}-gbp:{dateStr}";
        if (cache.TryGetValue(key, out decimal cached))
        {
            return cached;
        }

        var rate = await FetchGbpRateAsync(
            new Uri(FrankfurterBaseUri, $"/v1/{dateStr}?from={code}&to=GBP").AbsoluteUri,
            $"{code}->GBP on {dateStr}",
            ct
        );

        if (rate <= 0m)
        {
            if (code == "USD")
            {
                logger.LogWarning(
                    "FX {Code}->GBP missing for {Date}; using fallback {Fallback}",
                    code,
                    dateStr,
                    Fallback
                );
                return Fallback; // not cached — allow a retry
            }

            logger.LogWarning("FX {Code}->GBP unavailable for {Date}", code, dateStr);
            throw new FxUnavailableException(code, on);
        }

        cache.Set(key, rate); // no expiry: a past date's rate cannot change
        return rate;
    }

    // Shared fetch: returns 0m for both "fetched but no GBP rate" and "request threw",
    // collapsing both failure shapes into one signal so each caller applies its own
    // fallback policy (GetUsdToGbpAsync always falls back; GetGbpRateOnAsync only for USD).
    private async Task<decimal> FetchGbpRateAsync(string url, string label, CancellationToken ct)
    {
        try
        {
            var resp = await http.GetFromJsonAsync<FrankfurterResponse>(url, ct);
            return resp?.Rates is { } r && r.TryGetValue("GBP", out var gbp) ? gbp : 0m;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The HttpClient's own request timeout fired, not the caller's token -- a
            // TaskCanceledException from that looks identical to a genuine cancellation
            // (TaskCanceledException derives from OperationCanceledException) but must NOT
            // be treated as one: a timeout is an ordinary FX failure like any other (USD
            // falls back, other currencies reject just that row), not a reason to abort
            // the batch.
            logger.LogWarning(ex, "FX fetch timed out for {Label}", label);
            return 0m;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A client disconnect mid-request (ct itself cancelled) must abort the write,
            // not silently land at the 0.79 fallback rate -- the ledger is the first caller
            // where that would freeze a permanently wrong row rather than just mis-render
            // an estimate. Falls through uncaught here; the caller's ct.IsCancellationRequested
            // check above already claimed the non-caller-cancellation case.
            logger.LogWarning(ex, "FX fetch failed for {Label}", label);
            return 0m;
        }
    }

    private sealed record FrankfurterResponse(Dictionary<string, decimal>? Rates);
}
