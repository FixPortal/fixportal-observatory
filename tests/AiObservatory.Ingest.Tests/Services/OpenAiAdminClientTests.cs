using System.Net;
using System.Text;
using System.Text.Json;
using AiObservatory.Ingest.Services.OpenAi;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public sealed class OpenAiAdminClientTests : IDisposable
{
    private static readonly LocalDate From = new(2026, 8, 1);
    private static readonly LocalDate Through = new(2026, 8, 2);
    private readonly List<HttpClient> _clients = [];

    public void Dispose() => _clients.ForEach(client => client.Dispose());

    [Fact]
    public async Task GetUsageAsync_FetchesEveryPageWithAllPriceBearingGroups()
    {
        var handler = new QueueHandler(
            _ => Ok(UsagePage(From, "gpt-5.4", batch: false, "default", true, "cursor-2+/=")),
            _ => Ok(UsagePage(Through, "gpt-5.4", batch: true, "priority", false, null))
        );
        var sut = CreateSut(handler);

        var records = await sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        records.Should().HaveCount(2);
        records[0]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Model = "gpt-5.4",
                    Batch = (bool?)false,
                    ServiceTier = "default",
                    InputUncachedTokens = 500L,
                    InputCachedTokens = 400L,
                    InputCacheWriteTokens = 100L,
                    OutputTokens = 500L,
                    ModelRequests = 5L,
                }
            );
        records[1].Batch.Should().BeTrue();
        records[0].RawJson.Should().Contain("input_uncached_tokens").And.Contain("start_time");
        // Same O(N^2) guard as the Anthropic client: the bucket's "results" array must not
        // be embedded per record.
        records[0].RawJson.Should().NotContain("\"results\"");
        handler.Requests.Should().HaveCount(2);
        handler
            .Requests[0]
            .Should()
            .Contain("group_by%5B%5D=model")
            .And.Contain("group_by%5B%5D=batch")
            .And.Contain("group_by%5B%5D=service_tier");
        handler.Requests[1].Should().Contain("page=cursor-2%2B%2F%3D");
    }

    [Fact]
    public async Task GetUsageAsync_AcceptsTheOfficialShapeWithANullModel()
    {
        var sut = CreateSut(
            new QueueHandler(_ => Ok(UsagePage(From, null, batch: null, serviceTier: null, false, null)))
        );

        var records = await sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        records
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new
                {
                    Model = (string?)null,
                    InputUncachedTokens = 500L,
                    InputCachedTokens = 400L,
                    InputCacheWriteTokens = 100L,
                    OutputTokens = 500L,
                }
            );
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(42)]
    public async Task GetUsageAsync_RejectsAnInvalidNonNullModel(object model)
    {
        var sut = CreateSut(
            new QueueHandler(_ => Ok(UsagePage(From, model, batch: null, serviceTier: null, false, null)))
        );

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetCostsAsync_FetchesEveryPageAndPreservesOfficialMoneyFacts()
    {
        var handler = new QueueHandler(
            _ => Ok(CostPage(From, 12.34m, "usd", "input", "project-a", true, "cost-page-2")),
            _ => Ok(CostPage(Through, 0m, "usd", null, null, false, null))
        );
        var sut = CreateSut(handler);

        var records = await sut.GetCostsAsync(From, Through, TestContext.Current.CancellationToken);

        records.Should().HaveCount(2);
        records[0]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Amount = 12.34m,
                    Currency = "USD",
                    LineItem = "input",
                    ProjectId = "project-a",
                    Quantity = (decimal?)2.5m,
                    QuantityUnit = "tokens",
                }
            );
        records[0].RawJson.Should().Contain("line_item").And.Contain("amount");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Should().Contain("group_by%5B%5D=project_id").And.Contain("group_by%5B%5D=line_item");
        handler.Requests[1].Should().Contain("page=cost-page-2");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "same")]
    [InlineData(false, "unexpected")]
    public async Task GetUsageAsync_RejectsInvalidCursorContracts(bool secondHasMore, string? secondCursor)
    {
        var firstCursor = secondCursor == "same" ? "same" : "first";
        var handler = new QueueHandler(
            _ => Ok(UsagePage(From, "gpt-5.4", false, "default", true, firstCursor)),
            _ => Ok(UsagePage(Through, "gpt-5.4", false, "default", secondHasMore, secondCursor))
        );
        var sut = CreateSut(handler);

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetCostsAsync_RejectsARepeatedCursor()
    {
        var handler = new QueueHandler(
            _ => Ok(CostPage(From, 1m, "usd", "input", null, true, "same")),
            _ => Ok(CostPage(Through, 2m, "usd", "output", null, true, "same"))
        );
        var sut = CreateSut(handler);

        var act = () => sut.GetCostsAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetUsageAsync_RejectsMoreThanTenThousandPagesWithoutReturningAPrefix()
    {
        var handler = new AdvancingHandler();
        var sut = CreateSut(handler);

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        handler.RequestCount.Should().Be(10_000);
    }

    [Fact]
    public async Task GetUsageAsync_RejectsMalformedMiddlePageWithoutReturningAPrefix()
    {
        var handler = new QueueHandler(
            _ => Ok(UsagePage(From, "gpt-5.4", false, "default", true, "next")),
            _ => Ok("""{"object":"page","has_more":false,"next_page":null}""")
        );
        var sut = CreateSut(handler);

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetCostsAsync_PropagatesFailedMiddlePage()
    {
        var handler = new QueueHandler(
            _ => Ok(CostPage(From, 1m, "usd", "input", null, true, "next")),
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        );
        var sut = CreateSut(handler);

        var act = () => sut.GetCostsAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetUsageAsync_RejectsAnOversizedResponse()
    {
        var oversized = "{\"padding\":\"" + new string('x', 2 * 1024 * 1024) + "\"}";
        var sut = CreateSut(new QueueHandler(_ => Ok(oversized)));

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(-1, 400, 100, 500)]
    [InlineData(500, -1, 100, 500)]
    [InlineData(500, 400, -1, 500)]
    [InlineData(500, 400, 100, -1)]
    public async Task GetUsageAsync_RejectsNegativeTokenFacts(long uncached, long cached, long cacheWrite, long output)
    {
        var json = UsagePage(From, "gpt-5.4", false, "default", false, null, uncached, cached, cacheWrite, output);
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(-0.01, "usd")]
    [InlineData(1.00, "eur")]
    public async Task GetCostsAsync_RejectsInvalidMoneyFacts(double amount, string currency)
    {
        var sut = CreateSut(
            new QueueHandler(_ => Ok(CostPage(From, (decimal)amount, currency, null, null, false, null)))
        );

        var act = () => sut.GetCostsAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetUsageAsync_RejectsABucketOutsideTheRequestedRange()
    {
        var sut = CreateSut(
            new QueueHandler(_ => Ok(UsagePage(From.PlusDays(-1), "gpt-5.4", false, "default", false, null)))
        );

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetUsageAsync_PropagatesCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var handler = new QueueHandler(request =>
            throw new OperationCanceledException("cancelled", null, request.CancellationToken)
        );
        var sut = CreateSut(handler);

        var act = () => sut.GetUsageAsync(From, Through, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetUsageAsync_AcceptsTheDocumentedShapeWithOnlyTheCachedLaneAndDerivesUncached()
    {
        // The documented usage result carries input_tokens and input_cached_tokens but neither
        // input_uncached_tokens nor input_cache_write_tokens; uncached is derived from the total.
        var start = From.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var json = $$"""
            {
              "object": "page",
              "data": [
                {
                  "object": "bucket",
                  "start_time": {{start}},
                  "end_time": {{start + 86_400}},
                  "results": [
                    {
                      "object": "organization.usage.completions.result",
                      "input_tokens": 900,
                      "input_cached_tokens": 400,
                      "output_tokens": 500,
                      "num_model_requests": 5,
                      "model": "gpt-5.4",
                      "batch": false,
                      "service_tier": "default"
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var records = await sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        records
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new
                {
                    InputUncachedTokens = 500L,
                    InputCachedTokens = 400L,
                    InputCacheWriteTokens = 0L,
                    OutputTokens = 500L,
                    ModelRequests = 5L,
                }
            );
    }

    [Fact]
    public async Task GetUsageAsync_RejectsDerivingANegativeUncachedLane()
    {
        var start = From.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var json = $$"""
            {
              "object": "page",
              "data": [
                {
                  "object": "bucket",
                  "start_time": {{start}},
                  "end_time": {{start + 86_400}},
                  "results": [
                    {
                      "object": "organization.usage.completions.result",
                      "input_tokens": 100,
                      "input_cached_tokens": 400,
                      "output_tokens": 500,
                      "num_model_requests": 5,
                      "model": "gpt-5.4"
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("""{"value": "12.34", "currency": "usd"}""", "2.5")]
    [InlineData("""{"value": 12.34, "currency": "usd"}""", "\"7\"")]
    public async Task GetCostsAsync_RejectsAStringTypedMoneyFactAsInvalidDataNotInvalidOperation(
        string amountJson,
        string quantityJson
    )
    {
        // JsonElement.TryGetDecimal throws InvalidOperationException for a non-number kind, so
        // string-typed money facts must be rejected on ValueKind first to keep the documented
        // InvalidDataException contract — first case RequireDecimal, second OptionalDecimal.
        var start = From.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var json = $$"""
            {
              "object": "page",
              "data": [
                {
                  "object": "bucket",
                  "start_time": {{start}},
                  "end_time": {{start + 86_400}},
                  "results": [
                    {
                      "object": "organization.costs.result",
                      "amount": {{amountJson}},
                      "line_item": null,
                      "project_id": null,
                      "api_key_id": null,
                      "quantity": {{quantityJson}},
                      "quantity_unit": "tokens"
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var act = () => sut.GetCostsAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetUsageAsync_RejectsAStringTypedOptionalLaneAsInvalidDataNotInvalidOperation()
    {
        // JsonElement.TryGetInt64 throws InvalidOperationException for a non-number kind, so a
        // string-typed lane must be rejected on ValueKind first to keep the documented
        // InvalidDataException contract.
        var start = From.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var json = $$"""
            {
              "object": "page",
              "data": [
                {
                  "object": "bucket",
                  "start_time": {{start}},
                  "end_time": {{start + 86_400}},
                  "results": [
                    {
                      "object": "organization.usage.completions.result",
                      "input_tokens": 900,
                      "input_cached_tokens": "400",
                      "output_tokens": 500,
                      "num_model_requests": 5,
                      "model": "gpt-5.4"
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*input_cached_tokens*");
    }

    [Fact]
    public async Task GetUsageAsync_RejectsAStringTypedBucketTimestampAsInvalidDataNotInvalidOperation()
    {
        var start = From.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var json = $$"""
            {
              "object": "page",
              "data": [
                {
                  "object": "bucket",
                  "start_time": "{{start}}",
                  "end_time": {{start + 86_400}},
                  "results": []
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*start_time*");
    }

    [Fact]
    public async Task GetUsageAsync_StillRejectsAPresentLaneSplitThatDoesNotSumToTheTotal()
    {
        // All three lanes present but inconsistent with input_tokens — the invariant still bites.
        var start = From.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var json = $$"""
            {
              "object": "page",
              "data": [
                {
                  "object": "bucket",
                  "start_time": {{start}},
                  "end_time": {{start + 86_400}},
                  "results": [
                    {
                      "object": "organization.usage.completions.result",
                      "input_tokens": 1000,
                      "input_uncached_tokens": 501,
                      "input_cached_tokens": 400,
                      "input_cache_write_tokens": 100,
                      "output_tokens": 500,
                      "num_model_requests": 5,
                      "model": "gpt-5.4"
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetUsageAsync_RejectsAPartialLaneSplitThatDoesNotSumToTheTotal()
    {
        // input_cache_write_tokens omitted: with the uncached lane declared, the absent lane
        // counts as zero, so 601 + 400 != 1000 is as inconsistent as a full bad split.
        var start = From.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var json = $$"""
            {
              "object": "page",
              "data": [
                {
                  "object": "bucket",
                  "start_time": {{start}},
                  "end_time": {{start + 86_400}},
                  "results": [
                    {
                      "object": "organization.usage.completions.result",
                      "input_tokens": 1000,
                      "input_uncached_tokens": 601,
                      "input_cached_tokens": 400,
                      "output_tokens": 500,
                      "num_model_requests": 5,
                      "model": "gpt-5.4"
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var act = () => sut.GetUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    private OpenAiAdminClient CreateSut(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com") };
        _clients.Add(client);
        return new OpenAiAdminClient(client);
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string UsagePage(
        LocalDate day,
        object? model,
        bool? batch,
        string? serviceTier,
        bool hasMore,
        string? nextPage,
        long uncached = 500,
        long cached = 400,
        long cacheWrite = 100,
        long output = 500
    )
    {
        var start = day.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        return JsonSerializer.Serialize(
            new
            {
                @object = "page",
                data = new[]
                {
                    new
                    {
                        @object = "bucket",
                        start_time = start,
                        end_time = start + 86_400,
                        results = new[]
                        {
                            new
                            {
                                @object = "organization.usage.completions.result",
                                input_tokens = uncached + cached + cacheWrite,
                                input_cached_tokens = cached,
                                input_cache_write_tokens = cacheWrite,
                                input_uncached_tokens = uncached,
                                output_tokens = output,
                                input_text_tokens = uncached + cached + cacheWrite,
                                output_text_tokens = output,
                                input_cached_text_tokens = cached,
                                input_audio_tokens = 0,
                                input_cached_audio_tokens = 0,
                                output_audio_tokens = 0,
                                input_image_tokens = 0,
                                input_cached_image_tokens = 0,
                                output_image_tokens = 0,
                                num_model_requests = 5,
                                project_id = (string?)null,
                                user_id = (string?)null,
                                api_key_id = (string?)null,
                                model,
                                batch,
                                service_tier = serviceTier,
                            },
                        },
                    },
                },
                has_more = hasMore,
                next_page = nextPage,
            }
        );
    }

    private static string CostPage(
        LocalDate day,
        decimal amount,
        string currency,
        string? lineItem,
        string? projectId,
        bool hasMore,
        string? nextPage
    )
    {
        var start = day.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        return JsonSerializer.Serialize(
            new
            {
                @object = "page",
                data = new[]
                {
                    new
                    {
                        @object = "bucket",
                        start_time = start,
                        end_time = start + 86_400,
                        results = new[]
                        {
                            new
                            {
                                @object = "organization.costs.result",
                                amount = new { value = amount, currency },
                                line_item = lineItem,
                                project_id = projectId,
                                api_key_id = (string?)null,
                                quantity = 2.5m,
                                quantity_unit = "tokens",
                            },
                        },
                    },
                },
                has_more = hasMore,
                next_page = nextPage,
            }
        );
    }

    private sealed class QueueHandler(params Func<Request, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var response = responses[_index++](new Request(request, cancellationToken));
            return Task.FromResult(response);
        }
    }

    private sealed record Request(HttpRequestMessage Message, CancellationToken CancellationToken);

    private sealed class AdvancingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            return Task.FromResult(Ok(UsagePage(From, "gpt-5.4", false, "default", true, $"cursor-{RequestCount}")));
        }
    }
}
