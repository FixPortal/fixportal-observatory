using System.Globalization;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Data.Spend;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Services.Intelligence;

/// <summary>
/// Runs one insight-generation pass for a given analysis date: load the trailing
/// 30-day aggregates + active subscriptions, build the prompt, call the model,
/// parse and persist the insights, then re-check budget alerts.
/// Shared by the daily <see cref="IntelligenceWorkerService"/> and the on-demand
/// <c>POST /api/insights/generate</c> endpoint so both follow the exact same path.
/// </summary>
public interface IInsightGenerator
{
    Task<InsightGenerationResult> GenerateForDateAsync(LocalDate analysisDate, CancellationToken ct = default);
}

/// <summary>
/// How many insights the model produced (<see cref="Generated"/>) versus how many survived
/// deduplication and were persisted (<see cref="Persisted"/>), so a caller can tell "the LLM
/// produced nothing" apart from "everything was suppressed".
/// </summary>
public sealed record InsightGenerationResult(int Generated, int Persisted);

public sealed class InsightGenerator(
    IUsageRepository repository,
    AiObservatoryDbContext db,
    AnthropicIntelligenceClient client,
    PromptBuilder promptBuilder,
    InsightResponseParser parser,
    FxRateProvider fx,
    BudgetAlertService budgetAlertService,
    IClock clock,
    ILogger<InsightGenerator> logger
) : IInsightGenerator
{
    public async Task<InsightGenerationResult> GenerateForDateAsync(
        LocalDate analysisDate,
        CancellationToken ct = default
    )
    {
        if (!client.IsConfigured)
        {
            return new InsightGenerationResult(0, 0);
        }
        var today = clock.GetCurrentInstant().InUtc().Date;
        var from = analysisDate.PlusDays(-29);

        var aggregates = await repository.GetAggregatesAsync(from, analysisDate, ct);
        var subscriptions = await repository.GetActiveSubscriptionsAsync(today, ct);
        var usdToGbp = await fx.GetUsdToGbpAsync(ct);

        var prompt = promptBuilder.Build(aggregates, subscriptions, from, analysisDate, usdToGbp);
        var json = await client.GenerateInsightsJsonAsync(prompt, ct);
        var insights = parser.Parse(json, from, analysisDate, clock.GetCurrentInstant());

        // The daily worker and the generate endpoint can overlap, so the read-check-write runs
        // inside a transaction holding a per-date advisory lock, and the "recent" set is
        // (re-)read only after the lock is held. Without the lock both runs read the same
        // recent set and each persist the full batch — exactly the duplicate cards the
        // deduplicator exists to prevent. The prompt build and the LLM call stay outside:
        // they are slow and read nothing the lock protects.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockKey = $"insight-generation:{analysisDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            ct
        );

        // Recent, not-yet-dealt-with insights the new batch is checked against, so a
        // repeat of an already-open story is skipped -- see InsightDeduplicator. Grown
        // in place as insights are added, so two same-subject repeats generated in this
        // very run are also caught, not just repeats across days.
        var recent = (await repository.GetUnacknowledgedInsightsAsync(ct)).ToList();
        var knownSubjects = KnownSubjects(aggregates, subscriptions);
        var now = clock.GetCurrentInstant();

        var added = 0;
        foreach (var insight in insights)
        {
            if (InsightDeduplicator.ShouldSuppress(insight, recent, knownSubjects, now))
            {
                logger.LogInformation(
                    "Suppressed {InsightType} insight '{Title}': an unacknowledged insight already covers its subject.",
                    insight.InsightType,
                    insight.Title
                );
                continue;
            }
            await repository.AddInsightAsync(insight, ct);
            recent.Add(insight);
            added++;
        }

        await transaction.CommitAsync(ct);
        await budgetAlertService.CheckAndAlertAsync(ct);
        return new InsightGenerationResult(insights.Count, added);
    }

    private static InsightKnownSubjects KnownSubjects(
        IReadOnlyList<DailyAggregate> aggregates,
        IReadOnlyList<Subscription> subscriptions
    ) =>
        new(
            aggregates
                .Select(a => a.Model)
                .Where(model => !string.IsNullOrWhiteSpace(model) && model.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            aggregates
                .Select(a => a.Provider.ToString())
                .Concat(subscriptions.Select(s => s.Provider.ToString()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        );
}
