using NodaTime;

namespace AiObservatory.Data.Spend;

// Thrown by FxRateProvider.GetGbpRateOnAsync when a non-USD, non-GBP rate cannot be
// resolved. Unlike USD, there is no static fallback for every other currency, so the
// ledger write must fail rather than freeze a wrong rate.
public class FxUnavailableException(string currency, LocalDate on)
    : Exception($"FX rate unavailable for {currency} on {on:yyyy-MM-dd}");
