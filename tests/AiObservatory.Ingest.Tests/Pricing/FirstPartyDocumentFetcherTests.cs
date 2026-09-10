using System.Net;
using System.Text;
using AiObservatory.Ingest.Pricing;
using AwesomeAssertions;

namespace AiObservatory.Ingest.Tests.Pricing;

public sealed class FirstPartyDocumentFetcherTests
{
    private static readonly Uri Source = new("https://docs.example.test/pricing.md");

    [Fact]
    public void ConstructorRejectsANonHttpsSource()
    {
        var act = () =>
            new FirstPartyDocumentFetcher(
                new Uri("http://docs.example.test/pricing.md"),
                ["docs.example.test"],
                new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "unused"))
            );

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("https://user@docs.example.test/pricing.md")]
    [InlineData("https://docs.example.test:444/pricing.md")]
    [InlineData("https://docs.example.test/pricing.md?version=2")]
    [InlineData("https://docs.example.test/pricing.md#rates")]
    public void ConstructorRejectsAuthorityOrResourceComponentsOutsideTheFixedBoundary(string source)
    {
        var act = () =>
            new FirstPartyDocumentFetcher(
                new Uri(source),
                ["docs.example.test"],
                new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "unused"))
            );

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task FetchFollowsOnlyValidatedRedirectsAndReturnsStrictUtf8()
    {
        var handler = new RecordingHandler(
            (request, _) =>
                request.RequestUri!.AbsolutePath == "/pricing.md"
                    ? Redirect("/current.md")
                    : Response(HttpStatusCode.OK, "£ per million")
        );
        var fetcher = new FirstPartyDocumentFetcher(Source, ["docs.example.test"], handler);

        var result = await fetcher.FetchAsync(TestContext.Current.CancellationToken);

        result.Content.Should().Be("£ per million");
        handler
            .Requests.Select(uri => uri.AbsoluteUri)
            .Should()
            .Equal("https://docs.example.test/pricing.md", "https://docs.example.test/current.md");
    }

    [Fact]
    public async Task FetchRejectsARedirectHostBeforeSendingCredentialsOrContentToIt()
    {
        var handler = new RecordingHandler((_, _) => Redirect("https://evil.example/pricing.md"));
        var fetcher = new FirstPartyDocumentFetcher(Source, ["docs.example.test"], handler);

        var act = () => fetcher.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("https://user@docs.example.test/current.md")]
    [InlineData("https://docs.example.test:444/current.md")]
    [InlineData("https://docs.example.test/current.md?version=2")]
    [InlineData("https://docs.example.test/current.md#rates")]
    [InlineData("/current.md?version=2")]
    public async Task FetchRejectsRedirectsOutsideTheFixedResourceBoundary(string location)
    {
        var handler = new RecordingHandler((_, _) => Redirect(location));
        var fetcher = new FirstPartyDocumentFetcher(Source, ["docs.example.test"], handler);

        var act = () => fetcher.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task FetchRejectsMoreThanThreeRedirects()
    {
        var handler = new RecordingHandler(
            (request, _) =>
            {
                var segments = request.RequestUri!.Segments;
                var hop = int.TryParse(segments[^1].TrimEnd('/'), out var value) ? value : 0;
                return Redirect($"/{hop + 1}");
            }
        );
        var fetcher = new FirstPartyDocumentFetcher(Source, ["docs.example.test"], handler);

        var act = () => fetcher.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        handler.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task FetchRejectsADeclaredBodyOverTwoMebibytes()
    {
        var response = Response(HttpStatusCode.OK, "small");
        response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
        var fetcher = new FirstPartyDocumentFetcher(
            Source,
            ["docs.example.test"],
            new RecordingHandler((_, _) => response)
        );

        var act = () => fetcher.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task FetchStopsAnUndeclaredBodyAfterTwoMebibytes()
    {
        var content = new StreamContent(new MemoryStream(new byte[2 * 1024 * 1024 + 1]));
        content.Headers.ContentLength = null;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var fetcher = new FirstPartyDocumentFetcher(
            Source,
            ["docs.example.test"],
            new RecordingHandler((_, _) => response)
        );

        var act = () => fetcher.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task FetchAcceptsABodyOfExactlyTwoMebibytes()
    {
        var bytes = Enumerable.Repeat((byte)'a', 2 * 1024 * 1024).ToArray();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        var fetcher = new FirstPartyDocumentFetcher(
            Source,
            ["docs.example.test"],
            new RecordingHandler((_, _) => response)
        );

        var result = await fetcher.FetchAsync(TestContext.Current.CancellationToken);

        result.Content.Should().HaveLength(2 * 1024 * 1024);
    }

    [Fact]
    public async Task FetchRejectsInvalidUtf8()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xC3, 0x28]) };
        var fetcher = new FirstPartyDocumentFetcher(
            Source,
            ["docs.example.test"],
            new RecordingHandler((_, _) => response)
        );

        var act = () => fetcher.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DecoderFallbackException>();
    }

    [Fact]
    public async Task FetchAppliesTheLinkedTimeoutToABlockedHandler()
    {
        // Deterministic: the manual timer fires the linked timeout only after the handler is
        // genuinely blocked on it — no short real delay whose scheduling could outrace the
        // assertion on a loaded machine. The 30s request timeout never elapses in real time,
        // so an unwired seam hangs the fetch until the WaitAsync bound fails the test.
        var timeProvider = new ManualTimeProvider();
        var handlerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new FirstPartyDocumentFetcher(
            Source,
            ["docs.example.test"],
            new RecordingHandler(
                async (_, token) =>
                {
                    handlerBlocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Response(HttpStatusCode.OK, "unreachable");
                }
            ),
            TimeSpan.FromSeconds(30),
            timeProvider
        );

        var fetch = fetcher.FetchAsync(TestContext.Current.CancellationToken);
        await handlerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        timeProvider.FireTimers();

        var act = () => fetch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchRejectsAnUnsolicitedNotModified()
    {
        // The fetcher never sends conditional headers, so a 304 can only come from a
        // non-conforming intermediary — treat it as a failed fetch, not a silent skip.
        var fetcher = new FirstPartyDocumentFetcher(
            Source,
            ["docs.example.test"],
            new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotModified))
        );

        var act = () => fetcher.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task DisposeReleasesTheOwnedClientSoTheRefreshScopeDoesNotLeakIt()
    {
        // The pricing sources are scoped and rebuilt on every refresh pass; without
        // IDisposable the owned handler's connection pool was abandoned to the finalizer.
        var fetcher = new FirstPartyDocumentFetcher(
            Source,
            ["docs.example.test"],
            new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "unused"))
        );

        fetcher.Dispose();

        var act = () => fetcher.FetchAsync(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private static HttpResponseMessage Response(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "text/markdown") };

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
            : this((request, token) => Task.FromResult(response(request, token))) { }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        {
            _response = response;
        }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request.RequestUri!);
            return _response(request, cancellationToken);
        }
    }

    // A TimeProvider whose timers never fire on their own: Change records nothing, so the
    // only way a CancelAfter cancellation happens is the test calling FireTimers. That turns
    // "the linked timeout interrupted a blocked request" into a synchronised sequence instead
    // of a race against a short real delay.
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }

        public void FireTimers()
        {
            foreach (var timer in _timers)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Fire() => callback(state);

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
