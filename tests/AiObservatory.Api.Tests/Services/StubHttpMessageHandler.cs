using System.Net;
using System.Text;

namespace AiObservatory.Api.Tests.Services;

/// <summary>
/// A canned HTTP response for FxRateProvider's HttpClient, shared by FxRateProviderTests
/// (constructs FxRateProvider directly) and SpendEntriesEndpointsWafTests (wires this in
/// through the WebApplicationFactory's DI container via ConfigureTestServices instead), so
/// FX outage/failure scenarios are deterministic and never call out to the real
/// frankfurter.dev over the network.
/// </summary>
internal sealed class StubHttpMessageHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public List<string> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Requested.Add(request.RequestUri!.ToString());
        return Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
        );
    }
}
