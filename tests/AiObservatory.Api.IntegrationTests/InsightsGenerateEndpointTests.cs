using System.Net;
using AiObservatory.Api.Services.Intelligence;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using NSubstitute;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("FixedClockApiFactory")]
public class InsightsGenerateEndpointTests(FixedClockApiFactory fixture)
{
    // The daily watermark is the latest PeriodEnd, so analysing today would make the next
    // scheduled run skip that day and would label a partial day as "yesterday".
    [Fact]
    public async Task Generate_analyses_yesterday_not_the_partial_current_day()
    {
        var generator = Substitute.For<IInsightGenerator>();
        generator
            .GenerateForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(new InsightGenerationResult(2, 2));
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInsightGenerator>();
                services.AddSingleton(generator);
            })
        );
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Observatory-Key", AiObservatoryApiFactory.AdminKey);

        var response = await client.PostAsync("/api/insights/generate", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var today = FixedClockApiFactory.Now.InUtc().Date;
        await generator.Received(1).GenerateForDateAsync(today.PlusDays(-1), Arg.Any<CancellationToken>());
    }
}
