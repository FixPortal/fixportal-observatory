using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

public static class EventsEndpoints
{
    private static readonly JsonDocumentOptions RawPayloadJsonOptions = new() { AllowDuplicateProperties = false };

    public static void MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events/{id:guid}", GetEventByIdAsync).WithName("GetEventById");
        app.MapPost("/events", RecordEventAsync);
        app.MapGet("/events", GetEventsAsync);
        app.MapGet("/events/local-snapshots", GetLocalSnapshotsAsync);
        app.MapPatch("/events/{eventKey}/cost", PatchEventCostAsync);
    }

    private static async Task<IResult> GetEventByIdAsync(Guid id, AiObservatoryDbContext db)
    {
        var evt = await db.UsageEvents.FindAsync(id);
        return evt is not null ? Results.Ok(evt) : Results.NotFound();
    }

    private static async Task<IResult> RecordEventAsync(
        UsageEventRequest req,
        IUsageRepository repo,
        IClock clock,
        HttpContext ctx
    )
    {
        if (!Enum.TryParse<Provider>(req.Provider, ignoreCase: true, out var provider) || !Enum.IsDefined(provider))
        {
            return Results.BadRequest($"Unknown provider: {req.Provider}");
        }

        var provenanceError = TryReadProvenance(
            req,
            out var sourceId,
            out var sourceKind,
            out var usageScope,
            out var costBasis
        );
        if (provenanceError is not null)
        {
            return Results.BadRequest(provenanceError);
        }
        var costBasisError = ValidateCostBasis(req, costBasis);
        if (costBasisError is not null)
        {
            return Results.BadRequest(costBasisError);
        }

        var requestError = ValidateUsageRequest(req, out var rawPayload, out var eventKey);
        if (requestError is not null)
        {
            return Results.BadRequest(requestError);
        }

        var now = clock.GetCurrentInstant();
        var observedAt = req.ObservedAtUtc is { } suppliedObserved ? Instant.FromDateTimeOffset(suppliedObserved) : now;
        if (observedAt > now + Duration.FromMinutes(5))
        {
            return Results.BadRequest("ObservedAtUtc must not be in the future");
        }

        // Backfilled events (e.g. from the local usage sweeper) carry the time the
        // usage actually happened so they aggregate onto the right day; live hooks
        // omit it and get the ingestion instant, as before.
        var occurredAt = req.OccurredAtUtc is { } supplied ? Instant.FromDateTimeOffset(supplied) : now;
        if (occurredAt > now + Duration.FromMinutes(5))
        {
            return Results.BadRequest("OccurredAtUtc must not be in the future");
        }

        var evt = new UsageEvent
        {
            Provider = provider,
            OccurredAt = occurredAt,
            IngestedAt = now,
            Model = req.Model,
            InputTokens = req.InputTokens,
            OutputTokens = req.OutputTokens,
            CacheReadTokens = req.CacheReadTokens,
            CacheWriteTokens = req.CacheWriteTokens,
            CacheWrite1hTokens = req.CacheWrite1hTokens,
            ThoughtTokens = req.ThoughtTokens,
            CostUsd = IsEstimated(costBasis) ? null : req.CostUsd,
            Runtime = req.Runtime,
            SessionId = req.SessionId,
            AgentId = req.AgentId,
            RawPayload = rawPayload,
            SourceId = sourceId,
            SourceKind = sourceKind,
            UsageScope = usageScope,
            CostBasis = costBasis,
            ObservedAt = observedAt,
            EventKey = eventKey,
        };
        var result = IsEstimated(costBasis)
            ? await repo.RecordEstimatedEventAsync(evt, ctx.RequestAborted)
            : await repo.RecordEventAsync(evt, ctx.RequestAborted);

        return result.Disposition == RecordEventDisposition.Created
            ? Results.CreatedAtRoute("GetEventById", new { id = result.EventId }, new { Id = result.EventId })
            : Results.Ok(
                new
                {
                    Id = result.EventId,
                    Duplicate = result.Disposition == RecordEventDisposition.Unchanged,
                    Corrected = result.Disposition == RecordEventDisposition.Corrected,
                }
            );
    }

    private static async Task<IResult> GetEventsAsync(
        string provider,
        IUsageRepository repo,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 10_000
    )
    {
        if (!Enum.TryParse<Provider>(provider, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return Results.BadRequest($"Unknown provider: {provider}");
        }

        var fromInstant = from is { } f ? Instant.FromDateTimeOffset(f) : (Instant?)null;
        var toInstant = to is { } t ? Instant.FromDateTimeOffset(t) : (Instant?)null;
        var cappedLimit = Math.Clamp(limit, 1, 10_000);

        var events = await repo.GetEventsByProviderAsync(parsed, fromInstant, toInstant, cappedLimit, ct);
        return Results.Ok(events);
    }

    private static async Task<IResult> PatchEventCostAsync(
        string eventKey,
        string provider,
        string? sourceId,
        UpdateEventCostRequest req,
        IUsageRepository repo,
        CancellationToken ct
    )
    {
        if (!Enum.TryParse<Provider>(provider, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return Results.BadRequest($"Unknown provider: {provider}");
        }

        if (req.CostUsd < 0)
        {
            return Results.BadRequest("CostUsd must be non-negative");
        }

        if (req.CacheSavingsUsd < 0)
        {
            return Results.BadRequest("CacheSavingsUsd must be non-negative");
        }

        // An omitted sourceId targets only legacy-sourced rows: NormalizeSourceId defaults to
        // UsageSourceIds.LegacyApi and the repository lookup is source-scoped. A cost correction
        // for a non-legacy event with a matching eventKey therefore returns 404 — it never
        // patches a row stored under a different source identity.
        var normalizedSourceId = NormalizeSourceId(sourceId);
        if (normalizedSourceId.Length > 100)
        {
            return Results.BadRequest("SourceId must be 100 characters or fewer");
        }

        // Trim to match the stored key: POST persists req.EventKey.Trim(), so a padded
        // route value would otherwise miss the row and drop the cost correction as a 404.
        // Savings the operator did not itemise default to a known zero: the correction rebases
        // the row out of the repricer's scan, so an unknown (null) left behind could never be
        // cleared and would count in UnknownCacheSavingsCount forever.
        var result = await repo.PatchEventCostAsync(
            parsed,
            normalizedSourceId,
            eventKey.Trim(),
            req.CostUsd,
            req.CacheSavingsUsd ?? 0m,
            ct
        );

        return result is null
            ? Results.NotFound()
            : Results.Ok(
                new
                {
                    result.EventId,
                    result.OldCostUsd,
                    result.NewCostUsd,
                }
            );
    }

    private static async Task<IResult> GetLocalSnapshotsAsync(
        string? sourceId,
        IUsageRepository repo,
        CancellationToken ct
    )
    {
        // NormalizeSourceId would map a blank value to the legacy source; this endpoint
        // requires an explicit one, so reject blank before normalizing.
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return Results.BadRequest("SourceId is required");
        }

        var normalizedSourceId = NormalizeSourceId(sourceId);
        if (normalizedSourceId.Length > 100)
        {
            return Results.BadRequest("SourceId must be 100 characters or fewer");
        }

        return Results.Ok(await repo.GetLocalSnapshotsAsync(normalizedSourceId, ct));
    }

    internal static bool TryParseOrDefault<TEnum>(string? value, TEnum defaultValue, out TEnum parsed)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = defaultValue;
            return true;
        }

        var trimmed = value.Trim();
        return Enum.TryParse(trimmed, ignoreCase: true, out parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(Enum.GetName(parsed), trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSourceId(string? sourceId) =>
        string.IsNullOrWhiteSpace(sourceId) ? UsageSourceIds.LegacyApi : sourceId.Trim().ToLowerInvariant();

    private static string? TryReadProvenance(
        UsageEventRequest req,
        out string sourceId,
        out SourceKind sourceKind,
        out UsageScope usageScope,
        out CostBasis costBasis
    )
    {
        if (
            string.IsNullOrWhiteSpace(req.SourceId)
            || string.IsNullOrWhiteSpace(req.SourceKind)
            || string.IsNullOrWhiteSpace(req.UsageScope)
            || string.IsNullOrWhiteSpace(req.CostBasis)
        )
        {
            sourceId = UsageSourceIds.LegacyApi;
            sourceKind = SourceKind.Legacy;
            usageScope = UsageScope.Unknown;
            costBasis = CostBasis.Unknown;
            return "SourceId, SourceKind, UsageScope, and CostBasis provenance are required.";
        }

        sourceId = NormalizeSourceId(req.SourceId);
        sourceKind = SourceKind.Legacy;
        usageScope = UsageScope.Unknown;
        costBasis = CostBasis.Unknown;
        if (sourceId.Length > 100)
        {
            return "SourceId must be 100 characters or fewer";
        }

        if (!TryParseOrDefault(req.SourceKind, SourceKind.Legacy, out sourceKind))
        {
            return $"Unknown source kind: {req.SourceKind}";
        }

        if (!TryParseOrDefault(req.UsageScope, UsageScope.Unknown, out usageScope))
        {
            return $"Unknown usage scope: {req.UsageScope}";
        }

        return TryParseOrDefault(req.CostBasis, CostBasis.Unknown, out costBasis)
            ? null
            : $"Unknown cost basis: {req.CostBasis}";
    }

    private static string? ValidateCostBasis(UsageEventRequest req, CostBasis costBasis)
    {
        if (req.CostBasis is not null && costBasis == CostBasis.Billed)
        {
            return "Explicit Billed events must use the spend/billing path.";
        }
        // A None basis asserts the event carries no cost at all, so ANY non-zero CostUsd on the
        // same write is a contradiction; storing it would price a row the producer declared
        // cost-free. Guarding both signs keeps the rejection message specific — a negative value
        // would otherwise fall through to the generic token-count validation in ValidateUsageRequest.
        if (costBasis == CostBasis.None && req.CostUsd is not null and not 0m)
        {
            return "CostBasis None events must not carry a non-zero CostUsd.";
        }
        return null;
    }

    private static string? ValidateUsageRequest(UsageEventRequest req, out string rawPayload, out string? eventKey)
    {
        rawPayload = req.RawPayload ?? "{}";
        eventKey = string.IsNullOrWhiteSpace(req.EventKey) ? null : req.EventKey.Trim();
        if (HasInvalidTokenCounts(req))
        {
            return "Token counts and cost must be non-negative";
        }

        // CacheWrite1hTokens is a subset of CacheWriteTokens. This is enforced again by
        // the database constraint and lets pricing derive the five-minute remainder.
        if (req.CacheWrite1hTokens is { } cacheWrite1h && cacheWrite1h > (req.CacheWriteTokens ?? 0))
        {
            return "CacheWrite1hTokens must not exceed CacheWriteTokens";
        }

        try
        {
            JsonDocument.Parse(rawPayload, RawPayloadJsonOptions).Dispose();
        }
        catch (JsonException)
        {
            return "RawPayload must be valid JSON";
        }

        // 200 is coupled to the 256-char EventKey column (AiObservatoryDbContext): the repository
        // prepends "{Provider}:" for legacy-api rows, so the cap must leave headroom for the
        // longest provider name plus separator. Keep the three numbers in step.
        if (eventKey is { Length: > 200 })
        {
            return "EventKey must be 200 characters or fewer";
        }

        return HasOversizedIdentity(req) ? "Telemetry identity values must be 200 characters or fewer" : null;
    }

    private static bool HasInvalidTokenCounts(UsageEventRequest req) =>
        req.InputTokens < 0
        || req.OutputTokens < 0
        || req.CacheReadTokens is < 0
        || req.CacheWriteTokens is < 0
        || req.CacheWrite1hTokens is < 0
        || req.ThoughtTokens is < 0
        || req.CostUsd < 0;

    private static bool IsEstimated(CostBasis costBasis) =>
        costBasis is CostBasis.ListPriceEstimate or CostBasis.Notional;

    private static bool HasOversizedIdentity(UsageEventRequest req) =>
        req.Runtime is { Length: > 100 } || req.SessionId is { Length: > 200 } || req.AgentId is { Length: > 200 };
}

public record UsageEventRequest(
    string Provider,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    decimal? CostUsd,
    string? RawPayload,
    string? EventKey = null,
    DateTimeOffset? OccurredAtUtc = null,
    // Optional and defaulted so producers that predate the TTL split keep working: omitting
    // it prices the whole cache write at the five-minute rate, exactly as before.
    // ReSharper disable once InconsistentNaming
    long? CacheWrite1hTokens = null,
    long? ThoughtTokens = null,
    string? Runtime = null,
    string? SessionId = null,
    string? AgentId = null,
    string? SourceId = null,
    string? SourceKind = null,
    string? UsageScope = null,
    string? CostBasis = null,
    DateTimeOffset? ObservedAtUtc = null
);

public sealed record UpdateEventCostRequest(decimal CostUsd, decimal? CacheSavingsUsd);
