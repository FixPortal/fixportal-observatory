using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public static class AuditViewerProjection
{
    public static object Coverage(AuditCoverage coverage, bool detailsAvailable) =>
        new
        {
            comparedGroups = coverage.ComparedGroups.ToString(CultureInfo.InvariantCulture),
            totalGroups = coverage.TotalGroups.ToString(CultureInfo.InvariantCulture),
            diagnosticCodes = coverage.Gaps.Select(g => g.DiagnosticCode).Distinct(),
            exclusions = coverage.Exclusions,
            detailsAvailable = detailsAvailable || coverage.DetailsRetained == true,
        };

    public static object[] Checks(AuditResult result, bool detailsAvailable) =>
        result
            .Checks.Select(c =>
                (object)
                    new
                    {
                        c.Code,
                        c.Verdict,
                        supported = c.Verdict != CheckVerdict.Unsupported && c.Code != CheckCode.BillingReconciliation,
                        executed = c.Verdict is not (CheckVerdict.Excluded or CheckVerdict.Unsupported),
                        coverage = Coverage(c.Coverage, detailsAvailable),
                    }
            )
            .ToArray();

    public static IReadOnlyList<object> Findings(AuditResult result) =>
        result
            .Findings.Select(f => new
            {
                f.CheckCode,
                f.RuleCode,
                f.Field,
                f.Expected,
                f.Actual,
                f.Delta,
                f.Unit,
                f.Currency,
                f.Basis,
                f.DiagnosticCode,
            })
            .GroupBy(f => JsonSerializer.Serialize(f))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { count = g.LongCount().ToString(CultureInfo.InvariantCulture), value = g.First() })
            .Cast<object>()
            .ToArray();

    public static JsonNode Wire<T>(T value)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var node = JsonSerializer.SerializeToNode(value, options);
        Stringify(node);
        return node!;
    }

    private static void Stringify(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (
                    key != "schemaVersion"
                    && value is JsonValue scalar
                    && scalar.GetValueKind() == JsonValueKind.Number
                )
                {
                    obj[key] = scalar.ToJsonString();
                }
                else
                {
                    Stringify(value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                Stringify(item);
            }
        }
    }

    public static async Task<AuditResult> ResultAsync(AuditStore store, Guid id, CancellationToken ct)
    {
        return FromRun(await store.ReadReportAsync(id, ct));
    }

    public static AuditResult FromRun(AuditRunView run)
    {
        var bytes = run.Result ?? run.SanitizedSummary;
        if (bytes == null)
        {
            throw new AuditStoreException("report_unavailable", 409);
        }
        return AuditCanonicalJson.Deserialize<AuditResult>(bytes);
    }

    public static object Page<T>(IReadOnlyList<T> items, HttpRequest request, string binding)
    {
        var limit = 50;
        if (
            request.Query.ContainsKey("limit") && !int.TryParse(request.Query["limit"], out limit)
            || limit is <= 0 or > 200
        )
        {
            throw new AuditStoreException("invalid_page_limit", 400);
        }
        var offset = 0;
        var cursor = request.Query["cursor"].ToString();
        if (cursor.Length > 0)
        {
            var parts = cursor.Split('.');
            if (
                parts.Length != 2
                || parts[0] != binding
                || !int.TryParse(parts[1], out offset)
                || offset < 0
                || offset > items.Count
            )
            {
                throw new AuditStoreException("invalid_cursor", 400);
            }
        }
        var next = offset + Math.Min(limit, items.Count - offset);
        return new
        {
            schemaVersion = 1,
            items = items.Skip(offset).Take(limit).ToArray(),
            nextCursor = next < items.Count ? binding + "." + next.ToString(CultureInfo.InvariantCulture) : null,
        };
    }
}
