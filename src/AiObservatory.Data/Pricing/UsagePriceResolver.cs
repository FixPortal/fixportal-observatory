using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using Microsoft.Extensions.Logging;

namespace AiObservatory.Data.Pricing;

public sealed class UsagePriceResolver
{
    private const int MaximumWarningKeys = 4096;
    private const int MaximumLoggedModelLength = 160;
    private static readonly object WarningGate = new();
    private static readonly HashSet<(Provider Provider, string ModelFingerprint, string Missing)> Warnings = [];
    private static bool WarningCapLogged;
    private readonly PricingSnapshotStore _store;
    private readonly IReadOnlyDictionary<Provider, IProviderPriceCalculator> _calculators;
    private readonly ILogger<UsagePriceResolver> _logger;

    public UsagePriceResolver(
        PricingSnapshotStore store,
        IEnumerable<IProviderPriceCalculator> calculators,
        ILogger<UsagePriceResolver> logger
    )
    {
        _store = store;
        _calculators = calculators.ToDictionary(calculator => calculator.Provider);
        _logger = logger;
    }

    public Task<UsagePriceQuote?> ResolveAsync(UsageEvent usage, CancellationToken cancellationToken = default) =>
        ResolveAsync(usage, snapshotsBySourceId: null, cancellationToken);

    /// <summary>
    /// As <see cref="ResolveAsync(UsageEvent, CancellationToken)"/>, but resolves the snapshot through
    /// <paramref name="snapshotsBySourceId"/> so a pass over many events does not re-query it per event.
    /// The dictionary belongs to the caller and must not outlive the pass that created it.
    /// </summary>
    internal async Task<UsagePriceQuote?> ResolveAsync(
        UsageEvent usage,
        Dictionary<string, List<PricingSnapshot>>? snapshotsBySourceId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (IsExactZeroUsage(usage))
        {
            return new UsagePriceQuote(0m, 0m);
        }

        if (!_calculators.TryGetValue(usage.Provider, out var calculator))
        {
            WarnOnce(usage, "calculator");
            return null;
        }

        var snapshots = await _store.GetCoveringSnapshotsAsync(usage, snapshotsBySourceId, cancellationToken);
        if (snapshots.Count == 0)
        {
            WarnOnce(usage, "catalog");
            return null;
        }

        // The active snapshot comes first, but a refresh that retires this model must not make
        // the event unpriceable: fall through to older retained snapshots until one produces a
        // quote.
        foreach (var snapshot in snapshots)
        {
            var quote = calculator.Calculate(usage, snapshot.NormalizedCatalog);
            if (quote is not null)
            {
                return quote;
            }
        }

        WarnOnce(usage, MissingDimensions(usage));
        return null;
    }

    private void WarnOnce(UsageEvent usage, string missing)
    {
        var model = usage.Model ?? "<missing>";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(model)));
        if (TryMarkCapReached())
        {
            _logger.LogWarning(
                "Usage price warning cap of {MaximumWarningKeys} distinct keys reached; further unpriced-model warnings are suppressed.",
                MaximumWarningKeys
            );
            return;
        }

        if (!TryMarkReported(usage.Provider, fingerprint, missing))
        {
            return;
        }

        var sanitizedModel = model.Replace('\r', ' ').Replace('\n', ' ');
        if (sanitizedModel.Length > MaximumLoggedModelLength)
        {
            sanitizedModel = sanitizedModel[..MaximumLoggedModelLength] + "...";
        }

        _logger.LogWarning(
            "No exact {Provider} price for model '{Model}'; missing or unmatched dimensions: {MissingDimensions}.",
            usage.Provider,
            sanitizedModel,
            missing
        );
    }

    private static bool TryMarkReported(Provider provider, string modelFingerprint, string missing)
    {
        lock (WarningGate)
        {
            return Warnings.Count < MaximumWarningKeys && Warnings.Add((provider, modelFingerprint, missing));
        }
    }

    private static bool TryMarkCapReached()
    {
        lock (WarningGate)
        {
            // At the cap new unpriceable models would stop being reported entirely; say so once
            // rather than going silently dark.
            if (Warnings.Count < MaximumWarningKeys || WarningCapLogged)
            {
                return false;
            }

            WarningCapLogged = true;
            return true;
        }
    }

    private static bool IsExactZeroUsage(UsageEvent usage) =>
        usage.InputTokens == 0
        && usage.OutputTokens == 0
        && (usage.CacheReadTokens ?? 0) == 0
        && (usage.CacheWriteTokens ?? 0) == 0
        && (usage.CacheWrite1hTokens ?? 0) == 0
        && (usage.ThoughtTokens ?? 0) == 0;

    private static string MissingDimensions(UsageEvent usage)
    {
        var missing = new List<string>();
        try
        {
            using var evidence = ProviderPricingJson.Evidence(usage.RawPayload);
            var root = evidence.RootElement;
            switch (usage.Provider)
            {
                case Provider.OpenAI:
                    RequireString(root, "processing", missing);
                    RequireString(root, "context", missing);
                    RequireString(root, "region", missing);
                    break;
                case Provider.Anthropic:
                    RequireString(root, "service_tier", missing);
                    RequireString(root, "speed", missing);
                    RequireString(root, "inference_geo", missing);
                    if ((usage.CacheWriteTokens ?? 0) > 0)
                    {
                        RequireNestedInt(root, "cache_creation", "ephemeral_5m_input_tokens", missing);
                        RequireNestedInt(root, "cache_creation", "ephemeral_1h_input_tokens", missing);
                    }
                    break;
                case Provider.Moonshot:
                    RequireBoolean(root, "high_speed", missing);
                    RequireBoolean(root, "batch", missing);
                    break;
                case Provider.Google:
                    RequireGoogleDimensions(root, missing);
                    break;
            }
        }
        catch (JsonException)
        {
            missing.Add("raw_payload");
        }

        if (string.IsNullOrWhiteSpace(usage.Model) && usage.Provider != Provider.Google)
        {
            missing.Add("model");
        }

        return missing.Count == 0 ? "catalog-entry" : string.Join(',', missing.Order(StringComparer.Ordinal));
    }

    private static void RequireGoogleDimensions(JsonElement root, ICollection<string> missing)
    {
        RequireString(root, "service", missing);
        if (
            ProviderPricingJson.TryString(root, "service", out var service)
            && service.Equals("Gemini Developer API", StringComparison.OrdinalIgnoreCase)
        )
        {
            RequireString(root, "tier", missing);
            RequireString(root, "context", missing);
            return;
        }

        foreach (var dimension in new[] { "sku_id", "region", "modality", "tier", "cache_lane" })
        {
            RequireString(root, dimension, missing);
        }
        if (!ProviderPricingJson.TryInt64(root, "context_threshold", out _))
        {
            missing.Add("context_threshold");
        }
    }

    private static void RequireString(JsonElement root, string name, ICollection<string> missing)
    {
        if (!ProviderPricingJson.TryString(root, name, out _))
        {
            missing.Add(name);
        }
    }

    private static void RequireBoolean(JsonElement root, string name, ICollection<string> missing)
    {
        if (!ProviderPricingJson.TryBoolean(root, name, out _))
        {
            missing.Add(name);
        }
    }

    private static void RequireNestedInt(JsonElement root, string parent, string name, ICollection<string> missing)
    {
        if (!ProviderPricingJson.TryNestedInt64(root, parent, name, out _))
        {
            missing.Add($"{parent}.{name}");
        }
    }
}
