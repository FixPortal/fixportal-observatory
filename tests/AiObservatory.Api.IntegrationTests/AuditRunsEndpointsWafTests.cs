using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiObservatory.Audit.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AuditRunsEndpointsWafTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 10, 22, 12, 0));
    private readonly AiObservatoryApiFactory _factory;
    private readonly CancellationTokenSource _ceiling = CancellationTokenSource.CreateLinkedTokenSource(
        TestContext.Current.CancellationToken
    );
    private CancellationToken Ct => _ceiling.Token;

    public AuditRunsEndpointsWafTests()
    {
        _factory = new() { ClockOverride = _clock };
        _factory.ConfigurationOverrides["Audit:Enabled"] = "true";
        _factory.ConfigurationOverrides["Audit:WorkerEnabled"] = "false";
    }

    public async ValueTask InitializeAsync()
    {
        _ceiling.CancelAfter(TimeSpan.FromSeconds(45));
        await _factory.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _ceiling.Dispose();
    }

    private static AuditScope Scope =>
        new(
            new(2026, 10, 1),
            new(2026, 10, 7),
            [new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "claude-local")],
            [CheckCode.SourceAccounting],
            [],
            true
        );

    private HttpRequestMessage Create(string key, bool partial = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/audits/runs")
        {
            Content = JsonContent.Create(
                new
                {
                    schemaVersion = 1,
                    scope = JsonDocument
                        .Parse(
                            AiObservatory.Audit.Serialization.AuditCanonicalJson.Serialize(
                                Scope with
                                {
                                    AllowPartial = partial,
                                }
                            )
                        )
                        .RootElement,
                    captureIds = Array.Empty<Guid>(),
                    requestFreshCapture = false,
                }
            ),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Fact]
    public async Task Create_IdempotentConflictAndViewerReadOnly()
    {
        using var admin = _factory.CreateAdminClient();
        var response = await admin.SendAsync(Create("same"), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("runId").GetGuid();
        var again = await admin.SendAsync(Create("same"), Ct);
        (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("runId").GetGuid().Should().Be(id);
        (await admin.SendAsync(Create("same", false), Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (
            await admin.PostAsJsonAsync(
                $"/api/audits/runs/{id}/replay",
                new { schemaVersion = 1, verifierVersion = "99.0.0" },
                Ct
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
        using var viewer = _factory.CreateReadOnlyClient();
        (await viewer.GetAsync($"/api/audits/runs/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.SendAsync(Create("forbidden"), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await admin.PostAsync($"/api/audits/runs/{id}/cancel", null, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        (await admin.PostAsync($"/api/audits/runs/{id}/cancel", null, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ReadinessAndCapabilities_ObservationalAndUnsupportedVisible()
    {
        using var viewer = _factory.CreateReadOnlyClient();
        var response = await viewer.GetAsync(
            "/api/audits/readiness?from=2026-10-01&to=2026-10-07&sources=claude-local",
            Ct
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("capture_missing");
        (await viewer.GetAsync("/api/audits/capabilities", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var runs = await viewer.GetFromJsonAsync<JsonElement>("/api/audits/runs", Ct);
        runs.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task DisabledFeature_RoutesAbsent()
    {
        await using var disabled = new AiObservatoryApiFactory();
        await disabled.InitializeAsync();
        using var client = disabled.CreateAdminClient();
        (await client.SendAsync(Create("disabled"), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/audits/capabilities", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("array")]
    [InlineData("version")]
    [InlineData("boolean")]
    public async Task MalformedRunEnvelope_ReturnsSanitized400(string invalid)
    {
        using var client = _factory.CreateAdminClient();
        using var valid = Create("malformed");
        var node = JsonNode.Parse(await valid.Content!.ReadAsStringAsync(Ct))!;
        if (invalid == "version")
        {
            node["schemaVersion"] = "1";
        }
        if (invalid == "boolean")
        {
            node["requestFreshCapture"] = 1;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/audits/runs")
        {
            Content = new StringContent(
                invalid == "array" ? "[]" : node.ToJsonString(),
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Add("Idempotency-Key", "malformed");
        var response = await client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain("Exception");
    }

    [Fact]
    public async Task RunMutations_ViewerKeyDominatesAuthenticatedHuman()
    {
        await using var authenticated = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, SyntheticAuthenticatedStartup>()
            )
        );
        using var viewer = authenticated.CreateClient();
        viewer.DefaultRequestHeaders.Add("X-Observatory-Key", AiObservatoryApiFactory.ReadOnlyKey);
        (await viewer.SendAsync(Create("mixed-authority"), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        foreach (var action in new[] { "cancel", "proceed", "replay" })
        {
            (await viewer.PostAsync($"/api/audits/runs/{Guid.NewGuid()}/{action}", null, Ct))
                .StatusCode.Should()
                .Be(HttpStatusCode.Forbidden);
        }
        using var human = authenticated.CreateClient();
        (await human.SendAsync(Create("human"), Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task HistoryCursor_StableCreatedAtAndIdPagination()
    {
        using var client = _factory.CreateAdminClient();
        foreach (var key in new[] { "page-a", "page-b", "page-c" })
        {
            (await client.SendAsync(Create(key), Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        var ids = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            var page = await client.GetFromJsonAsync<JsonElement>(
                "/api/audits/runs?limit=1" + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)),
                Ct
            );
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                ids.Add(item.GetProperty("id").GetGuid()).Should().BeTrue();
            }
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor != null);
        ids.Should().HaveCount(3);
        var overflow = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"999999999999999999999999999999|{Guid.NewGuid():D}")
        );
        (await client.GetAsync("/api/audits/runs?cursor=" + Uri.EscapeDataString(overflow), Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
    }

    private sealed class SyntheticAuthenticatedStartup : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, invokeNext) =>
                    {
                        context.User = new ClaimsPrincipal(
                            new ClaimsIdentity(
                                [new Claim(ClaimTypes.NameIdentifier, "synthetic-human")],
                                "synthetic-entra"
                            )
                        );
                        await invokeNext();
                    }
                );
                next(app);
            };
    }
}
