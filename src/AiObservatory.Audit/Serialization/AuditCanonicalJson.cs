using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Serialization;

/// <summary>Version-one canonical wire format. Object keys and identity-bearing sets use ordinal ordering.</summary>
public static class AuditCanonicalJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize<T>(T value)
    {
        AuditContractValidation.Validate(value);
        var element = JsonSerializer.SerializeToElement(value, Options);
        return CanonicalBytes(element, null);
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray());
            RejectDuplicateProperties(document.RootElement);
            var value = JsonSerializer.Deserialize<T>(utf8, Options);
            AuditContractValidation.Validate(value);
            // Return the same normalized facts identified by their canonical bytes, including set multiplicity.
            var canonical = Serialize(value);
            return JsonSerializer.Deserialize<T>(canonical, Options)!;
        }
        catch (JsonException)
        {
            throw new AuditValidationException("invalid_json_contract");
        }
        catch (NotSupportedException)
        {
            throw new AuditValidationException("invalid_json_contract");
        }
    }

    public static string ContentHash(ReadOnlySpan<byte> utf8) => Convert.ToHexStringLower(SHA256.HashData(utf8));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            AllowOutOfOrderMetadataProperties = true,
            MaxDepth = 64,
        };
        options.Converters.Add(new CanonicalDecimalConverter());
        options.Converters.Add(new UtcTimestampConverter());
        options.Converters.Add(new StrictEnumConverterFactory());
        return options;
    }

    private static byte[] CanonicalBytes(JsonElement element, string? collectionName)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, element, collectionName);
        }
        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, string? collectionName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name == "value" && element.TryGetProperty("kind", out var kind))
                    {
                        WriteNumericValue(writer, kind.GetString(), property.Value.GetString()!);
                    }
                    else
                    {
                        Write(writer, property.Value, property.Name);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                WriteArray(writer, element, collectionName);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteNumericValue(Utf8JsonWriter writer, string? kind, string text)
    {
        if (kind == "integer")
        {
            writer.WriteStringValue(
                long.Parse(text, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            );
        }
        else
        {
            _ = CanonicalDecimalConverter.TryParse(text, out var value);
            writer.WriteStringValue(CanonicalDecimalConverter.Format(value));
        }
    }

    private static void WriteArray(Utf8JsonWriter writer, JsonElement element, string? name)
    {
        writer.WriteStartArray();
        if (name == "chunkHashes")
        {
            // Chunk index is meaningful; reordering even identical captures changes their evidence stream.
            foreach (var item in element.EnumerateArray())
            {
                Write(writer, item, null);
            }
        }
        else
        {
            var items = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var item in element.EnumerateArray())
            {
                var bytes = CanonicalBytes(item, null);
                var key = StableKey(item, name, bytes);
                if (items.TryGetValue(key, out var previous) && !previous.AsSpan().SequenceEqual(bytes))
                {
                    throw new AuditValidationException("conflicting_identity");
                }
                items[key] = bytes;
            }
            foreach (var bytes in items.Values)
            {
                writer.WriteRawValue(bytes, skipInputValidation: false);
            }
        }
        writer.WriteEndArray();
    }

    private static string StableKey(JsonElement item, string? name, byte[] canonical)
    {
        name ??= InferCollectionName(item);
        string[] fields = name switch
        {
            "records" => ["sourceId", "generation", "fileId", "sequence"],
            "sources" => ["machineId", "sourceId"],
            "events" or "spend" => ["rowId"],
            "aggregates" or "compatibleGroups" or "chronologicalGroups" => ["key"],
            "captures" => ["captureId"],
            "sourceFormats" => ["sourceId", "format", "version"],
            "diagnostics" or "checks" => ["code"],
            "sourceStatuses" => ["sourceId"],
            "pricingCatalogs" or "fxRates" => ["sourceId", "id", "contentHash"],
            "rates" => ["id"],
            "tolerances" => ["name"],
            "findings" => ["checkCode", "ruleCode", "groupKey", "evidenceRefs", "field"],
            "gaps" => ["sourceId", "groupKey", "diagnosticCode"],
            "values" => ["field", "groupKey"],
            _ => [],
        };
        if (fields.Length == 0)
        {
            return Encoding.UTF8.GetString(canonical);
        }
        // JSON encoding makes the tuple unambiguous even when identifier punctuation overlaps separators.
        return string.Join(
            '\0',
            fields.Select(field =>
            {
                var value = item.GetProperty(field);
                return value.ValueKind == JsonValueKind.Number
                    ? value.GetInt64().ToString("D20", CultureInfo.InvariantCulture)
                    : Encoding.UTF8.GetString(CanonicalBytes(value, field));
            })
        );
    }

    private static string? InferCollectionName(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        (string Field, string Collection)[] markers =
        [
            ("fileId", "records"),
            ("rowId", "events"),
            ("key", "aggregates"),
            ("captureId", "captures"),
            ("checkCode", "findings"),
            ("contentHash", "pricingCatalogs"),
            ("machineId", "sources"),
            ("code", "checks"),
            ("name", "tolerances"),
        ];
        foreach (var (field, collection) in markers)
        {
            if (item.TryGetProperty(field, out _))
            {
                return collection;
            }
        }
        return null;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new AuditValidationException("duplicate_property");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                RejectDuplicateProperties(child);
            }
        }
    }
}
