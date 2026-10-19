using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

/// <summary>
/// Where the tax portal reports which expenses its spend feed could not post. The Observatory
/// keeps one health row and a counts-only summary: the body carries expense ids and reason
/// codes, never vendor text, descriptors or amounts, and the ids themselves are not stored.
/// </summary>
public static class SpendFeedStatusEndpoints
{
    public const string SourceId = "portal-spend-feed";

    // The portal runs the feed daily; the source reads stale after twice this.
    public static readonly Duration ExpectedRefreshInterval = Duration.FromDays(1);

    private const int MaxSkipped = 5000;

    private static readonly Duration MaxClockSkew = Duration.FromMinutes(5);

    private static readonly string[] Reasons = ["no-vendor", "no-category", "aged-out", "foreign-unreviewed"];

    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapSpendFeedStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/spend/feed-status", RecordAsync).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
        return app;
    }

    private static async Task<IResult> RecordAsync(
        SpendFeedStatusRequest req,
        SourceSyncStateStore states,
        IClock clock,
        CancellationToken ct
    )
    {
        if (Validate(req) is { } rejection)
        {
            return Results.BadRequest(rejection);
        }

        var now = clock.GetCurrentInstant();
        if (req.ReportedAt is { } claimed && Instant.FromDateTimeOffset(claimed) > now + MaxClockSkew)
        {
            return Results.BadRequest("reportedAt must not be in the future");
        }

        // A delayed report carries its own time so an older skip cannot overwrite a newer clean run.
        var attemptedAt = req.ReportedAt is { } reported ? Instant.FromDateTimeOffset(reported) : now;
        if (req.Skipped.Count == 0)
        {
            await states.MarkSuccessAsync(SourceId, ExpectedRefreshInterval, attemptedAt, null, ct);
        }
        else
        {
            // Unavailable (not failing, not stale) is the status that surfaces missing data in
            // the panel and the digest; a clean report flips it back to fresh.
            await states.MarkUnavailableAsync(
                SourceId,
                ExpectedRefreshInterval,
                attemptedAt,
                DescribeSkips(req.Skipped),
                ct,
                onlyIfLatestAttempt: req.ReportedAt is not null
            );
        }

        return Results.NoContent();
    }

    /// <summary>Returns a plain rejection message, or null when the report is sound. Never echoes caller text.</summary>
    internal static string? Validate(SpendFeedStatusRequest req)
    {
        if (req.Attempted < 0)
        {
            return "attempted must be zero or more";
        }
        if (req.Posted < 0 || req.Posted > req.Attempted)
        {
            return "posted must be between zero and attempted";
        }
        if (req.Skipped is null)
        {
            return "skipped is required";
        }
        if (req.Skipped.Count > MaxSkipped)
        {
            return $"skipped must have at most {MaxSkipped} entries";
        }
        if (req.Skipped.Any(s => s is null))
        {
            return "skipped entries must not be null";
        }
        if (req.Skipped.Any(s => s.ExpenseId <= 0))
        {
            return "expenseId must be positive";
        }
        if (req.Skipped.Any(s => !Reasons.Contains(s.Reason)))
        {
            return $"reason must be one of: {string.Join(", ", Reasons)}";
        }

        return null;
    }

    /// <summary>Counts only, e.g. "3 expenses skipped: 2 no-vendor, 1 no-category".</summary>
    internal static string DescribeSkips(IReadOnlyCollection<SpendFeedSkip> skipped)
    {
        var counts = skipped
            .GroupBy(s => s.Reason)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key}");
        return $"{skipped.Count} {(skipped.Count == 1 ? "expense" : "expenses")} skipped: {string.Join(", ", counts)}";
    }
}

public sealed record SpendFeedStatusRequest(
    int Attempted,
    int Posted,
    IReadOnlyList<SpendFeedSkip> Skipped,
    DateTimeOffset? ReportedAt = null
);

public sealed record SpendFeedSkip(long ExpenseId, string Reason);
