using System.Text;
using AiObservatory.Api.Audit;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using NodaTime;

namespace AiObservatory.Api.Tests.Audit;

public sealed class AuditSelectedInvocationRequestReaderTests
{
    [Fact]
    public async Task DigestValueMustUseCanonicalLowercaseBytes()
    {
        var escaped = string.Concat(Enumerable.Repeat("\\u0061", 64));
        await Refused(Request(Body().Replace(Authority.Binding.ScopeDigest, escaped, StringComparison.Ordinal)));
        await Refused(
            Request(Body().Replace(Authority.Binding.ScopeDigest, new string('A', 64), StringComparison.Ordinal))
        );
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("Application/JSON; charset=\"UTF-8\"")]
    public async Task StandardUtf8JsonContentTypesAreAccepted(string type)
    {
        var request = Request(Body());
        request.ContentType = type;
        (
            await AuditSelectedInvocationRequestReader.ReadAsync(
                request,
                Authority,
                TestContext.Current.CancellationToken
            )
        )
            .ExpectedRevision.Should()
            .Be(Authority.Binding.ExpectedRevision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9223372036854775807)]
    public async Task ExactCanonicalTupleAcceptsWhitespaceAndPropertyOrder(long revision)
    {
        var authority = Authority with { Binding = Authority.Binding with { ExpectedRevision = revision } };
        var body =
            " { \"fullScopeDigest\":\""
            + authority.Binding.ScopeDigest
            + "\", \"expectedRevision\":"
            + revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ", \"schemaVersion\":1 } ";
        var result = await AuditSelectedInvocationRequestReader.ReadAsync(
            Request(body),
            authority,
            TestContext.Current.CancellationToken
        );
        result.Should().Be(new SelectedInvocationRequest(1, revision, authority.Binding.ScopeDigest));
    }

    [Theory]
    [InlineData("\"7\"")]
    [InlineData("7.0")]
    [InlineData("7e0")]
    [InlineData("-0")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task NoncanonicalRevisionLexemesAreRejected(string lexeme)
    {
        await Refused(
            Request(
                Body().Replace("\"expectedRevision\":7", "\"expectedRevision\":" + lexeme, StringComparison.Ordinal)
            )
        );
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"expectedRevision\":7,\"fullScopeDigest\":\"DIGEST\"}")]
    [InlineData("{\"schemaVersion\":1,\"expectedRevision\":7,\"expectedRevision\":8,\"fullScopeDigest\":\"DIGEST\"}")]
    [InlineData(
        "{\"schemaVersion\":1,\"expectedRevision\":7,\"fullScopeDigest\":\"DIGEST\",\"fullScopeDigest\":\"DIGEST\"}"
    )]
    [InlineData(
        "{\"schemaVersion\":1,\"expectedRevision\":7,\"fullScopeDigest\":\"DIGEST\",\"PRIVATE_UNKNOWN_SENTINEL\":1}"
    )]
    [InlineData("{\"SchemaVersion\":1,\"expectedRevision\":7,\"fullScopeDigest\":\"DIGEST\"}")]
    [InlineData("{\"schemaVersion\":1.0,\"expectedRevision\":7,\"fullScopeDigest\":\"DIGEST\"}")]
    [InlineData("{\"schemaVersion\":2,\"expectedRevision\":7,\"fullScopeDigest\":\"DIGEST\"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"expectedRevision\":7,\"fullScopeDigest\":\"DIGEST\"}")]
    [InlineData("{\"schemaVersion\":1,\"expectedRevision\":7,\"fullScopeDigest\":null}")]
    [InlineData("{\"schemaVersion\":1,\"expectedRevision\":7,\"fullScopeDigest\":\"PRIVATE_DIGEST_SENTINEL\"}")]
    [InlineData("{\"schemaVersion\":1,\"expectedRevision\":7,\"fullScopeDigest\":\"DIGEST\",}")]
    [InlineData("/*PRIVATE_COMMENT_SENTINEL*/{}")]
    public async Task ClosedJsonShapeIsRequired(string json)
    {
        await Refused(Request(json.Replace("DIGEST", Authority.Binding.ScopeDigest, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DifferentCanonicalDigestConflicts()
    {
        await Refused(
            Request(Body().Replace(new string('a', 64), new string('b', 64), StringComparison.Ordinal)),
            409,
            "selected_binding_conflict"
        );
    }

    [Theory]
    [InlineData("?PRIVATE_QUERY_SENTINEL=value", null)]
    [InlineData("", "PRIVATE_ETAG_SENTINEL")]
    [InlineData("", "")]
    public async Task QueryAndAnyIfMatchAreRejected(string query, string? ifMatch)
    {
        var request = Request(Body());
        request.QueryString = new QueryString(query);
        if (ifMatch != null)
        {
            request.Headers["If-Match"] = ifMatch;
        }
        await Refused(request);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("comma")]
    [InlineData("upper")]
    [InlineData("braces")]
    [InlineData("no-hyphens")]
    [InlineData("spaces")]
    [InlineData("other")]
    public async Task CorrelationIsOneExactCanonicalConfiguredValue(string variation)
    {
        var request = Request(Body());
        var key = Authority.InvocationId.ToString("D");
        request.Headers["Idempotency-Key"] = variation switch
        {
            "missing" => Microsoft.Extensions.Primitives.StringValues.Empty,
            "duplicate" => new[] { key, key },
            "comma" => key + "," + key,
            "upper" => key.ToUpperInvariant(),
            "braces" => Authority.InvocationId.ToString("B"),
            "no-hyphens" => Authority.InvocationId.ToString("N"),
            "spaces" => " " + key,
            _ => "PRIVATE_KEY_SENTINEL",
        };
        await Refused(request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/problem+json")]
    public async Task OnlyUtf8JsonContentTypeIsAccepted(string? contentType)
    {
        var request = Request(Body());
        request.ContentType = contentType;
        await Refused(request, 415, "selected_content_type");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyCounterAccepts1024AndStopsAfter1025RegardlessOfDeclaredLength(bool chunked)
    {
        var valid = Body();
        var request = Request(valid.PadRight(1024));
        request.ContentLength = chunked ? null : 1;
        if (chunked)
        {
            request.Headers.TransferEncoding = "chunked";
        }
        await AuditSelectedInvocationRequestReader.ReadAsync(request, Authority, TestContext.Current.CancellationToken);
        var stream = new CountingBody(Encoding.UTF8.GetBytes(valid.PadRight(4096)));
        request.Body = stream;
        await Refused(request, 413, "selected_body_limit");
        stream.BytesRead.Should().Be(1025);
    }

    [Fact]
    public async Task DeclaredOversizeIsRejectedBeforeReading()
    {
        var request = Request(Body());
        var stream = new CountingBody(Encoding.UTF8.GetBytes(Body()));
        request.Body = stream;
        request.ContentLength = 1025;
        await Refused(request, 413, "selected_body_limit");
        stream.BytesRead.Should().Be(0);
    }

    [Fact]
    public async Task MalformedUtf8FailsWithoutEchoingBytes()
    {
        var request = Request(Body());
        request.Body = new MemoryStream([0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d]);
        await Refused(request);
    }

    [Fact]
    public async Task CancellationReachesAnAwaitedBodyRead()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var request = Request(Body());
        var stream = new AwaitedBody();
        request.Body = stream;
        var read = AuditSelectedInvocationRequestReader.ReadAsync(request, Authority, cancellation.Token);
        read.IsCompleted.Should().BeFalse("the body is held behind an explicit asynchronous gate");
        await stream.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var action = () => read;
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class CountingBody(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 13)], cancellationToken);
            BytesRead += count;
            return count;
        }
    }

    private sealed class AwaitedBody : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _body = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            Started.TrySetResult();
            return await _body.Task.WaitAsync(cancellationToken);
        }
    }

    private static string Body() =>
        "{\"schemaVersion\":1,\"expectedRevision\":7,\"fullScopeDigest\":\"" + Authority.Binding.ScopeDigest + "\"}";

    private static async Task Refused(HttpRequest request, int status = 400, string code = "selected_request_invalid")
    {
        var action = () =>
            AuditSelectedInvocationRequestReader.ReadAsync(request, Authority, TestContext.Current.CancellationToken);
        var failure = (await action.Should().ThrowAsync<AuditStoreException>()).Which;
        failure.StatusCode.Should().Be(status);
        failure.Code.Should().Be(code);
        failure.Message.Should().Be(code);
    }

    private static readonly SelectedInvocationAuthority Authority = new(
        false,
        new(Guid.Parse("4a0a3de8-572d-40b6-8d42-4b074b3ce080"), new string('a', 64), 7),
        Guid.Parse("bf326bf0-0762-4372-b9f6-5b8de7e223d9"),
        Instant.FromUtc(2026, 10, 23, 12, 0)
    );

    [Fact]
    public async Task BodyCannotRefreshConfiguredRevision()
    {
        var request = Request(
            "{\"schemaVersion\":1,\"expectedRevision\":8,\"fullScopeDigest\":\"" + Authority.Binding.ScopeDigest + "\"}"
        );
        var action = () =>
            AuditSelectedInvocationRequestReader.ReadAsync(request, Authority, TestContext.Current.CancellationToken);
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("selected_binding_conflict");
    }

    private static HttpRequest Request(string body)
    {
        var request = new DefaultHttpContext().Request;
        request.ContentType = "application/json; charset=utf-8";
        request.Headers["Idempotency-Key"] = Authority.InvocationId.ToString("D");
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return request;
    }
}
