using AwesomeAssertions;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class IntegrationTraitTests
{
    [Theory]
    [InlineData(typeof(LegacyAlertEmailStartupTests))]
    [InlineData(typeof(SourceHealthDigestServiceTests))]
    public void Database_backed_test_classes_carry_the_Integration_category(Type testClass)
    {
        testClass
            .GetCustomAttributes(typeof(TraitAttribute), inherit: false)
            .Cast<TraitAttribute>()
            .Should()
            .Contain(trait => trait.Name == "Category" && trait.Value == "Integration");
    }
}
