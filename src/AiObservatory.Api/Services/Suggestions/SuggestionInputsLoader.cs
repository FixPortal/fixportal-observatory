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
    public virtual async Task<SuggestionInputs> LoadAsync(CancellationToken ct = default)
    {
        var settings = options.Value;
        var today = clock.GetCurrentInstant().InUtc().Date;
        var last = today.PlusDays(-1);
        var seatWindow = new DateWindow(last.PlusDays(1 - settings.SeatWindowDays), last);
        var planWindow = new DateWindow(last.PlusDays(1 - settings.PlanWindowDays), last);

        var runs = (await reviews.GetRunsAsync(ct: ct))
            .Where(r =>
            {
                var date = r.RecordedAt.InUtc().Date;
                return date >= seatWindow.From && date <= seatWindow.To;
            })
            .ToList();
        var runIds = runs.Select(r => r.RunId).ToHashSet(StringComparer.Ordinal);
        var findings = (await reviews.GetFindingsAsync(ct: ct)).Where(f => runIds.Contains(f.RunId)).ToList();

        return new SuggestionInputs(
            seatWindow,
            planWindow,
            today,
            runs,
            findings,
            await usage.GetActiveSubscriptionsAsync(today, ct),
            await usage.GetAggregatesAsync(planWindow.From, planWindow.To, ct),
            await fx.TryGetUsdToGbpAsync(ct)
        );
    }
}
