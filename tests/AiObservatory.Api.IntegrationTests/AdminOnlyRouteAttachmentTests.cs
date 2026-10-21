using System.Net;
using AwesomeAssertions;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// The read-only share-link key must be refused on every GET route that carries
/// <see cref="AdminOnlyApiKeyEndpointFilter"/>. The group filter accepts that key on any GET, so
/// only a read-only caller proves the route filter is attached: an anonymous request is already
/// refused by the group filter and would pass with the route filter removed. One case per route,
/// because a single representative would not notice the filter being dropped from the others.
/// </summary>
[Collection("ApiFactory")]
public class AdminOnlyRouteAttachmentTests(AiObservatoryApiFactory factory)
{
    public static TheoryData<string> AdminOnlyGetRoutes =>
        [
            "/api/activity/daily",
            "/api/activity/by-project",
            "/api/github/prs",
            "/api/github/reviews",
            "/api/github/commits/summary",
            "/api/github/ci",
            "/api/spend/entries",
        ];

    [Theory]
    [MemberData(nameof(AdminOnlyGetRoutes))]
    public async Task ReadOnlyKeyIsRefusedOnAnAdminOnlyRoute(string route)
    {
        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetAsync(route, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the read-only key must not reach {0}", route);
    }

    /// <summary>
    /// Control: the admin key reaches the same routes, so the 401s above come from the key class
    /// and not from a route that is unreachable for every caller.
    /// </summary>
    [Theory]
    [MemberData(nameof(AdminOnlyGetRoutes))]
    public async Task AdminKeyIsAcceptedOnAnAdminOnlyRoute(string route)
    {
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync(route, TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }
}
