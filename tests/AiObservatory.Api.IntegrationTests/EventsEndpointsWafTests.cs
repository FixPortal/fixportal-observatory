using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// AIO-H3: POST /api/events validation branches (future-date, non-JSON RawPayload) and the
/// duplicate-vs-created status-code contract (200 OK+Duplicate for a repeated EventKey,
/// 201 Created for a genuinely new one). WebApplicationFactory end-to-end — these branches
/// live in the minimal-API handler itself, unreachable from a unit test.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class EventsEndpointsWafTests(AiObservatoryApiFactory factory)
{
    private static object NewEventBody(string? eventKey = null, DateTimeOffset? occurredAtUtc = null) =>
        new
        {
            Provider = "anthropic",
            Model = "claude-sonnet-4-6",
            InputTokens = 100,
            OutputTokens = 50,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 0.01m,
            RawPayload = "{}",
            EventKey = eventKey,
            OccurredAtUtc = occurredAtUtc,
            SourceId = UsageSourceIds.AnthropicUsageApi,
            SourceKind = "providerApi",
            UsageScope = "api",
            CostBasis = "unknown",
        };

    [Fact]
    public async Task PostEvent_WhenOccurredAtIsInTheFuture_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var body = NewEventBody(occurredAtUtc: DateTimeOffset.UtcNow.AddHours(1));

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenRawPayloadIsNotValidJson_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var raw =
            """{"Provider":"anthropic","Model":"m","InputTokens":1,"OutputTokens":1,"CacheReadTokens":0,"CacheWriteTokens":0,"CostUsd":0.01,"RawPayload":"not json","SourceId":"anthropic-usage-api","SourceKind":"providerApi","UsageScope":"api","CostBasis":"unknown"}""";

        using var content = new StringContent(raw, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/events", content, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenRawPayloadHasDuplicateProperties_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            CostUsd = 0.01m,
            RawPayload = """{"request":"first","request":"last"}""",
            SourceId = UsageSourceIds.OpenAiUsageApi,
            SourceKind = "providerApi",
            UsageScope = "api",
            CostBasis = "unknown",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenUnknownProvider_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var raw =
            """{"Provider":"not-a-real-provider","Model":"m","InputTokens":1,"OutputTokens":1,"CacheReadTokens":0,"CacheWriteTokens":0,"CostUsd":0.01,"RawPayload":"{}"}""";

        using var content = new StringContent(raw, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/events", content, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public async Task PostEvent_WhenTokenCountsNegative_ReturnsBadRequest(long inputTokens, long outputTokens)
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "anthropic",
            Model = "m",
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 0.01m,
            RawPayload = "{}",
            SourceId = UsageSourceIds.AnthropicUsageApi,
            SourceKind = "providerApi",
            UsageScope = "api",
            CostBasis = "unknown",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenGenuinelyNew_ReturnsCreatedWithLocation()
    {
        using var client = factory.CreateAdminClient();
        var body = NewEventBody(eventKey: $"waf-test-new-{Guid.NewGuid():N}");

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task PostEvent_WhenEventKeyAlreadyRecorded_ReturnsOkWithDuplicateFlag()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-test-dup-{Guid.NewGuid():N}";
        var body = NewEventBody(eventKey: key, occurredAtUtc: new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));

        var first = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        json.GetProperty("corrected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PostEvent_with_maximum_key_preserves_the_provider_namespace()
    {
        using var client = factory.CreateAdminClient();
        var key = new string('x', 200);
        var response = await client.PostAsJsonAsync(
            "/api/events",
            new
            {
                Provider = "anthropic",
                Model = "claude-sonnet-4-6",
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = 1m,
                RawPayload = "{}",
                EventKey = key,
                SourceId = UsageSourceIds.LegacyApi,
                SourceKind = "legacy",
                UsageScope = "unknown",
                CostBasis = "unknown",
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        (await client.GetFromJsonAsync<JsonElement>($"/api/events/{id}", TestContext.Current.CancellationToken))
            .GetProperty("eventKey")
            .GetString()
            .Should()
            .Be($"Anthropic:{key}");
    }

    [Fact]
    public async Task Legacy_post_and_patch_treat_an_already_prefixed_key_as_the_stored_form()
    {
        using var client = factory.CreateAdminClient();
        var rawKey = $"waf-legacy-prefixed-{Guid.NewGuid():N}";
        var occurredAtUtc = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var rows = new Dictionary<(string Provider, string EventKey), Guid>();

        async Task<Guid> Post(string provider, string eventKey)
        {
            var response = await client.PostAsJsonAsync(
                "/api/events",
                new
                {
                    Provider = provider,
                    Model = "shared-model",
                    InputTokens = 1,
                    OutputTokens = 1,
                    CostUsd = 1m,
                    RawPayload = "{}",
                    EventKey = eventKey,
                    OccurredAtUtc = occurredAtUtc,
                    SourceId = UsageSourceIds.LegacyApi,
                    SourceKind = "legacy",
                    UsageScope = "unknown",
                    CostBasis = "unknown",
                },
                TestContext.Current.CancellationToken
            );
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
                .GetProperty("id")
                .GetGuid();
        }

        // The unprefixed key is stored provider-prefixed on each provider.
        rows[("openai", rawKey)] = await Post("openai", rawKey);
        rows[("google", rawKey)] = await Post("google", rawKey);

        // Re-posting the stored (already-prefixed) form addresses the same row — the stored-form
        // prefix is idempotent, so no "OpenAI:OpenAI:..." twin is created.
        var repost = await client.PostAsJsonAsync(
            "/api/events",
            new
            {
                Provider = "openai",
                Model = "shared-model",
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = 1m,
                RawPayload = "{}",
                EventKey = $"OpenAI:{rawKey}",
                OccurredAtUtc = occurredAtUtc,
                SourceId = UsageSourceIds.LegacyApi,
                SourceKind = "legacy",
                UsageScope = "unknown",
                CostBasis = "unknown",
            },
            TestContext.Current.CancellationToken
        );
        repost.StatusCode.Should().Be(HttpStatusCode.OK);
        var repostJson = await repost.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        repostJson.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        repostJson.GetProperty("id").GetGuid().Should().Be(rows[("openai", rawKey)]);

        // Another provider's prefix is not its own, so it still stores a distinct row.
        rows[("google", $"OpenAI:{rawKey}")] = await Post("google", $"OpenAI:{rawKey}");

        // PATCH accepts either the raw or the stored form for the same legacy row.
        var patches = new[]
        {
            (Provider: "openai", EventKey: rawKey, CostUsd: 2m),
            (Provider: "openai", EventKey: $"OpenAI:{rawKey}", CostUsd: 4m),
            (Provider: "google", EventKey: $"OpenAI:{rawKey}", CostUsd: 3m),
        };
        foreach (var patch in patches)
        {
            (
                await client.PatchAsJsonAsync(
                    $"/api/events/{patch.EventKey}/cost?provider={patch.Provider}",
                    new { patch.CostUsd },
                    TestContext.Current.CancellationToken
                )
            )
                .StatusCode.Should()
                .Be(HttpStatusCode.OK);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var storedCosts = await scope
            .ServiceProvider.GetRequiredService<AiObservatoryDbContext>()
            .UsageEvents.AsNoTracking()
            .Where(e => rows.Values.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.CostUsd, TestContext.Current.CancellationToken);

        storedCosts.Should().HaveCount(3);
        storedCosts[rows[("openai", rawKey)]].Should().Be(4m);
        storedCosts[rows[("google", rawKey)]].Should().Be(1m);
        storedCosts[rows[("google", $"OpenAI:{rawKey}")]].Should().Be(3m);
    }

    [Fact]
    public async Task PostEvent_WhenProvenanceIsOmitted_RejectsTheWrite()
    {
        using var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync(
            "/api/events",
            new
            {
                Provider = "anthropic",
                Model = "claude-sonnet-4-6",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
                RawPayload = "{}",
                EventKey = $"waf-legacy-provenance-{Guid.NewGuid():N}",
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should()
            .Contain("provenance");
    }

    [Fact]
    public async Task PostEvent_WhenProvenanceIsExplicit_NormalizesAndRoundTripsIt()
    {
        using var client = factory.CreateAdminClient();
        var observedAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 10,
            OutputTokens = 2,
            CostUsd = 0.01m,
            RawPayload = "{}",
            EventKey = $"waf-explicit-provenance-{Guid.NewGuid():N}",
            SourceId = "  CoDeX-LoCaL  ",
            SourceKind = "lOcAlTeLeMeTrY",
            UsageScope = "SUBSCRIPTION",
            CostBasis = "nOtIoNaL",
            ObservedAtUtc = observedAt,
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored.GetProperty("sourceId").GetString().Should().Be(UsageSourceIds.CodexLocal);
        stored.GetProperty("sourceKind").GetString().Should().Be("localTelemetry");
        stored.GetProperty("usageScope").GetString().Should().Be("subscription");
        stored.GetProperty("costBasis").GetString().Should().Be("notional");
        stored.GetProperty("observedAt").GetDateTimeOffset().Should().Be(observedAt);
    }

    [Fact]
    public async Task PostEvent_ZeroLocalCorrectionStoresKnownZeroWithoutPricingDimensions()
    {
        using var client = factory.CreateAdminClient();
        var model = $"removed-snapshot-{Guid.NewGuid():N}";
        var eventKey = $"codex:tombstone:{Guid.NewGuid():N}";
        var occurredAt = factory
            .Services.GetRequiredService<NodaTime.IClock>()
            .GetCurrentInstant()
            .Minus(NodaTime.Duration.FromMinutes(1))
            .ToDateTimeOffset();
        var response = await client.PostAsJsonAsync(
            "/api/events",
            new
            {
                Provider = "openai",
                Model = model,
                InputTokens = 0,
                OutputTokens = 0,
                CacheReadTokens = 0,
                CacheWriteTokens = 0,
                CacheWrite1hTokens = 0,
                ThoughtTokens = 0,
                CostUsd = 99m,
                RawPayload = "{\"source\":\"observatory-sweep\",\"tombstone\":true}",
                EventKey = eventKey,
                OccurredAtUtc = occurredAt,
                SourceId = UsageSourceIds.CodexLocal,
                SourceKind = "localTelemetry",
                UsageScope = "subscription",
                CostBasis = "notional",
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var stored = await db
            .UsageEvents.AsNoTracking()
            .SingleAsync(e => e.EventKey == eventKey, TestContext.Current.CancellationToken);
        var aggregate = await db
            .DailyAggregates.AsNoTracking()
            .SingleAsync(a => a.Model == model, TestContext.Current.CancellationToken);
        stored.CostUsd.Should().Be(0m);
        stored.CacheSavingsUsd.Should().Be(0m);
        aggregate.CostUsd.Should().Be(0m);
        aggregate.CacheSavingsUsd.Should().Be(0m);
        aggregate.UnknownCostCount.Should().Be(0);
        aggregate.UnknownCacheSavingsCount.Should().Be(0);
    }

    [Theory]
    [InlineData("SourceKind", "not-a-kind")]
    [InlineData("UsageScope", "0")]
    [InlineData("CostBasis", "not-a-basis")]
    public async Task PostEvent_WhenAProvenanceEnumIsInvalid_ReturnsBadRequest(string field, string value)
    {
        using var client = factory.CreateAdminClient();
        var body = new Dictionary<string, object?>
        {
            ["Provider"] = "openai",
            ["Model"] = "gpt-5.4",
            ["InputTokens"] = 1,
            ["OutputTokens"] = 1,
            ["RawPayload"] = "{}",
            ["SourceId"] = UsageSourceIds.OpenAiUsageApi,
            ["SourceKind"] = "providerApi",
            ["UsageScope"] = "api",
            ["CostBasis"] = "unknown",
            [field] = value,
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenSourceIdExceedsStorageBoundary_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            RawPayload = "{}",
            SourceId = new string('s', 101),
            SourceKind = "providerApi",
            UsageScope = "api",
            CostBasis = "unknown",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenObservedAtIsInTheFuture_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var now = factory.Services.GetRequiredService<NodaTime.IClock>().GetCurrentInstant();
        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            RawPayload = "{}",
            ObservedAtUtc = now.Plus(NodaTime.Duration.FromHours(1)).ToDateTimeOffset(),
            SourceId = UsageSourceIds.OpenAiUsageApi,
            SourceKind = "providerApi",
            UsageScope = "api",
            CostBasis = "unknown",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenSnapshotChanges_ReturnsCorrectedWithoutDuplicate()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-corrected-{Guid.NewGuid():N}";
        var first = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            RawPayload = "{}",
            EventKey = key,
            SourceId = UsageSourceIds.CodexLocal,
            SourceKind = "localTelemetry",
            UsageScope = "subscription",
            CostBasis = "notional",
        };
        var corrected = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 2,
            OutputTokens = 1,
            RawPayload = "{}",
            EventKey = key,
            SourceId = UsageSourceIds.CodexLocal,
            SourceKind = "localTelemetry",
            UsageScope = "subscription",
            CostBasis = "notional",
        };

        (await client.PostAsJsonAsync("/api/events", first, TestContext.Current.CancellationToken))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var response = await client.PostAsJsonAsync("/api/events", corrected, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("duplicate").GetBoolean().Should().BeFalse();
        json.GetProperty("corrected").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PostLocalTelemetry_RefreshesSourceStateForCreatedCorrectedAndUnchangedSnapshots()
    {
        await using (var cleanupScope = factory.Services.CreateAsyncScope())
        {
            await cleanupScope
                .ServiceProvider.GetRequiredService<AiObservatoryDbContext>()
                .SourceSyncStates.Where(x => x.SourceId == UsageSourceIds.CodexLocal)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient();
        var key = $"codex:2026-08-24:gpt-5.4:{Guid.NewGuid():N}";
        var now = NodaTime.Instant.FromUnixTimeSeconds(
            factory.Services.GetRequiredService<NodaTime.IClock>().GetCurrentInstant().ToUnixTimeSeconds()
        );
        object Snapshot(long inputTokens, NodaTime.Instant observedAt) =>
            new
            {
                Provider = "openai",
                Model = "gpt-5.4",
                InputTokens = inputTokens,
                OutputTokens = 2,
                RawPayload = "{}",
                EventKey = key,
                OccurredAtUtc = (now - NodaTime.Duration.FromHours(1)).ToDateTimeOffset(),
                SourceId = UsageSourceIds.CodexLocal,
                SourceKind = "localTelemetry",
                UsageScope = "subscription",
                CostBasis = "notional",
                ObservedAtUtc = observedAt.ToDateTimeOffset(),
            };

        (
            await client.PostAsJsonAsync(
                "/api/events",
                Snapshot(1, now - NodaTime.Duration.FromMinutes(3)),
                TestContext.Current.CancellationToken
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var corrected = await client.PostAsJsonAsync(
            "/api/events",
            Snapshot(2, now - NodaTime.Duration.FromMinutes(2)),
            TestContext.Current.CancellationToken
        );
        corrected.StatusCode.Should().Be(HttpStatusCode.OK);
        (await corrected.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("corrected")
            .GetBoolean()
            .Should()
            .BeTrue();
        var unchanged = await client.PostAsJsonAsync(
            "/api/events",
            Snapshot(2, now - NodaTime.Duration.FromMinutes(1)),
            TestContext.Current.CancellationToken
        );
        unchanged.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unchanged.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("duplicate")
            .GetBoolean()
            .Should()
            .BeTrue();

        await using var scope = factory.Services.CreateAsyncScope();
        var state = await scope
            .ServiceProvider.GetRequiredService<AiObservatoryDbContext>()
            .SourceSyncStates.AsNoTracking()
            .SingleAsync(x => x.SourceId == UsageSourceIds.CodexLocal, TestContext.Current.CancellationToken);
        state.IsConfigured.Should().BeTrue();
        state.IsAvailable.Should().BeTrue();
        state.ExpectedRefreshIntervalSeconds.Should().Be(86_400);
        state.LatestObservationAt.Should().Be(now - NodaTime.Duration.FromMinutes(1));
        state.ConsecutiveFailureCount.Should().Be(0);
    }

    [Fact]
    public async Task PatchEventCost_WhenSourceIdIsSupplied_UpdatesOnlyThatSourceIdentity()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-source-patch-{Guid.NewGuid():N}";
        async Task<Guid> Post(string? sourceId)
        {
            var body = new
            {
                Provider = "openai",
                Model = "gpt-5.4",
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = 1m,
                RawPayload = "{}",
                EventKey = key,
                SourceId = sourceId ?? UsageSourceIds.LegacyApi,
                SourceKind = sourceId is null ? "legacy" : "localTelemetry",
                UsageScope = sourceId is null ? "unknown" : "subscription",
                CostBasis = sourceId is null ? "unknown" : "notional",
            };
            var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            return (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
                .GetProperty("id")
                .GetGuid();
        }

        var legacyId = await Post(null);
        var localId = await Post(UsageSourceIds.CodexLocal);
        var patch = await client.PatchAsJsonAsync(
            $"/api/events/{key}/cost?provider=openai&sourceId={UsageSourceIds.CodexLocal}",
            new { CostUsd = 2m },
            TestContext.Current.CancellationToken
        );

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement>($"/api/events/{legacyId}", TestContext.Current.CancellationToken))
            .GetProperty("costUsd")
            .GetDecimal()
            .Should()
            .Be(1m);
        (await client.GetFromJsonAsync<JsonElement>($"/api/events/{localId}", TestContext.Current.CancellationToken))
            .GetProperty("costUsd")
            .GetDecimal()
            .Should()
            .Be(2m);
    }

    [Fact]
    public async Task PatchEventCost_WhenSourceIdIsOmitted_TargetsLegacyOnlyAndMissesNonLegacyEvent()
    {
        // AR-7: the PATCH lookup is source-scoped and an omitted sourceId defaults to the
        // legacy source identity, so omitting it for a non-legacy event is a 404 miss —
        // never a patch of the row that happens to share the eventKey.
        using var client = factory.CreateAdminClient();
        var key = $"waf-source-patch-miss-{Guid.NewGuid():N}";
        var created = await client.PostAsJsonAsync(
            "/api/events",
            new
            {
                Provider = "openai",
                Model = "gpt-5.4",
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = 1m,
                RawPayload = "{}",
                EventKey = key,
                SourceId = UsageSourceIds.CodexLocal,
                SourceKind = "localTelemetry",
                UsageScope = "subscription",
                CostBasis = "unknown",
            },
            TestContext.Current.CancellationToken
        );
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var localId = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();

        var patch = await client.PatchAsJsonAsync(
            $"/api/events/{key}/cost?provider=openai",
            new { CostUsd = 2m },
            TestContext.Current.CancellationToken
        );

        patch.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<JsonElement>($"/api/events/{localId}", TestContext.Current.CancellationToken))
            .GetProperty("costUsd")
            .GetDecimal()
            .Should()
            .Be(1m);
    }

    [Fact]
    public async Task PostEvent_WhenLegacyProvenanceIsExplicit_PreservesClientCost()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-test-cost-{Guid.NewGuid():N}";

        var body = new
        {
            Provider = "anthropic",
            Model = "claude-sonnet-5",
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            CacheReadTokens = 1_000_000,
            CacheWriteTokens = 1_000_000,
            CostUsd = 999.99m,
            RawPayload = "{}",
            EventKey = key,
            OccurredAtUtc = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
            SourceId = UsageSourceIds.LegacyApi,
            SourceKind = "legacy",
            UsageScope = "unknown",
            CostBasis = "unknown",
        };

        var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored.GetProperty("costUsd").GetDecimal().Should().Be(999.99m);
        stored.GetProperty("costBasis").GetString().Should().Be("unknown");
    }

    [Fact]
    public async Task PostEvent_WhenEstimateCannotBeResolved_IgnoresClientCostAndStoresUnknown()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-test-cost-1h-{Guid.NewGuid():N}";

        var body = new
        {
            Provider = "anthropic",
            Model = "claude-sonnet-5",
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            CacheReadTokens = 1_000_000,
            CacheWriteTokens = 1_000_000,
            CacheWrite1hTokens = 1_000_000, // the entire write is one-hour TTL
            CostUsd = 0m,
            RawPayload = "{}",
            SourceId = UsageSourceIds.AnthropicUsageApi,
            SourceKind = "providerApi",
            UsageScope = "api",
            CostBasis = "listPriceEstimate",
            EventKey = key,
            OccurredAtUtc = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
        };

        var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored.GetProperty("costUsd").ValueKind.Should().Be(JsonValueKind.Null);
        stored.GetProperty("cacheSavingsUsd").ValueKind.Should().Be(JsonValueKind.Null);
        stored.GetProperty("costBasis").GetString().Should().Be("listPriceEstimate");
    }

    [Fact]
    public async Task PostEvent_WhenExplicitlyBilled_RequiresTheSpendPath()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            CostUsd = 1m,
            RawPayload = "{}",
            SourceId = UsageSourceIds.OpenAiUsageApi,
            SourceKind = "providerApi",
            UsageScope = "api",
            CostBasis = "billed",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should()
            .Contain("spend/billing path");
    }

    /// <summary>
    /// The one-hour count is a subset of the total, so an over-large one is a malformed
    /// request rather than something to silently clamp at the boundary.
    /// </summary>
    [Fact]
    public async Task PostEvent_WhenCacheWrite1hExceedsCacheWrite_IsRejected()
    {
        using var client = factory.CreateAdminClient();

        var body = new
        {
            Provider = "anthropic",
            Model = "claude-sonnet-5",
            InputTokens = 0,
            OutputTokens = 0,
            CacheReadTokens = 0,
            CacheWriteTokens = 1_000,
            CacheWrite1hTokens = 1_001,
            CostUsd = 0m,
            RawPayload = "{}",
            EventKey = $"waf-test-cost-1h-bad-{Guid.NewGuid():N}",
            OccurredAtUtc = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
            SourceId = UsageSourceIds.AnthropicUsageApi,
            SourceKind = "providerApi",
            UsageScope = "api",
            CostBasis = "unknown",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Explicit unknown-basis events retain their supplied cost because the API cannot
    /// legitimately reprice evidence with no declared estimate basis.
    /// </summary>
    [Fact]
    public async Task PostEvent_WhenCostBasisIsUnknown_KeepsSuppliedCost()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-test-cost-other-{Guid.NewGuid():N}";

        var body = new
        {
            Provider = "openai",
            Model = "gpt-5.4",
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 42.5m,
            RawPayload = "{}",
            EventKey = key,
            SourceId = UsageSourceIds.LegacyApi,
            SourceKind = "legacy",
            UsageScope = "unknown",
            CostBasis = "unknown",
        };

        var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored.GetProperty("costUsd").GetDecimal().Should().Be(42.5m);
    }

    [Fact]
    public async Task PostEvent_PersistsTelemetryIdentityThoughtsAndUnknownCost()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Runtime = "codex",
            SessionId = "session-42",
            AgentId = "main",
            Model = "gpt-5.4",
            InputTokens = 100,
            OutputTokens = 50,
            CacheReadTokens = 20,
            CacheWriteTokens = 10,
            CacheWrite1hTokens = 0,
            ThoughtTokens = 30,
            CostUsd = (decimal?)null,
            RawPayload = "{}",
            EventKey = "openai:codex:session-42:main:gpt-5.4:100:50:20:10:0:30",
            SourceId = UsageSourceIds.CodexLocal,
            SourceKind = "localTelemetry",
            UsageScope = "subscription",
            CostBasis = "notional",
        };

        var created = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();
        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events/{id}",
            TestContext.Current.CancellationToken
        );

        stored.GetProperty("runtime").GetString().Should().Be("codex");
        stored.GetProperty("sessionId").GetString().Should().Be("session-42");
        stored.GetProperty("agentId").GetString().Should().Be("main");
        stored.GetProperty("thoughtTokens").GetInt64().Should().Be(30);
        stored.GetProperty("costUsd").ValueKind.Should().Be(JsonValueKind.Null);

        var events = await client.GetFromJsonAsync<JsonElement>(
            "/api/events?provider=openai",
            TestContext.Current.CancellationToken
        );
        var listed = events.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        listed.GetProperty("runtime").GetString().Should().Be("codex");
        listed.GetProperty("sessionId").GetString().Should().Be("session-42");
        listed.GetProperty("agentId").GetString().Should().Be("main");
        listed.GetProperty("thoughtTokens").GetInt64().Should().Be(30);
        listed.GetProperty("costUsd").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PostEvent_WhenThoughtTokensAreNegative_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Runtime = "codex",
            SessionId = "session-42",
            AgentId = "main",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            ThoughtTokens = -1,
            CostUsd = 0.01m,
            RawPayload = "{}",
            SourceId = UsageSourceIds.CodexLocal,
            SourceKind = "localTelemetry",
            UsageScope = "subscription",
            CostBasis = "notional",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(100, HttpStatusCode.Created)]
    [InlineData(101, HttpStatusCode.BadRequest)]
    public async Task PostEvent_EnforcesTheRuntimeStorageBoundary(int runtimeLength, HttpStatusCode expectedStatus)
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "openai",
            Runtime = new string('r', runtimeLength),
            SessionId = "session-42",
            AgentId = "main",
            Model = "gpt-5.4",
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = (long?)null,
            CacheWriteTokens = (long?)null,
            CacheWrite1hTokens = (long?)null,
            ThoughtTokens = (long?)null,
            CostUsd = 0.01m,
            RawPayload = "{}",
            SourceId = UsageSourceIds.CodexLocal,
            SourceKind = "localTelemetry",
            UsageScope = "subscription",
            CostBasis = "notional",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task PostEvent_RejectsOneHourCacheWriteWithoutATotalCacheWrite()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = "anthropic",
            Model = "claude-sonnet-5",
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = (long?)null,
            CacheWriteTokens = (long?)null,
            CacheWrite1hTokens = 1L,
            CostUsd = 0m,
            RawPayload = "{}",
            SourceId = UsageSourceIds.AnthropicUsageApi,
            SourceKind = "providerApi",
            UsageScope = "api",
            CostBasis = "unknown",
        };

        var response = await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetLocalSnapshots_ReturnsOnlyTheExactSourceWithoutRawPayload()
    {
        using var client = factory.CreateAdminClient();
        var key = $"codex:2026-08-24:gpt-5.4:{Guid.NewGuid():N}";
        var occurredAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        static object Snapshot(
            string sourceId,
            string eventKey,
            DateTimeOffset occurredAtUtc,
            string sourceKind = "localTelemetry"
        ) =>
            new
            {
                Provider = "openai",
                Model = "gpt-5.4",
                InputTokens = 30,
                OutputTokens = 5,
                CacheReadTokens = 3,
                CacheWriteTokens = 2,
                CacheWrite1hTokens = 1,
                ThoughtTokens = 4,
                CostUsd = 0.01m,
                RawPayload = "{\"private\":\"transcript evidence\"}",
                EventKey = eventKey,
                OccurredAtUtc = occurredAtUtc,
                Runtime = "codex",
                SourceId = sourceId,
                SourceKind = sourceKind,
                UsageScope = "subscription",
                CostBasis = "notional",
            };

        (
            await client.PostAsJsonAsync(
                "/api/events",
                Snapshot(UsageSourceIds.CodexLocal, key, occurredAt),
                TestContext.Current.CancellationToken
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        (
            await client.PostAsJsonAsync(
                "/api/events",
                Snapshot(UsageSourceIds.KimiLocal, $"kimi:{Guid.NewGuid():N}", occurredAt),
                TestContext.Current.CancellationToken
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var nonLocalKey = $"codex:legacy:{Guid.NewGuid():N}";
        (
            await client.PostAsJsonAsync(
                "/api/events",
                Snapshot(UsageSourceIds.CodexLocal, nonLocalKey, occurredAt, "legacy"),
                TestContext.Current.CancellationToken
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var tombstoneKey = $"codex:tombstone:{Guid.NewGuid():N}";
        (
            await client.PostAsJsonAsync(
                "/api/events",
                new
                {
                    Provider = "openai",
                    Model = "gpt-5.4",
                    InputTokens = 0,
                    OutputTokens = 0,
                    CacheReadTokens = 0,
                    CacheWriteTokens = 0,
                    CacheWrite1hTokens = 0,
                    ThoughtTokens = 0,
                    CostUsd = 0m,
                    RawPayload = "{\"source\":\"observatory-sweep\",\"tool\":\"codex-cli\",\"tombstone\":true,\"reason\":\"removed\"}",
                    EventKey = tombstoneKey,
                    OccurredAtUtc = occurredAt,
                    Runtime = "codex-cli",
                    SourceId = UsageSourceIds.CodexLocal,
                    SourceKind = "localTelemetry",
                    UsageScope = "subscription",
                    CostBasis = "notional",
                },
                TestContext.Current.CancellationToken
            )
        ).StatusCode.Should().Be(HttpStatusCode.Created);
        var zeroKey = $"codex:zero:{Guid.NewGuid():N}";
        (
            await client.PostAsJsonAsync(
                "/api/events",
                new
                {
                    Provider = "openai",
                    Model = "gpt-5.4",
                    InputTokens = 0,
                    OutputTokens = 0,
                    CacheReadTokens = 0,
                    CacheWriteTokens = 0,
                    CacheWrite1hTokens = 0,
                    ThoughtTokens = 0,
                    CostUsd = (decimal?)null,
                    RawPayload = "{\"source\":\"observatory-sweep\",\"tombstone\":false}",
                    EventKey = zeroKey,
                    OccurredAtUtc = occurredAt,
                    Runtime = "codex",
                    SourceId = UsageSourceIds.CodexLocal,
                    SourceKind = "localTelemetry",
                    UsageScope = "subscription",
                    CostBasis = "notional",
                },
                TestContext.Current.CancellationToken
            )
        ).StatusCode.Should().Be(HttpStatusCode.Created);
        var thoughtOnlyKey = $"codex:thought-only:{Guid.NewGuid():N}";
        (
            await client.PostAsJsonAsync(
                "/api/events",
                new
                {
                    Provider = "openai",
                    Model = "gpt-5.4",
                    InputTokens = 0,
                    OutputTokens = 0,
                    CacheReadTokens = 0,
                    CacheWriteTokens = 0,
                    CacheWrite1hTokens = 0,
                    ThoughtTokens = 7,
                    CostUsd = (decimal?)null,
                    RawPayload = "{\"source\":\"thought-only\",\"tombstone\":true}",
                    EventKey = thoughtOnlyKey,
                    OccurredAtUtc = occurredAt,
                    Runtime = "codex",
                    SourceId = UsageSourceIds.CodexLocal,
                    SourceKind = "localTelemetry",
                    UsageScope = "subscription",
                    CostBasis = "notional",
                },
                TestContext.Current.CancellationToken
            )
        ).StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await client.GetAsync(
            $"/api/events/local-snapshots?sourceId={UsageSourceIds.CodexLocal}",
            TestContext.Current.CancellationToken
        );
        var inventory = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = inventory.EnumerateArray().Single(x => x.GetProperty("eventKey").GetString() == key);
        item.GetProperty("provider").GetString().Should().Be("openai");
        item.GetProperty("sourceId").GetString().Should().Be(UsageSourceIds.CodexLocal);
        item.GetProperty("sourceKind").GetString().Should().Be("localTelemetry");
        item.GetProperty("occurredAtUtc").GetDateTimeOffset().Should().Be(occurredAt);
        item.GetProperty("hasCost").GetBoolean().Should().BeFalse();
        item.TryGetProperty("inputTokens", out _).Should().BeFalse();
        item.TryGetProperty("rawPayload", out _).Should().BeFalse();
        inventory
            .EnumerateArray()
            .Should()
            .NotContain(x => x.GetProperty("sourceId").GetString() == UsageSourceIds.KimiLocal);
        inventory.EnumerateArray().Should().NotContain(x => x.GetProperty("eventKey").GetString() == nonLocalKey);
        inventory.EnumerateArray().Should().NotContain(x => x.GetProperty("eventKey").GetString() == tombstoneKey);
        inventory.EnumerateArray().Should().Contain(x => x.GetProperty("eventKey").GetString() == zeroKey);
        inventory.EnumerateArray().Should().Contain(x => x.GetProperty("eventKey").GetString() == thoughtOnlyKey);
    }

    // A None basis asserts the event carries no cost; a positive CostUsd on the same write is a
    // contradiction and must be rejected rather than stored (CostBasis.None check).
    [Fact]
    public async Task PostEvent_WhenNoneBasisCarriesPositiveCost_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync(
            "/api/events",
            new
            {
                Provider = "openai",
                Model = "gpt-5.4",
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = 0.01m,
                RawPayload = "{}",
                EventKey = $"waf-none-basis-cost-{Guid.NewGuid():N}",
                SourceId = UsageSourceIds.CodexLocal,
                SourceKind = "localTelemetry",
                UsageScope = "subscription",
                CostBasis = "none",
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_WhenNoneBasisCarriesNoCost_IsAccepted()
    {
        using var client = factory.CreateAdminClient();
        var response = await client.PostAsJsonAsync(
            "/api/events",
            new
            {
                Provider = "openai",
                Model = "gpt-5.4",
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = (decimal?)null,
                RawPayload = "{}",
                EventKey = $"waf-none-basis-free-{Guid.NewGuid():N}",
                SourceId = UsageSourceIds.CodexLocal,
                SourceKind = "localTelemetry",
                UsageScope = "subscription",
                CostBasis = "none",
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
