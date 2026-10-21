using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiObservatory.Ingest.Services.Anthropic;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public sealed class AnthropicAdminClientTests : IDisposable
{
    private static readonly LocalDate From = new(2026, 8, 1);
    private static readonly LocalDate Through = new(2026, 8, 2);
    private readonly List<HttpClient> _clients = [];

    public void Dispose() => _clients.ForEach(client => client.Dispose());

    [Fact]
    public async Task GetMessageUsageAsync_FetchesEveryPageAndPreservesPricingDimensions()
    {
        var handler = new QueueHandler(
            _ => Ok(MessagesPage(From, "claude-sonnet-5", true, "next+/=")),
            _ => Ok(MessagesPage(Through, null, false, null))
        );
        var sut = CreateSut(handler);

        var records = await sut.GetMessageUsageAsync(From, Through, TestContext.Current.CancellationToken);

        records.Should().HaveCount(2);
        records[0]
            .Should()
            .BeEquivalentTo(
                new
                {
                    BucketStart = Instant.FromUtc(2026, 8, 1, 0, 0),
                    BucketEnd = Instant.FromUtc(2026, 8, 2, 0, 0),
                    Model = "claude-sonnet-5",
                    ServiceTier = "batch",
                    InferenceGeo = "us",
                    Speed = "standard",
                    InputTokens = 1500L,
                    OutputTokens = 500L,
                    CacheReadTokens = 200L,
                    CacheWrite5mTokens = 500L,
                    CacheWrite1hTokens = 1000L,
                }
            );
        records[1].Model.Should().BeNull();
        records[0].RawJson.Should().Contain("ephemeral_1h_input_tokens").And.Contain("starting_at");
        // The per-record evidence keeps the bucket's window bounds but not its "results"
        // array — embedding it made payloads grow O(N^2) with the bucket's row count.
        records[0].RawJson.Should().NotContain("\"results\"");
        handler.Requests.Should().HaveCount(2);
        handler
            .Requests[0]
            .Should()
            .Contain("/v1/organizations/usage_report/messages")
            .And.Contain("group_by%5B%5D=model")
            .And.Contain("group_by%5B%5D=service_tier")
            .And.Contain("group_by%5B%5D=inference_geo")
            .And.Contain("group_by%5B%5D=speed");
        handler.Requests[1].Should().Contain("page=next%2B%2F%3D");
    }

    [Theory]
    [InlineData("model")]
    [InlineData("service_tier")]
    [InlineData("inference_geo")]
    [InlineData("speed")]
    public async Task GetMessageUsageAsync_RequiresNullableGroupedFieldsToRemainPresent(string property)
    {
        var json = RemoveResultProperty(MessagesPage(From, null, false, null), property);
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var act = () => sut.GetMessageUsageAsync(From, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetCostsAsync_FetchesEveryPageAndPreservesPlatformCostFacts()
    {
        var handler = new QueueHandler(
            _ => Ok(CostPage(From, "123.78912", "Code Execution Usage", true, "cost-2")),
            _ => Ok(CostPage(Through, "0", "Web Search Usage", false, null))
        );
        var sut = CreateSut(handler);

        var records = await sut.GetCostsAsync(From, Through, TestContext.Current.CancellationToken);

        records.Should().HaveCount(2);
        records[0]
            .Should()
            .BeEquivalentTo(
                new
                {
                    AmountFractionalCents = 123.78912m,
                    Currency = "USD",
                    WorkspaceId = "wrkspc_test",
                    Description = "Code Execution Usage",
                    CostType = "code_execution",
                    Model = "claude-sonnet-5",
                    ContextWindow = "0-200k",
                    InferenceGeo = "global",
                    ServiceTier = "standard",
                    TokenType = "uncached_input_tokens",
                }
            );
        records[0].RawJson.Should().Contain("workspace_id").And.Contain("amount");
        handler.Requests[0].Should().Contain("group_by%5B%5D=workspace_id").And.Contain("group_by%5B%5D=description");
        handler.Requests[1].Should().Contain("page=cost-2");
    }

    [Theory]
    [InlineData("workspace_id")]
    [InlineData("description")]
    public async Task GetCostsAsync_RequiresNullableRequestedGroupsToRemainPresent(string property)
    {
        var json = CostPage(From, "1", "Tokens", false, null)
            .Replace($"\"{property}\":\"{(property == "workspace_id" ? "wrkspc_test" : "Tokens")}\",", string.Empty);
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var act = () => sut.GetCostsAsync(From, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetClaudeCodeUsageAsync_FetchesEveryPageForEveryDayAndFlattensModelLanes()
    {
        var handler = new QueueHandler(
            _ => Ok(ClaudeCodePage(From, "user_actor", "dev@example.com", "subscription", true, "day-1-next")),
            _ => Ok(ClaudeCodePage(From, "api_actor", "automation", "api", false, null, estimatedMinor: null)),
            _ => Ok(ClaudeCodePage(Through, "user_actor", "dev@example.com", "subscription", false, null))
        );
        var sut = CreateSut(handler);

        var records = await sut.GetClaudeCodeUsageAsync(From, Through, TestContext.Current.CancellationToken);

        records.Should().HaveCount(3);
        records[0]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Date = From,
                    ActorType = "user_actor",
                    ActorIdentifier = "dev@example.com",
                    OrganizationId = "org-test",
                    CustomerType = "subscription",
                    SubscriptionType = "team",
                    IsRemote = false,
                    TerminalType = "vscode",
                    Model = "claude-sonnet-5",
                    InputTokens = 100L,
                    OutputTokens = 20L,
                    CacheReadTokens = 30L,
                    CacheCreationTokens = 10L,
                    EstimatedCostMinor = (decimal?)123.45m,
                    Currency = "USD",
                }
            );
        records[1].EstimatedCostMinor.Should().BeNull();
        records[1].Currency.Should().BeNull();
        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Should().Contain("starting_at=2026-08-01").And.Contain("limit=1000");
        handler.Requests[1].Should().Contain("page=day-1-next");
        handler.Requests[2].Should().Contain("starting_at=2026-08-02").And.NotContain("page=");
    }

    [Fact]
    public async Task GetClaudeCodeUsageAsync_AcceptsOfficialApiCustomerWithEnterpriseSubscriptionType()
    {
        var sut = CreateSut(
            new QueueHandler(_ =>
                Ok(
                    ClaudeCodePage(
                        From,
                        "user_actor",
                        "user@emaildomain.com",
                        "api",
                        false,
                        null,
                        subscriptionType: "enterprise"
                    )
                )
            )
        );

        var records = await sut.GetClaudeCodeUsageAsync(From, From, TestContext.Current.CancellationToken);

        records
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(new { CustomerType = "api", SubscriptionType = "enterprise" });
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("costs")]
    [InlineData("claude-code")]
    public async Task CompleteReportMethods_RejectMalformedMiddlePagesWithoutReturningPrefixes(string lane)
    {
        var handler = new QueueHandler(
            _ => Ok(FirstPage(lane, true, "next")),
            _ => Ok("""{"data":[],"has_more":false}""")
        );
        var sut = CreateSut(handler);

        var act = () => InvokeAsync(sut, lane, From, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("costs")]
    [InlineData("claude-code")]
    public async Task CompleteReportMethods_PropagateFailedMiddlePages(string lane)
    {
        var handler = new QueueHandler(
            _ => Ok(FirstPage(lane, true, "next")),
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        );
        var sut = CreateSut(handler);

        var act = () => InvokeAsync(sut, lane, From, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("costs")]
    [InlineData("claude-code")]
    public async Task CompleteReportMethods_RejectOversizedMiddlePages(string lane)
    {
        var oversized = "{\"padding\":\"" + new string('x', 2 * 1024 * 1024) + "\"}";
        var handler = new QueueHandler(_ => Ok(FirstPage(lane, true, "next")), _ => Ok(oversized));
        var sut = CreateSut(handler);

        var act = () => InvokeAsync(sut, lane, From, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("messages", true, null)]
    [InlineData("messages", true, "same")]
    [InlineData("costs", false, "unexpected")]
    [InlineData("claude-code", true, " ")]
    public async Task CompleteReportMethods_RejectInvalidCursorContracts(
        string lane,
        bool secondHasMore,
        string? secondCursor
    )
    {
        var firstCursor = secondCursor == "same" ? "same" : "first";
        var handler = new QueueHandler(
            _ => Ok(FirstPage(lane, true, firstCursor)),
            _ => Ok(FirstPage(lane, secondHasMore, secondCursor))
        );
        var sut = CreateSut(handler);

        var act = () => InvokeAsync(sut, lane, From, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task GetMessageUsageAsync_RejectsMoreThanTenThousandPages()
    {
        var handler = new AdvancingHandler();
        var sut = CreateSut(handler);

        var act = () => sut.GetMessageUsageAsync(From, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        handler.RequestCount.Should().Be(10_000);
    }

    [Fact]
    public async Task GetClaudeCodeUsageAsync_AppliesPageLimitAcrossDays()
    {
        var handler = new CrossDayAdvancingHandler();
        var sut = CreateSut(handler);

        var act = () => sut.GetClaudeCodeUsageAsync(From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        handler.RequestCount.Should().Be(10_000);
    }

    [Theory]
    [InlineData(-1, 500, 200, 500, 1000)]
    [InlineData(1500, -1, 200, 500, 1000)]
    [InlineData(1500, 500, -1, 500, 1000)]
    [InlineData(1500, 500, 200, -1, 1000)]
    [InlineData(1500, 500, 200, 500, -1)]
    public async Task GetMessageUsageAsync_RejectsNegativeTokenFacts(
        long input,
        long output,
        long cacheRead,
        long cache5m,
        long cache1h
    )
    {
        var sut = CreateSut(
            new QueueHandler(_ =>
                Ok(MessagesPage(From, "claude-sonnet-5", false, null, input, output, cacheRead, cache5m, cache1h))
            )
        );

        var act = () => sut.GetMessageUsageAsync(From, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("-0.01", "USD")]
    [InlineData("1", "EUR")]
    [InlineData("NaN", "USD")]
    public async Task GetCostsAsync_RejectsInvalidMoneyFacts(string amount, string currency)
    {
        var sut = CreateSut(new QueueHandler(_ => Ok(CostPage(From, amount, "Tokens", false, null, currency))));

        var act = () => sut.GetCostsAsync(From, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("costs")]
    public async Task DailyReports_RejectShiftedTwentyFourHourBuckets(string lane)
    {
        var json = FirstPage(lane, false, null).Replace("T00:00:00Z", "T01:00:00Z", StringComparison.Ordinal);
        var sut = CreateSut(new QueueHandler(_ => Ok(json)));

        var act = () => InvokeAsync(sut, lane, From, Through, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(-1, "USD")]
    [InlineData(100, "EUR")]
    public async Task GetClaudeCodeUsageAsync_RejectsInvalidEstimatedCosts(double amount, string currency)
    {
        var sut = CreateSut(
            new QueueHandler(_ =>
                Ok(
                    ClaudeCodePage(
                        From,
                        "user_actor",
                        "dev@example.com",
                        "subscription",
                        false,
                        null,
                        (decimal)amount,
                        currency
                    )
                )
            )
        );

        var act = () => sut.GetClaudeCodeUsageAsync(From, From, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("costs")]
    [InlineData("claude-code")]
    public async Task CompleteReportMethods_ClassifyOnlyExplicitStructuredFeatureIneligibility(string lane)
    {
        var unavailable = CreateSut(
            new QueueHandler(_ =>
                Error(
                    HttpStatusCode.Forbidden,
                    "permission_error",
                    "Claude Code analytics is not available for this organization."
                )
            )
        );
        var missingScope = CreateSut(
            new QueueHandler(_ => Error(HttpStatusCode.Forbidden, "permission_error", "Missing required scopes."))
        );

        var unavailableAct = () => InvokeAsync(unavailable, lane, From, From, TestContext.Current.CancellationToken);
        var missingScopeAct = () => InvokeAsync(missingScope, lane, From, From, TestContext.Current.CancellationToken);

        await unavailableAct.Should().ThrowAsync<SourceUnavailableException>();
        await missingScopeAct.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task CompleteReportMethods_PropagateCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var sut = CreateSut(
            new QueueHandler(request =>
                throw new OperationCanceledException("cancelled", null, request.CancellationToken)
            )
        );

        var act = () => sut.GetMessageUsageAsync(From, Through, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private AnthropicAdminClient CreateSut(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com") };
        _clients.Add(client);
        return new AnthropicAdminClient(client);
    }

    private static async Task InvokeAsync(
        IAnthropicAdminClient client,
        string lane,
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        switch (lane)
        {
            case "messages":
                await client.GetMessageUsageAsync(from, through, cancellationToken);
                break;
            case "costs":
                await client.GetCostsAsync(from, through, cancellationToken);
                break;
            case "claude-code":
                await client.GetClaudeCodeUsageAsync(from, through, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(lane));
        }
    }

    private static string FirstPage(string lane, bool hasMore, string? nextPage) =>
        lane switch
        {
            "messages" => MessagesPage(From, "claude-sonnet-5", hasMore, nextPage),
            "costs" => CostPage(From, "1", "Tokens", hasMore, nextPage),
            "claude-code" => ClaudeCodePage(From, "user_actor", "dev@example.com", "subscription", hasMore, nextPage),
            _ => throw new ArgumentOutOfRangeException(nameof(lane)),
        };

    private static string RemoveResultProperty(string json, string property)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["data"]![0]!["results"]![0]!.AsObject().Remove(property).Should().BeTrue();
        return root.ToJsonString();
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string type, string message) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        type = "error",
                        error = new { type, message },
                        request_id = "req_test",
                    }
                ),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private static string MessagesPage(
        LocalDate day,
        object? model,
        bool hasMore,
        string? nextPage,
        long input = 1500,
        long output = 500,
        long cacheRead = 200,
        long cache5m = 500,
        long cache1h = 1000
    ) =>
        JsonSerializer.Serialize(
            new
            {
                data = new[]
                {
                    new
                    {
                        starting_at = $"{day:yyyy-MM-dd}T00:00:00Z",
                        ending_at = $"{day.PlusDays(1):yyyy-MM-dd}T00:00:00Z",
                        results = new[]
                        {
                            new
                            {
                                model,
                                service_tier = "batch",
                                inference_geo = "us",
                                speed = "standard",
                                uncached_input_tokens = input,
                                output_tokens = output,
                                cache_read_input_tokens = cacheRead,
                                cache_creation = new
                                {
                                    ephemeral_5m_input_tokens = cache5m,
                                    ephemeral_1h_input_tokens = cache1h,
                                },
                                context_window = "0-200k",
                                server_tool_use = new { web_search_requests = 1 },
                                workspace_id = "wrkspc_test",
                                api_key_id = "apikey_test",
                                account_id = (string?)null,
                                service_account_id = (string?)null,
                            },
                        },
                    },
                },
                has_more = hasMore,
                next_page = nextPage,
            }
        );

    private static string CostPage(
        LocalDate day,
        string amount,
        string description,
        bool hasMore,
        string? nextPage,
        string currency = "USD"
    ) =>
        JsonSerializer.Serialize(
            new
            {
                data = new[]
                {
                    new
                    {
                        starting_at = $"{day:yyyy-MM-dd}T00:00:00Z",
                        ending_at = $"{day.PlusDays(1):yyyy-MM-dd}T00:00:00Z",
                        results = new[]
                        {
                            new
                            {
                                amount,
                                currency,
                                workspace_id = "wrkspc_test",
                                description,
                                cost_type = "code_execution",
                                model = "claude-sonnet-5",
                                context_window = "0-200k",
                                inference_geo = "global",
                                service_tier = "standard",
                                token_type = "uncached_input_tokens",
                            },
                        },
                    },
                },
                has_more = hasMore,
                next_page = nextPage,
            }
        );

    private static string ClaudeCodePage(
        LocalDate day,
        string actorType,
        string actorIdentifier,
        string customerType,
        bool hasMore,
        string? nextPage,
        decimal? estimatedMinor = 123.45m,
        string currency = "USD",
        string? subscriptionType = null
    )
    {
        object actor =
            actorType == "user_actor"
                ? new { type = actorType, email_address = actorIdentifier }
                : new { type = actorType, api_key_name = actorIdentifier };
        object? estimatedCost = estimatedMinor is null ? null : new { amount = estimatedMinor, currency };
        return JsonSerializer.Serialize(
            new
            {
                data = new[]
                {
                    new
                    {
                        date = $"{day:yyyy-MM-dd}T00:00:00Z",
                        actor,
                        organization_id = "org-test",
                        customer_type = customerType,
                        subscription_type = subscriptionType ?? (customerType == "subscription" ? "team" : null),
                        is_remote = false,
                        terminal_type = "vscode",
                        core_metrics = new
                        {
                            num_sessions = 2,
                            lines_of_code = new { added = 10, removed = 3 },
                            commits_by_claude_code = 1,
                            pull_requests_by_claude_code = 0,
                        },
                        tool_actions = new
                        {
                            edit_tool = new { accepted = 2, rejected = 1 },
                            write_tool = new { accepted = 1, rejected = 0 },
                        },
                        model_breakdown = new[]
                        {
                            new
                            {
                                model = "claude-sonnet-5",
                                tokens = new
                                {
                                    input = 100,
                                    output = 20,
                                    cache_read = 30,
                                    cache_creation = 10,
                                },
                                estimated_cost = estimatedCost,
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
            return Task.FromResult(responses[_index++](new Request(request, cancellationToken)));
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
            return Task.FromResult(Ok(MessagesPage(From, "claude-sonnet-5", true, $"cursor-{RequestCount}")));
        }
    }

    private sealed class CrossDayAdvancingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            return Task.FromResult(
                RequestCount == 1
                    ? Ok(ClaudeCodePage(From, "user_actor", "dev@example.com", "subscription", false, null))
                    : Ok(
                        ClaudeCodePage(
                            Through,
                            "user_actor",
                            "dev@example.com",
                            "subscription",
                            true,
                            $"cursor-{RequestCount}"
                        )
                    )
            );
        }
    }
}
