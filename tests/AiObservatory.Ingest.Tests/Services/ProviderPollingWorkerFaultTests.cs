using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

/// <summary>
/// Faults outside the per-source handlers. Needs no database, so it lives apart from the
/// Postgres-backed <see cref="ProviderPollingWorkerServiceTests"/> collection.
/// </summary>
public sealed class ProviderPollingWorkerFaultTests
{
    [Fact]
    public async Task ExecuteAsync_WhenAPollPassFaultsOutsideThePerSourceHandlers_KeepsTheHostAlive()
    {
        // BackgroundServiceExceptionBehavior defaults to StopHost, so one transient fault in
        // the scope factory or a DI resolution (neither is inside a per-source catch) would
        // take the whole ingest host down. PricingRefreshWorkerService already guards this.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var secondScopeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        // CreateAsyncScope is an extension over CreateScope, so the interface method is the seam.
        // The first call is LogEnabledArms at startup; the second is the first poll pass.
        scopeFactory
            .CreateScope()
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return new ServiceCollection().BuildServiceProvider().CreateScope();
                }
                secondScopeRequested.TrySetResult();
                throw new InvalidOperationException("database unreachable");
            });
        var worker = new ProviderPollingWorkerService(
            scopeFactory,
            new FakeClock(NodaTime.Instant.FromUtc(2026, 10, 12, 12, 0)),
            NullLogger<ProviderPollingWorkerService>.Instance,
            Options.Create(new IngestOptions { LookbackDays = 1, PollingIntervalMinutes = 60 })
        );

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await secondScopeRequested.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Stop BEFORE asserting: reaching the throw is not the same as the exception having
        // been handled, and StopAsync awaits ExecuteTask's terminal state (see
        // PricingRefreshWorkerServiceTests for the same ordering).
        await worker.StopAsync(CancellationToken.None);

        worker.ExecuteTask.Should().NotBeNull();
        worker.ExecuteTask.IsFaulted.Should().BeFalse();
        worker.CyclesCompleted.Should().Be(0, "a faulted pass is not a completed cycle");
    }
}
