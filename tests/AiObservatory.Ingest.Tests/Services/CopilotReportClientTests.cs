using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiObservatory.Ingest.Services.Copilot;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public sealed class CopilotReportClientTests : IDisposable
{
    private readonly List<HttpClient> _clients = [];

    public void Dispose() => _clients.ForEach(client => client.Dispose());

    [Fact]
    public async Task GetLatestOrganizationReportAsync_UsesCurrentDescriptorAndCompletesEveryHeaderIsolatedDownload()
    {
        var descriptorHandler = new QueueHandler(_ =>
            Json(
                JsonSerializer.Serialize(
                    new
                    {
                        download_links = new[]
                        {
                            "https://reports.example/first.ndjson?sig=secret-one",
                            "https://reports.example/second.ndjson?sig=secret-two",
                        },
                        report_start_day = "2026-07-25",
                        report_end_day = "2026-08-21",
                    }
                )
            )
        );
        var downloadHandler = new QueueHandler(
            request => HeaderIsolatedNdjson(request, Wrapper(new LocalDate(2026, 8, 20), "2026-08-22T10:15:30Z")),
            request => HeaderIsolatedNdjson(request, Wrapper(new LocalDate(2026, 8, 21), null))
        );
        var sut = CreateSut(descriptorHandler, downloadHandler);

        var records = await sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        records.Should().HaveCount(2);
        records[0]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Day = new LocalDate(2026, 8, 20),
                    OrganizationId = "987654",
                    DailyActiveUsers = (int?)2,
                    WeeklyActiveUsers = (int?)7,
                    MonthlyActiveUsers = (int?)19,
                    UserInitiatedInteractionCount = 42L,
                    CodeGenerationActivityCount = 36L,
                    CodeAcceptanceActivityCount = 24L,
                    ObservedAt = (Instant?)Instant.FromUtc(2026, 8, 22, 10, 15, 30),
                }
            );
        records[1].ObservedAt.Should().BeNull("the source owns the single-clock fallback");
        records[1]
            .RawJson.Should()
            .NotContain("created_at")
            .And.Contain("\"daily_active_cli_users\":null")
            .And.Contain("\"daily_active_copilot_app_users\":1")
            .And.Contain("\"enterprise_id\":\"123456\"");
        descriptorHandler.Requests.Should().ContainSingle();
        descriptorHandler
            .Requests[0]
            .PathAndQuery.Should()
            .Be("/orgs/FixPortal/copilot/metrics/reports/organization-28-day/latest");
        descriptorHandler.Requests[0].Accept.Should().Be("application/vnd.github+json");
        descriptorHandler.Requests[0].ApiVersion.Should().Be("2026-03-10");
        descriptorHandler.Requests[0].Authorization.Should().Be("Bearer github-secret");
        downloadHandler.Requests.Select(request => request.PathAndQuery).Should().HaveCount(2);
    }

    public static TheoryData<string[]> InvalidDownloadLinks =>
        [
            Array.Empty<string>(),
            new[] { "relative.ndjson" },
            new[] { "http://reports.example/report.ndjson" },
            new[] { "https://user:password@reports.example/report.ndjson" },
            new[] { "not a URI" },
            new[] { "https://reports.example/report.ndjson", "https://REPORTS.example/report.ndjson" },
        ];

    [Theory]
    [MemberData(nameof(InvalidDownloadLinks))]
    public async Task GetLatestOrganizationReportAsync_RejectsInvalidSignedLinkSets(string[] links)
    {
        var downloads = new QueueHandler(_ => throw new InvalidOperationException("download must not start"));
        var sut = CreateSut(new QueueHandler(_ => Json(Descriptor(links))), downloads);

        var act = () => sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        downloads.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("wrong-top-level")]
    [InlineData("wrong-day-totals")]
    [InlineData("missing-day")]
    [InlineData("missing-active-users")]
    [InlineData("negative-count")]
    [InlineData("fractional-count")]
    [InlineData("invalid-date")]
    [InlineData("non-utc-created-at")]
    [InlineData("window-mismatch")]
    [InlineData("day-outside-window")]
    public async Task GetLatestOrganizationReportAsync_RejectsInvalidCompleteReportWithoutReturningAPrefix(
        string invalid
    )
    {
        var content = InvalidWrapper(invalid);
        var sut = CreateSut(
            new QueueHandler(_ => Json(Descriptor(["https://reports.example/report.ndjson"]))),
            new QueueHandler(_ => HeaderIsolatedNdjson(_, content))
        );

        var act = () => sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetLatestOrganizationReportAsync_RejectsBlankMiddleLine()
    {
        var content = Wrapper(new LocalDate(2026, 8, 20), null) + "\n \n" + Wrapper(new LocalDate(2026, 8, 21), null);
        var sut = CreateSut(
            new QueueHandler(_ => Json(Descriptor(["https://reports.example/report.ndjson"]))),
            new QueueHandler(request => HeaderIsolatedNdjson(request, content))
        );

        var act = () => sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetLatestOrganizationReportAsync_RejectsDuplicateReportDayIdentityAcrossFiles()
    {
        var wrapper = Wrapper(new LocalDate(2026, 8, 21), null);
        var sut = CreateSut(
            new QueueHandler(_ =>
                Json(Descriptor(["https://reports.example/first.ndjson", "https://reports.example/second.ndjson"]))
            ),
            new QueueHandler(
                request => HeaderIsolatedNdjson(request, wrapper),
                request => HeaderIsolatedNdjson(request, wrapper)
            )
        );

        var act = () => sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetLatestOrganizationReportAsync_FailedLaterDownloadReturnsNoPrefix()
    {
        var sut = CreateSut(
            new QueueHandler(_ =>
                Json(Descriptor(["https://reports.example/first.ndjson", "https://reports.example/second.ndjson"]))
            ),
            new QueueHandler(
                request => HeaderIsolatedNdjson(request, Wrapper(new LocalDate(2026, 8, 20), null)),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            )
        );

        var act = () => sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetLatestOrganizationReportAsync_RejectsAnAggregateDeclaredSizeAboveFiftyMiB()
    {
        var sut = CreateSut(
            new QueueHandler(_ =>
                Json(Descriptor(["https://reports.example/first.ndjson", "https://reports.example/second.ndjson"]))
            ),
            new QueueHandler(
                _ => SizedNdjson(30L * 1024 * 1024, Wrapper(new LocalDate(2026, 8, 20), null)),
                _ => SizedNdjson(21L * 1024 * 1024, Wrapper(new LocalDate(2026, 8, 21), null))
            )
        );

        var act = () => sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetLatestOrganizationReportAsync_RejectsActualUndeclaredBytesAboveFiftyMiB()
    {
        var content = new StreamContent(new OversizedUndeclaredReportStream());
        content.Headers.ContentLength.Should().BeNull();
        var sut = CreateSut(
            new QueueHandler(_ => Json(Descriptor(["https://reports.example/report.ndjson"]))),
            new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content })
        );

        var act = () => sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetLatestOrganizationReportAsync_AcceptsOneUtf8BomAtTheFirstReportLine()
    {
        var body = WithPreamble(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            Wrapper(new(2026, 8, 21), null)
        );
        var sut = CreateSut(
            new QueueHandler(_ => Json(Descriptor(["https://reports.example/report.ndjson"]))),
            new QueueHandler(_ => Bytes(body))
        );

        var records = await sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        records.Should().ContainSingle().Which.Day.Should().Be(new LocalDate(2026, 8, 21));
    }

    [Fact]
    public async Task GetLatestOrganizationReportAsync_AcceptsOneUtf8BomPerDownloadShard()
    {
        // Each shard is an independent stream read, so a BOM on the second file must strip
        // exactly like one on the first — previously only the first shard allowed it.
        var bom = WithPreamble(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            Wrapper(new(2026, 8, 21), null)
        );
        var sut = CreateSut(
            new QueueHandler(_ =>
                Json(Descriptor(["https://reports.example/first.ndjson", "https://reports.example/second.ndjson"]))
            ),
            new QueueHandler(
                request => HeaderIsolatedNdjson(request, Wrapper(new LocalDate(2026, 8, 20), null)),
                _ => Bytes(bom)
            )
        );

        var act = () => sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-32")]
    public async Task GetLatestOrganizationReportAsync_RejectsBomSelectedNonUtf8Transport(string encodingName)
    {
        Encoding encoding =
            encodingName == "utf-16"
                ? new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true)
                : new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
        var body = WithPreamble(encoding, Wrapper(new(2026, 8, 21), null));
        var sut = CreateSut(
            new QueueHandler(_ => Json(Descriptor(["https://reports.example/report.ndjson"]))),
            new QueueHandler(_ => Bytes(body))
        );

        var act = () => sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetLatestOrganizationReportAsync_RejectsOversizedDescriptorBeforeAnyDownload()
    {
        var descriptor = Json(Descriptor(["https://reports.example/report.ndjson"]));
        descriptor.Content.Headers.ContentLength = 2L * 1024 * 1024 + 1;
        var downloads = new QueueHandler(_ => throw new InvalidOperationException("download must not start"));
        var sut = CreateSut(new QueueHandler(_ => descriptor), downloads);

        var act = () => sut.GetLatestOrganizationReportAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        downloads.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("download")]
    public async Task GetLatestOrganizationReportAsync_PropagatesCancellation(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelled = new QueueHandler(request =>
            throw new OperationCanceledException("cancelled", null, request.CancellationToken)
        );
        var descriptor =
            stage == "descriptor"
                ? cancelled
                : new QueueHandler(_ => Json(Descriptor(["https://reports.example/report.ndjson"])));
        var download = stage == "download" ? cancelled : new QueueHandler(_ => throw new InvalidOperationException());
        var sut = CreateSut(descriptor, download);

        var act = () => sut.GetLatestOrganizationReportAsync(cancellation.Token);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task GetLatestOrganizationReportAsync_PropagatesCancellationWhileParsing()
    {
        using var cancellation = new CancellationTokenSource();
        var sut = CreateSut(
            new QueueHandler(_ => Json(Descriptor(["https://reports.example/report.ndjson"]))),
            new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancellingStream(cancellation)),
            })
        );

        var act = () => sut.GetLatestOrganizationReportAsync(cancellation.Token);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cancellation.Token);
    }

    private CopilotReportClient CreateSut(HttpMessageHandler descriptorHandler, HttpMessageHandler downloadHandler)
    {
        var descriptor = new HttpClient(descriptorHandler) { BaseAddress = new Uri("https://api.github.com") };
        descriptor.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        descriptor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "github-secret");
        descriptor.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        var download = new HttpClient(downloadHandler);
        _clients.Add(descriptor);
        _clients.Add(download);
        return new CopilotReportClient(descriptor, download, "FixPortal");
    }

    private static HttpResponseMessage HeaderIsolatedNdjson(Request request, string ndjson)
    {
        if (request.Authorization is not null || request.ApiVersion is not null || request.Accept is not null)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjson + "\n", Encoding.UTF8, "application/x-ndjson"),
        };
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static byte[] WithPreamble(Encoding encoding, string line) =>
        [.. encoding.GetPreamble(), .. encoding.GetBytes(line + "\n")];

    private static string Descriptor(string[] links) =>
        JsonSerializer.Serialize(
            new
            {
                download_links = links,
                report_start_day = "2026-07-25",
                report_end_day = "2026-08-21",
            }
        );

    private static HttpResponseMessage SizedNdjson(long declaredLength, string ndjson)
    {
        var response = HeaderIsolatedNdjson(new Request("/", null, null, null, default), ndjson);
        response.Content.Headers.ContentLength = declaredLength;
        return response;
    }

    private static string InvalidWrapper(string invalid)
    {
        if (invalid == "invalid-json")
        {
            return "{";
        }
        if (invalid == "wrong-top-level")
        {
            return "[]";
        }

        var root = JsonNode.Parse(Wrapper(new LocalDate(2026, 8, 21), "2026-08-22T10:15:30Z"))!.AsObject();
        var day = root["day_totals"]!.AsArray()[0]!.AsObject();
        switch (invalid)
        {
            case "wrong-day-totals":
                root["day_totals"] = new JsonObject();
                break;
            case "missing-day":
                day.Remove("day");
                break;
            case "missing-active-users":
                day.Remove("weekly_active_users");
                break;
            case "negative-count":
                day["user_initiated_interaction_count"] = -1;
                break;
            case "fractional-count":
                day["code_generation_activity_count"] = 1.5;
                break;
            case "invalid-date":
                day["day"] = "2026-02-30";
                break;
            case "non-utc-created-at":
                root["created_at"] = "2026-08-22T11:15:30+01:00";
                break;
            case "window-mismatch":
                root["report_start_day"] = "2026-07-24";
                break;
            case "day-outside-window":
                day["day"] = "2026-07-24";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalid));
        }
        return root.ToJsonString();
    }

    private static string Wrapper(LocalDate day, string? createdAt)
    {
        var wrapper = new Dictionary<string, object?>
        {
            ["report_start_day"] = "2026-07-25",
            ["report_end_day"] = "2026-08-21",
            ["enterprise_id"] = "123456",
            ["organization_id"] = "987654",
            ["etl_id"] = "green",
            ["day_partition"] = "2026-08-21",
            ["entity_id_partition"] = 987654,
            ["day_totals"] = new[]
            {
                new
                {
                    day = $"{day:yyyy-MM-dd}",
                    enterprise_id = "123456",
                    organization_id = "987654",
                    daily_active_users = 2,
                    weekly_active_users = 7,
                    monthly_active_users = 19,
                    monthly_active_chat_users = 11,
                    monthly_active_agent_users = 5,
                    daily_active_copilot_cloud_agent_users = 1,
                    weekly_active_copilot_cloud_agent_users = 3,
                    monthly_active_copilot_cloud_agent_users = 4,
                    daily_active_copilot_code_review_users = 1,
                    weekly_active_copilot_code_review_users = 2,
                    monthly_active_copilot_code_review_users = 3,
                    daily_passive_copilot_code_review_users = 0,
                    weekly_passive_copilot_code_review_users = 1,
                    monthly_passive_copilot_code_review_users = 2,
                    daily_active_cli_users = (int?)null,
                    daily_active_copilot_app_users = (int?)1,
                    user_initiated_interaction_count = 42,
                    code_generation_activity_count = 36,
                    code_acceptance_activity_count = 24,
                    loc_suggested_to_add_sum = 100,
                    loc_suggested_to_delete_sum = 2,
                    loc_added_sum = 80,
                    loc_deleted_sum = 4,
                    totals_by_ide = Array.Empty<object>(),
                    totals_by_feature = Array.Empty<object>(),
                    totals_by_language_feature = Array.Empty<object>(),
                    totals_by_language_model = Array.Empty<object>(),
                    totals_by_model_feature = Array.Empty<object>(),
                    totals_by_cli = (object?)null,
                    totals_by_copilot_app = (object?)null,
                    totals_by_3rd_party_agent = (object?)null,
                    totals_by_ai_adoption_phase = (object?)null,
                    pull_requests = new { total_created = 0 },
                },
            },
        };
        if (createdAt is not null)
        {
            wrapper["created_at"] = createdAt;
        }
        return JsonSerializer.Serialize(wrapper);
    }

    private sealed class QueueHandler(params Func<Request, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<Request> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var captured = new Request(
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.SingleOrDefault()?.MediaType,
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var values) ? values.Single() : null,
                cancellationToken
            );
            Requests.Add(captured);
            return Task.FromResult(responses[_index++](captured));
        }
    }

    private sealed record Request(
        string PathAndQuery,
        string? Authorization,
        string? Accept,
        string? ApiVersion,
        CancellationToken CancellationToken
    );

    private sealed class CancellingStream(CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromException<int>(
                new OperationCanceledException("cancelled while parsing", null, cancellation.Token)
            );
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OversizedUndeclaredReportStream : Stream
    {
        private const int LineCount = 52;
        private const int PaddingLength = 1024 * 1024;
        private int _lineIndex;
        private byte[] _current = [];
        private int _currentOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_currentOffset == _current.Length && !MoveNextLine())
            {
                return 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsSpan(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private bool MoveNextLine()
        {
            if (_lineIndex == LineCount)
            {
                return false;
            }

            var root = JsonNode.Parse(Wrapper(new LocalDate(2026, 8, 21), null))!.AsObject();
            var organizationId = $"oversized-{_lineIndex++}";
            root["organization_id"] = organizationId;
            root["day_totals"]!.AsArray()[0]!["organization_id"] = organizationId;
            root["padding"] = new string('x', PaddingLength);
            _current = Encoding.UTF8.GetBytes(root.ToJsonString() + "\n");
            _currentOffset = 0;
            return true;
        }
    }
}
