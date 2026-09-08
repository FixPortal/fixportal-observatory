using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiObservatory.Api.Tests.Services;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public class SpendEntriesEndpointsWafTests(AiObservatoryApiFactory factory) : IClassFixture<AiObservatoryApiFactory>
{
    /// <summary>Creates a category and a vendor and returns their ids.</summary>
    private static async Task<(Guid CategoryId, Guid VendorId)> SeedCatalogAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ct = TestContext.Current.CancellationToken;

        var cat = await client.PostAsJsonAsync(
            "/api/spend/categories",
            new
            {
                Key = $"credits-{suffix}",
                DisplayName = "Credits",
                ColorVar = "--c",
                SortOrder = 1,
            },
            ct
        );
        var categoryId = (await cat.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var ven = await client.PostAsJsonAsync(
            "/api/spend/vendors",
            new
            {
                Key = $"anthropic-{suffix}",
                DisplayName = "Anthropic",
                Provider = "anthropic",
            },
            ct
        );
        var vendorId = (await ven.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        return (categoryId, vendorId);
    }

    private static object Entry(
        Guid categoryId,
        Guid vendorId,
        string? entryKey,
        decimal amount = 80m,
        string currency = "GBP",
        string source = "Csv"
    ) =>
        new
        {
            OccurredOn = "2026-07-12",
            VendorId = vendorId,
            CategoryId = categoryId,
            Amount = amount,
            Currency = currency,
            Description = "Top-up",
            Source = source,
            EntryKey = entryKey,
        };

    /// <summary>Posts a single-row batch and returns the created row's id.</summary>
    private static async Task<Guid> CreateEntryAsync(
        HttpClient client,
        Guid categoryId,
        Guid vendorId,
        decimal amount = 80m,
        string currency = "GBP"
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync(
            "/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", amount, currency) },
            ct
        );
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().Single();
        return result.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Wires a stubbed primary HTTP handler onto FxRateProvider's typed client for one
    /// test's host, so FX scenarios (outage, unresolvable currency) are deterministic and
    /// never call out to the real frankfurter.dev. Caller owns disposal of the returned
    /// factory (it spins up its own TestServer, separate from the shared one).
    /// </summary>
    private static WebApplicationFactory<Program> WithStubbedFx(
        AiObservatoryApiFactory outer,
        HttpMessageHandler handler
    ) =>
        outer.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
                s.AddHttpClient<FxRateProvider>().ConfigurePrimaryHttpMessageHandler(() => handler)
            )
        );

    private static HttpClient AdminClient(WebApplicationFactory<Program> stubFactory)
    {
        var client = stubFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Observatory-Key", AiObservatoryApiFactory.AdminKey);
        return client;
    }

    /// <summary>
    /// The single most important test here. Re-posting an identical payload must land
    /// nothing and leave the total untouched — the failure this project has been burned by.
    /// </summary>
    [Fact]
    public async Task RePostingTheSamePayload_LandsNothingAndLeavesTheTotalUnchanged()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var key = $"k-{Guid.NewGuid():N}";
        var payload = new[] { Entry(categoryId, vendorId, key) };

        var first = await client.PostAsJsonAsync("/api/spend/entries", payload, ct);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray()
            .Single()
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("created");

        var totalAfterFirst = await TotalAsync(client, vendorId);

        var second = await client.PostAsJsonAsync("/api/spend/entries", payload, ct);
        (await second.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray()
            .Single()
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("duplicate");

        (await TotalAsync(client, vendorId)).Should().Be(totalAfterFirst, "a duplicate must not move the total");
    }

    [Fact]
    public async Task PostEntry_persists_manual_ledger_provenance()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", source: "Manual") },
            ct
        );
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray()
            .Single()
            .GetProperty("id")
            .GetGuid();

        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        var entry = rows.EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == id);
        entry.GetProperty("sourceId").GetString().Should().Be(UsageSourceIds.ManualLedger);
        entry.GetProperty("sourceKind").GetString().Should().Be("manual");
        entry.GetProperty("usageScope").GetString().Should().Be("unknown");
        entry.GetProperty("costBasis").GetString().Should().Be("billed");
        entry.GetProperty("observedAt").GetString().Should().Be(entry.GetProperty("recordedAt").GetString());
    }

    /// <summary>
    /// RawPayload is the provider billing API response stored verbatim and unredacted, and
    /// <see cref="SpendEntry"/>'s own doc comment says not to surface it without review. The
    /// route used to return the EF entity directly, so it went out on the wire to anyone the
    /// route admitted. It is not in the client's declared contract and nothing renders it.
    /// </summary>
    [Fact]
    public async Task GetEntries_DoesNotPutTheVerbatimProviderPayloadOnTheWire()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        await client.PostAsJsonAsync(
            "/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", source: "Manual") },
            ct
        );

        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);

        var entry = rows.EnumerateArray().Single();
        entry.TryGetProperty("rawPayload", out _).Should().BeFalse();
        // The provenance columns the ledger's consumers do read must survive the projection.
        entry.GetProperty("sourceId").GetString().Should().Be(UsageSourceIds.ManualLedger);
        entry.GetProperty("amountGbp").GetDecimal().Should().NotBe(0);
    }

    /// <summary>
    /// The Spend tab is readonlyHidden in the dashboard, so a share-link viewer is not meant to
    /// reach the row-level ledger at all. Only the group filter guarded this route, and that
    /// admits the readonly key on any GET — so the product intent was enforced in the client
    /// and nowhere else. /spend/reporting is deliberately NOT gated: the Reporting tab is
    /// visible to readonly viewers and returns aggregates only.
    /// </summary>
    [Fact]
    public async Task GetEntries_RejectsTheReadOnlyViewerKey()
    {
        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetAsync("/api/spend/entries", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetReporting_StillAllowsTheReadOnlyViewerKey()
    {
        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetAsync(
            "/api/spend/reporting?from=2019-07-01&to=2019-07-31",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MixedBatch_ReturnsPerRowVerdictsAndLandsOnlyTheGoodRow()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var existingKey = $"k-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/api/spend/entries", new[] { Entry(categoryId, vendorId, existingKey) }, ct);

        var mixed = new[]
        {
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}"), // good
            Entry(categoryId, vendorId, existingKey), // duplicate
            // Zero, not negative: a negative amount is a valid refund since
            // AllowNegativeSpendAmounts, so zero is now the amount that gets rejected.
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", 0m), // rejected: zero
        };

        var response = await client.PostAsJsonAsync("/api/spend/entries", mixed, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var statuses = (await response.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray()
            .Select(r => r.GetProperty("status").GetString())
            .ToArray();

        statuses.Should().Equal("created", "duplicate", "rejected");

        // The status array alone doesn't prove landing was scoped to the one good row --
        // confirm the table itself: the pre-existing row plus exactly one new one.
        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        entries
            .EnumerateArray()
            .Should()
            .HaveCount(2, "only the pre-existing row and the one genuinely new row should have landed");
    }

    [Fact]
    public async Task MissingOccurredOnIsRejectedInsteadOfWritingTheDefaultDate()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/spend/entries",
            new[]
            {
                new
                {
                    VendorId = vendorId,
                    CategoryId = categoryId,
                    Amount = 1m,
                    Currency = "GBP",
                    Source = "Portal",
                },
            },
            ct
        );
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().Single();

        result.GetProperty("status").GetString().Should().Be("rejected");
        result.GetProperty("reason").GetString().Should().Contain("OccurredOn");

        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        entries.EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task ManualEntriesAreNeverDeduplicated()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var payload = new[] { Entry(categoryId, vendorId, entryKey: null, source: "Manual") };

        await client.PostAsJsonAsync("/api/spend/entries", payload, ct);
        var second = await client.PostAsJsonAsync("/api/spend/entries", payload, ct);

        (await second.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray()
            .Single()
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("created", "a person typing the same charge twice is a mistake to show, not silence");
    }

    [Fact]
    public async Task GbpEntryStoresRateOneAndTheSameGbpAmount()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        await client.PostAsJsonAsync(
            "/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}") },
            ct
        );

        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        var entry = entries.EnumerateArray().Single();

        entry.GetProperty("fxRate").GetDecimal().Should().Be(1m);
        entry.GetProperty("amountGbp").GetDecimal().Should().Be(80m);
    }

    [Fact]
    public async Task UnknownVendorIsRejectedRatherThanCreated()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, _) = await SeedCatalogAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/spend/entries",
            new[] { Entry(categoryId, Guid.NewGuid(), $"k-{Guid.NewGuid():N}") },
            ct
        );

        (await response.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray()
            .Single()
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("rejected");
    }

    /// <summary>
    /// After Task 2's review, GetGbpRateOnAsync throws FxUnavailableException for any
    /// currency other than GBP (short-circuit) or USD (static fallback) when the rate
    /// cannot be resolved. Stubbed rather than a live call to frankfurter.dev: a real
    /// network failure and a genuine missing-rate response both land in the same catch, so
    /// a live call could pass this test for the wrong reason (e.g. a DNS blip in CI) rather
    /// than because the endpoint actually translates the exception correctly.
    /// </summary>
    [Fact]
    public async Task AnEntryWhoseFxCannotBeResolvedIsRejectedRatherThanFrozen()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "");
        await using var stubFactory = WithStubbedFx(factory, handler);
        using var client = AdminClient(stubFactory);
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        var batch = new[]
        {
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", 80m, "EUR"), // unresolvable
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", 50m), // unaffected
        };

        var response = await client.PostAsJsonAsync("/api/spend/entries", batch, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().ToArray();

        results[0].GetProperty("status").GetString().Should().Be("rejected");
        results[0].GetProperty("reason").GetString().Should().Contain("EUR");

        // This is what the deviation actually exists to guarantee: one unresolvable
        // currency must not fail the batch -- the untroubled GBP row still lands.
        results[1]
            .GetProperty("status")
            .GetString()
            .Should()
            .Be(
                "created",
                "an unresolvable EUR rate must not stop an unrelated GBP row in the same batch from landing"
            );
    }

    /// <summary>
    /// Injects a side effect into the FX HTTP call for a row so a real, non-unique-violation
    /// DbUpdateException can be forced deterministically: the vendor a later row references
    /// is deleted from Postgres (via a second DbContext scope) at the moment the FX rate is
    /// fetched, so by the time that row's SaveChangesAsync runs the foreign key is dangling.
    /// </summary>
    private sealed class SideEffectHttpMessageHandler(Func<CancellationToken, Task> sideEffect, string body)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await sideEffect(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// Proves the general DbUpdateException catch (added alongside the narrow unique-violation
    /// one): a row that fails for any other database reason must be reported as "rejected" and
    /// must not take the rest of the batch down with it, and — the point Gitar's finding was
    /// really about — an earlier row's "created" verdict must correspond to a row that is
    /// genuinely committed and readable back, not one that got rolled back by the later
    /// unhandled exception.
    ///
    /// A check-constraint or HasMaxLength violation is not reachable through this endpoint:
    /// Validate rejects a zero Amount before the insert (so CK_SpendEntry_Amount_NonZero
    /// can't be reached), FxRateProvider.GetGbpRateOnAsync never returns a
    /// non-positive rate without throwing FxUnavailableException first (so
    /// CK_SpendEntry_FxRate_Positive can't be reached either), and Validate's own length checks
    /// on Currency/Description/EntryKey exactly match the columns' HasMaxLength. A foreign-key
    /// violation is reachable, though: the vendor/category id sets are loaded ONCE up front and
    /// never rechecked per row, so a vendor deleted mid-batch (after the snapshot, before that
    /// row's save) still passes Validate and reaches SaveChangesAsync as a real FK violation --
    /// which is exactly what this test forces.
    /// </summary>
    [Fact]
    public async Task ARowThatFailsForAnyOtherDbReasonIsRejectedAndEarlierRowsSurvive()
    {
        var ct = TestContext.Current.CancellationToken;
        var vendorToDeleteId = Guid.Empty;

        // ReSharper disable once AccessToModifiedClosure
        var handler = new SideEffectHttpMessageHandler(
            async sideEffectCt =>
            {
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
                await db.SpendVendors.Where(v => v.Id == vendorToDeleteId).ExecuteDeleteAsync(sideEffectCt);
            },
            """{"rates":{"GBP":0.8}}"""
        );
        await using var stubFactory = WithStubbedFx(factory, handler);
        using var client = AdminClient(stubFactory);

        var (categoryId, survivingVendorId) = await SeedCatalogAsync(client);

        // A second, unrelated vendor -- not referenced by any row that stays in the DB --
        // so it can actually be deleted (SpendVendor -> SpendEntry is Restrict).
        var vendorResponse = await client.PostAsJsonAsync(
            "/api/spend/vendors",
            new
            {
                Key = $"to-delete-{Guid.NewGuid():N}",
                DisplayName = "ToDelete",
                Provider = "anthropic",
            },
            ct
        );
        vendorToDeleteId = (await vendorResponse.Content.ReadFromJsonAsync<JsonElement>(ct))
            .GetProperty("id")
            .GetGuid();

        var batch = new[]
        {
            // GBP short-circuits before any FX HTTP call, so this row saves and commits
            // before the second row's FX lookup (and side effect) ever runs.
            Entry(categoryId, survivingVendorId, $"k-{Guid.NewGuid():N}"),
            // USD forces the stubbed FX HTTP call, whose side effect deletes this row's
            // vendor out from under it before SaveChangesAsync runs for this row.
            Entry(categoryId, vendorToDeleteId, $"k-{Guid.NewGuid():N}", 50m, "USD"),
        };

        var response = await client.PostAsJsonAsync("/api/spend/entries", batch, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().ToArray();

        results[0].GetProperty("status").GetString().Should().Be("created");
        var survivingId = results[0].GetProperty("id").GetGuid();

        results[1].GetProperty("status").GetString().Should().Be("rejected");
        results[1].GetProperty("reason").GetString().Should().NotBeNullOrEmpty();

        // The point of the finding: the earlier row's "created" verdict must be real, not
        // discarded alongside the response by an unhandled exception from the later row.
        var entries = await client.GetFromJsonAsync<JsonElement>(
            $"/api/spend/entries?vendorId={survivingVendorId}",
            ct
        );
        entries.EnumerateArray().Should().ContainSingle(e => e.GetProperty("id").GetGuid() == survivingId);
    }

    [Fact]
    public async Task PatchWithUnknownVendorIdIsRejectedAndDoesNotMutateTheRow()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId);

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{id}", new { VendorId = Guid.NewGuid() }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        entries
            .EnumerateArray()
            .Should()
            .ContainSingle(
                e => e.GetProperty("id").GetGuid() == id,
                "a rejected patch must leave the original vendor link in place"
            );
    }

    [Fact]
    public async Task PatchWithUnknownCategoryIdIsRejectedAndDoesNotMutateTheRow()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId);

        var response = await client.PatchAsJsonAsync(
            $"/api/spend/entries/{id}",
            new { CategoryId = Guid.NewGuid() },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        entries
            .EnumerateArray()
            .Should()
            .ContainSingle(
                e => e.GetProperty("id").GetGuid() == id,
                "a rejected patch must leave the original category link in place"
            );
    }

    [Fact]
    public async Task PatchWithDefaultOccurredOnIsRejectedAndDoesNotMutateTheRow()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId);

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{id}", new { OccurredOn = "0001-01-01" }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        entries
            .EnumerateArray()
            .Single(e => e.GetProperty("id").GetGuid() == id)
            .GetProperty("occurredOn")
            .GetString()
            .Should()
            .Be("2026-07-12");
    }

    [Fact]
    public async Task PatchAgainstANonExistentEntryIsNotFound()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PatchAsJsonAsync(
            $"/api/spend/entries/{Guid.NewGuid()}",
            new { Description = "Anything" },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchingAmountReResolvesAmountGbpAtTheStoredRate()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId, amount: 80m, currency: "GBP");

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{id}", new { Amount = 100m }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        entry.GetProperty("fxRate").GetDecimal().Should().Be(1m);
        entry
            .GetProperty("amountGbp")
            .GetDecimal()
            .Should()
            .Be(100m, "changing the amount must re-resolve AmountGbp rather than leave the old conversion");
    }

    [Fact]
    public async Task PatchingOccurredOnReResolvesTheConversionForTheNewDate()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.8}}""");
        await using var stubFactory = WithStubbedFx(factory, handler);
        using var client = AdminClient(stubFactory);
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        // USD, not GBP: GBP short-circuits before any FX lookup or cache entry, so it could
        // never prove a fresh lookup happened on the new date.
        var id = await CreateEntryAsync(client, categoryId, vendorId, amount: 80m, currency: "USD");

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{id}", new { OccurredOn = "2026-08-01" }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler
            .Requested.Should()
            .HaveCount(
                2,
                "one FX lookup for the original charge date at creation, a second for the patched date -- "
                    + "a stale cached/frozen rate would mean only one request total"
            );

        var entry = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var amount = entry.GetProperty("amount").GetDecimal();
        var fxRate = entry.GetProperty("fxRate").GetDecimal();
        entry
            .GetProperty("amountGbp")
            .GetDecimal()
            .Should()
            .Be(
                decimal.Round(amount * fxRate, 4),
                "AmountGbp must stay consistent with whatever rate is actually stored, not a stale figure"
            );
    }

    [Fact]
    public async Task PatchingOnlyDescriptionLeavesTheFrozenConversionUntouched()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId, amount: 80m, currency: "GBP");

        var response = await client.PatchAsJsonAsync($"/api/spend/entries/{id}", new { Description = "Renamed" }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        entry.GetProperty("description").GetString().Should().Be("Renamed");
        entry.GetProperty("fxRate").GetDecimal().Should().Be(1m);
        entry
            .GetProperty("amountGbp")
            .GetDecimal()
            .Should()
            .Be(80m, "a description-only patch must not re-resolve FX -- that would defeat the freeze");
    }

    [Fact]
    public async Task DeletingAnEntryReturnsNoContent()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId);

        var response = await client.DeleteAsync($"/api/spend/entries/{id}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeletingAnUnknownIdIsNotFound()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.DeleteAsync(
            $"/api/spend/entries/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// DELETE is the only destructive operation the UI exposes -- prove it removes
    /// exactly the targeted row and leaves every other row, and the total, untouched.
    /// </summary>
    [Fact]
    public async Task DeletingOneEntryLeavesTheOthersAndTheTotalIntact()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var keptA = await CreateEntryAsync(client, categoryId, vendorId, amount: 30m);
        var toDelete = await CreateEntryAsync(client, categoryId, vendorId, amount: 20m);
        var keptB = await CreateEntryAsync(client, categoryId, vendorId, amount: 10m);
        var totalBeforeDelete = await TotalAsync(client, vendorId);

        var response = await client.DeleteAsync($"/api/spend/entries/{toDelete}", ct);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        var remainingIds = entries.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToArray();
        remainingIds.Should().BeEquivalentTo([keptA, keptB], "only the targeted row should be gone");

        (await TotalAsync(client, vendorId))
            .Should()
            .Be(totalBeforeDelete - 20m, "the total must drop by exactly the deleted row's amount, not more or less");
    }

    /// <summary>
    /// The reason AllowNegativeSpendAmounts exists. A refund must reduce the total, and it
    /// must do so through the ordinary SUM of AmountGbp with no refund-aware special case —
    /// that unconditional sum is exactly why a signed amount beat an IsRefund flag.
    /// </summary>
    [Fact]
    public async Task Refund_LandsAsANegativeRowAndNetsOffTheTotal()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        await CreateEntryAsync(client, categoryId, vendorId, 100m);
        var totalAfterCharge = await TotalAsync(client, vendorId);
        totalAfterCharge.Should().Be(100m);

        var response = await client.PostAsJsonAsync(
            "/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", -30m) },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().Single();
        result.GetProperty("status").GetString().Should().Be("created");

        (await TotalAsync(client, vendorId)).Should().Be(70m, "a refund must net off the charges, not add to them");
    }

    [Fact]
    public async Task ReportingFiltersVendorAndCategoryBeforeAggregating()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (includedCategoryId, includedVendorId) = await SeedCatalogAsync(client);
        var (otherCategoryId, otherVendorId) = await SeedCatalogAsync(client);

        await CreateEntryAsync(client, includedCategoryId, includedVendorId, 30m);
        await CreateEntryAsync(client, otherCategoryId, otherVendorId, 70m);

        var report = await client.GetFromJsonAsync<JsonElement>(
            $"/api/spend/reporting?from=2026-07-12&to=2026-07-12&vendorId={includedVendorId}&categoryId={includedCategoryId}",
            ct
        );

        report.GetProperty("entryCount").GetInt32().Should().Be(1);
        report.GetProperty("totalGbp").GetDecimal().Should().Be(30m);
        report
            .GetProperty("vendorSeries")
            .EnumerateArray()
            .Should()
            .ContainSingle(point => point.GetProperty("vendorId").GetGuid() == includedVendorId);
        report
            .GetProperty("categorySeries")
            .EnumerateArray()
            .Should()
            .ContainSingle(point => point.GetProperty("categoryId").GetGuid() == includedCategoryId);
    }

    [Fact]
    public async Task Refund_FreezesANegativeAmountGbpAtTheChargeDateRate()
    {
        // USD, not GBP: GBP short-circuits to rate 1 before any FX lookup, so a GBP row
        // would assert the conversion without ever exercising it — the rate and the amount
        // would match by construction and a broken conversion would still pass.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.8}}""");
        await using var stubFactory = WithStubbedFx(factory, handler);
        using var client = AdminClient(stubFactory);
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", -30m, "USD") },
            ct
        );
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray()
            .Single()
            .GetProperty("id")
            .GetGuid();

        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        var row = entries.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);

        row.GetProperty("amount").GetDecimal().Should().Be(-30m);
        // The rate stays positive; the amount carries the sign.
        row.GetProperty("fxRate").GetDecimal().Should().Be(0.8m);
        // The GBP column must carry the sign too — it is the only column ever summed, so a
        // positive AmountGbp on a negative Amount would silently turn a refund into a charge.
        row.GetProperty("amountGbp")
            .GetDecimal()
            .Should()
            .Be(-24m, "-30 USD at 0.8 is -24 GBP, frozen at the charge date");
    }

    /// <summary>
    /// AmountGbp is the column every total sums, so the signed invariant matters more there
    /// than on Amount: a zero or opposite-sign value flips a refund into a charge across
    /// every aggregate at once. This asserts the property in the ordinary case; the
    /// rounding boundary that can actually trip the constraint is covered below.
    /// </summary>
    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public async Task PostEntry_StoresAmountGbpWithTheSameSignAsAmount(double amount)
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", (decimal)amount, "USD") },
            ct
        );
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray()
            .Single()
            .GetProperty("id")
            .GetGuid();

        var entries = await client.GetFromJsonAsync<JsonElement>($"/api/spend/entries?vendorId={vendorId}", ct);
        var row = entries.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);

        var stored = row.GetProperty("amount").GetDecimal();
        var storedGbp = row.GetProperty("amountGbp").GetDecimal();
        (stored * storedGbp)
            .Should()
            .BePositive("a non-GBP conversion must preserve the sign, or a refund reads as a charge in every total");
    }

    /// <summary>
    /// The one way CK_SpendEntry_AmountGbp_SameSign is genuinely reachable: an amount small
    /// enough that the conversion rounds to zero at 4dp. The row is refused up front — before
    /// any DB work, with the same actionable message the billing writer produces — rather than
    /// stored as a zero-GBP entry or caught by the constraint and translated by SaveRowAsync
    /// into the opaque "Could not save this entry", and it must not take the batch with it.
    /// </summary>
    [Fact]
    public async Task PostEntry_WhoseConversionRoundsToZero_IsRejectedWithoutFailingTheBatch()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"rates":{"GBP":0.5}}""");
        await using var stubFactory = WithStubbedFx(factory, handler);
        using var client = AdminClient(stubFactory);
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        var batch = new[]
        {
            // 0.0001 USD at 0.5 is 0.00005, which rounds to 0.0000 at the stored scale.
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", 0.0001m, "USD"),
            Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", 50m, "USD"),
        };

        var response = await client.PostAsJsonAsync("/api/spend/entries", batch, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().ToArray();
        results[0].GetProperty("status").GetString().Should().Be("rejected");
        results[0]
            .GetProperty("reason")
            .GetString()
            .Should()
            .Be(
                "Amount converts to zero GBP at the stored 4-decimal scale — the entry is too small to record",
                "the caller gets the actionable rounds-to-zero message, not an opaque DB-constraint rejection"
            );
        results[1]
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("created", "a row rejected by the constraint must not take the rest of the batch with it");
    }

    [Theory]
    [InlineData(0)]
    public async Task PostEntry_WithZeroAmount_IsRejected(double amount)
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/spend/entries",
            new[] { Entry(categoryId, vendorId, $"k-{Guid.NewGuid():N}", (decimal)amount) },
            ct
        );

        var result = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().Single();
        result.GetProperty("status").GetString().Should().Be("rejected");
        result.GetProperty("reason").GetString().Should().Contain("zero");
    }

    [Fact]
    public async Task PatchEntry_ToANegativeAmount_TurnsAChargeIntoARefund()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId, 50m);

        var patch = await client.PatchAsJsonAsync($"/api/spend/entries/{id}", new { Amount = -50m }, ct);

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patch.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("amount").GetDecimal().Should().Be(-50m);
        // ReResolveFxAsync recomputes AmountGbp from the new amount; the sign must survive it.
        body.GetProperty("amountGbp").GetDecimal().Should().Be(-50m);
    }

    [Fact]
    public async Task PatchEntry_ToZeroAmount_IsRejected()
    {
        using var client = factory.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var (categoryId, vendorId) = await SeedCatalogAsync(client);
        var id = await CreateEntryAsync(client, categoryId, vendorId, 50m);

        var patch = await client.PatchAsJsonAsync($"/api/spend/entries/{id}", new { Amount = 0m }, ct);

        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<decimal> TotalAsync(HttpClient client, Guid vendorId)
    {
        var entries = await client.GetFromJsonAsync<JsonElement>(
            $"/api/spend/entries?vendorId={vendorId}",
            TestContext.Current.CancellationToken
        );
        return entries.EnumerateArray().Sum(e => e.GetProperty("amountGbp").GetDecimal());
    }
}
