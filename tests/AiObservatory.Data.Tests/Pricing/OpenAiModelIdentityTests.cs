using AiObservatory.Data.Pricing.Catalogs;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Data.Tests.Pricing;

public sealed class OpenAiModelIdentityTests
{
    [Theory]
    [InlineData("gpt-example", true)]
    [InlineData("GPT-EXAMPLE", true)]
    [InlineData("explicit-alias", true)]
    [InlineData("gpt-example-2026-08-24", true)]
    [InlineData("explicit-alias-2024-02-29", true)]
    [InlineData("gpt-example-mini", false)]
    [InlineData("gpt-example-mini-2026-08-24", false)]
    [InlineData("gpt-example-unknown", false)]
    [InlineData("gpt-example-2026-02-29", false)]
    [InlineData("gpt-example-2026-08-24-extra", false)]
    [InlineData("gpt-example-2026-8-24", false)]
    [InlineData("gpt-example2", false)]
    public void ResolveRequiresExactAliasOrValidatedDateSuffix(string model, bool matches)
    {
        var entry = new OpenAiPriceEntry(
            "gpt-example",
            ["explicit-alias"],
            new LocalDate(2026, 1, 1),
            false,
            "standard",
            "short",
            "global",
            2m,
            0.2m,
            10m,
            null
        );
        var catalog = new OpenAiPriceCatalog(
            "USD",
            "https://example.com/pricing",
            Instant.FromUtc(2026, 8, 24, 0, 0),
            [entry]
        );

        var result = catalog.Resolve(model, "standard", "short", "global", new LocalDate(2026, 8, 24));

        if (matches)
        {
            result.Should().BeSameAs(entry);
        }
        else
        {
            result.Should().BeNull();
        }
    }
}
