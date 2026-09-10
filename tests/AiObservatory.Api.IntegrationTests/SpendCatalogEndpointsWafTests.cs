using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public class SpendCatalogEndpointsWafTests(AiObservatoryApiFactory factory) : IClassFixture<AiObservatoryApiFactory>
{
    private static object NewCategory(string key) =>
        new
        {
            Key = key,
            DisplayName = "Code Review",
            ColorVar = "--spend-code-review",
            SortOrder = 10,
        };

    private static object NewVendor(string key) =>
        new
        {
            Key = key,
            DisplayName = "Anthropic",
            Provider = (string?)null,
        };

    [Fact]
    public async Task PostCategory_CreatesAndListsIt()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync(
            "/api/spend/categories",
            NewCategory(key),
            TestContext.Current.CancellationToken
        );
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await client.GetFromJsonAsync<JsonElement>(
            "/api/spend/categories",
            TestContext.Current.CancellationToken
        );
        list.EnumerateArray().Select(c => c.GetProperty("key").GetString()).Should().Contain(key);
    }

    [Fact]
    public async Task PostCategory_WithDuplicateKey_IsRejected()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/api/spend/categories", NewCategory(key), TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync(
            "/api/spend/categories",
            NewCategory(key),
            TestContext.Current.CancellationToken
        );

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PostVendor_WithUnknownProvider_IsRejected()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/spend/vendors",
            new
            {
                Key = $"v-{Guid.NewGuid():N}",
                DisplayName = "Nope",
                Provider = "not-a-provider",
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostVendor_WithNullProvider_IsAccepted()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/spend/vendors",
            new
            {
                Key = $"v-{Guid.NewGuid():N}",
                DisplayName = "CodeRabbit",
                Provider = (string?)null,
            },
            TestContext.Current.CancellationToken
        );

        response
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, "vendors with no token estimate are the point of a separate vendor axis");
    }

    [Fact]
    public async Task ArchivedCategory_IsExcludedFromTheDefaultList()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync(
            "/api/spend/categories",
            NewCategory(key),
            TestContext.Current.CancellationToken
        );
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();

        var patch = await client.PatchAsJsonAsync(
            $"/api/spend/categories/{id}",
            new { Archived = true },
            TestContext.Current.CancellationToken
        );
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<JsonElement>(
            "/api/spend/categories",
            TestContext.Current.CancellationToken
        );
        list.EnumerateArray().Select(c => c.GetProperty("key").GetString()).Should().NotContain(key);

        var all = await client.GetFromJsonAsync<JsonElement>(
            "/api/spend/categories?includeArchived=true",
            TestContext.Current.CancellationToken
        );
        all.EnumerateArray()
            .Select(c => c.GetProperty("key").GetString())
            .Should()
            .Contain(key, "history still references archived categories");
    }

    [Fact]
    public async Task PostCategory_WithNonSlugKey_IsRejected()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/spend/categories",
            new
            {
                Key = "Not A Slug!",
                DisplayName = "Code Review",
                ColorVar = "--spend-code-review",
                SortOrder = 10,
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostCategory_WithOverlongDisplayName_IsRejected()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(
            "/api/spend/categories",
            new
            {
                Key = key,
                DisplayName = new string('x', 101),
                ColorVar = "--spend-code-review",
                SortOrder = 10,
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostVendor_WithUnknownDefaultCategory_IsRejected()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/spend/vendors",
            new
            {
                Key = $"v-{Guid.NewGuid():N}",
                DisplayName = "Ghost Vendor",
                Provider = (string?)null,
                DefaultCategoryId = Guid.NewGuid(),
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchCategory_WithUnknownId_IsNotFound()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PatchAsJsonAsync(
            $"/api/spend/categories/{Guid.NewGuid()}",
            new { DisplayName = "Anything" },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchCategory_Unarchiving_RestoresItToTheList()
    {
        using var client = factory.CreateAdminClient();
        var key = $"cat-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync(
            "/api/spend/categories",
            NewCategory(key),
            TestContext.Current.CancellationToken
        );
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();

        await client.PatchAsJsonAsync(
            $"/api/spend/categories/{id}",
            new { Archived = true },
            TestContext.Current.CancellationToken
        );

        var unarchive = await client.PatchAsJsonAsync(
            $"/api/spend/categories/{id}",
            new { Archived = false },
            TestContext.Current.CancellationToken
        );
        unarchive.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<JsonElement>(
            "/api/spend/categories",
            TestContext.Current.CancellationToken
        );
        list.EnumerateArray()
            .Select(c => c.GetProperty("key").GetString())
            .Should()
            .Contain(key, "un-archiving restores it to the default (non-archived) list");
    }

    [Fact]
    public async Task PostVendor_WithDuplicateKey_IsRejected()
    {
        using var client = factory.CreateAdminClient();
        var key = $"v-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/api/spend/vendors", NewVendor(key), TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync(
            "/api/spend/vendors",
            NewVendor(key),
            TestContext.Current.CancellationToken
        );

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ArchivedVendor_IsExcludedFromTheDefaultList()
    {
        using var client = factory.CreateAdminClient();
        var key = $"v-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync(
            "/api/spend/vendors",
            NewVendor(key),
            TestContext.Current.CancellationToken
        );
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();

        var patch = await client.PatchAsJsonAsync(
            $"/api/spend/vendors/{id}",
            new { Archived = true },
            TestContext.Current.CancellationToken
        );
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<JsonElement>(
            "/api/spend/vendors",
            TestContext.Current.CancellationToken
        );
        list.EnumerateArray().Select(v => v.GetProperty("key").GetString()).Should().NotContain(key);

        var all = await client.GetFromJsonAsync<JsonElement>(
            "/api/spend/vendors?includeArchived=true",
            TestContext.Current.CancellationToken
        );
        all.EnumerateArray()
            .Select(v => v.GetProperty("key").GetString())
            .Should()
            .Contain(key, "history still references archived vendors");
    }

    [Fact]
    public async Task PatchVendor_Unarchiving_RestoresItToTheList()
    {
        using var client = factory.CreateAdminClient();
        var key = $"v-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync(
            "/api/spend/vendors",
            NewVendor(key),
            TestContext.Current.CancellationToken
        );
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id")
            .GetGuid();

        await client.PatchAsJsonAsync(
            $"/api/spend/vendors/{id}",
            new { Archived = true },
            TestContext.Current.CancellationToken
        );

        var unarchive = await client.PatchAsJsonAsync(
            $"/api/spend/vendors/{id}",
            new { Archived = false },
            TestContext.Current.CancellationToken
        );
        unarchive.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<JsonElement>(
            "/api/spend/vendors",
            TestContext.Current.CancellationToken
        );
        list.EnumerateArray()
            .Select(v => v.GetProperty("key").GetString())
            .Should()
            .Contain(key, "un-archiving restores it to the default (non-archived) list");
    }

    [Fact]
    public async Task PatchVendor_RenamesAndRepointsDefaultCategory()
    {
        using var client = factory.CreateAdminClient();
        var categoryKey = $"cat-{Guid.NewGuid():N}";

        var categoryCreated = await client.PostAsJsonAsync(
            "/api/spend/categories",
            NewCategory(categoryKey),
            TestContext.Current.CancellationToken
        );
        var categoryId = (
            await categoryCreated.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)
        )
            .GetProperty("id")
            .GetGuid();

        var vendorCreated = await client.PostAsJsonAsync(
            "/api/spend/vendors",
            new
            {
                Key = $"v-{Guid.NewGuid():N}",
                DisplayName = "Old Name",
                Provider = (string?)null,
            },
            TestContext.Current.CancellationToken
        );
        var vendorId = (
            await vendorCreated.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)
        )
            .GetProperty("id")
            .GetGuid();

        var patch = await client.PatchAsJsonAsync(
            $"/api/spend/vendors/{vendorId}",
            new { DisplayName = "New Name", DefaultCategoryId = categoryId },
            TestContext.Current.CancellationToken
        );
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await patch.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("displayName").GetString().Should().Be("New Name");
        body.GetProperty("defaultCategoryId").GetGuid().Should().Be(categoryId);
    }

    /// <summary>
    /// The tri-state contract on <c>defaultCategoryId</c>. A plain <c>Guid?</c> could not
    /// express it — an omitted property and an explicit null both deserialized to null, so
    /// the clear path did not exist and a vendor's default category was set-once for life.
    /// </summary>
    [Fact]
    public async Task PatchVendor_WithExplicitNullDefaultCategory_ClearsIt()
    {
        using var client = factory.CreateAdminClient();
        var (vendorId, _) = await CreateVendorWithDefaultCategoryAsync(client);

        var patch = await client.PatchAsJsonAsync(
            $"/api/spend/vendors/{vendorId}",
            new { DefaultCategoryId = (Guid?)null },
            TestContext.Current.CancellationToken
        );

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patch.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("defaultCategoryId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PatchVendor_WithoutMentioningDefaultCategory_LeavesItAlone()
    {
        using var client = factory.CreateAdminClient();
        var (vendorId, categoryId) = await CreateVendorWithDefaultCategoryAsync(client);

        // Only DisplayName is sent — the absent defaultCategoryId must not be read as a clear.
        var patch = await client.PatchAsJsonAsync(
            $"/api/spend/vendors/{vendorId}",
            new { DisplayName = "Renamed Only" },
            TestContext.Current.CancellationToken
        );

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patch.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("displayName").GetString().Should().Be("Renamed Only");
        body.GetProperty("defaultCategoryId").GetGuid().Should().Be(categoryId);
    }

    [Fact]
    public async Task PatchVendor_WithUnknownDefaultCategory_IsRejected()
    {
        using var client = factory.CreateAdminClient();
        var (vendorId, categoryId) = await CreateVendorWithDefaultCategoryAsync(client);

        var patch = await client.PatchAsJsonAsync(
            $"/api/spend/vendors/{vendorId}",
            new { DefaultCategoryId = Guid.NewGuid() },
            TestContext.Current.CancellationToken
        );

        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var after = await client.GetFromJsonAsync<JsonElement>(
            $"/api/spend/vendors/{vendorId}",
            TestContext.Current.CancellationToken
        );
        after
            .GetProperty("defaultCategoryId")
            .GetGuid()
            .Should()
            .Be(categoryId, "a rejected patch must not have written anything");
    }

    [Fact]
    public async Task PatchVendor_WithNonGuidDefaultCategory_IsRejected()
    {
        using var client = factory.CreateAdminClient();
        var (vendorId, _) = await CreateVendorWithDefaultCategoryAsync(client);

        using var content = new StringContent(
            """{"defaultCategoryId":"not-a-guid"}""",
            Encoding.UTF8,
            "application/json"
        );
        var patch = await client.PatchAsync(
            $"/api/spend/vendors/{vendorId}",
            content,
            TestContext.Current.CancellationToken
        );

        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The seeded catalog must reach a migrated database intact. Asserted through the API
    /// rather than the model so it covers the migrations too — a HasData edit that never
    /// made it into a migration would pass a model-only check and still leave a deployed
    /// database missing the vendor.
    /// </summary>
    [Theory]
    [InlineData("anthropic", "anthropic")]
    [InlineData("github-actions", null)]
    [InlineData("coderabbit", null)]
    [InlineData("gitar", null)]
    [InlineData("moonshot", "moonshot")]
    // Lower-case, not the camelCase converter's "openAI" — see Provider.OpenAI. The
    // frontend keys its PROVIDERS lookup on this exact string.
    [InlineData("openai", "openai")]
    [InlineData("google", "google")]
    [InlineData("microsoft", null)]
    [InlineData("openrouter", null)]
    [InlineData("blacksmith", null)]
    // Copilot is the one vendor whose tokens the ingest worker already meters but whose
    // billed spend had nowhere to go, so its Provider link is the point of the row.
    [InlineData("copilot", "copilot")]
    public async Task SeededVendor_IsPresentAndCarriesAProviderOnlyWhenTokensAreMetered(
        string key,
        string? expectedProvider
    )
    {
        using var client = factory.CreateAdminClient();

        var vendors = await client.GetFromJsonAsync<JsonElement>(
            "/api/spend/vendors",
            TestContext.Current.CancellationToken
        );

        var vendor = vendors
            .EnumerateArray()
            .Should()
            .ContainSingle(v => v.GetProperty("key").GetString() == key)
            .Which;

        // Provider is the ONLY join between billed spend and the token estimate. Assigning
        // one to a vendor with no tokens (Microsoft, OpenRouter, Blacksmith, CodeRabbit,
        // Gitar, GitHub Actions) would fabricate a variance comparison that was never
        // possible — so the null cases matter at least as much as the set ones.
        var provider = vendor.GetProperty("provider");
        if (expectedProvider is null)
        {
            provider
                .ValueKind.Should()
                .Be(JsonValueKind.Null, $"{key} has no metered tokens, so it must not claim a Provider");
        }
        else
        {
            provider.GetString().Should().Be(expectedProvider);
        }
    }

    [Fact]
    public async Task SeededCategories_CoverEveryKindOfSpendTheLedgerRecords()
    {
        using var client = factory.CreateAdminClient();

        var categories = await client.GetFromJsonAsync<JsonElement>(
            "/api/spend/categories",
            TestContext.Current.CancellationToken
        );

        categories
            .EnumerateArray()
            .Select(c => c.GetProperty("key").GetString())
            .Should()
            .Contain(["code-review", "credits", "ci", "subscription", "cloud"]);
    }

    /// <summary>Creates a category and a vendor already pointed at it.</summary>
    private async Task<(Guid VendorId, Guid CategoryId)> CreateVendorWithDefaultCategoryAsync(HttpClient client)
    {
        var categoryCreated = await client.PostAsJsonAsync(
            "/api/spend/categories",
            NewCategory($"cat-{Guid.NewGuid():N}"),
            TestContext.Current.CancellationToken
        );
        var categoryId = (
            await categoryCreated.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)
        )
            .GetProperty("id")
            .GetGuid();

        var vendorCreated = await client.PostAsJsonAsync(
            "/api/spend/vendors",
            new
            {
                Key = $"v-{Guid.NewGuid():N}",
                DisplayName = "Defaulted Vendor",
                Provider = (string?)null,
                DefaultCategoryId = categoryId,
            },
            TestContext.Current.CancellationToken
        );
        var vendorId = (
            await vendorCreated.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)
        )
            .GetProperty("id")
            .GetGuid();

        return (vendorId, categoryId);
    }
}
