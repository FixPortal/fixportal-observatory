using System.Text.Json;
using System.Text.Json.Nodes;
using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api.Services.Intelligence;

public class InsightResponseParser
{
    public IReadOnlyList<Insight> Parse(string json, LocalDate periodStart, LocalDate periodEnd, Instant generatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        // Model-authored text is a trust boundary: malformed JSON must fail the run loudly
        // (rather than surface as a JsonException mid-persistence), and an item without a
        // usable string title is dropped rather than persisted as a blank card.
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Intelligence response was not valid JSON.", exception);
        }

        var array =
            root as JsonArray ?? throw new InvalidOperationException("Intelligence response was not a JSON array.");

        var insights = new List<Insight>();
        foreach (var node in array)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var title = StringOrNull(item, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            insights.Add(
                new Insight
                {
                    GeneratedAt = generatedAt,
                    PeriodStart = periodStart,
                    PeriodEnd = periodEnd,
                    InsightType = ParseType(StringOrNull(item, "type") ?? "summary"),
                    Title = title,
                    Body = StringOrNull(item, "body") ?? "",
                    // Only an object is kept: the model may answer with "data": [], 5 or "text",
                    // and every consumer of Insight.Data (e.g. InsightDeduplicator's costBasis
                    // read) expects the object shape — normalise anything else to none.
                    Data = item["data"] is JsonObject dataObject ? dataObject.ToJsonString() : "{}",
                }
            );
        }

        return insights;
    }

    private static string? StringOrNull(JsonObject item, string property) =>
        item[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static InsightType ParseType(string type) =>
        type.ToLowerInvariant() switch
        {
            "summary" => InsightType.Summary,
            "efficiency" => InsightType.Efficiency,
            "anomaly" => InsightType.Anomaly,
            "recommendation" => InsightType.Recommendation,
            _ => InsightType.Summary,
        };
}
