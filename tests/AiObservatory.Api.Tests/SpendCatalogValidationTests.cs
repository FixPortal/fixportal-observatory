using AiObservatory.Api.Endpoints;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests;

/// <summary>
/// The catalog's own gate. Keys matter more than they look: the portal feed and CSV imports
/// resolve vendors and categories by slug, so a key that normalises inconsistently would send
/// a real charge to the wrong axis or fail to resolve at all.
/// </summary>
public class SpendCatalogValidationTests
{
    [Theory]
    [InlineData("code-review", "code-review")]
    [InlineData("Code-Review", "code-review")]
    [InlineData("  ci  ", "ci")]
    [InlineData("github_actions", "github_actions")]
    [InlineData("openai4", "openai4")]
    public void NormalisesAValidKeyToALowerCaseSlug(string raw, string expected)
    {
        SpendCatalogEndpoints.Slug(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsAnEmptyKey(string? raw)
    {
        SpendCatalogEndpoints.Slug(raw).Should().BeNull();
    }

    [Theory]
    [InlineData("code review")]
    [InlineData("code.review")]
    [InlineData("code/review")]
    [InlineData("café")]
    [InlineData("code+review")]
    public void RejectsAKeyWithCharactersOutsideTheSlugAlphabet(string raw)
    {
        SpendCatalogEndpoints
            .Slug(raw)
            .Should()
            .BeNull("only ASCII letters, digits, hyphen and underscore resolve reliably in a feed");
    }

    [Fact]
    public void AcceptsAKeyAtTheLengthLimit()
    {
        SpendCatalogEndpoints.Slug(new string('a', 60)).Should().HaveLength(60);
    }

    [Fact]
    public void RejectsAKeyOverTheLengthLimit()
    {
        SpendCatalogEndpoints.Slug(new string('a', 61)).Should().BeNull();
    }

    [Fact]
    public void MeasuresTheKeyLengthAfterTrimming()
    {
        SpendCatalogEndpoints
            .Slug("  " + new string('a', 60) + "  ")
            .Should()
            .HaveLength(60, "surrounding whitespace is stripped, so it must not count toward the limit");
    }

    [Theory]
    [InlineData("CI")]
    [InlineData("Code Review")]
    public void AcceptsAPresentDisplayName(string name)
    {
        SpendCatalogEndpoints.ValidateName(name).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsAMissingDisplayName(string? name)
    {
        SpendCatalogEndpoints
            .ValidateName(name)
            .Should()
            .Be("DisplayName is required and must be 100 characters or fewer");
    }

    [Fact]
    public void AcceptsADisplayNameAtTheLengthLimit()
    {
        SpendCatalogEndpoints.ValidateName(new string('x', 100)).Should().BeNull();
    }

    [Fact]
    public void RejectsADisplayNameOverTheLengthLimit()
    {
        SpendCatalogEndpoints
            .ValidateName(new string('x', 101))
            .Should()
            .Be("DisplayName is required and must be 100 characters or fewer");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--chart-1")]
    public void AcceptsAnAbsentOrShortColorVar(string? color)
    {
        SpendCatalogEndpoints.ValidateColorVar(color).Should().BeNull();
    }

    [Fact]
    public void AcceptsAColorVarAtTheLengthLimit()
    {
        SpendCatalogEndpoints.ValidateColorVar(new string('x', 60)).Should().BeNull();
    }

    [Fact]
    public void RejectsAColorVarOverTheLengthLimit()
    {
        SpendCatalogEndpoints
            .ValidateColorVar(new string('x', 61))
            .Should()
            .Be("ColorVar must be 60 characters or fewer");
    }
}
