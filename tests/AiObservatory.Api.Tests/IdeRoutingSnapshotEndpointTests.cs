using System.Text.Json;
using AiObservatory.Api.Endpoints;
using AiObservatory.Api.Routing;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NodaTime.Testing;

namespace AiObservatory.Api.Tests;

public sealed class IdeRoutingSnapshotEndpointTests
{
    [Fact]
    public void ProducesTheSharedGoldenSnapshot()
    {
        var observedAt = NodaTime.Instant.FromUtc(2026, 8, 4, 12, 0);
        var metric = new RoutingEvidenceMetric(0.5, "operatorBaseline", "routing-catalog:2026-08-04", observedAt, null);
        var snapshot = new RoutingSnapshot(
            1,
            1,
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            observedAt,
            [
                new RoutingSnapshotModel(
                    "gpt-5.6-sol",
                    "openai",
                    "gpt-5.6",
                    ["codex"],
                    "adapterDefaultDeclared",
                    "codex",
                    observedAt,
                    ["code"],
                    "unpriced",
                    null,
                    new RoutingEvidence(metric, metric, metric, metric, metric)
                ),
            ]
        );
        var actual = JsonSerializer.Serialize(snapshot, RoutingCatalogService.SerializerOptions);
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing-snapshot.v1.json"));

        actual.Should().Be(fixture.TrimEnd());
    }

    [Fact]
    public async Task ReturnsAWeakEtagThenHonorsAConditionalRequest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Routing", "routing-catalog.json");
        var catalog = RoutingCatalogService.Load(path);
        var clock = new FakeClock(NodaTime.Instant.FromUtc(2026, 8, 4, 12, 0));
        var first = NewContext();

        var firstResult = IdeEndpoints.GetRoutingSnapshot(first, catalog, clock);
        await firstResult.ExecuteAsync(first);

        first.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var etag = first.Response.Headers.ETag.Single();
        etag.Should().StartWith("W/\"sha256:").And.EndWith("\"");
        first.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            first.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken
        );
        document.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("models").GetArrayLength().Should().Be(3);

        var second = NewContext();
        second.Request.Headers.IfNoneMatch = etag;
        var secondResult = IdeEndpoints.GetRoutingSnapshot(second, catalog, clock);
        await secondResult.ExecuteAsync(second);

        second.Response.StatusCode.Should().Be(StatusCodes.Status304NotModified);
        second.Response.Body.Length.Should().Be(0);
    }

    [Theory]
    [InlineData("\"unrelated\", {etag}")]
    [InlineData("*")]
    public async Task HonorsHttpIfNoneMatchListsAndWildcard(string condition)
    {
        var catalog = RoutingCatalogService.Load(
            Path.Combine(AppContext.BaseDirectory, "Routing", "routing-catalog.json")
        );
        var clock = new FakeClock(NodaTime.Instant.FromUtc(2026, 8, 4, 12, 0));
        var first = NewContext();
        await IdeEndpoints.GetRoutingSnapshot(first, catalog, clock).ExecuteAsync(first);
        var conditional = NewContext();
        conditional.Request.Headers.IfNoneMatch = condition.Replace(
            "{etag}",
            first.Response.Headers.ETag.Single(),
            StringComparison.Ordinal
        );

        await IdeEndpoints.GetRoutingSnapshot(conditional, catalog, clock).ExecuteAsync(conditional);

        conditional.Response.StatusCode.Should().Be(StatusCodes.Status304NotModified);
        conditional.Response.Body.Length.Should().Be(0);
    }

    private static DefaultHttpContext NewContext()
    {
        return new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
    }
}
