using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// Every route the host maps under /api (the IDE partner surface has its own key and its own
/// tests) must refuse a caller with no key. The route table is enumerated from the running host
/// rather than listed by hand, so a route mapped outside the /api group, or one added later
/// without the group filter, is caught instead of silently shipping open. A verb the table
/// maps is exercised with that verb; the existing attachment tests cover only a hand-picked
/// subset of GETs and one POST.
/// </summary>
[Collection("ApiFactory")]
public class AnonymousRouteSweepTests(AiObservatoryApiFactory factory)
{
    private const string IdePrefix = "/api/ide/v1";

    // Minimal API binds parameters before the endpoint filters run, so a route with a required
    // query parameter answers 400 to a request that omits it, whoever the caller is. These
    // routes get the parameter so the request reaches the key check.
    // The same holds for the body: a route bound to an array answers 400 to an object.
    private static readonly HashSet<string> ArrayBodies = ["POST /api/spend/entries"];

    private static readonly Dictionary<string, string> RequiredQuery = new(StringComparer.Ordinal)
    {
        ["GET /api/events"] = "?provider=Anthropic",
        ["PATCH /api/events/00000000-0000-0000-0000-000000000000/cost"] = "?provider=Anthropic",
    };

    [Fact]
    public async Task EveryApiRouteAndVerbRefusesACallerWithNoKey()
    {
        var routes = MappedApiRoutes();
        routes.Should().NotBeEmpty("the sweep must see the mapped table, not an empty one");
        using var client = factory.CreateAnonymousClient();
        var notRefused = new List<string>();

        foreach (var (method, path) in routes)
        {
            var query = RequiredQuery.GetValueOrDefault($"{method} {path}", "");
            using var request = new HttpRequestMessage(new HttpMethod(method), path + query);
            if (method is not ("GET" or "DELETE"))
            {
                var body = ArrayBodies.Contains($"{method} {path}") ? "[]" : "{}";
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                notRefused.Add($"{method} {path} -> {(int)response.StatusCode}");
            }
        }

        notRefused.Should().BeEmpty("every /api route must answer 401 to a caller with no key");
    }

    [Fact]
    public void TheSweepSeesTheRoutesTheAttachmentTestsAlreadyNameAndTheDeletePurges()
    {
        var routes = MappedApiRoutes();

        routes.Should().Contain(("GET", "/api/spend/reporting"));
        routes.Should().Contain(("GET", "/api/notification-settings"));
        routes.Should().Contain(("DELETE", "/api/aggregates"));
        routes.Should().Contain(("DELETE", "/api/insights"));
    }

    private List<(string Method, string Path)> MappedApiRoutes()
    {
        var routes = new List<(string, string)>();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var template = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
            if (
                !template.StartsWith("/api/", StringComparison.Ordinal)
                || template.StartsWith(IdePrefix + "/", StringComparison.Ordinal)
            )
            {
                continue;
            }

            var path = FillParameters(template);
            var methods = endpoint.Metadata.GetRequiredMetadata<HttpMethodMetadata>().HttpMethods;
            foreach (var method in methods)
            {
                routes.Add((method, path));
            }
        }

        return routes;
    }

    private static string FillParameters(string template)
    {
        var segments = template.Split('/').Select(segment => segment.StartsWith('{') ? Guid.Empty.ToString() : segment);
        return string.Join('/', segments);
    }
}
