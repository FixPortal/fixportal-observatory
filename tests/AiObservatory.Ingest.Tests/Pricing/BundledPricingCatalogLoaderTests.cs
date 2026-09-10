using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Pricing;
using AiObservatory.Ingest.Sources;
using AiObservatory.Ingest.Tests.Services;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Npgsql;

namespace AiObservatory.Ingest.Tests.Pricing;

[Collection("ProviderPollingWorker")]
public sealed class BundledPricingCatalogLoaderTests(ProviderPollingDatabase database)
{
    [Fact]
    public async Task ActivatesAllStrictlyValidatedBundlesOnColdStart()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Loader.LoadAsync(TestContext.Current.CancellationToken);

        var snapshots = await harness
            .Db.PricingSnapshots.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
        snapshots
            .Select(snapshot => snapshot.SourceId)
            .Should()
            .BeEquivalentTo(
                PricingSourceIds.OpenAi,
                PricingSourceIds.Claude,
                PricingSourceIds.Kimi,
                PricingSourceIds.GoogleCloudCatalog,
                PricingSourceIds.GeminiDeveloperApi
            );
        snapshots.Should().OnlyContain(snapshot => snapshot.IsActive);
        var google = snapshots.Single(snapshot => snapshot.SourceId == PricingSourceIds.GoogleCloudCatalog);
        PricingCatalogJson.Deserialize<GooglePriceCatalog>(google.NormalizedCatalog).Entries.Should().BeEmpty();
        var gemini = snapshots.Single(snapshot => snapshot.SourceId == PricingSourceIds.GeminiDeveloperApi);
        PricingCatalogJson
            .Deserialize<GeminiDeveloperPriceCatalog>(gemini.NormalizedCatalog)
            .Entries.Should()
            .HaveCount(4);
    }

    [Fact]
    public async Task HashesAndRetainsTheExactBundledJsonAsRawEvidence()
    {
        await using var harness = await CreateHarnessAsync();
        var raw = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "openai.json"),
            TestContext.Current.CancellationToken
        );

        await harness.Loader.LoadAsync(TestContext.Current.CancellationToken);

        var snapshot = await harness
            .Db.PricingSnapshots.AsNoTracking()
            .SingleAsync(
                candidate => candidate.SourceId == PricingSourceIds.OpenAi,
                TestContext.Current.CancellationToken
            );
        snapshot.RawEvidence.Should().Be(raw);
        snapshot
            .ContentHash.Should()
            .Be(
                PricingSnapshotCandidate.ComputeContentHash(
                    raw,
                    PricingCatalogJson.Serialize(PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(raw))
                )
            );
    }

    [Fact]
    public async Task DoesNotReplaceAnExistingActiveSnapshotWithTheBundle()
    {
        await using var harness = await CreateHarnessAsync();
        var raw = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "openai.json"),
            TestContext.Current.CancellationToken
        );
        var catalog = PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(raw);
        var current = PricingCandidate.Create(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            catalog.RetrievedAt,
            catalog.SourceUrl,
            "newer remote evidence",
            catalog
        );
        await harness.Store.ActivateAsync(current, TestContext.Current.CancellationToken);

        await harness.Loader.LoadAsync(TestContext.Current.CancellationToken);

        var active = await harness.Store.GetActiveAsync(PricingSourceIds.OpenAi, TestContext.Current.CancellationToken);
        active!.ContentHash.Should().Be(current.ContentHash);
        (
            await harness.Db.PricingSnapshots.CountAsync(
                candidate => candidate.SourceId == PricingSourceIds.OpenAi,
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task RepricesExistingNotionalUsageWhenCalculatorCodeChanges()
    {
        await using var harness = await CreateHarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        await harness.Loader.LoadAsync(ct);
        var usage = new UsageEvent
        {
            Provider = Provider.OpenAI,
            Model = "gpt-5.4",
            OccurredAt = Instant.FromUtc(2026, 8, 23, 12, 0),
            IngestedAt = Instant.FromUtc(2026, 8, 27, 12, 0),
            ObservedAt = Instant.FromUtc(2026, 8, 27, 12, 0),
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            RawPayload = """{"processing":"standard","context":"short","region":"global"}""",
            SourceId = UsageSourceIds.CodexLocal,
            SourceKind = SourceKind.LocalTelemetry,
            UsageScope = UsageScope.Subscription,
            CostBasis = CostBasis.Notional,
            EventKey = $"startup-reprice-{Guid.NewGuid():N}",
        };
        await new UsageRepository(harness.Db).RecordEventAsync(usage, ct);

        await harness.Loader.LoadAsync(ct);

        harness.Db.ChangeTracker.Clear();
        (await harness.Db.UsageEvents.SingleAsync(row => row.Id == usage.Id, ct)).CostUsd.Should().NotBeNull();
    }

    [Fact]
    public async Task ReplacesTheBundledOnlyGeminiCatalogWhenTheShippedEvidenceChanges()
    {
        await using var harness = await CreateHarnessAsync();
        var raw = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "gemini-developer-api.json"),
            TestContext.Current.CancellationToken
        );
        var bundled = PricingCatalogJson.Deserialize<GeminiDeveloperPriceCatalog>(raw);
        var stale = bundled with { Entries = [bundled.Entries[0] with { Input = 99m }, .. bundled.Entries.Skip(1)] };
        await harness.Store.ActivateAsync(
            PricingCandidate.Create(
                Provider.Google,
                PricingSourceIds.GeminiDeveloperApi,
                bundled.RetrievedAt,
                bundled.SourceUrl,
                "stale Gemini evidence",
                stale
            ),
            TestContext.Current.CancellationToken
        );

        await harness.Loader.LoadAsync(TestContext.Current.CancellationToken);

        var active = await harness.Store.GetActiveAsync(
            PricingSourceIds.GeminiDeveloperApi,
            TestContext.Current.CancellationToken
        );
        active!
            .ContentHash.Should()
            .Be(
                PricingSnapshotCandidate.ComputeContentHash(
                    raw,
                    PricingCatalogJson.Serialize(PricingCatalogJson.Deserialize<GeminiDeveloperPriceCatalog>(raw))
                )
            );
    }

    [Fact]
    public async Task RejectsAnUnmappedBundleMemberWithoutAbortingTheBundlePass()
    {
        await using var harness = await CreateHarnessAsync();
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var bundleDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "Pricing", "Bundled"));
            await File.WriteAllTextAsync(
                Path.Combine(bundleDirectory.FullName, "openai.json"),
                """
                {
                  "currency": "USD",
                  "sourceUrl": "https://developers.openai.com/api/docs/pricing.md",
                  "retrievedAt": "2026-08-24T12:00:00Z",
                  "entries": [],
                  "unexpected": true
                }
                """,
                TestContext.Current.CancellationToken
            );
            var loader = new BundledPricingCatalogLoader(
                harness.Store,
                harness.Repricing,
                NullLogger<BundledPricingCatalogLoader>.Instance,
                directory.FullName
            );

            await loader.LoadAsync(TestContext.Current.CancellationToken);

            (await harness.Store.GetActiveAsync(PricingSourceIds.OpenAi, TestContext.Current.CancellationToken))
                .Should()
                .BeNull();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BundleWaitsForRemoteActivationAndCannotOverwriteIt()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AiObservatoryDbContext>(options =>
            options.UseNpgsql(database.ConnectionString, npgsql => npgsql.UseNodaTime())
        );
        await using var provider = services.BuildServiceProvider();
        await using var remoteScope = provider.CreateAsyncScope();
        await using var bundleScope = provider.CreateAsyncScope();
        var remoteDb = remoteScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        await remoteDb.PricingSnapshots.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await remoteDb.SourceSyncStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var remoteStore = new PricingSnapshotStore(remoteDb);
        var bundleDb = bundleScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var bundleStore = new PricingSnapshotStore(bundleDb);
        var loader = new BundledPricingCatalogLoader(
            bundleStore,
            CreateRepricing(bundleDb, bundleStore),
            NullLogger<BundledPricingCatalogLoader>.Instance,
            AppContext.BaseDirectory
        );
        var raw = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "openai.json"),
            TestContext.Current.CancellationToken
        );
        var catalog = PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(raw);
        var remoteCandidate = PricingCandidate.Create(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            catalog.RetrievedAt,
            catalog.SourceUrl,
            "remote evidence",
            catalog
        );
        var remoteHasLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteActivation = remoteStore.ActivateAsync(
            remoteCandidate,
            TestContext.Current.CancellationToken,
            async (_, cancellationToken) =>
            {
                remoteHasLock.SetResult();
                await releaseRemote.Task.WaitAsync(cancellationToken);
            }
        );
        await remoteHasLock.Task.WaitAsync(TestContext.Current.CancellationToken);

        var bundleActivation = loader.LoadAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAdvisoryLockWaitAsync();
        }
        finally
        {
            releaseRemote.TrySetResult();
        }
        await Task.WhenAll(remoteActivation, bundleActivation);

        var active = await remoteStore.GetActiveAsync(PricingSourceIds.OpenAi, TestContext.Current.CancellationToken);
        active!.ContentHash.Should().Be(remoteCandidate.ContentHash);
        active.RawEvidence.Should().Be("remote evidence");
    }

    private async Task WaitForAdvisoryLockWaitAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND NOT granted
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
            )
            """;
        while (!(bool)(await command.ExecuteScalarAsync(timeout.Token) ?? false))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private async Task<LoaderHarness> CreateHarnessAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AiObservatoryDbContext>(options =>
            options.UseNpgsql(database.ConnectionString, npgsql => npgsql.UseNodaTime())
        );
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        await db.PricingSnapshots.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.SourceSyncStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var store = new PricingSnapshotStore(db);
        var repricing = CreateRepricing(db, store);
        return new LoaderHarness(
            provider,
            scope,
            db,
            store,
            repricing,
            new BundledPricingCatalogLoader(store, repricing, NullLogger<BundledPricingCatalogLoader>.Instance)
        );
    }

    private static PricingRepricingService CreateRepricing(AiObservatoryDbContext db, PricingSnapshotStore store) =>
        new(
            db,
            new UsageRepository(db),
            new UsagePriceResolver(
                store,
                [
                    new OpenAiPriceCalculator(),
                    new AnthropicPriceCalculator(),
                    new KimiPriceCalculator(),
                    new GooglePriceCalculator(),
                ],
                NullLogger<UsagePriceResolver>.Instance
            ),
            store
        );

    private sealed class LoaderHarness(
        ServiceProvider services,
        AsyncServiceScope scope,
        AiObservatoryDbContext db,
        PricingSnapshotStore store,
        PricingRepricingService repricing,
        BundledPricingCatalogLoader loader
    ) : IAsyncDisposable
    {
        public AiObservatoryDbContext Db { get; } = db;
        public PricingSnapshotStore Store { get; } = store;
        public PricingRepricingService Repricing { get; } = repricing;
        public BundledPricingCatalogLoader Loader { get; } = loader;

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await services.DisposeAsync();
        }
    }
}
