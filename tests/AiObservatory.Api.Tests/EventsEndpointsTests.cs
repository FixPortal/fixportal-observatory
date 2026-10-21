using AiObservatory.Api.Endpoints;
using AiObservatory.Data.Entities;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests;

public class EventsEndpointsTests
{
    [Fact]
    public void OmittedProvenanceEnumsUseExactLegacyDefaults()
    {
        EventsEndpoints.TryParseOrDefault(null, SourceKind.Legacy, out SourceKind sourceKind).Should().BeTrue();
        EventsEndpoints.TryParseOrDefault(null, UsageScope.Unknown, out UsageScope usageScope).Should().BeTrue();
        EventsEndpoints.TryParseOrDefault(null, CostBasis.Unknown, out CostBasis costBasis).Should().BeTrue();

        sourceKind.Should().Be(SourceKind.Legacy);
        usageScope.Should().Be(UsageScope.Unknown);
        costBasis.Should().Be(CostBasis.Unknown);
    }

    [Fact]
    public void ProvenanceEnumsParseNamesCaseInsensitivelyButRejectNumericValues()
    {
        EventsEndpoints
            .TryParseOrDefault("lOcAlTeLeMeTrY", SourceKind.Legacy, out SourceKind sourceKind)
            .Should()
            .BeTrue();
        EventsEndpoints.TryParseOrDefault("0", SourceKind.Legacy, out _).Should().BeFalse();

        sourceKind.Should().Be(SourceKind.LocalTelemetry);
    }
}
