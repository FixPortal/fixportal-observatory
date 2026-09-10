using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.IntegrationTests;

[Collection("ApiFactory")]
public class ApiKeyAttachmentTests(AiObservatoryApiFactory factory)
{
    public static TheoryData<HttpMethod, string> ProtectedGetRoutes =>
        new()
        {
            { HttpMethod.Get, "/api/events/00000000-0000-0000-0000-000000000000" },
            { HttpMethod.Get, "/api/caveman-stats" },
            { HttpMethod.Get, "/api/activity/daily" },
            { HttpMethod.Get, "/api/github/prs" },
            { HttpMethod.Get, "/api/adversarial-review/runs" },
            { HttpMethod.Get, "/api/aggregates" },
            { HttpMethod.Get, "/api/insights" },
            { HttpMethod.Get, "/api/subscriptions" },
            { HttpMethod.Get, "/api/budget-rules" },
            { HttpMethod.Get, "/api/spend/categories" },
        };

    [Theory]
    [MemberData(nameof(ProtectedGetRoutes))]
    public async Task ApiGroupRouteWithoutAKeyReturnsUnauthorized(HttpMethod method, string route)
    {
        using var client = factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(method, route);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SpendEntryWriteWithoutAKeyReturnsUnauthorized()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/spend/entries",
            Array.Empty<object>(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
