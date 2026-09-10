using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiObservatory.Api.Routing;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace AiObservatory.Api.Endpoints;

public static class IdeEndpoints
{
    private static readonly Guid ObservatoryPartnerId = Guid.Parse("753cb584-cd0b-4e16-9f08-6c0ce130a84a");
    private static readonly JsonSerializerOptions EventJsonOptions = CreateEventJsonOptions();

    public static void MapIdeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/routing-snapshot", GetRoutingSnapshot);
        endpoints.MapPost("/events", ReceiveEventAsync);
    }

    internal static IResult GetRoutingSnapshot(HttpContext context, RoutingCatalogService catalog, IClock clock)
    {
        var snapshot = catalog.GetSnapshot(clock.GetCurrentInstant());
        var etag = new EntityTagHeaderValue($"\"{snapshot.SnapshotId}\"", isWeak: true);
        context.Response.GetTypedHeaders().ETag = etag;
        if (
            context
                .Request.GetTypedHeaders()
                .IfNoneMatch?.Any(candidate =>
                    candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(etag, false)
                ) == true
        )
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.Bytes(
            JsonSerializer.SerializeToUtf8Bytes(snapshot, RoutingCatalogService.SerializerOptions),
            "application/json"
        );
    }

    internal static IdeEventEnvelope ParseEvent(ReadOnlySpan<byte> bytes, Instant now)
    {
        if (bytes.Length is 0 or > 65_536)
        {
            throw new InvalidDataException("IDE event exceeds the declared bound.");
        }
        IdeEventEnvelope envelope;
        try
        {
            envelope =
                JsonSerializer.Deserialize<IdeEventEnvelope>(bytes, EventJsonOptions)
                ?? throw new InvalidDataException("IDE event is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("IDE event JSON is invalid.", exception);
        }
        if (
            envelope.PartnerId.Value != ObservatoryPartnerId
            || envelope.Classification != 0
            // Same 5-minute clock-skew allowance as /api/events: a producer whose clock
            // runs slightly fast must not have a legitimate event rejected.
            || envelope.OccurredAt > now + Duration.FromMinutes(5)
            || string.IsNullOrWhiteSpace(envelope.IdempotencyKey)
            || envelope.IdempotencyKey.Length > 256
            || envelope.EventType is not ("run.completed" or "operator.intervened" or "routing.decided")
            || !HasCompleteIdentity(envelope.Identity)
        )
        {
            throw new InvalidDataException("IDE event identity or classification is invalid.");
        }
        ValidatePayload(envelope.EventType, envelope.Payload);
        return envelope;
    }

    private static async Task<IResult> ReceiveEventAsync(
        HttpRequest request,
        AiObservatoryDbContext db,
        IClock clock,
        CancellationToken cancellationToken
    )
    {
        var bytes = await ReadBoundedAsync(request.Body, cancellationToken);
        if (bytes is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        IdeEventEnvelope envelope;
        try
        {
            envelope = ParseEvent(bytes, clock.GetCurrentInstant());
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest();
        }
        var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var existing = await db
            .IdeEvents.AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.PartnerId == envelope.PartnerId.Value && row.IdempotencyKey == envelope.IdempotencyKey,
                cancellationToken
            );
        if (existing is not null)
        {
            return existing.ContentSha256 == hash ? Results.Ok() : Results.Conflict();
        }
        db.IdeEvents.Add(
            new IdeEvent
            {
                PartnerId = envelope.PartnerId.Value,
                IdempotencyKey = envelope.IdempotencyKey,
                EventType = envelope.EventType,
                EnvelopeJson = new UTF8Encoding(false, true).GetString(bytes),
                ContentSha256 = hash,
                OccurredAt = envelope.OccurredAt,
                ReceivedAt = clock.GetCurrentInstant(),
            }
        );
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created();
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winnerHash = await db
                .IdeEvents.AsNoTracking()
                .Where(row =>
                    row.PartnerId == envelope.PartnerId.Value && row.IdempotencyKey == envelope.IdempotencyKey
                )
                .Select(row => row.ContentSha256)
                .SingleOrDefaultAsync(cancellationToken);
            if (winnerHash is null)
            {
                throw;
            }
            return winnerHash == hash ? Results.Ok() : Results.Conflict();
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return body.ToArray();
            }
            if (body.Length + read > 65_536)
            {
                return null;
            }
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool HasCompleteIdentity(IdeEventIdentity identity) =>
        identity is not null
        && identity.MissionId.Value != Guid.Empty
        && identity.TaskId.Value != Guid.Empty
        && identity.SessionId.Value != Guid.Empty
        && identity.RunId.Value != Guid.Empty
        && identity.EvidenceId.Value != Guid.Empty
        && Present(identity.Role, 128)
        && Present(identity.Repository, 512)
        && Present(identity.Worktree, 512)
        && Present(identity.Commit, 128)
        && Present(identity.Outcome, 128);

    private static bool Present(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && value == value.Trim();

    private static void ValidatePayload(string eventType, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("IDE event payload must be an object.");
        }
        var properties = IndexPayload(payload);
        var required = eventType switch
        {
            "run.completed" => new[] { "modelId", "adapterId", "durationMilliseconds", "terminalStatus" },
            "operator.intervened" => new[] { "modelId", "reasonCode" },
            _ => new[] { "selectedModelId", "snapshotId", "routingRuleVersion" },
        };
        if (properties.Count != required.Length || required.Any(name => !properties.ContainsKey(name)))
        {
            throw new InvalidDataException("IDE event payload members are invalid.");
        }
        foreach (var name in required.Where(name => name != "durationMilliseconds"))
        {
            if (
                properties[name].Value.ValueKind != JsonValueKind.String
                || !Present(properties[name].Value.GetString()!, 256)
            )
            {
                throw new InvalidDataException("IDE event payload value is invalid.");
            }
        }
        if (
            eventType == "run.completed"
            && (
                !properties["durationMilliseconds"].Value.TryGetInt64(out var duration)
                || duration < 0
                || properties["terminalStatus"].Value.GetString() is not ("succeeded" or "failed" or "cancelled")
            )
        )
        {
            throw new InvalidDataException("Run completion payload is invalid.");
        }
        if (
            eventType == "operator.intervened"
            && properties["reasonCode"].Value.GetString() is not ("operator-input" or "manual-override" or "approval")
        )
        {
            throw new InvalidDataException("Operator intervention reason is invalid.");
        }
    }

    private static Dictionary<string, JsonProperty> IndexPayload(JsonElement payload)
    {
        try
        {
            return payload.EnumerateObject().ToDictionary(property => property.Name, StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("IDE event payload members are invalid.", exception);
        }
    }

    private static JsonSerializerOptions CreateEventJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            // Identity wrappers are reference-type records: without this, a missing JSON member
            // deserializes as null and ParseEvent/HasCompleteIdentity throw NullReferenceException.
            RespectRequiredConstructorParameters = true,
            // /api/events rejects duplicate JSON properties (JsonDocumentOptions); the envelope
            // must match, or a shadowed duplicate idempotencyKey/occurredAt hashes differently
            // from how it parses.
            AllowDuplicateProperties = false,
        };
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return options;
    }
}

internal sealed record IdeIdentityValue(Guid Value);

internal sealed record IdeEventIdentity(
    IdeIdentityValue MissionId,
    IdeIdentityValue TaskId,
    string Role,
    IdeIdentityValue SessionId,
    IdeIdentityValue RunId,
    string Repository,
    string Worktree,
    string Commit,
    string Outcome,
    IdeIdentityValue EvidenceId
);

internal sealed record IdeEventEnvelope(
    IdeIdentityValue PartnerId,
    string EventType,
    string IdempotencyKey,
    IdeEventIdentity Identity,
    JsonElement Payload,
    Instant OccurredAt,
    int Classification
);
