using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using AiObservatory.Ingest.Services.Google;
using AwesomeAssertions;
using Google;
using Google.Apis.Bigquery.v2;
using Google.Apis.Bigquery.v2.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Cloud.BigQuery.V2;
using Newtonsoft.Json;
using NodaTime;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

public sealed class GoogleBillingExportClientTests
{
    [Theory]
    [InlineData("project.dataset.table")]
    [InlineData("proj_1.dataset_2.view_3")]
    public void ValidateExportTable_accepts_three_safe_identifier_segments(string table) =>
        GoogleBillingExportClient.ValidateExportTable(table).Should().Be(table);

    [Theory]
    [InlineData("")]
    [InlineData("project.dataset")]
    [InlineData("project.dataset.table.extra")]
    [InlineData("project. dataset.table")]
    [InlineData("project.dataset.`table`")]
    [InlineData("project.dataset.table; DROP TABLE rows")]
    [InlineData("project.dataset.table*")]
    [InlineData("project.dataset.table--comment")]
    public void ValidateExportTable_rejects_any_unsafe_identifier(string table)
    {
        var act = () => GoogleBillingExportClient.ValidateExportTable(table);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildQuery_binds_time_values_and_reaggregates_late_corrections()
    {
        var query = GoogleBillingExportClient.BuildQuery("project.dataset.table");

        query.Sql.Should().Contain("@from").And.Contain("@through_exclusive").And.Contain("@changes_since");
        query.Sql.Should().Contain("export_time > @changes_since");
        // Partition pruning: the source scan needs its own date predicate — neither the
        // IS NOT DISTINCT FROM join nor the OR'd export_time filter lets BigQuery prune.
        query
            .Sql.Should()
            .Contain("WHERE DATE(source.usage_start_time) >= DATE_SUB(DATE(@changes_since), INTERVAL 31 DAY)");
        query.Sql.Should().Contain("FROM `project.dataset.table` AS source");
        query.Sql.Should().Contain("UNNEST(source.credits)");
        query.Sql.Should().Contain("CAST(source.cost * 1000000 AS INT64)");
        query.Sql.Should().Contain("SUM(CAST(credit.amount * 1000000 AS INT64))");
        query.Sql.Should().NotContain("ROUND(");
        query
            .Sql.Should()
            .Contain("ARRAY_AGG(service_description ORDER BY export_time DESC, service_description DESC LIMIT 1)");
        query
            .Sql.Should()
            .Contain("ARRAY_AGG(sku_description ORDER BY export_time DESC, sku_description DESC LIMIT 1)");
        query.Sql.Should().Contain("GROUP BY usage_date, billing_period, service_id, sku_id, currency");
        query.Sql.Should().NotContain("USING (usage_date");
        query.Sql.Should().Contain("IS NOT DISTINCT FROM affected.usage_date");
        query.Sql.Should().Contain("service_description DESC");
        query.Sql.Should().Contain("sku_description DESC");
        query
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .BeEquivalentTo("from", "through_exclusive", "changes_since");
    }

    [Fact]
    public void BuildQuery_places_the_line_items_predicate_after_the_join()
    {
        var query = GoogleBillingExportClient.BuildQuery("project.dataset.table");

        var fromSource = query.Sql.IndexOf("FROM `project.dataset.table` AS source", StringComparison.Ordinal);
        var join = query.Sql.IndexOf("INNER JOIN affected_keys", StringComparison.Ordinal);
        var where = query.Sql.IndexOf("WHERE DATE(source.usage_start_time)", StringComparison.Ordinal);
        fromSource.Should().BeGreaterThanOrEqualTo(0);
        join.Should().BeGreaterThan(fromSource);
        where
            .Should()
            .BeGreaterThan(
                join,
                "GoogleSQL parses the join as part of the from-clause, so a WHERE before INNER JOIN rejects the whole query"
            );
    }

    [Fact]
    public void BuildQuery_filters_the_source_scan_at_day_granularity_so_a_mid_day_changes_since_keeps_the_whole_boundary_day()
    {
        var query = GoogleBillingExportClient.BuildQuery("project.dataset.table");

        // affected_keys is keyed on DATE(usage_start_time), so the coarse scan predicate must admit
        // whole days. A raw-timestamp bound (usage_start_time >= @changes_since - INTERVAL 31 DAY)
        // splits the day exactly 31 days before a mid-day changes_since at that time-of-day, and the
        // partial re-aggregation overwrites the stored total wholesale as a correction.
        query
            .Sql.Should()
            .Contain("WHERE DATE(source.usage_start_time) >= DATE_SUB(DATE(@changes_since), INTERVAL 31 DAY)");
        query.Sql.Should().NotContain("source.usage_start_time >= @changes_since - INTERVAL 31 DAY");
    }

    [Fact]
    public async Task GetBillingRecordsAsync_binds_exact_UTC_timestamp_parameters_and_propagates_cancellation()
    {
        var sdk = Substitute.For<BigQueryClient>();
        string? sql = null;
        BigQueryParameter[]? parameters = null;
        CancellationToken queryCancellation = default;
        sdk.ExecuteQueryAsync(
                Arg.Do<string>(value => sql = value),
                Arg.Do<IEnumerable<BigQueryParameter>>(value => parameters = value.ToArray()),
                Arg.Is<QueryOptions>(options => options.UseLegacySql == false),
                null,
                Arg.Do<CancellationToken>(value => queryCancellation = value)
            )
            .Returns(Results(sdk));
        using var cancellation = new CancellationTokenSource();

        var records = await Client(sdk)
            .GetBillingRecordsAsync(
                Instant.FromUtc(2026, 8, 1, 1, 2),
                Instant.FromUtc(2026, 8, 2, 3, 4),
                Instant.FromUtc(2026, 7, 31, 5, 6),
                cancellation.Token
            );

        records.Records.Should().BeEmpty();
        sql.Should().Contain("@from").And.Contain("@through_exclusive").And.Contain("@changes_since");
        parameters.Should().NotBeNull();
        parameters.Select(parameter => parameter.Name).Should().Equal("from", "through_exclusive", "changes_since");
        parameters.Select(parameter => parameter.Type).Should().OnlyContain(type => type == BigQueryDbType.Timestamp);
        parameters
            .Select(parameter => parameter.Value)
            .Should()
            .Equal(
                new DateTime(2026, 8, 1, 1, 2, 0, DateTimeKind.Utc),
                new DateTime(2026, 8, 2, 3, 4, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 31, 5, 6, 0, DateTimeKind.Utc)
            );
        parameters
            .Select(parameter => ((DateTime)parameter.Value!).Kind)
            .Should()
            .OnlyContain(kind => kind == DateTimeKind.Utc);
        queryCancellation.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task GetBillingRecordsAsync_throws_without_returning_a_prefix_when_query_fails()
    {
        var sdk = Substitute.For<BigQueryClient>();
        sdk.ExecuteQueryAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<BigQueryParameter>>(),
                Arg.Any<QueryOptions>(),
                null,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromException<BigQueryResults>(new InvalidOperationException("query failed")));

        var act = () => FetchAsync(Client(sdk));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("query failed");
    }

    [Fact]
    public async Task GetBillingRecordsAsync_enumerates_two_SDK_result_pages_before_returning()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(JsonResponse(Response(null, RestRow("sku-2"))))
        );
        using var pagingSdk = PagingClient(handler);
        var querySdk = QueryClient(Results(pagingSdk, "next-page", RestRow("sku-1")));

        var records = await FetchAsync(Client(querySdk), TestContext.Current.CancellationToken);

        records.Records.Select(record => record.SkuId).Should().Equal("sku-1", "sku-2");
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetBillingRecordsAsync_throws_without_returning_a_prefix_when_a_later_page_fails()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            "{\"error\":{\"message\":\"page failed\"}}",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    }
                )
        );
        using var pagingSdk = PagingClient(handler);
        var querySdk = QueryClient(Results(pagingSdk, "next-page", RestRow("sku-1")));

        var act = () => FetchAsync(Client(querySdk));

        await act.Should().ThrowAsync<GoogleApiException>();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetBillingRecordsAsync_throws_without_returning_a_prefix_when_a_later_row_is_invalid()
    {
        var sdk = Substitute.For<BigQueryClient>();
        var results = Results(sdk, null, RestRow("sku-1"), RestRow("sku-2", net: "7"));
        StubQuery(sdk, results);

        var act = () => FetchAsync(Client(sdk));

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetBillingRecordsAsync_cancels_during_SDK_result_enumeration_without_returning_a_prefix()
    {
        var pageRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(
            async (_, cancellationToken) =>
            {
                pageRequested.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonResponse(Response(null));
            }
        );
        using var pagingSdk = PagingClient(handler);
        var querySdk = QueryClient(Results(pagingSdk, "next-page", RestRow("sku-1")));
        using var cancellation = new CancellationTokenSource();

        var fetch = FetchAsync(Client(querySdk), cancellation.Token);
        await pageRequested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        var act = () => fetch.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetBillingRecordsAsync_returns_an_immutable_collection()
    {
        var sdk = Substitute.For<BigQueryClient>();
        StubQuery(sdk, Results(sdk, null, RestRow("sku-1")));

        var records = await FetchAsync(Client(sdk), TestContext.Current.CancellationToken);

        records.Records.Should().BeAssignableTo<IImmutableList<GoogleBillingRecord>>();
    }

    [Fact]
    public void BuildOutOfRangeKeysQuery_counts_affected_keys_below_the_scan_floor()
    {
        var query = GoogleBillingExportClient.BuildOutOfRangeKeysQuery("project.dataset.table");

        // Same unbounded export_time arm as the main query's affected_keys: that arm is what
        // admits corrections for usage older than the line_items scan floor.
        query.Sql.Should().Contain("export_time > @changes_since");
        query.Sql.Should().Contain("SELECT COUNT(*) AS out_of_range_keys");
        query.Sql.Should().Contain("WHERE usage_date < DATE_SUB(DATE(@changes_since), INTERVAL 31 DAY)");
        query.Sql.Should().Contain("FROM `project.dataset.table`");
        query.Sql.Should().NotContain("line_items");
        query
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .BeEquivalentTo("from", "through_exclusive", "changes_since");
    }

    [Fact]
    public async Task GetBillingRecordsAsync_surfaces_the_out_of_range_affected_key_count()
    {
        var sdk = Substitute.For<BigQueryClient>();
        StubQuery(sdk, Results(sdk, null, RestRow("sku-1")), outOfRangeKeys: 3);

        var result = await FetchAsync(Client(sdk), TestContext.Current.CancellationToken);

        result.Records.Select(record => record.SkuId).Should().Equal("sku-1");
        result.OutOfRangeAffectedKeyCount.Should().Be(3);
    }

    [Fact]
    public async Task GetBillingRecordsAsync_when_the_count_query_fails_still_returns_records_with_a_null_count()
    {
        // The count only feeds the stale-correction warning, so a transient failure must not
        // abort the export: the main query still runs and the count degrades to null.
        var sdk = Substitute.For<BigQueryClient>();
        sdk.ExecuteQueryAsync(
                Arg.Is<string>(sql => sql.Contains("out_of_range_keys", StringComparison.Ordinal)),
                Arg.Any<IEnumerable<BigQueryParameter>>(),
                Arg.Any<QueryOptions>(),
                null,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromException<BigQueryResults>(new InvalidOperationException("count failed")));
        sdk.ExecuteQueryAsync(
                Arg.Is<string>(sql => !sql.Contains("out_of_range_keys", StringComparison.Ordinal)),
                Arg.Any<IEnumerable<BigQueryParameter>>(),
                Arg.Any<QueryOptions>(),
                null,
                Arg.Any<CancellationToken>()
            )
            .Returns(Results(sdk, null, RestRow("sku-1")));

        var result = await FetchAsync(Client(sdk), TestContext.Current.CancellationToken);

        result.Records.Select(record => record.SkuId).Should().Equal("sku-1");
        result.OutOfRangeAffectedKeyCount.Should().BeNull();
        // The failure is carried, not discarded, so the "count unavailable" warning can name
        // the cause.
        result
            .OutOfRangeCountFailure.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Be("count failed");
    }

    [Fact]
    public async Task GetBillingRecordsAsync_when_the_count_query_is_cancelled_still_throws()
    {
        var sdk = Substitute.For<BigQueryClient>();
        sdk.ExecuteQueryAsync(
                Arg.Is<string>(sql => sql.Contains("out_of_range_keys", StringComparison.Ordinal)),
                Arg.Any<IEnumerable<BigQueryParameter>>(),
                Arg.Any<QueryOptions>(),
                null,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromException<BigQueryResults>(new OperationCanceledException()));

        var act = () => FetchAsync(Client(sdk), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(" 02608")]
    [InlineData("2026+8")]
    [InlineData("2026 8")]
    public async Task GetBillingRecordsAsync_rejects_invoice_month_with_non_ASCII_digits(string billingPeriod)
    {
        var sdk = Substitute.For<BigQueryClient>();
        StubQuery(sdk, Results(sdk, null, RestRow("sku-1", billingPeriod: billingPeriod)));

        var act = () => FetchAsync(Client(sdk));

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("billing_period", 1, " ")]
    [InlineData("service_id", 2, "")]
    [InlineData("service_description", 3, " ")]
    [InlineData("sku_id", 4, "")]
    [InlineData("sku_description", 5, " ")]
    [InlineData("currency", 6, "")]
    [InlineData("currency", 6, "eur")]
    [InlineData("currency", 6, "EURO")]
    [InlineData("raw_json", 11, "{")]
    public async Task GetBillingRecordsAsync_rejects_blank_identity_and_invalid_text_fields(
        string field,
        int cellIndex,
        string value
    )
    {
        var sdk = Substitute.For<BigQueryClient>();
        var row = RestRow("sku-1");
        row.F[cellIndex].V = value;
        StubQuery(sdk, Results(sdk, null, row));

        var act = () => FetchAsync(Client(sdk));

        await act.Should().ThrowAsync<InvalidDataException>("because {0} is invalid", field);
    }

    [Fact]
    public async Task MapRowsAsync_buffers_every_page_before_returning_records()
    {
        var records = await GoogleBillingExportClient.MapRowsAsync(
            Rows(ValidRow("sku-1"), ValidRow("sku-2")),
            TestContext.Current.CancellationToken
        );

        records.Select(record => record.SkuId).Should().BeEquivalentTo("sku-1", "sku-2");
    }

    [Fact]
    public async Task MapRowsAsync_fails_closed_when_a_later_row_is_invalid()
    {
        var invalid = ValidRow("sku-2");
        invalid["net_amount"] = 7m;

        var act = () =>
            GoogleBillingExportClient.MapRowsAsync(
                Rows(ValidRow("sku-1"), invalid),
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("gross_amount", null)]
    [InlineData("gross_amount", "10.0")]
    [InlineData("gross_amount", 10.0d)]
    [InlineData("usage_date", "2026-02-30")]
    [InlineData("billing_period", "202600")]
    [InlineData("billing_period", "202613")]
    public async Task MapRowsAsync_rejects_invalid_provider_field_values(string field, object? value)
    {
        var row = ValidRow("sku-1");
        row[field] = value;

        var act = () => GoogleBillingExportClient.MapRowsAsync(Rows(row), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapRowsAsync_rejects_non_UTC_observation_timestamps(bool useOffsetTimestamp)
    {
        var row = ValidRow("sku-1");
        row["observed_at"] = useOffsetTimestamp
            ? (object)new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.FromHours(1))
            : new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Unspecified);
        if (!useOffsetTimestamp)
        {
            row["observed_at"].Should().BeOfType<DateTime>();
        }

        var act = () => GoogleBillingExportClient.MapRowsAsync(Rows(row), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task MapRowsAsync_returns_an_immutable_result()
    {
        var records = await GoogleBillingExportClient.MapRowsAsync(
            Rows(ValidRow("sku-1")),
            TestContext.Current.CancellationToken
        );

        records.Should().NotBeAssignableTo<List<GoogleBillingRecord>>();
    }

    [Fact]
    public async Task MapRowsAsync_honours_cancellation_while_enumerating()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => GoogleBillingExportClient.MapRowsAsync(Rows(ValidRow("sku-1")), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows(
        params IReadOnlyDictionary<string, object?>[] rows
    )
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.Yield();
        }
    }

    private static GoogleBillingExportClient Client(BigQueryClient sdk) =>
        new(new Lazy<BigQueryClient>(() => sdk), "project.dataset.table");

    private static Task<GoogleBillingExportResult> FetchAsync(
        GoogleBillingExportClient client,
        CancellationToken cancellationToken = default
    ) =>
        client.GetBillingRecordsAsync(
            Instant.FromUtc(2026, 8, 1, 0, 0),
            Instant.FromUtc(2026, 8, 2, 0, 0),
            Instant.FromUtc(2026, 7, 31, 0, 0),
            cancellationToken
        );

    private static BigQueryClient QueryClient(BigQueryResults results)
    {
        var sdk = Substitute.For<BigQueryClient>();
        StubQuery(sdk, results);
        return sdk;
    }

    private static void StubQuery(BigQueryClient sdk, BigQueryResults results, long outOfRangeKeys = 0)
    {
        sdk.ExecuteQueryAsync(
                Arg.Is<string>(sql => sql.Contains("out_of_range_keys", StringComparison.Ordinal)),
                Arg.Any<IEnumerable<BigQueryParameter>>(),
                Arg.Any<QueryOptions>(),
                null,
                Arg.Any<CancellationToken>()
            )
            .Returns(CountResults(sdk, outOfRangeKeys));
        sdk.ExecuteQueryAsync(
                Arg.Is<string>(sql => !sql.Contains("out_of_range_keys", StringComparison.Ordinal)),
                Arg.Any<IEnumerable<BigQueryParameter>>(),
                Arg.Any<QueryOptions>(),
                null,
                Arg.Any<CancellationToken>()
            )
            .Returns(results);
    }

    private static BigQueryResults CountResults(BigQueryClient sdk, long count) =>
        new(
            sdk,
            new GetQueryResultsResponse
            {
                JobComplete = true,
                JobReference = new JobReference
                {
                    ProjectId = "query-project",
                    JobId = "job-1",
                    Location = "EU",
                },
                Rows = [new TableRow { F = [new TableCell { V = count.ToString(CultureInfo.InvariantCulture) }] }],
                Schema = new TableSchema
                {
                    Fields = [new TableFieldSchema { Name = "out_of_range_keys", Type = "INTEGER" }],
                },
                TotalRows = 1,
            },
            null,
            null
        );

    private static BigQueryResults Results(BigQueryClient sdk, string? nextPageToken = null, params TableRow[] rows) =>
        new(sdk, Response(nextPageToken, rows), null, null);

    private static GetQueryResultsResponse Response(string? nextPageToken, params TableRow[] rows) =>
        new()
        {
            JobComplete = true,
            JobReference = new JobReference
            {
                ProjectId = "query-project",
                JobId = "job-1",
                Location = "EU",
            },
            PageToken = nextPageToken,
            Rows = rows,
            Schema = Schema(),
            TotalRows = (ulong)rows.Length,
        };

    private static TableSchema Schema() =>
        new()
        {
            Fields =
            [
                new TableFieldSchema { Name = "usage_date", Type = "DATE" },
                new TableFieldSchema { Name = "billing_period", Type = "STRING" },
                new TableFieldSchema { Name = "service_id", Type = "STRING" },
                new TableFieldSchema { Name = "service_description", Type = "STRING" },
                new TableFieldSchema { Name = "sku_id", Type = "STRING" },
                new TableFieldSchema { Name = "sku_description", Type = "STRING" },
                new TableFieldSchema { Name = "currency", Type = "STRING" },
                new TableFieldSchema { Name = "gross_amount", Type = "NUMERIC" },
                new TableFieldSchema { Name = "credit_amount", Type = "NUMERIC" },
                new TableFieldSchema { Name = "net_amount", Type = "NUMERIC" },
                new TableFieldSchema { Name = "observed_at", Type = "TIMESTAMP" },
                new TableFieldSchema { Name = "raw_json", Type = "STRING" },
            ],
        };

    private static TableRow RestRow(string skuId, string net = "8", string billingPeriod = "202608") =>
        new()
        {
            F =
            [
                new TableCell { V = "2026-08-01" },
                new TableCell { V = billingPeriod },
                new TableCell { V = "6F81" },
                new TableCell { V = "Vertex AI" },
                new TableCell { V = skuId },
                new TableCell { V = "Gemini" },
                new TableCell { V = "EUR" },
                new TableCell { V = "10" },
                new TableCell { V = "-2" },
                new TableCell { V = net },
                new TableCell { V = "1785888000000000" },
                new TableCell { V = "{}" },
            ],
        };

    private static BigQueryClient PagingClient(HttpMessageHandler handler)
    {
        var service = new BigqueryService(
            new BaseClientService.Initializer
            {
                ApplicationName = "AiObservatory.Ingest.Tests",
                HttpClientFactory = new StubHttpClientFactory(handler),
            }
        );
        return new BigQueryClientImpl("query-project", service);
    }

    private static HttpResponseMessage JsonResponse(GetQueryResultsResponse response) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonConvert.SerializeObject(response), Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync
    ) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            return sendAsync(request, cancellationToken);
        }
    }

    private static Dictionary<string, object?> ValidRow(string skuId) =>
        new(StringComparer.Ordinal)
        {
            ["usage_date"] = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            ["billing_period"] = "202608",
            ["service_id"] = "6F81",
            ["service_description"] = "Vertex AI",
            ["sku_id"] = skuId,
            ["sku_description"] = "Gemini",
            ["currency"] = "EUR",
            ["gross_amount"] = 10m,
            ["credit_amount"] = -2m,
            ["net_amount"] = 8m,
            ["observed_at"] = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
            ["raw_json"] = "{}",
        };
}
