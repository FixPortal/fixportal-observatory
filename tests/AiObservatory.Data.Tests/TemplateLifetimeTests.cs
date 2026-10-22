using AiObservatory.Testing;
using AwesomeAssertions;

namespace AiObservatory.Data.Tests;

public sealed class TemplateLifetimeTests
{
    [Fact]
    public async Task DisposalOwnsAndDrainsStartedInitializationBeforeExactCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ownedToken = default;
        string? allocated = null;
        string? deleted = null;
        var owner = new OwnedTemplateDatabase(
            "Host=localhost;Database=postgres",
            async (connection, ct) =>
            {
                allocated = connection;
                ownedToken = ct;
                entered.TrySetResult();
                await release.Task;
            },
            (connection, _) =>
            {
                deleted = connection;
                return Task.CompletedTask;
            }
        );
        await entered.Task;
        var disposal = owner.DisposeAsync().AsTask();
        try
        {
            disposal.IsCompleted.Should().BeFalse("the started migration still owns an external template");
            ownedToken.IsCancellationRequested.Should().BeTrue();
        }
        finally
        {
            release.TrySetResult();
            await disposal;
            try
            {
                await owner.Initialization;
            }
            catch (OperationCanceledException)
            { /* Disposal cancelled owned initialization. */
            }
        }
        deleted.Should().Be(allocated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitializationRetainsOriginalFailureAndExactCleanup(bool cleanupFails)
    {
        var original = new InvalidOperationException("migration failed");
        var cleanup = new IOException("cleanup failed");
        string? allocated = null;
        string? deleted = null;
        var owner = new OwnedTemplateDatabase(
            "Host=localhost;Database=postgres",
            (connection, _) =>
            {
                allocated = connection;
                return Task.FromException(original);
            },
            (connection, _) =>
            {
                deleted = connection;
                return cleanupFails ? Task.FromException(cleanup) : Task.CompletedTask;
            }
        );
        var initialize = () => owner.Initialization;
        if (cleanupFails)
        {
            var failure = (await initialize.Should().ThrowAsync<AggregateException>()).Which;
            failure.InnerExceptions.Should().Equal(original, cleanup);
        }
        else
        {
            (await initialize.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(original);
        }
        deleted.Should().Be(allocated);
        var dispose = () => owner.DisposeAsync().AsTask();
        await dispose.Should().ThrowAsync<Exception>();
    }
}
