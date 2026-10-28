using System.Net;
using System.Text;
using System.Text.Json;
using AiObservatory.Ingest.Services.Moonshot;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public sealed class MoonshotAdminClientTests : IDisposable
{
    private static readonly LocalDate From = new(2026, 10, 1);
    private static readonly LocalDate Through = new(2026, 10, 2);
    private readonly List<HttpClient> _clients = [];

    public void Dispose() => _clients.ForEach(client => client.Dispose());

    [Fact]
    public async Task GetDailyBillsAsync_RequestsTheChinaTimeDayWindowInEpochMilliseconds()
    {
        // 1790784000000 and 1792598400000 are the 2026-10-01 and 2026-10-22 00:00 CST examples in Moonshot's docs.
        var handler = new QueueHandler(_ => Ok(Page(false, null)));
        var sut = CreateSut(handler);

        await sut.GetDailyBillsAsync(From, new LocalDate(2026, 10, 21), TestContext.Current.CancellationToken);

        handler
            .Requests.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("/v1/admin/organizations/billings/daily_bills?")
            .And.Contain("start=1790784000000")
            .And.Contain("end=1792598400000")
            .And.Contain("limit=1000");
    }

    [Fact]
    public async Task GetDailyBillsAsync_FollowsTheCursorAndFlattensModelLinesToUsd()
    {
        var handler = new QueueHandler(
            _ =>
                Ok(
                    Page(
                        true,
                        "cursor+2/=",
                        Day(
                            "2026-10-01",
                            250_000,
                            200_000,
                            50_000,
                            Detail("kimi-k3.1-pro", "prompt", 1_000, 250_000, 200_000, 50_000)
                        )
                    )
                ),
            _ =>
                Ok(
                    Page(
                        false,
                        null,
                        Day(
                            "2026-10-02",
                            123_456,
                            123_456,
                            0,
                            Detail("kimi-k3.1-pro", "completion", 77, 123_456, 123_456, 0)
                        )
                    )
                )
        );
        var sut = CreateSut(handler);

        var records = await sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        records.Should().HaveCount(2);
        records[0]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Date = From,
                    ModelId = "kimi-k3.1-pro",
                    TokenType = "prompt",
                    KeyId = "key-1",
                    Tokens = (long?)1_000,
                    TotalUsd = 2.5m,
                    CashUsd = 2m,
                    VoucherUsd = 0.5m,
                    Sku = "kimi-k3.1-pro/prompt",
                }
            );
        records[1].TotalUsd.Should().Be(1.23456m);
        records[0].RawJson.Should().Contain("\"date\":\"2026-10-01\"").And.Contain("\"token_type\":\"prompt\"");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Contain("cursor=cursor%2B2%2F%3D");
    }

    [Fact]
    public async Task GetDailyBillsAsync_KeepsChargesTheBreakdownOmitsAsAResidualLine()
    {
        var sut = CreateSut(
            new QueueHandler(_ =>
                Ok(
                    Page(
                        false,
                        null,
                        Day(
                            "2026-10-01",
                            300_000,
                            250_000,
                            50_000,
                            Detail("kimi-k3", "prompt", 10, 200_000, 150_000, 50_000)
                        )
                    )
                )
            )
        );

        var records = await sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        records.Should().HaveCount(2);
        records[1]
            .Should()
            .BeEquivalentTo(
                new
                {
                    ModelId = (string?)null,
                    TokenType = (string?)null,
                    KeyId = (string?)null,
                    Tokens = (long?)null,
                    TotalUsd = 1m,
                    CashUsd = 1m,
                    VoucherUsd = 0m,
                    Sku = MoonshotBillRecord.ResidualSku,
                }
            );
        records.Sum(record => record.TotalUsd).Should().Be(3m);
    }

    [Fact]
    public async Task GetDailyBillsAsync_AddsNoResidualWhenTheBreakdownCoversTheDay()
    {
        var sut = CreateSut(
            new QueueHandler(_ =>
                Ok(
                    Page(
                        false,
                        null,
                        Day("2026-10-01", 200_000, 200_000, 0, Detail("kimi-k3", "prompt", 10, 200_000, 200_000, 0))
                    )
                )
            )
        );

        var records = await sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        records.Should().ContainSingle().Which.ModelId.Should().Be("kimi-k3");
    }

    [Fact]
    public async Task GetDailyBillsAsync_ReturnsAZeroFeeDayWithNoLines()
    {
        var sut = CreateSut(new QueueHandler(_ => Ok(Page(false, null, Day("2026-10-01", 0, 0, 0)))));

        var records = await sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        records.Should().BeEmpty();
    }

    [Theory]
    [InlineData("split")]
    [InlineData("detail-split")]
    [InlineData("details-exceed-total")]
    [InlineData("details-exceed-cash")]
    [InlineData("date-before-range")]
    [InlineData("date-after-range")]
    [InlineData("date-malformed")]
    [InlineData("duplicate-date")]
    [InlineData("negative-fee")]
    [InlineData("string-fee")]
    [InlineData("blank-model")]
    [InlineData("missing-token-type")]
    [InlineData("negative-tokens")]
    [InlineData("missing-has-more")]
    [InlineData("missing-next")]
    [InlineData("final-page-cursor")]
    [InlineData("blank-cursor")]
    [InlineData("not-an-object")]
    public async Task GetDailyBillsAsync_RejectsAMalformedOrInconsistentPage(string mutation)
    {
        var sut = CreateSut(new QueueHandler(_ => Ok(Mutated(mutation))));

        var act = () => sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetDailyBillsAsync_RejectsTwoDetailRowsWithTheSameModelTokenTypeAndKey()
    {
        // Both rows are individually valid and the day still sums, so only the identity check can catch them:
        // they would share an observation key and the second would silently replace the first.
        var detail = Detail("kimi-k3", "prompt", 10, 200_000, 150_000, 50_000);
        var sut = CreateSut(
            new QueueHandler(_ => Ok(Page(false, null, Day("2026-10-01", 400_000, 300_000, 100_000, detail, detail))))
        );

        var act = () => sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*duplicate model details*");
    }

    [Fact]
    public async Task GetDailyBillsAsync_AllowsTheSameModelAndTokenTypeUnderDifferentKeys()
    {
        var first = Detail("kimi-k3", "prompt", 10, 200_000, 150_000, 50_000);
        var second = DetailJson("kimi-k3", "prompt", 10, 200_000, 150_000, 50_000, key: "key-2");
        var sut = CreateSut(
            new QueueHandler(_ => Ok(Page(false, null, Day("2026-10-01", 400_000, 300_000, 100_000, first, second))))
        );

        var records = await sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        records.Select(record => record.KeyId).Should().BeEquivalentTo("key-1", "key-2");
    }

    [Fact]
    public async Task GetDailyBillsAsync_RejectsARepeatedCursor()
    {
        var sut = CreateSut(
            new QueueHandler(
                _ => Ok(Page(true, "same", Day("2026-10-01", 0, 0, 0))),
                _ => Ok(Page(true, "same", Day("2026-10-02", 0, 0, 0)))
            )
        );

        var act = () => sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*repeated a cursor*");
    }

    [Fact]
    public async Task GetDailyBillsAsync_TreatsDataNotReadyAsUnavailableNotAFailure()
    {
        var sut = CreateSut(new QueueHandler(_ => Error(HttpStatusCode.BadRequest, "data_not_ready")));

        var act = () => sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SourceUnavailableException>();
    }

    [Fact]
    public async Task GetDailyBillsAsync_TreatsNoDataForTheOrganizationAsEmpty()
    {
        var sut = CreateSut(new QueueHandler(_ => Error(HttpStatusCode.NotFound, "resource_not_found")));

        var records = await sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        records.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDailyBillsAsync_FailsOnAnyOtherNotFoundOrAuthError_WithoutEchoingTheBody()
    {
        var sut = CreateSut(
            new QueueHandler(_ => Error(HttpStatusCode.Forbidden, "insufficient_scope", "sk-admin-secret-leak"))
        );

        var act = () => sut.GetDailyBillsAsync(From, Through, TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        thrown.Which.Message.Should().Contain("403").And.Contain("insufficient_scope").And.NotContain("sk-admin");
    }

    [Fact]
    public async Task GetDailyBillsAsync_RejectsAnEndBeforeTheStart()
    {
        var sut = CreateSut(new QueueHandler());

        var act = () => sut.GetDailyBillsAsync(Through, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static string Mutated(string mutation)
    {
        var day = Day(
            "2026-10-01",
            200_000,
            150_000,
            50_000,
            Detail("kimi-k3", "prompt", 10, 200_000, 150_000, 50_000)
        );
        return mutation switch
        {
            "split" => Page(false, null, Day("2026-10-01", 200_000, 100_000, 50_000)),
            "detail-split" => Page(
                false,
                null,
                Day("2026-10-01", 200_000, 150_000, 50_000, Detail("kimi-k3", "prompt", 10, 200_000, 100_000, 50_000))
            ),
            "details-exceed-total" => Page(
                false,
                null,
                Day("2026-10-01", 100_000, 100_000, 0, Detail("kimi-k3", "prompt", 10, 200_000, 200_000, 0))
            ),
            "details-exceed-cash" => Page(
                false,
                null,
                Day("2026-10-01", 200_000, 100_000, 100_000, Detail("kimi-k3", "prompt", 10, 200_000, 200_000, 0))
            ),
            "date-before-range" => Page(false, null, Day("2026-09-30", 0, 0, 0)),
            "date-after-range" => Page(false, null, Day("2026-10-03", 0, 0, 0)),
            "date-malformed" => Page(false, null, Day("01/10/2026", 0, 0, 0)),
            "duplicate-date" => Page(false, null, Day("2026-10-01", 0, 0, 0), Day("2026-10-01", 0, 0, 0)),
            "negative-fee" => Page(false, null, Day("2026-10-01", -1, 0, 0)),
            "string-fee" =>
                """{"data":[{"date":"2026-10-01","total_fee":"5","recharge_fee":5,"voucher_fee":0,"model_details":[]}],"has_more":false,"next":null}""",
            "blank-model" => Page(
                false,
                null,
                Day("2026-10-01", 200_000, 150_000, 50_000, Detail(" ", "prompt", 10, 200_000, 150_000, 50_000))
            ),
            "missing-token-type" => Page(
                false,
                null,
                Day("2026-10-01", 200_000, 150_000, 50_000, DetailJson("kimi-k3", null, 10, 200_000, 150_000, 50_000))
            ),
            "negative-tokens" => Page(
                false,
                null,
                Day("2026-10-01", 200_000, 150_000, 50_000, Detail("kimi-k3", "prompt", -1, 200_000, 150_000, 50_000))
            ),
            "missing-has-more" => """{"data":[],"next":null}""",
            "missing-next" => """{"data":[],"has_more":false}""",
            "final-page-cursor" => Page(false, "unexpected"),
            "blank-cursor" => Page(true, " ", day),
            "not-an-object" => "[]",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    private static string Page(bool hasMore, string? next, params string[] days) =>
        $$"""{"data":[{{string.Join(",", days)}}],"has_more":{{(hasMore ? "true" : "false")}},"next":{{(next is null ? "null" : JsonSerializer.Serialize(next))}}}""";

    private static string Day(string date, long total, long cash, long voucher, params string[] details) =>
        $$"""{"date":{{JsonSerializer.Serialize(date)}},"total_fee":{{total}},"recharge_fee":{{cash}},"voucher_fee":{{voucher}},"model_details":[{{string.Join(",", details)}}]}""";

    private static string Detail(string model, string tokenType, long tokens, long total, long cash, long voucher) =>
        DetailJson(model, tokenType, tokens, total, cash, voucher);

    private static string DetailJson(
        string model,
        string? tokenType,
        long tokens,
        long total,
        long cash,
        long voucher,
        string key = "key-1"
    ) =>
        $$"""{"model_id":{{JsonSerializer.Serialize(model)}}{{(tokenType is null ? string.Empty : $",\"token_type\":{JsonSerializer.Serialize(tokenType)}")}},"tokens":{{tokens}},"key":{{JsonSerializer.Serialize(key)}},"total_fee":{{total}},"recharge_fee":{{cash}},"voucher_fee":{{voucher}}}""";

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message = "failed") =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        error = new
                        {
                            type = "invalid_request_error",
                            code,
                            message,
                            request_id = "r1",
                        },
                    }
                ),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private MoonshotAdminClient CreateSut(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.moonshot.ai") };
        _clients.Add(http);
        return new MoonshotAdminClient(http);
    }

    private sealed class QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _next;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(responses[_next++](request));
        }
    }
}
