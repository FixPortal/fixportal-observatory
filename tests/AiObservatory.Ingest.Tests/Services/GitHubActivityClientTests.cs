using System.Net;
using AiObservatory.Ingest.Services.GitHub;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public sealed class GitHubActivityClientTests : IDisposable
{
    private readonly List<HttpClient> _httpClients = [];

    public void Dispose() => _httpClients.ForEach(client => client.Dispose());

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse(string json, int rateLimitRemaining = 4999)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-RateLimit-Remaining", rateLimitRemaining.ToString());
        return response;
    }

    private GitHubActivityClient CreateSut(StubHandler handler, ILogger<GitHubActivityClient>? logger = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") };
        _httpClients.Add(http);
        return new GitHubActivityClient(http, logger ?? NullLogger<GitHubActivityClient>.Instance);
    }

    // Captures Warning-level log entries so pagination-cap tests can assert the
    // client actually logged, without wrestling ILogger's generic Log<TState> overload
    // through a mocking library.
    private sealed class CapturingLogger : ILogger<GitHubActivityClient>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task GetPullRequestsAsync_ParsesFieldsAndFetchesReviewCount()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                return JsonResponse(
                    """[{"submitted_at":"2026-07-01T12:00:00Z"},{"submitted_at":"2026-07-01T14:00:00Z"}]"""
                );
            }
            return JsonResponse(
                """
                [{"number":42,"title":"Add feature","user":{"login":"chris"},"state":"open",
                  "created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T10:00:00Z","merged_at":null,"closed_at":null}]
                """
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        var pr = result.Single();
        pr.Number.Should().Be(42);
        pr.Author.Should().Be("chris");
        pr.State.Should().Be("open");
        pr.UpdatedAt.Should().Be(Instant.FromUtc(2026, 7, 1, 10, 0));
        pr.ReviewCount.Should().Be(2);
        pr.FirstReviewAt.Should().Be(Instant.FromUtc(2026, 7, 1, 12, 0));
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenNoReviews_ReviewCountZeroAndFirstReviewAtNull()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                return JsonResponse("[]");
            }
            return JsonResponse(
                """
                [{"number":1,"title":"WIP","user":{"login":"chris"},"state":"open",
                  "created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}]
                """
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Single().ReviewCount.Should().Be(0);
        result.Single().FirstReviewAt.Should().BeNull();
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenReviewIsPending_ExcludesItFromFirstReviewAtButCountsIt()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                // Pending reviews omit submitted_at entirely.
                return JsonResponse("""[{"submitted_at":null},{"submitted_at":"2026-07-01T14:00:00Z"}]""");
            }
            return JsonResponse(
                """
                [{"number":42,"title":"Add feature","user":{"login":"chris"},"state":"open",
                  "created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}]
                """
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        var pr = result.Single();
        pr.ReviewCount.Should().Be(2);
        pr.FirstReviewAt.Should().Be(Instant.FromUtc(2026, 7, 1, 14, 0));
    }

    [Fact]
    public async Task GetPullRequestsAsync_CapturesReviewerIdentityStateAndBotFlag()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                return JsonResponse(
                    """
                    [{"id":1001,"user":{"login":"coderabbitai[bot]","type":"Bot"},"state":"CHANGES_REQUESTED","submitted_at":"2026-07-01T09:05:00Z"},
                     {"id":1002,"user":{"login":"chris","type":"User"},"state":"APPROVED","submitted_at":"2026-07-01T15:00:00Z"}]
                    """
                );
            }
            return JsonResponse(
                """
                [{"number":42,"title":"Add feature","user":{"login":"chris"},"state":"open",
                  "created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T16:00:00Z","merged_at":null,"closed_at":null}]
                """
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        var reviews = result.Single().Reviews.Should().NotBeNull().And.HaveCount(2).And.Subject.ToList();
        var agent = reviews[0];
        agent.ReviewId.Should().Be(1001);
        agent.Repo.Should().Be("fix-portal/example");
        agent.Number.Should().Be(42);
        agent.Reviewer.Should().Be("coderabbitai[bot]");
        agent.IsBot.Should().BeTrue();
        agent.State.Should().Be("CHANGES_REQUESTED");
        agent.SubmittedAt.Should().Be(Instant.FromUtc(2026, 7, 1, 9, 5));
        reviews[1].Reviewer.Should().Be("chris");
        reviews[1].IsBot.Should().BeFalse();
    }

    // "[bot]" is a display convention, not an identity: only GitHub's own user.type says an
    // account is an app. A human whose login ends in "[bot]" must not be counted as an agent.
    [Fact]
    public async Task GetPullRequestsAsync_WhenLoginLooksLikeABotButTypeIsUser_IsNotFlaggedAsBot()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.ToString().Contains("/reviews")
                ? JsonResponse(
                    """[{"id":5,"user":{"login":"not-a-bot[bot]","type":"User"},"state":"COMMENTED","submitted_at":"2026-07-01T10:00:00Z"}]"""
                )
                : JsonResponse(
                    """
                    [{"number":7,"title":"t","user":{"login":"chris"},"state":"open",
                      "created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T10:00:00Z","merged_at":null,"closed_at":null}]
                    """
                )
        );
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Single().Reviews!.Single().IsBot.Should().BeFalse();
    }

    // A deleted account leaves its reviews behind with a null user. Dropping the row would
    // make Reviews.Count disagree with the ReviewCount stored on the PR itself.
    [Fact]
    public async Task GetPullRequestsAsync_WhenReviewerAccountIsDeleted_KeepsReviewAsGhost()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.ToString().Contains("/reviews")
                ? JsonResponse("""[{"id":9,"user":null,"state":"COMMENTED","submitted_at":"2026-07-01T10:00:00Z"}]""")
                : JsonResponse(
                    """
                    [{"number":8,"title":"t","user":{"login":"chris"},"state":"open",
                      "created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T10:00:00Z","merged_at":null,"closed_at":null}]
                    """
                )
        );
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        var pr = result.Single();
        pr.ReviewCount.Should().Be(1);
        pr.Reviews!.Single().Reviewer.Should().Be("ghost");
    }

    [Fact]
    public async Task GetPullRequestsAsync_PaginatesUntilShortPage()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                return JsonResponse("[]");
            }

            // Page 1: a full 100-row page (forces a second page request); page 2: short page, stop.
            if (req.RequestUri.ToString().Contains("page=2"))
            {
                return JsonResponse(
                    """[{"number":2,"title":"b","user":{"login":"chris"},"state":"open","created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}]"""
                );
            }
            var page = string.Join(
                ",",
                Enumerable
                    .Range(1, 100)
                    .Select(i =>
                        $$"""{"number":{{i}},"title":"t","user":{"login":"chris"},"state":"open","created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}"""
                    )
            );
            return JsonResponse($"[{page}]");
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Should().HaveCount(101); // 100 from page 1 + 1 from page 2
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenOldPrWasRecentlyUpdated_IsStillIncludedAndDoesNotEarlyBreak()
    {
        // Sorted by updated/desc: an old PR (created months ago) that was updated within
        // `since` must still be captured, and pagination must key off updated_at — not
        // created_at — so a stale-but-recently-updated PR further down the page doesn't
        // trigger an early break before the next (older-updated) page is even requested.
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                return JsonResponse("[]");
            }
            if (req.RequestUri.ToString().Contains("page=2"))
            {
                return JsonResponse("""[]""");
            }
            return JsonResponse(
                """
                [{"number":99,"title":"Old PR, recently merged","user":{"login":"chris"},"state":"closed",
                  "created_at":"2026-05-01T09:00:00Z","updated_at":"2026-07-01T10:00:00Z","merged_at":"2026-07-01T10:00:00Z","closed_at":"2026-07-01T10:00:00Z"}]
                """
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        var pr = result.Single();
        pr.Number.Should().Be(99);
        pr.State.Should().Be("merged");
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenPrWasMerged_ReturnsStateMerged()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                return JsonResponse("[]");
            }
            return JsonResponse(
                """
                [{"number":42,"title":"Add feature","user":{"login":"chris"},"state":"closed",
                  "created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T10:00:00Z","merged_at":"2026-07-01T10:00:00Z","closed_at":"2026-07-01T10:00:00Z"}]
                """
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Single().State.Should().Be("merged");
    }

    [Fact]
    public async Task GetPullRequestsAsync_TruncatesExternalStringsToDatabaseLimits()
    {
        var longTitle = new string('t', 600);
        var longAuthor = new string('a', 250);
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                return JsonResponse("[]");
            }
            return JsonResponse(
                $$"""
                [{"number":42,"title":"{{longTitle}}","user":{"login":"{{longAuthor}}"},"state":"open",
                  "created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T10:00:00Z","merged_at":null,"closed_at":null}]
                """
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        var pr = result.Single();
        pr.Title.Should().HaveLength(500);
        pr.Author.Should().HaveLength(200);
    }

    [Fact]
    public async Task GetPullRequestsAsync_PaginatesReviewsUntilShortPage()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/reviews") && url.Contains("page=2"))
            {
                return JsonResponse("""[{"submitted_at":"2026-07-01T14:00:00Z"}]""");
            }
            if (url.Contains("/reviews"))
            {
                var page = string.Join(
                    ",",
                    Enumerable.Range(1, 100).Select(_ => """{"submitted_at":"2026-07-01T12:00:00Z"}""")
                );
                return JsonResponse($"[{page}]");
            }
            return JsonResponse(
                """
                [{"number":42,"title":"Add feature","user":{"login":"chris"},"state":"open",
                  "created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T10:00:00Z","merged_at":null,"closed_at":null}]
                """
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Single().ReviewCount.Should().Be(101);
        handler.RequestedUrls.Should().Contain(u => u.Contains("/pulls/42/reviews?per_page=100&page=1"));
        handler.RequestedUrls.Should().Contain(u => u.Contains("/pulls/42/reviews?per_page=100&page=2"));
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenPrClosedWithoutMerge_ReturnsStateClosed()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                return JsonResponse("[]");
            }
            return JsonResponse(
                """
                [{"number":43,"title":"Abandoned","user":{"login":"chris"},"state":"closed",
                  "created_at":"2026-07-01T09:00:00Z","updated_at":"2026-07-01T10:00:00Z","merged_at":null,"closed_at":"2026-07-01T10:00:00Z"}]
                """
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Single().State.Should().Be("closed");
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenRateLimitNearZero_ThrowsRateLimitException()
    {
        var handler = new StubHandler(_ => JsonResponse("[]", rateLimitRemaining: 10));
        var sut = CreateSut(handler);

        var act = () =>
            sut.GetPullRequestsAsync(
                "fix-portal/example",
                new LocalDate(2026, 7, 1),
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<GitHubRateLimitExceededException>();
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenRepoForbidden_ThrowsHttpRequestException()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var sut = CreateSut(handler);

        var act = () =>
            sut.GetPullRequestsAsync(
                "fix-portal/private-no-access",
                new LocalDate(2026, 7, 1),
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetCommitsAsync_FetchesShaAuthorDateAndChurnStats()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/commits/abc123"))
            {
                return JsonResponse("""{"sha":"abc123","stats":{"additions":10,"deletions":2}}""");
            }
            return JsonResponse(
                """
                [{"sha":"abc123","commit":{"author":{"name":"chris","date":"2026-07-01T09:00:00Z"}}}]
                """
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetCommitsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        var commit = result.Single();
        commit.Sha.Should().Be("abc123");
        commit.Author.Should().Be("chris");
        commit.Additions.Should().Be(10);
        commit.Deletions.Should().Be(2);
    }

    [Fact]
    public async Task GetCommitsAsync_TruncatesExternalStringsToDatabaseLimits()
    {
        var longAuthor = new string('a', 250);
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/commits/abc123"))
            {
                return JsonResponse("""{"sha":"abc123","stats":{"additions":10,"deletions":2}}""");
            }
            return JsonResponse(
                "[{\"sha\":\"abc123\",\"commit\":{\"author\":{\"name\":\""
                    + longAuthor
                    + "\",\"date\":\"2026-07-01T09:00:00Z\"}}}]"
            );
        });
        var sut = CreateSut(handler);

        var result = await sut.GetCommitsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Single().Author.Should().HaveLength(200);
    }

    [Fact]
    public async Task GetCommitsAsync_PassesSinceAsQueryParam()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/commits/"))
            {
                return JsonResponse("""{"sha":"x","stats":{"additions":0,"deletions":0}}""");
            }
            return JsonResponse("[]");
        });
        var sut = CreateSut(handler);

        await sut.GetCommitsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        // HttpClient does not URL-encode colons in the request URI passed to GetAsync.
        handler.RequestedUrls.Should().Contain(u => u.Contains("since=2026-07-01T00:00:00Z"));
    }

    [Fact]
    public async Task GetWorkflowRunsAsync_UsesConclusionWhenCompleted()
    {
        var handler = new StubHandler(_ =>
            JsonResponse(
                """
                {"workflow_runs":[{"id":123,"name":"CI","status":"completed","conclusion":"success","created_at":"2026-07-01T09:00:00Z"}]}
                """
            )
        );
        var sut = CreateSut(handler);

        var result = await sut.GetWorkflowRunsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        var run = result.Runs.Single();
        result.Truncated.Should().BeFalse();
        run.RunId.Should().Be(123);
        run.WorkflowName.Should().Be("CI");
        run.Status.Should().Be("success");
    }

    [Fact]
    public async Task GetWorkflowRunsAsync_UsesStatusWhenNotYetCompleted()
    {
        var handler = new StubHandler(_ =>
            JsonResponse(
                """
                {"workflow_runs":[{"id":124,"name":"CI","status":"in_progress","conclusion":null,"created_at":"2026-07-01T09:00:00Z"}]}
                """
            )
        );
        var sut = CreateSut(handler);

        var result = await sut.GetWorkflowRunsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Runs.Single().Status.Should().Be("in_progress");
    }

    [Fact]
    public async Task GetWorkflowRunsAsync_WhenNameIsNull_UsesPlaceholder()
    {
        var handler = new StubHandler(_ =>
            JsonResponse(
                """
                {"workflow_runs":[{"id":124,"name":null,"status":"in_progress","conclusion":null,"created_at":"2026-07-01T09:00:00Z"}]}
                """
            )
        );
        var sut = CreateSut(handler);

        var result = await sut.GetWorkflowRunsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Runs.Single().WorkflowName.Should().Be("(unnamed)");
    }

    [Fact]
    public async Task GetWorkflowRunsAsync_TruncatesExternalStringsToDatabaseLimits()
    {
        var longName = new string('w', 250);
        var handler = new StubHandler(_ =>
            JsonResponse(
                $$"""
                {"workflow_runs":[{"id":124,"name":"{{longName}}","status":"in_progress","conclusion":null,"created_at":"2026-07-01T09:00:00Z"}]}
                """
            )
        );
        var sut = CreateSut(handler);

        var result = await sut.GetWorkflowRunsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Runs.Single().WorkflowName.Should().HaveLength(200);
    }

    [Fact]
    public async Task GetWorkflowRunsAsync_WhenPaginationCapReached_StopsPagingAndLogsWarning()
    {
        // WorkflowRunsPaginationCap is 1000 and PerPage is 100: every page returned here is
        // a full 100-row page, so the client never sees a short page to stop on naturally —
        // only the `page * PerPage >= WorkflowRunsPaginationCap` check (hit at page 10) does.
        var fullPage = string.Join(
            ",",
            Enumerable
                .Range(1, 100)
                .Select(i =>
                    $$"""{"id":{{i}},"name":"CI","status":"completed","conclusion":"success","created_at":"2026-07-01T09:00:00Z"}"""
                )
        );
        var handler = new StubHandler(_ => JsonResponse($$"""{"workflow_runs":[{{fullPage}}]}"""));
        var logger = new CapturingLogger();
        var sut = CreateSut(handler, logger);

        var result = await sut.GetWorkflowRunsAsync(
            "fix-portal/example",
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        result.Runs.Should().HaveCount(1000); // 10 pages * 100, then the cap stops it
        result.Truncated.Should().BeTrue();
        // Since every stub response is a full page, a client that refetched page 1 forever
        // would also return 1000 rows - so pin that pagination actually advanced 1..10 and
        // stopped at the cap rather than requesting an 11th page.
        foreach (var page in Enumerable.Range(1, 10))
        {
            handler.RequestedUrls.Should().Contain(u => u.Contains($"page={page}", StringComparison.Ordinal));
        }
        handler.RequestedUrls.Should().NotContain(u => u.Contains("page=11", StringComparison.Ordinal));
        logger.Warnings.Should().ContainSingle(w => w.Contains("result cap", StringComparison.OrdinalIgnoreCase));
    }
}
