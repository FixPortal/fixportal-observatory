using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Services;

public class InsightResponseParserTests
{
    [Fact]
    public void Parse_returns_insight_records_from_valid_json()
    {
        var json = """
            [
              {"type":"summary","title":"Daily summary","body":"You spent $4.12 yesterday.","data":{}},
              {"type":"efficiency","title":"Opus overuse","body":"41% of Opus calls under 400 tokens.","data":{"estimatedWeeklySaving":9.20}}
            ]
            """;

        var sut = new InsightResponseParser();
        var now = Instant.FromUtc(2026, 6, 2, 8, 0);
        var results = sut.Parse(json, new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), now);

        results.Should().HaveCount(2);
        results[0].InsightType.Should().Be(InsightType.Summary);
        results[1].InsightType.Should().Be(InsightType.Efficiency);
        results[1].Body.Should().Contain("400 tokens");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("\"text\"")]
    public void Parse_normalises_non_object_data_to_an_empty_object(string data)
    {
        // Consumers of Insight.Data expect the object shape (e.g. the deduplicator's costBasis
        // read); the model may answer with "data": [], 5 or "text", so anything but an object
        // is normalised to none rather than stored.
        var json = $$"""[{"type":"anomaly","title":"Odd spike","body":"Worth keeping.","data":{{data}}}]""";

        var sut = new InsightResponseParser();
        var now = Instant.FromUtc(2026, 6, 2, 8, 0);
        var results = sut.Parse(json, new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), now);

        results.Should().ContainSingle().Which.Data.Should().Be("{}");
    }

    [Fact]
    public void Parse_keeps_an_object_data_document_verbatim()
    {
        var json = """[{"type":"anomaly","title":"Odd spike","body":"Worth keeping.","data":{"costBasis":"billed"}}]""";

        var sut = new InsightResponseParser();
        var now = Instant.FromUtc(2026, 6, 2, 8, 0);
        var results = sut.Parse(json, new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), now);

        results.Should().ContainSingle().Which.Data.Should().Be("""{"costBasis":"billed"}""");
    }

    [Fact]
    public void Parse_throws_a_descriptive_error_for_malformed_json()
    {
        var sut = new InsightResponseParser();
        var now = Instant.FromUtc(2026, 6, 2, 8, 0);

        var act = () => sut.Parse("not json at all", new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public void Parse_throws_a_descriptive_error_when_the_response_is_not_an_array()
    {
        var sut = new InsightResponseParser();
        var now = Instant.FromUtc(2026, 6, 2, 8, 0);

        var act = () => sut.Parse("""{"insights": []}""", new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a JSON array*");
    }

    [Fact]
    public void Parse_skips_items_with_missing_blank_or_non_string_titles()
    {
        var json = """
            [
              {"type":"summary","title":"Real insight","body":"Worth keeping.","data":{}},
              {"type":"anomaly","body":"No title at all.","data":{}},
              {"type":"anomaly","title":"  ","body":"Blank title.","data":{}},
              {"type":"anomaly","title":42,"body":"Numeric title.","data":{}},
              "not even an object"
            ]
            """;

        var sut = new InsightResponseParser();
        var now = Instant.FromUtc(2026, 6, 2, 8, 0);
        var results = sut.Parse(json, new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), now);

        results.Should().ContainSingle().Which.Title.Should().Be("Real insight");
    }
}
