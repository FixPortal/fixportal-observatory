using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace AiObservatory.Api.Routing;

public sealed record RoutingCatalog(int CatalogRevision, IReadOnlyList<RoutingModelVersion> Models);

public sealed record RoutingModelVersion(
    string ModelId,
    string Vendor,
    string ModelFamily,
    IReadOnlyList<string> Aliases,
    string IdentityBasis,
    string IdentityAdapterAlias,
    Instant IdentityObservedAt,
    IReadOnlyList<string> Capabilities,
    string CostBasis,
    decimal? EstimatedCostUsd,
    RoutingEvidence Evidence,
    Instant EffectiveFrom,
    Instant? EffectiveTo
);

public sealed record RoutingEvidence(
    RoutingEvidenceMetric Quality,
    RoutingEvidenceMetric Reliability,
    RoutingEvidenceMetric InterventionRate,
    RoutingEvidenceMetric ToolFit,
    RoutingEvidenceMetric ContextFit
);

public sealed record RoutingEvidenceMetric(
    double Value,
    string Basis,
    string Source,
    Instant ObservedAt,
    int? SampleCount
);

public sealed record RoutingSnapshot(
    int SchemaVersion,
    int CatalogRevision,
    string SnapshotId,
    Instant GeneratedAt,
    IReadOnlyList<RoutingSnapshotModel> Models
);

public sealed record RoutingSnapshotModel(
    string ModelId,
    string Vendor,
    string ModelFamily,
    IReadOnlyList<string> Aliases,
    string IdentityBasis,
    string IdentityAdapterAlias,
    Instant IdentityObservedAt,
    IReadOnlyList<string> Capabilities,
    string CostBasis,
    decimal? EstimatedCostUsd,
    RoutingEvidence Evidence
);

public sealed class RoutingCatalogService
{
    private const int MaximumModels = 16;
    private const int MaximumSnapshotBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly RoutingCatalog _catalog;
    private readonly object _gate = new();
    private RoutingSnapshot? _cached;

    internal static JsonSerializerOptions SerializerOptions => JsonOptions;

    private RoutingCatalogService(RoutingCatalog catalog) => _catalog = catalog;

    public static RoutingCatalogService Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var catalog =
                JsonSerializer.Deserialize<RoutingCatalog>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Routing projection is empty.");
            Validate(catalog);
            return new RoutingCatalogService(catalog);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw new InvalidDataException("Routing projection is invalid.", exception);
        }
    }

    public RoutingSnapshot GetSnapshot(Instant now)
    {
        var models = _catalog
            .Models.Where(model => model.EffectiveFrom <= now && (model.EffectiveTo is null || now < model.EffectiveTo))
            .OrderBy(model => model.ModelId, StringComparer.Ordinal)
            .Select(model => new RoutingSnapshotModel(
                model.ModelId,
                model.Vendor,
                model.ModelFamily,
                model.Aliases.Order(StringComparer.Ordinal).ToArray(),
                model.IdentityBasis,
                model.IdentityAdapterAlias,
                model.IdentityObservedAt,
                model.Capabilities.Order(StringComparer.Ordinal).ToArray(),
                model.CostBasis,
                model.EstimatedCostUsd,
                model.Evidence
            ))
            .ToArray();
        if (models.Length > MaximumModels)
        {
            throw new InvalidDataException($"Routing snapshot exceeds {MaximumModels} models.");
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(
            new RoutingSnapshotContent(_catalog.CatalogRevision, models),
            JsonOptions
        );
        var snapshotId = "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        lock (_gate)
        {
            if (_cached?.SnapshotId == snapshotId)
            {
                return _cached;
            }
            var snapshot = new RoutingSnapshot(1, _catalog.CatalogRevision, snapshotId, now, models);
            if (JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions).Length > MaximumSnapshotBytes)
            {
                throw new InvalidDataException($"Routing snapshot exceeds {MaximumSnapshotBytes} bytes.");
            }
            _cached = snapshot;
            return snapshot;
        }
    }

    private static void Validate(RoutingCatalog catalog)
    {
        if (catalog.CatalogRevision <= 0 || catalog.Models is null)
        {
            throw new InvalidDataException("Routing projection requires a positive revision and models.");
        }
        foreach (var model in catalog.Models)
        {
            ValidateModel(model);
        }
        for (var leftIndex = 0; leftIndex < catalog.Models.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < catalog.Models.Count; rightIndex++)
            {
                var left = catalog.Models[leftIndex];
                var right = catalog.Models[rightIndex];
                if (!Overlaps(left, right))
                {
                    continue;
                }
                if (StringComparer.Ordinal.Equals(left.ModelId, right.ModelId))
                {
                    throw new InvalidDataException($"Overlapping versions exist for model '{left.ModelId}'.");
                }
                if (left.Aliases.Intersect(right.Aliases, StringComparer.Ordinal).Any())
                {
                    throw new InvalidDataException("An active alias cannot identify two models.");
                }
            }
        }
    }

    private static void ValidateModel(RoutingModelVersion model)
    {
        if (
            string.IsNullOrWhiteSpace(model.ModelId)
            || string.IsNullOrWhiteSpace(model.Vendor)
            || string.IsNullOrWhiteSpace(model.ModelFamily)
            || model.Aliases is null
            || model.Aliases.Count == 0
            || model.Aliases.Any(string.IsNullOrWhiteSpace)
            || model.Aliases.Distinct(StringComparer.Ordinal).Count() != model.Aliases.Count
            || model.Capabilities is null
            || model.Capabilities.Any(string.IsNullOrWhiteSpace)
            || model.Capabilities.Distinct(StringComparer.Ordinal).Count() != model.Capabilities.Count
            || model.IdentityBasis != "adapterDefaultDeclared"
            || string.IsNullOrWhiteSpace(model.IdentityAdapterAlias)
            || !model.Aliases.Contains(model.IdentityAdapterAlias, StringComparer.Ordinal)
            || model.EffectiveTo is { } end && end <= model.EffectiveFrom
        )
        {
            throw new InvalidDataException("Routing model identity, aliases, capabilities, or window are invalid.");
        }
        if (model.CostBasis is not ("meteredTokenEstimate" or "subscriptionAllocation" or "unpriced"))
        {
            throw new InvalidDataException("Routing cost basis is unsupported.");
        }
        var isUnpriced = model.CostBasis == "unpriced";
        var hasNoCost = model.EstimatedCostUsd is null;
        if (isUnpriced != hasNoCost || model.EstimatedCostUsd < 0)
        {
            throw new InvalidDataException("Routing cost does not match its basis.");
        }
        foreach (var metric in Metrics(model.Evidence))
        {
            if (
                !double.IsFinite(metric.Value)
                || metric.Value is < 0 or > 1
                || metric.Basis is not ("operatorBaseline" or "measured")
                || string.IsNullOrWhiteSpace(metric.Source)
                || metric.SampleCount < 0
            )
            {
                throw new InvalidDataException("Routing evidence is invalid.");
            }
        }
    }

    private static IEnumerable<RoutingEvidenceMetric> Metrics(RoutingEvidence evidence)
    {
        if (evidence is null)
        {
            throw new InvalidDataException("Routing evidence is required.");
        }
        return
        [
            evidence.Quality,
            evidence.Reliability,
            evidence.InterventionRate,
            evidence.ToolFit,
            evidence.ContextFit,
        ];
    }

    private static bool Overlaps(RoutingModelVersion left, RoutingModelVersion right) =>
        (right.EffectiveTo is null || left.EffectiveFrom < right.EffectiveTo)
        && (left.EffectiveTo is null || right.EffectiveFrom < left.EffectiveTo);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return options;
    }

    private sealed record RoutingSnapshotContent(int CatalogRevision, IReadOnlyList<RoutingSnapshotModel> Models);
}
