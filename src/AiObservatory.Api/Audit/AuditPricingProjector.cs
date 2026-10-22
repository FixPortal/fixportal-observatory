using System.Text.Json;
using AiObservatory.Audit.Contracts;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;

namespace AiObservatory.Api.Audit;

/// <summary>Catalog JSON never crosses the boundary; known rates, dimensions and modifiers do.</summary>
public static class AuditPricingProjector
{
    public static FrozenPricing Catalog(PricingSnapshot snapshot)
    {
        using var json = JsonDocument.Parse(snapshot.NormalizedCatalog);
        var rates = new List<FrozenPriceRate>();
        foreach (var e in json.RootElement.GetProperty("entries").EnumerateArray())
        {
            AddEntry(rates, e);
        }
        return new(
            snapshot.SourceId,
            snapshot.Id.ToString(),
            snapshot.ContentHash,
            snapshot.Provider.ToString(),
            json.RootElement.GetProperty("currency").GetString()!,
            snapshot.RetrievedAt.ToDateTimeOffset(),
            rates
        );
    }

    private static void AddEntry(List<FrozenPriceRate> rates, JsonElement e)
    {
        var dimensions = new PricingDimensions(
            Tier(Text(e, "processing") ?? Text(e, "tier")),
            false,
            Text(e, "region"),
            null
        )
        {
            Processing = Text(e, "processing"),
            Context = Text(e, "context"),
            Service = Text(e, "service"),
            Sku = Text(e, "skuId"),
            Modality = Text(e, "modality"),
            CacheLane = Text(e, "cacheLane"),
            HighSpeed = e.TryGetProperty("highSpeed", out var h) ? h.GetBoolean() : null,
            ContextThreshold = e.TryGetProperty("contextThreshold", out var c) ? c.GetInt64() : null,
        };
        if (Number(e, "cacheWrite5m") is not null)
        {
            dimensions = dimensions with
            {
                Tier = ServiceTier.Standard,
                SourceServiceTier = "standard",
                Speed = "standard",
                InferenceGeo = "global",
            };
        }
        if (Number(e, "longContextInput") is not null)
        {
            dimensions = dimensions with { LongContext = false };
        }
        if (dimensions.Processing is null && dimensions.Context is not null && Number(e, "cachedInput") is not null)
        {
            dimensions = dimensions with { Service = "gemini_developer_api" };
        }
        AddLane(rates, e, dimensions, "standard", 1m, null);
        AddSpecialLanes(rates, e, dimensions);
    }

    private static void AddSpecialLanes(List<FrozenPriceRate> rates, JsonElement e, PricingDimensions d)
    {
        if (Number(e, "batchInput") is { } batch && Number(e, "input") is { } input)
        {
            AddLane(
                rates,
                e,
                d with
                {
                    Tier = ServiceTier.Unknown,
                    Batch = true,
                    SourceServiceTier = "batch",
                    Speed = "standard",
                    InferenceGeo = "global",
                },
                "batch",
                batch / input,
                null
            );
        }
        if (Number(e, "fastInput") is { } fast && Number(e, "input") is { } normal)
        {
            AddLane(
                rates,
                e,
                d with
                {
                    Tier = ServiceTier.Standard,
                    SourceServiceTier = "standard",
                    Speed = "fast",
                    InferenceGeo = "global",
                },
                "fast",
                fast / normal,
                null
            );
        }
        if (Number(e, "batchMultiplier") is { } multiplier)
        {
            AddLane(rates, e, d with { Batch = true }, "batch", multiplier, null);
        }
        if (Number(e, "longContextInput") is not null)
        {
            AddLane(rates, e, d with { LongContext = true }, "long", 1m, null);
        }
        if (Number(e, "usInferenceMultiplier") is { } geo)
        {
            AddLane(
                rates,
                e,
                d with
                {
                    SourceServiceTier = "standard",
                    Speed = "standard",
                    InferenceGeo = "us",
                },
                "standard",
                1m,
                geo
            );
            if (Number(e, "batchInput") is { } batchInput && Number(e, "input") is { } inputRate)
            {
                AddLane(
                    rates,
                    e,
                    d with
                    {
                        Tier = ServiceTier.Unknown,
                        Batch = true,
                        SourceServiceTier = "batch",
                        Speed = "standard",
                        InferenceGeo = "us",
                    },
                    "batch",
                    batchInput / inputRate,
                    geo
                );
            }
            if (Number(e, "fastInput") is { } fastInput && Number(e, "input") is { } baseRate)
            {
                AddLane(
                    rates,
                    e,
                    d with
                    {
                        Tier = ServiceTier.Standard,
                        SourceServiceTier = "standard",
                        Speed = "fast",
                        InferenceGeo = "us",
                    },
                    "fast",
                    fastInput / baseRate,
                    geo
                );
            }
        }
    }

    private static void AddLane(
        List<FrozenPriceRate> rates,
        JsonElement e,
        PricingDimensions dimensions,
        string lane,
        decimal modifier,
        decimal? geo
    )
    {
        var model = Text(e, "modelPrefix") ?? Text(e, "model") ?? Text(e, "skuId")!;
        var aliases = e.TryGetProperty("aliases", out var a)
            ? a.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : [];
        var from = DateOnly.ParseExact(
            e.GetProperty("effectiveFrom").GetString()!,
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture
        );
        var declared = e.GetProperty("effectiveDateIsProviderDeclared").GetBoolean();
        var key = PricingEvidence.RateKey(e);
        var prefix = lane == "long" ? "longContext" : "";
        decimal? LaneRate(string baseName) =>
            prefix.Length == 0
                ? Number(e, baseName)
                : Number(e, prefix + char.ToUpperInvariant(baseName[0]) + baseName[1..]);
        var input = LaneRate("input") ?? Number(e, "cacheMiss");
        var output = LaneRate("output");
        if (lane == "batch")
        {
            output = Number(e, "batchOutput") ?? output * modifier;
        }
        else if (lane == "fast")
        {
            output = Number(e, "fastOutput") ?? output * modifier;
        }
        else
        {
            output *= modifier;
        }
        (PriceMeter Meter, decimal? Price)[] meters =
        [
            (PriceMeter.Input, input * modifier),
            (PriceMeter.Output, output),
            (
                PriceMeter.CacheRead,
                (LaneRate("cachedInput") ?? Number(e, "cacheRead") ?? Number(e, "cacheHit")) * modifier
            ),
            (PriceMeter.CacheWrite5m, (Number(e, "cacheWrite5m") ?? Number(e, "cacheWrite")) * modifier),
            (PriceMeter.CacheWrite1h, Number(e, "cacheWrite1h") * modifier),
        ];
        if (Number(e, "rate") is { } rate)
        {
            meters = [(GoogleMeter(dimensions), rate)];
        }
        foreach (var (meter, price) in meters.Where(m => m.Price is not null))
        {
            var id = key + "-" + lane + "-" + (geo is null ? "global" : "us") + "-" + meter;
            rates.Add(
                new(
                    id,
                    model,
                    aliases,
                    from,
                    null,
                    declared,
                    meter,
                    dimensions,
                    dimensions.ContextThreshold,
                    1_000_000,
                    price!.Value,
                    geo
                )
                {
                    CatalogEntryKey = key,
                }
            );
        }
    }

    private static PriceMeter GoogleMeter(PricingDimensions d)
    {
        if (d.CacheLane is "read" or "cache-read" or "hit")
        {
            return PriceMeter.CacheRead;
        }
        if (d.CacheLane is "write" or "cache-write")
        {
            return PriceMeter.CacheWrite5m;
        }
        return d.Modality?.Contains("output", StringComparison.OrdinalIgnoreCase) == true
            ? PriceMeter.Output
            : PriceMeter.Input;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()?.ToLowerInvariant().Replace(' ', '_')
            : null;

    private static decimal? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : null;

    private static ServiceTier Tier(string? value) =>
        Enum.TryParse<ServiceTier>(value, true, out var tier) ? tier : ServiceTier.Unknown;
}
