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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Pricing;

[Collection("ProviderPollingWorker")]
public sealed class PricingRefreshWorkerServiceTests(ProviderPollingDatabase database)
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 25, 12, 0);

    [Fact]
    public async Task ExecuteAsync_WhenTheRefreshPassThrows_LogsAndKeepsTheHostAlive()
    {
        // RunOnceAsync's unguarded awaits (catalog load, DI resolution, state reads) must not
        // fault the BackgroundService — the default StopHost behavior would kill the host.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        // The real completion signal, rather than a sleep: the worker reaching its throwing
        // dependency is the event this test was waiting for, so make it observable.
        var refreshPassEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // CreateAsyncScope is an extension over CreateScope, so the interface method throws.
        scopeFactory
            .CreateScope()
            .Returns(_ =>
            {
                refreshPassEntered.TrySetResult();
                throw new InvalidOperationException("database unreachable");
            });
        var worker = new PricingRefreshWorkerService(
            scopeFactory,
            new FakeClock(Now),
            NullLogger<PricingRefreshWorkerService>.Instance
        );

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await refreshPassEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // STOP BEFORE ASSERTING, which the sleeping version could not do. Reaching the throw
        // is not the same as the exception having been handled, so a check taken the instant
        // the signal fires would pass even against an implementation that lets the exception
        // escape -- the fault has not propagated yet. StopAsync awaits ExecuteTask, so once it
        // returns the task has reached its terminal state and IsFaulted is decided.
        await worker.StopAsync(CancellationToken.None);

        worker.ExecuteTask.Should().NotBeNull();
        worker.ExecuteTask.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheRefreshPassThrows_LogsTheSanitizedErrorWithoutTheRawException()
    {
        // Attaching the exception object would let the provider render its full ToString()
        // beside the sanitized field, defeating the query-string redaction the log exists for.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var refreshPassEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scopeFactory
            .CreateScope()
            .Returns(_ =>
            {
                refreshPassEntered.TrySetResult();
                throw new InvalidOperationException("refresh failed for https://example.test/export?signature=secret");
            });
        var logger = new CapturingLogger<PricingRefreshWorkerService>();
        var worker = new PricingRefreshWorkerService(scopeFactory, new FakeClock(Now), logger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await refreshPassEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        // StopAsync awaits the terminal state of ExecuteTask, so the catch block has logged
        // by the time it returns — no sleep needed to observe the entry.
        await worker.StopAsync(CancellationToken.None);

        var messages = logger.Messages;
        var exceptions = logger.Exceptions;
        var failed = messages
            .Select((message, index) => (message, exception: exceptions[index]))
            .Where(entry => entry.message.Contains("pricing refresh pass failed", StringComparison.Ordinal))
            .ToList();
        failed.Should().ContainSingle();
        failed[0].exception.Should().BeNull();
        failed[0].message.Should().NotContain("?signature=").And.NotContain("secret");
    }

    [Fact]
    public async Task LoadsBundlesBeforeFetchingRemoteSources()
    {
        IPricingSource? source = null;
        source = Substitute.For<IPricingSource>();
        source.SourceId.Returns(PricingSourceIds.OpenAi);
        await using var harness = await CreateHarnessAsync([source], [Definition(PricingSourceIds.OpenAi)]);
        source
            .FetchAsync(Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                (await harness.LoadActiveAsync(PricingSourceIds.OpenAi)).Should().NotBeNull();
                return await Task.FromResult<PricingSnapshotCandidate?>(null);
            });

        await harness.Worker.RunOnceAsync(TestContext.Current.CancellationToken);

        await source.Received(1).FetchAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipsASourceWhosePersistedSuccessIsNewerThanOneDay()
    {
        var source = Source(PricingSourceIds.OpenAi, null);
        await using var harness = await CreateHarnessAsync([source], [Definition(PricingSourceIds.OpenAi)]);
        await harness.MarkSuccessAsync(PricingSourceIds.OpenAi, Now - Duration.FromHours(23));

        await harness.Worker.RunOnceAsync(TestContext.Current.CancellationToken);

        await source.DidNotReceive().FetchAsync(Arg.Any<CancellationToken>());
        (await harness.LoadStateAsync(PricingSourceIds.OpenAi)).LastAttemptAt.Should().Be(Now - Duration.FromHours(23));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    public async Task RefreshesASourceWhosePersistedSuccessIsNotNewerThanOneDay(int ageHours)
    {
        var source = Source(PricingSourceIds.OpenAi, null);
        await using var harness = await CreateHarnessAsync([source], [Definition(PricingSourceIds.OpenAi)]);
        await harness.MarkSuccessAsync(PricingSourceIds.OpenAi, Now - Duration.FromHours(ageHours));

        await harness.Worker.RunOnceAsync(TestContext.Current.CancellationToken);

        await source.Received(1).FetchAsync(Arg.Any<CancellationToken>());
        var state = await harness.LoadStateAsync(PricingSourceIds.OpenAi);
        state.LastAttemptAt.Should().Be(Now);
        state.LastSuccessAt.Should().Be(Now);
        state.ConsecutiveFailureCount.Should().Be(0);
    }

    [Fact]
    public async Task MarksADefinitionWithoutARegisteredSourceUnconfiguredWithoutFalseSuccess()
    {
        await using var harness = await CreateHarnessAsync(
            [],
            [new PricingSourceDefinition(PricingSourceIds.GoogleCloudCatalog, false, Duration.FromDays(1))]
        );

        await harness.Worker.RunOnceAsync(TestContext.Current.CancellationToken);

        var state = await harness.LoadStateAsync(PricingSourceIds.GoogleCloudCatalog);
        state.IsConfigured.Should().BeFalse();
        state.LastAttemptAt.Should().BeNull();
        state.LastSuccessAt.Should().BeNull();
    }

    [Fact]
    public async Task IsolatesFailureSanitizesStateAndActivatesAHealthyCandidate()
    {
        var broken = Substitute.For<IPricingSource>();
        broken.SourceId.Returns(PricingSourceIds.Claude);
        broken
            .FetchAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<PricingSnapshotCandidate?>(
                    new InvalidDataException("bad\r\nhttps://example.test/catalog?signature=secret")
                )
            );
        var healthyCandidate = await CandidateAsync(PricingSourceIds.OpenAi, "remote evidence");
        var healthy = Source(PricingSourceIds.OpenAi, healthyCandidate);
        await using var harness = await CreateHarnessAsync(
            [broken, healthy],
            [Definition(PricingSourceIds.Claude), Definition(PricingSourceIds.OpenAi)]
        );

        await harness.Worker.RunOnceAsync(TestContext.Current.CancellationToken);

        await broken.Received(1).FetchAsync(Arg.Any<CancellationToken>());
        await healthy.Received(1).FetchAsync(Arg.Any<CancellationToken>());
        (await harness.LoadActiveAsync(PricingSourceIds.OpenAi))!.ContentHash.Should().Be(healthyCandidate.ContentHash);
        var state = await harness.LoadStateAsync(PricingSourceIds.Claude);
        state.LastAttemptAt.Should().Be(Now);
        state.LastSuccessAt.Should().BeNull();
        state.ConsecutiveFailureCount.Should().Be(1);
        state.LastError.Should().Be("bad  https://example.test/catalog");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task IsolatesBundleFailureAndContinuesLaterBundlesAndRemoteRefresh(string failure)
    {
        var directory = await CopyBundlesWithBrokenOpenAiAsync(failure);
        var bundleLogger = new CapturingLogger<BundledPricingCatalogLoader>();
        try
        {
            var healthyCandidate = await CandidateAsync(PricingSourceIds.Claude, "healthy remote evidence");
            var healthy = Substitute.For<IPricingSource>();
            healthy.SourceId.Returns(PricingSourceIds.Claude);
            WorkerHarness? harness = null;
            healthy
                .FetchAsync(Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    var activeHarness = harness ?? throw new InvalidOperationException("Harness was not initialized.");
                    (await activeHarness.LoadActiveAsync(PricingSourceIds.Claude)).Should().NotBeNull();
                    return (PricingSnapshotCandidate?)healthyCandidate;
                });
            harness = await CreateHarnessAsync(
                [healthy],
                [Definition(PricingSourceIds.Claude)],
                directory.FullName,
                bundleLogger
            );
            await using (harness)
            {
                await harness.Worker.RunOnceAsync(TestContext.Current.CancellationToken);

                await healthy.Received(1).FetchAsync(Arg.Any<CancellationToken>());
                (await harness.LoadActiveAsync(PricingSourceIds.Claude))!
                    .ContentHash.Should()
                    .Be(healthyCandidate.ContentHash);
                (await harness.LoadActiveAsync(PricingSourceIds.Kimi)).Should().NotBeNull();
                (await harness.LoadActiveAsync(PricingSourceIds.GoogleCloudCatalog)).Should().NotBeNull();
                (await harness.LoadActiveAsync(PricingSourceIds.GeminiDeveloperApi)).Should().NotBeNull();
                (await harness.LoadActiveAsync(PricingSourceIds.OpenAi)).Should().BeNull();
                (await harness.FindStateAsync(PricingSourceIds.OpenAi)).Should().BeNull();
            }

            bundleLogger.Messages.Should().ContainSingle();
            var message = bundleLogger.Messages.Single();
            message.Should().Contain(PricingSourceIds.OpenAi);
            message.Should().NotContain(directory.FullName);
            message.Should().NotContain("secret");
            message.Should().NotContain("?signature=");
            message.Length.Should().BeLessThan(600);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FailureAndUnchangedFetchBothPreserveTheActiveHash()
    {
        var failed = Substitute.For<IPricingSource>();
        failed.SourceId.Returns(PricingSourceIds.Claude);
        failed
            .FetchAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PricingSnapshotCandidate?>(new HttpRequestException("offline")));
        await using var harness = await CreateHarnessAsync([failed], [Definition(PricingSourceIds.Claude)]);
        await harness.LoaderLoadAsync(TestContext.Current.CancellationToken);
        var beforeFailure = (await harness.LoadActiveAsync(PricingSourceIds.Claude))!.ContentHash;

        await harness.Worker.RunOnceAsync(TestContext.Current.CancellationToken);
        (await harness.LoadActiveAsync(PricingSourceIds.Claude))!.ContentHash.Should().Be(beforeFailure);

        await harness.DeleteStateAsync(PricingSourceIds.Claude);
        var unchangedCandidate = await CandidateAsync(
            PricingSourceIds.Claude,
            (await harness.LoadActiveAsync(PricingSourceIds.Claude))!.RawEvidence
        );
        failed.FetchAsync(Arg.Any<CancellationToken>()).Returns(unchangedCandidate);
        await harness.Worker.RunOnceAsync(TestContext.Current.CancellationToken);

        (await harness.LoadActiveAsync(PricingSourceIds.Claude))!.ContentHash.Should().Be(beforeFailure);
        (await harness.LoadStateAsync(PricingSourceIds.Claude)).LastSuccessAt.Should().Be(Now);
    }

    [Fact]
    public async Task PropagatesCancellationWithoutFetchingLaterSources()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelled = Substitute.For<IPricingSource>();
        cancelled.SourceId.Returns(PricingSourceIds.OpenAi);
        cancelled
            .FetchAsync(Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<PricingSnapshotCandidate?>(call.Arg<CancellationToken>());
            });
        var later = Source(PricingSourceIds.Claude, null);
        await using var harness = await CreateHarnessAsync(
            [cancelled, later],
            [Definition(PricingSourceIds.OpenAi), Definition(PricingSourceIds.Claude)]
        );

        var act = () => harness.Worker.RunOnceAsync(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await later.DidNotReceive().FetchAsync(Arg.Any<CancellationToken>());
    }

    private static PricingSourceDefinition Definition(string sourceId) => new(sourceId, true, Duration.FromDays(1));

    private static IPricingSource Source(string sourceId, PricingSnapshotCandidate? candidate)
    {
        var source = Substitute.For<IPricingSource>();
        source.SourceId.Returns(sourceId);
        source.FetchAsync(Arg.Any<CancellationToken>()).Returns(candidate);
        return source;
    }

    private static async Task<PricingSnapshotCandidate> CandidateAsync(string sourceId, string evidence)
    {
        var file = sourceId switch
        {
            PricingSourceIds.OpenAi => "openai.json",
            PricingSourceIds.Claude => "claude.json",
            _ => throw new ArgumentOutOfRangeException(nameof(sourceId)),
        };
        var raw = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", file),
            TestContext.Current.CancellationToken
        );
        return sourceId switch
        {
            PricingSourceIds.OpenAi => PricingCandidate.Create(
                Provider.OpenAI,
                sourceId,
                PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(raw).RetrievedAt,
                PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(raw).SourceUrl,
                evidence,
                PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(raw)
            ),
            PricingSourceIds.Claude => PricingCandidate.Create(
                Provider.Anthropic,
                sourceId,
                PricingCatalogJson.Deserialize<AnthropicPriceCatalog>(raw).RetrievedAt,
                PricingCatalogJson.Deserialize<AnthropicPriceCatalog>(raw).SourceUrl,
                evidence,
                PricingCatalogJson.Deserialize<AnthropicPriceCatalog>(raw)
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceId)),
        };
    }

    private static async Task<DirectoryInfo> CopyBundlesWithBrokenOpenAiAsync(string failure)
    {
        var directory = Directory.CreateTempSubdirectory();
        var bundleDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "Pricing", "Bundled"));
        foreach (var fileName in new[] { "claude.json", "kimi.json", "google.json", "gemini-developer-api.json" })
        {
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", fileName),
                Path.Combine(bundleDirectory.FullName, fileName)
            );
        }
        if (failure == "malformed")
        {
            await File.WriteAllTextAsync(
                Path.Combine(bundleDirectory.FullName, "openai.json"),
                """
                { "sourceUrl": "https://example.test/catalog?signature=secret", "entries": [
                """,
                TestContext.Current.CancellationToken
            );
        }
        return directory;
    }

    private async Task<WorkerHarness> CreateHarnessAsync(
        IReadOnlyList<IPricingSource> sources,
        IReadOnlyList<PricingSourceDefinition> definitions,
        string? bundleBaseDirectory = null,
        ILogger<BundledPricingCatalogLoader>? bundleLogger = null
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AiObservatoryDbContext>(options =>
            options.UseNpgsql(database.ConnectionString, npgsql => npgsql.UseNodaTime())
        );
        services.AddScoped<PricingSnapshotStore>();
        services.AddScoped<SourceSyncStateStore>();
        services.AddScoped<IUsageRepository, UsageRepository>();
        services.AddScoped<IProviderPriceCalculator, OpenAiPriceCalculator>();
        services.AddScoped<IProviderPriceCalculator, AnthropicPriceCalculator>();
        services.AddScoped<IProviderPriceCalculator, KimiPriceCalculator>();
        services.AddScoped<IProviderPriceCalculator, GooglePriceCalculator>();
        services.AddScoped<UsagePriceResolver>();
        services.AddScoped<PricingRepricingService>();
        services.AddScoped(provider => new BundledPricingCatalogLoader(
            provider.GetRequiredService<PricingSnapshotStore>(),
            provider.GetRequiredService<PricingRepricingService>(),
            bundleLogger ?? NullLogger<BundledPricingCatalogLoader>.Instance,
            bundleBaseDirectory ?? AppContext.BaseDirectory
        ));
        foreach (var source in sources)
        {
            services.AddScoped<IPricingSource>(_ => source);
        }
        foreach (var definition in definitions)
        {
            services.AddSingleton(definition);
        }
        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await db.PricingSnapshots.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await db.SourceSyncStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
        var worker = new PricingRefreshWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(Now),
            NullLogger<PricingRefreshWorkerService>.Instance
        );
        return new WorkerHarness(provider, worker);
    }

    private sealed class WorkerHarness(ServiceProvider services, PricingRefreshWorkerService worker) : IAsyncDisposable
    {
        public PricingRefreshWorkerService Worker { get; } = worker;

        public async Task<SourceSyncState> LoadStateAsync(string sourceId)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope
                    .ServiceProvider.GetRequiredService<SourceSyncStateStore>()
                    .GetAsync(sourceId, TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("Source state was not persisted.");
        }

        public async Task<SourceSyncState?> FindStateAsync(string sourceId)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<SourceSyncStateStore>()
                .GetAsync(sourceId, TestContext.Current.CancellationToken);
        }

        public async Task<PricingSnapshot?> LoadActiveAsync(string sourceId)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<PricingSnapshotStore>()
                .GetActiveAsync(sourceId, TestContext.Current.CancellationToken);
        }

        public async Task MarkSuccessAsync(string sourceId, Instant at)
        {
            await using var scope = services.CreateAsyncScope();
            await scope
                .ServiceProvider.GetRequiredService<SourceSyncStateStore>()
                .MarkSuccessAsync(sourceId, Duration.FromDays(1), at, at, TestContext.Current.CancellationToken);
        }

        public async Task DeleteStateAsync(string sourceId)
        {
            await using var scope = services.CreateAsyncScope();
            await scope
                .ServiceProvider.GetRequiredService<AiObservatoryDbContext>()
                .SourceSyncStates.Where(state => state.SourceId == sourceId)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        public async Task LoaderLoadAsync(CancellationToken cancellationToken)
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BundledPricingCatalogLoader>().LoadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => services.DisposeAsync();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _messages = [];
        private readonly List<Exception?> _exceptions = [];
        private readonly NullScope _nullScope = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public IReadOnlyList<Exception?> Exceptions
        {
            get
            {
                lock (_gate)
                {
                    return [.. _exceptions];
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => _nullScope;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
                _exceptions.Add(exception);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
