using System.Text.RegularExpressions;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

/// <summary>
/// Where a local sweep reports sessions it could not attribute to a provider. Those sessions are
/// not posted, so without this report their spend is invisible. One health row per machine, counts
/// only: tool and model names, never paths or session content.
/// </summary>
public static partial class SweepStatusEndpoints
{
    public const string SourceIdPrefix = "sweep-placement@";

    // Sweeps run on a schedule of about a day; the source reads stale after twice this.
    public static readonly Duration ExpectedRefreshInterval = Duration.FromDays(1);

    private const int MaxEntries = 200;

    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapSweepStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/sources/sweep-status", RecordAsync);
        return app;
    }

    private static async Task<IResult> RecordAsync(
        SweepStatusRequest req,
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
        var sourceId = SourceIdPrefix + req.Machine;
        if (req.Unplaced.Count == 0)
        {
            await states.MarkSuccessAsync(sourceId, ExpectedRefreshInterval, now, null, ct);
        }
        else
        {
            // Unavailable surfaces in the panel and the daily digest until a sweep places everything.
            await states.MarkUnavailableAsync(sourceId, ExpectedRefreshInterval, now, Describe(req.Unplaced), ct);
        }

        return Results.NoContent();
    }

    /// <summary>Returns a plain rejection message, or null when the report is sound. Never echoes caller text.</summary>
    internal static string? Validate(SweepStatusRequest req)
    {
        if (req.Machine is null || !MachinePattern().IsMatch(req.Machine))
        {
            return "machine must be 1-64 characters of a-z, 0-9 and hyphen";
        }
        if (req.Unplaced is null)
        {
            return "unplaced is required";
        }
        if (req.Unplaced.Count > MaxEntries)
        {
            return $"unplaced must have at most {MaxEntries} entries";
        }
        if (req.Unplaced.Any(u => u is null || u.Count <= 0))
        {
            return "count must be positive";
        }
        if (req.Unplaced.Any(u => u.Tool is null || !TokenPattern().IsMatch(u.Tool)))
        {
            return "tool must be 1-40 characters of a-z, 0-9 and hyphen";
        }
        if (req.Unplaced.Any(u => u.Model is null || !ModelPattern().IsMatch(u.Model)))
        {
            return "model must be 1-80 characters of letters, digits and . _ : / -";
        }

        return null;
    }

    private const int LastErrorMaxLength = 500;

    /// <summary>Counts only, e.g. "5 sessions unplaced: codex gpt-x (3), codex unknown (2)".</summary>
    internal static string Describe(IReadOnlyCollection<SweepUnplaced> unplaced)
    {
        var total = unplaced.Sum(u => u.Count);
        var parts = unplaced
            .OrderByDescending(u => u.Count)
            .ThenBy(u => u.Tool, StringComparer.Ordinal)
            .ThenBy(u => u.Model, StringComparer.Ordinal)
            .Take(5)
            .Select(u => $"{u.Tool} {u.Model} ({u.Count})");
        var more = unplaced.Count > 5 ? $", +{unplaced.Count - 5} more" : "";
        var text = $"{total} {(total == 1 ? "session" : "sessions")} unplaced: {string.Join(", ", parts)}{more}";
        // LastError is varchar(500); the total leads, so cutting the tail keeps the part that matters.
        return text.Length <= LastErrorMaxLength ? text : text[..(LastErrorMaxLength - 3)] + "...";
    }

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex MachinePattern();

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._:/-]{1,80}$")]
    private static partial Regex ModelPattern();
}

public sealed record SweepStatusRequest(string Machine, IReadOnlyList<SweepUnplaced> Unplaced);

public sealed record SweepUnplaced(string Tool, string Model, int Count);
