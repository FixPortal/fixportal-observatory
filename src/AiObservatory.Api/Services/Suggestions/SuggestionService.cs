using Microsoft.Extensions.Options;

namespace AiObservatory.Api.Services.Suggestions;

public class SuggestionService(
    SuggestionInputsLoader loader,
    IEnumerable<ISuggestionRule> rules,
    SuggestionStore store,
    IOptions<SuggestionOptions> options,
    ILogger<SuggestionService> logger
)
{
    /// <summary>Loads the inputs, runs every rule and applies the results. Returns the outcomes for logging.</summary>
    public virtual async Task<IReadOnlyList<RuleOutcome>> EvaluateAsync(CancellationToken ct = default)
    {
        var settings = options.Value;
        // The store takes its lock before this runs, so the inputs are never older than the stored state.
        return await store.EvaluateAndSaveAsync(
            async token =>
            {
                var inputs = await loader.LoadAsync(token);
                return rules.Select(rule => Run(rule, inputs, settings)).ToList();
            },
            settings,
            ct
        );
    }

    // One failing rule must not hide the others: it is recorded as not evaluated with its error class.
    private RuleOutcome Run(ISuggestionRule rule, SuggestionInputs inputs, SuggestionOptions settings)
    {
        try
        {
            return rule.Evaluate(inputs, settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Suggestion rule {RuleId} failed", rule.RuleId);
            return RuleOutcome.NotEvaluated(rule.RuleId, $"Rule failed: {ex.GetType().Name}");
        }
    }
}
