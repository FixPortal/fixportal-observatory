using System.Text.Json;
using AiObservatory.Api.Services.Suggestions;
using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class SuggestionsEndpoints
{
    private static readonly string[] Statuses = ["open", "dismissed", "snoozed", "resolved"];

    // Returning the builder is the standard fluent endpoint-mapping convention.
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapSuggestionsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/suggestions", GetSuggestionsAsync);
        app.MapPost("/suggestions/{id:guid}/dismiss", DismissAsync);
        app.MapPost("/suggestions/{id:guid}/snooze", SnoozeAsync);
        app.MapPost("/suggestions/refresh", RefreshAsync);
        return app;
    }

    private static async Task<IResult> GetSuggestionsAsync(string? status, SuggestionStore store, CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(status) ? "open" : status;
        if (!Statuses.Contains(wanted))
        {
            return Results.BadRequest("status must be one of open, dismissed, snoozed, resolved");
        }

        var suggestions = await store.ListAsync(wanted, ct);
        var ruleRuns = await store.ListRuleRunsAsync(ct);
        return Results.Ok(
            new
            {
                suggestions = suggestions.Select(ToDto),
                notEvaluated = ruleRuns
                    .Where(r => r.Status == "notEvaluated")
                    .Select(r => new { ruleId = r.RuleId, reason = r.Reason ?? "" }),
            }
        );
    }

    private static object ToDto(Suggestion s) =>
        new
        {
            id = s.Id,
            key = s.Key,
            ruleId = s.RuleId,
            subject = s.Subject,
            severity = s.Severity,
            title = s.Title,
            status = s.Status,
            snoozedUntil = s.SnoozedUntil?.ToString(),
            firstSeenAt = s.FirstSeenAt.ToString(),
            lastSeenAt = s.LastSeenAt.ToString(),
            evidence = JsonDocument.Parse(s.EvidenceJson).RootElement.Clone(),
        };

    private static async Task<IResult> DismissAsync(Guid id, SuggestionStore store, CancellationToken ct) =>
        await store.ChangeAsync(id, SuggestionLifecycle.Dismiss, ct) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> SnoozeAsync(
        Guid id,
        int? days,
        SuggestionStore store,
        IClock clock,
        CancellationToken ct
    )
    {
        // Optional in the signature so that a missing value is a 400 from here, after authorisation:
        // a required parameter that fails to bind answers 400 before the key filter can answer 401.
        if (days is not (>= 1 and <= 90))
        {
            return Results.BadRequest("days must be between 1 and 90");
        }

        var now = clock.GetCurrentInstant();
        return await store.ChangeAsync(id, s => SuggestionLifecycle.Snooze(s, days.Value, now), ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> RefreshAsync(SuggestionService service, CancellationToken ct)
    {
        var outcomes = await service.EvaluateAsync(ct);
        return Results.Ok(
            new
            {
                rules = outcomes.Select(o => new
                {
                    ruleId = o.RuleId,
                    evaluated = o.Evaluated,
                    reason = o.Reason,
                    suggestions = o.Candidates.Count,
                }),
            }
        );
    }
}
