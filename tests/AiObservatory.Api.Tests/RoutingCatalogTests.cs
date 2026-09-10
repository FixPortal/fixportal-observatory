using AiObservatory.Api.Routing;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

public sealed class RoutingCatalogTests
{
    [Fact]
    public void ProducesAStableOrderedSnapshotFromTheAuthoritativeProjection()
    {
        using var file = CatalogFile.Create(ValidCatalog);

        var service = RoutingCatalogService.Load(file.Path);
        var first = service.GetSnapshot(Instant.FromUtc(2026, 8, 4, 12, 0));
        var second = service.GetSnapshot(Instant.FromUtc(2026, 8, 4, 12, 5));

        first.CatalogRevision.Should().Be(1);
        first.Models.Select(model => model.ModelId).Should().Equal("claude-fable-5", "gpt-5.6-sol");
        first.Models[1].Aliases.Should().Equal("codex", "openai");
        first.SnapshotId.Should().StartWith("sha256:").And.HaveLength(71);
        second.SnapshotId.Should().Be(first.SnapshotId);
        second.GeneratedAt.Should().Be(first.GeneratedAt);
    }

    [Theory]
    [MemberData(nameof(InvalidCatalogs))]
    public void RejectsAnInvalidProjectionInsteadOfServingPartialEvidence(string json)
    {
        using var file = CatalogFile.Create(json);

        var load = () => RoutingCatalogService.Load(file.Path);

        load.Should().Throw<InvalidDataException>();
    }

    public static TheoryData<string> InvalidCatalogs =>
        [
            ValidCatalog.Replace("\"catalogRevision\": 1", "\"catalogRevision\": 0", StringComparison.Ordinal),
            ValidCatalog.Replace("\"modelId\": \"gpt-5.6-sol\"", "\"modelId\": \" \"", StringComparison.Ordinal),
            ValidCatalog.Replace(
                "\"aliases\": [\"openai\", \"codex\"]",
                "\"aliases\": [\"codex\", \"codex\"]",
                StringComparison.Ordinal
            ),
            ValidCatalog.Replace("\"value\": 0.5", "\"value\": 1.1", StringComparison.Ordinal),
            ValidCatalog.Replace("\"costBasis\": \"unpriced\"", "\"costBasis\": \"mystery\"", StringComparison.Ordinal),
            ValidCatalog.Replace("\"estimatedCostUsd\": null", "\"estimatedCostUsd\": -1", StringComparison.Ordinal),
            ValidCatalog.Replace(
                "\"effectiveFrom\": \"2026-08-04T00:00:00Z\"",
                "\"effectiveFrom\": \"2026-08-04T00:00:00Z\", \"unknown\": true",
                StringComparison.Ordinal
            ),
        ];

    private const string ValidCatalog = """
        {
          "catalogRevision": 1,
          "models": [
            {
              "modelId": "gpt-5.6-sol",
              "vendor": "openai",
              "modelFamily": "gpt-5.6",
              "aliases": ["openai", "codex"],
              "identityBasis": "adapterDefaultDeclared",
              "identityAdapterAlias": "codex",
              "identityObservedAt": "2026-08-03T19:24:21Z",
              "capabilities": ["code"],
              "costBasis": "unpriced",
              "estimatedCostUsd": null,
              "evidence": {
                "quality": { "value": 0.5, "basis": "operatorBaseline", "source": "observatory-routing-baseline:2026-08-04", "observedAt": "2026-08-04T00:00:00Z", "sampleCount": null },
                "reliability": { "value": 0.5, "basis": "operatorBaseline", "source": "observatory-routing-baseline:2026-08-04", "observedAt": "2026-08-04T00:00:00Z", "sampleCount": null },
                "interventionRate": { "value": 0.5, "basis": "operatorBaseline", "source": "observatory-routing-baseline:2026-08-04", "observedAt": "2026-08-04T00:00:00Z", "sampleCount": null },
                "toolFit": { "value": 0.5, "basis": "operatorBaseline", "source": "observatory-routing-baseline:2026-08-04", "observedAt": "2026-08-04T00:00:00Z", "sampleCount": null },
                "contextFit": { "value": 0.5, "basis": "operatorBaseline", "source": "observatory-routing-baseline:2026-08-04", "observedAt": "2026-08-04T00:00:00Z", "sampleCount": null }
              },
              "effectiveFrom": "2026-08-04T00:00:00Z",
              "effectiveTo": null
            },
            {
              "modelId": "claude-fable-5",
              "vendor": "anthropic",
              "modelFamily": "claude-5",
              "aliases": ["claude"],
              "identityBasis": "adapterDefaultDeclared",
              "identityAdapterAlias": "claude",
              "identityObservedAt": "2026-08-03T19:24:21Z",
              "capabilities": ["code"],
              "costBasis": "unpriced",
              "estimatedCostUsd": null,
              "evidence": {
                "quality": { "value": 0.5, "basis": "operatorBaseline", "source": "observatory-routing-baseline:2026-08-04", "observedAt": "2026-08-04T00:00:00Z", "sampleCount": null },
                "reliability": { "value": 0.5, "basis": "operatorBaseline", "source": "observatory-routing-baseline:2026-08-04", "observedAt": "2026-08-04T00:00:00Z", "sampleCount": null },
                "interventionRate": { "value": 0.5, "basis": "operatorBaseline", "source": "observatory-routing-baseline:2026-08-04", "observedAt": "2026-08-04T00:00:00Z", "sampleCount": null },
                "toolFit": { "value": 0.5, "basis": "operatorBaseline", "source": "observatory-routing-baseline:2026-08-04", "observedAt": "2026-08-04T00:00:00Z", "sampleCount": null },
                "contextFit": { "value": 0.5, "basis": "operatorBaseline", "source": "observatory-routing-baseline:2026-08-04", "observedAt": "2026-08-04T00:00:00Z", "sampleCount": null }
              },
              "effectiveFrom": "2026-08-04T00:00:00Z",
              "effectiveTo": null
            }
          ]
        }
        """;

    private sealed class CatalogFile : IDisposable
    {
        private CatalogFile(string path) => Path = path;

        public string Path { get; }

        public static CatalogFile Create(string content)
        {
            var path = System.IO.Path.GetTempFileName();
            File.WriteAllText(path, content);
            return new CatalogFile(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
