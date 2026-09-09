using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Google.Cloud.BigQuery.V2;
using NodaTime;

namespace AiObservatory.Ingest.Services.Google;

public sealed class GoogleBillingExportClient(Lazy<BigQueryClient> client, string table)
    : IGoogleBillingExportClient,
        IDisposable
{
    private const string QueryTemplate = """
        WITH affected_keys AS (
          SELECT DISTINCT DATE(usage_start_time) AS usage_date, invoice.month AS billing_period, service.id AS service_id,
            sku.id AS sku_id, currency
          FROM `%TABLE%`
          WHERE (usage_start_time >= @from AND usage_start_time < @through_exclusive) OR export_time > @changes_since
        ), line_items AS (
          SELECT DATE(source.usage_start_time) AS usage_date, source.invoice.month AS billing_period, source.service.id AS service_id,
            source.service.description AS service_description, source.sku.id AS sku_id, source.sku.description AS sku_description,
            source.currency, CAST(source.cost * 1000000 AS INT64) AS gross_micros,
            IFNULL((SELECT SUM(CAST(credit.amount * 1000000 AS INT64)) FROM UNNEST(source.credits) AS credit), 0) AS credit_micros,
            source.export_time
          FROM `%TABLE%` AS source
          INNER JOIN affected_keys AS affected ON DATE(source.usage_start_time) IS NOT DISTINCT FROM affected.usage_date
            AND source.invoice.month IS NOT DISTINCT FROM affected.billing_period AND source.service.id IS NOT DISTINCT FROM affected.service_id
            AND source.sku.id IS NOT DISTINCT FROM affected.sku_id AND source.currency IS NOT DISTINCT FROM affected.currency
          -- Coarse predicate so BigQuery can prune partitions on the source scan: neither the
          -- five-column IS NOT DISTINCT FROM join nor the OR'd export_time filter in
          -- affected_keys lets it. The bound is at the affected key's day granularity: a raw
          -- timestamp bound splits the boundary day at the time-of-day of a mid-day
          -- @changes_since, and the partial re-aggregation would overwrite the stored total
          -- wholesale. Slack covers late-exported rows for recent corrections.
          WHERE DATE(source.usage_start_time) >= DATE_SUB(DATE(@changes_since), INTERVAL 31 DAY)
        )
        SELECT usage_date, billing_period, service_id,
          ARRAY_AGG(service_description ORDER BY export_time DESC, service_description DESC LIMIT 1)[OFFSET(0)] AS service_description,
          sku_id, ARRAY_AGG(sku_description ORDER BY export_time DESC, sku_description DESC LIMIT 1)[OFFSET(0)] AS sku_description, currency,
          CAST(SUM(gross_micros) AS NUMERIC) / 1000000 AS gross_amount,
          CAST(SUM(credit_micros) AS NUMERIC) / 1000000 AS credit_amount,
          CAST(SUM(gross_micros + credit_micros) AS NUMERIC) / 1000000 AS net_amount,
          MAX(export_time) AS observed_at,
          TO_JSON_STRING(STRUCT(usage_date, billing_period, service_id,
            ARRAY_AGG(service_description ORDER BY export_time DESC, service_description DESC LIMIT 1)[OFFSET(0)] AS service_description,
            sku_id, ARRAY_AGG(sku_description ORDER BY export_time DESC, sku_description DESC LIMIT 1)[OFFSET(0)] AS sku_description, currency,
            CAST(SUM(gross_micros) AS NUMERIC) / 1000000 AS gross_amount,
            CAST(SUM(credit_micros) AS NUMERIC) / 1000000 AS credit_amount,
            CAST(SUM(gross_micros + credit_micros) AS NUMERIC) / 1000000 AS net_amount,
            MAX(export_time) AS observed_at)) AS raw_json
        FROM line_items
        GROUP BY usage_date, billing_period, service_id, sku_id, currency
        """;

    // The export_time arm of affected_keys has no usage-date bound, so a correction exported
    // today for usage older than the 31-day line_items scan floor selects keys the main query
    // can never satisfy: no rows, empty aggregate, and the stored observation silently stays
    // stale. This companion count makes that loss visible — the source logs it as a warning.
    // It deliberately re-scans the affected_keys input once per cycle: BigQuery cannot share a
    // CTE across query jobs, and a single-job script cannot return two result sets.
    private const string OutOfRangeKeysQueryTemplate = """
        WITH affected_keys AS (
          SELECT DISTINCT DATE(usage_start_time) AS usage_date, invoice.month AS billing_period, service.id AS service_id,
            sku.id AS sku_id, currency
          FROM `%TABLE%`
          WHERE (usage_start_time >= @from AND usage_start_time < @through_exclusive) OR export_time > @changes_since
        )
        SELECT COUNT(*) AS out_of_range_keys
        FROM affected_keys
        WHERE usage_date < DATE_SUB(DATE(@changes_since), INTERVAL 31 DAY)
        """;

    private readonly string _table = ValidateExportTable(table);

    public void Dispose()
    {
        if (client.IsValueCreated)
        {
            client.Value.Dispose();
        }
    }

    public async Task<GoogleBillingExportResult> GetBillingRecordsAsync(
        Instant from,
        Instant throughExclusive,
        Instant changesSince,
        CancellationToken cancellationToken = default
    )
    {
        if (throughExclusive <= from)
        {
            throw new ArgumentOutOfRangeException(nameof(throughExclusive));
        }
        // The companion count runs first: a correction for usage older than the scan floor is
        // reported even if the main query below legitimately returns rows for everything else.
        // It only feeds a warning, so a transient count failure must not abort the export —
        // degrade to "count unavailable" (null) and still run the main query. Cancellation is
        // not a failure and keeps its normal semantics.
        long? outOfRangeKeys;
        Exception? outOfRangeCountFailure = null;
        try
        {
            outOfRangeKeys = await CountOutOfRangeKeysAsync(from, throughExclusive, changesSince, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            outOfRangeKeys = null;
            outOfRangeCountFailure = exception;
        }
        var query = BuildQuery(_table, from, throughExclusive, changesSince);
        var results = await client.Value.ExecuteQueryAsync(
            query.Sql,
            query.Parameters,
            new QueryOptions { UseLegacySql = false },
            cancellationToken: cancellationToken
        );
        var records = new List<GoogleBillingRecord>();
        await foreach (var row in results.GetRowsAsync().WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.Add(MapRow(row));
        }
        return new GoogleBillingExportResult(records.ToImmutableArray(), outOfRangeKeys, outOfRangeCountFailure);
    }

    private async Task<long> CountOutOfRangeKeysAsync(
        Instant from,
        Instant throughExclusive,
        Instant changesSince,
        CancellationToken cancellationToken
    )
    {
        var query = BuildOutOfRangeKeysQuery(_table, from, throughExclusive, changesSince);
        var results = await client.Value.ExecuteQueryAsync(
            query.Sql,
            query.Parameters,
            new QueryOptions { UseLegacySql = false },
            cancellationToken: cancellationToken
        );
        // COUNT(*) always yields exactly one row; zero rows is treated as zero rather than an
        // error so a stubbed or empty result cannot mask the main query's records.
        var rows = results.GetRowsAsync();
        await using var enumerator = rows.GetAsyncEnumerator(cancellationToken);
        if (await enumerator.MoveNextAsync())
        {
            return enumerator.Current["out_of_range_keys"] is long count
                ? count
                : throw new InvalidDataException("Google billing export returned an invalid out-of-range key count.");
        }
        return 0;
    }

    internal static string ValidateExportTable(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        var parts = table.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts.Any(part => !IsSafeIdentifier(part)))
        {
            throw new ArgumentException(
                "Google billing export table must be project.dataset.table with safe identifier segments.",
                nameof(table)
            );
        }
        return table;
    }

    internal static GoogleBillingQuery BuildQuery(string table) =>
        new(
            QueryTemplate.Replace("%TABLE%", ValidateExportTable(table), StringComparison.Ordinal),
            [
                new BigQueryParameter("from", BigQueryDbType.Timestamp),
                new BigQueryParameter("through_exclusive", BigQueryDbType.Timestamp),
                new BigQueryParameter("changes_since", BigQueryDbType.Timestamp),
            ]
        );

    internal static GoogleBillingQuery BuildOutOfRangeKeysQuery(string table) =>
        new(
            OutOfRangeKeysQueryTemplate.Replace("%TABLE%", ValidateExportTable(table), StringComparison.Ordinal),
            [
                new BigQueryParameter("from", BigQueryDbType.Timestamp),
                new BigQueryParameter("through_exclusive", BigQueryDbType.Timestamp),
                new BigQueryParameter("changes_since", BigQueryDbType.Timestamp),
            ]
        );

    private static GoogleBillingQuery BuildQuery(
        string table,
        Instant from,
        Instant throughExclusive,
        Instant changesSince
    ) =>
        new(
            QueryTemplate.Replace("%TABLE%", ValidateExportTable(table), StringComparison.Ordinal),
            [
                new BigQueryParameter("from", BigQueryDbType.Timestamp, from.ToDateTimeUtc()),
                new BigQueryParameter("through_exclusive", BigQueryDbType.Timestamp, throughExclusive.ToDateTimeUtc()),
                new BigQueryParameter("changes_since", BigQueryDbType.Timestamp, changesSince.ToDateTimeUtc()),
            ]
        );

    private static GoogleBillingQuery BuildOutOfRangeKeysQuery(
        string table,
        Instant from,
        Instant throughExclusive,
        Instant changesSince
    ) =>
        new(
            OutOfRangeKeysQueryTemplate.Replace("%TABLE%", ValidateExportTable(table), StringComparison.Ordinal),
            [
                new BigQueryParameter("from", BigQueryDbType.Timestamp, from.ToDateTimeUtc()),
                new BigQueryParameter("through_exclusive", BigQueryDbType.Timestamp, throughExclusive.ToDateTimeUtc()),
                new BigQueryParameter("changes_since", BigQueryDbType.Timestamp, changesSince.ToDateTimeUtc()),
            ]
        );

    internal static async Task<IReadOnlyList<GoogleBillingRecord>> MapRowsAsync(
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken
    )
    {
        var records = new List<GoogleBillingRecord>();
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.Add(MapRow(row));
        }
        return records.ToImmutableArray();
    }

    private static GoogleBillingRecord MapRow(BigQueryRow row) =>
        MapRow(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["usage_date"] = row["usage_date"],
                ["billing_period"] = row["billing_period"],
                ["service_id"] = row["service_id"],
                ["service_description"] = row["service_description"],
                ["sku_id"] = row["sku_id"],
                ["sku_description"] = row["sku_description"],
                ["currency"] = row["currency"],
                ["gross_amount"] = row["gross_amount"],
                ["credit_amount"] = row["credit_amount"],
                ["net_amount"] = row["net_amount"],
                ["observed_at"] = row["observed_at"],
                ["raw_json"] = row["raw_json"],
            }
        );

    private static GoogleBillingRecord MapRow(IReadOnlyDictionary<string, object?> row)
    {
        var gross = Decimal("gross_amount", row);
        var credit = Decimal("credit_amount", row);
        var net = Decimal("net_amount", row);
        if (gross + credit != net)
        {
            throw new InvalidDataException(
                "Google billing export returned inconsistent gross, credit, and net amounts."
            );
        }
        var rawJson = Text("raw_json", row);
        try
        {
            using var _ = JsonDocument.Parse(rawJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Google billing export returned invalid raw JSON.", exception);
        }
        var currency = Text("currency", row);
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new InvalidDataException("Google billing export returned an invalid currency.");
        }
        var period = Text("billing_period", row);
        if (
            period.Length != 6
            || period.Any(character => !char.IsAsciiDigit(character))
            || !int.TryParse(period[..4], CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(period[4..], CultureInfo.InvariantCulture, out var month)
            || year < 1
            || month is < 1 or > 12
        )
        {
            throw new InvalidDataException("Google billing export returned an invalid invoice month.");
        }
        return new GoogleBillingRecord(
            Date("usage_date", row),
            period,
            Text("service_id", row),
            Text("service_description", row),
            Text("sku_id", row),
            Text("sku_description", row),
            currency,
            gross,
            credit,
            net,
            Timestamp("observed_at", row),
            rawJson
        );
    }

    private static bool IsSafeIdentifier(string segment) =>
        segment.Length > 0
        && !segment.Contains("--", StringComparison.Ordinal)
        && (char.IsAsciiLetter(segment[0]) || segment[0] == '_')
        && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string Text(string name, IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidDataException($"Google billing export returned a blank {name}.");

    private static decimal Decimal(string name, IReadOnlyDictionary<string, object?> row)
    {
        if (!row.TryGetValue(name, out var value))
        {
            throw new InvalidDataException($"Google billing export omitted {name}.");
        }
        return value switch
        {
            BigQueryNumeric numeric => numeric.ToDecimal(LossOfPrecisionHandling.Throw),
            decimal amount => amount,
            _ => throw new InvalidDataException($"Google billing export returned a non-NUMERIC {name}."),
        };
    }

    private static LocalDate Date(string name, IReadOnlyDictionary<string, object?> row)
    {
        if (row.TryGetValue(name, out var value) && value is DateTime date)
        {
            return new LocalDate(date.Year, date.Month, date.Day);
        }
        if (value is DateOnly dateOnly)
        {
            return new LocalDate(dateOnly.Year, dateOnly.Month, dateOnly.Day);
        }
        throw new InvalidDataException($"Google billing export returned an invalid {name}.");
    }

    private static Instant Timestamp(string name, IReadOnlyDictionary<string, object?> row)
    {
        if (!row.TryGetValue(name, out var value))
        {
            throw new InvalidDataException($"Google billing export omitted {name}.");
        }
        return value switch
        {
            DateTime { Kind: DateTimeKind.Utc } timestamp => Instant.FromDateTimeUtc(timestamp),
            DateTimeOffset { Offset: var offset } timestamp when offset == TimeSpan.Zero => Instant.FromDateTimeOffset(
                timestamp
            ),
            _ => throw new InvalidDataException($"Google billing export returned a non-UTC or invalid {name}."),
        };
    }
}

internal sealed record GoogleBillingQuery(string Sql, IReadOnlyList<BigQueryParameter> Parameters);
