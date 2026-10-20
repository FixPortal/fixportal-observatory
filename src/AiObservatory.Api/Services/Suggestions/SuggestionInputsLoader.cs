using AiObservatory.Data.Repositories;
using AiObservatory.Data.Spend;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Services.Suggestions;

/// <summary>Loads the immutable snapshot the rules evaluate. All windows end on the last complete UTC day.</summary>
public class SuggestionInputsLoader(
    IUsageRepository usage,
    IAdversarialReviewRepository reviews,
    FxRateProvider fx,
    IClock clock,
    IOptions<SuggestionOptions> options
)
{
    /// <summary>
    /// The live USD to GBP rate, fetched by the caller before it takes the store lock: a failed lookup is
    /// not cached and can run to the HTTP timeout, which must not happen while dismiss and snooze wait.
    /// </summary>
    public virtual Task<decimal?> ResolveUsdToGbpAsync(CancellationToken ct = default) => fx.TryGetUsdToGbpAsync(ct);

    public virtual async Task<SuggestionInputs> LoadAsync(decimal? usdToGbp, CancellationToken ct = default)
    {
        var settings = options.Value;
        var today = clock.GetCurrentInstant().InUtc().Date;
        var last = today.PlusDays(-1);
        var seatWindow = new DateWindow(last.PlusDays(1 - settings.SeatWindowDays), last);
        var planWindow = new DateWindow(last.PlusDays(1 - settings.PlanWindowDays), last);

        var runs = await reviews.GetRunsRecordedBetweenAsync(
            seatWindow.From.AtMidnight().InUtc().ToInstant(),
            today.AtMidnight().InUtc().ToInstant(),
            ct
        );
        var runIds = runs.Select(r => r.RunId).ToHashSet(StringComparer.Ordinal);
        var findings = await reviews.GetFindingsForRunsAsync(runIds, ct);

        return new SuggestionInputs(
            seatWindow,
            planWindow,
            today,
            runs,
            findings,
            await usage.GetActiveSubscriptionsAsync(today, ct),
            await usage.GetAggregatesAsync(planWindow.From, planWindow.To, ct),
            usdToGbp
        );
    }
}
