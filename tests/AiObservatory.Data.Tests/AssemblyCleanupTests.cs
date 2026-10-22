using System.Collections.Concurrent;
using AiObservatory.Testing;
using AwesomeAssertions;

namespace AiObservatory.Data.Tests;

public sealed class AssemblyCleanupTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OwnerFailureDoesNotSkipLaterOwnersOrExactContainer(bool timeout, bool containerFails)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new InvalidOperationException("migration failure");
        var failingInitialization = timeout ? release.Task : Task.FromException(original);
        var containerFailure = new IOException("container failure");
        var allocated = new Dictionary<string, string>();
        var deleted = new ConcurrentBag<string>();
        var templates = new ConcurrentDictionary<string, Lazy<OwnedTemplateDatabase>>();
        string? failingKey = null;
        foreach (var key in new[] { "first", "second" })
        {
            templates[key] = new(() =>
                new OwnedTemplateDatabase(
                    "Host=localhost;Database=postgres",
                    (connection, _) =>
                    {
                        allocated[key] = connection;
                        return key == failingKey ? failingInitialization : Task.CompletedTask;
                    },
                    (connection, _) =>
                    {
                        deleted.Add(connection);
                        return Task.CompletedTask;
                    },
                    TimeSpan.Zero
                )
            );
        }
        // Select the actual enumeration's first owner so a later initialized owner must still be attempted.
        failingKey = templates.First().Key;
        var owners = templates.Values.Select(value => value.Value).ToArray();
        var resets = 0;
        var container = new FakeContainer(() =>
        {
            release.TrySetResult();
            return containerFails ? ValueTask.FromException(containerFailure) : ValueTask.CompletedTask;
        });
        try
        {
            var failure = await Record.ExceptionAsync(() =>
                PostgresTestAssemblyFixture.DisposeOwnedAsync(templates, container, () => resets++).AsTask()
            );
            container.Disposals.Should().Be(1, "the exact owned container must be attempted after an owner failure");
            resets.Should().Be(1);
            deleted.Should().Contain(allocated.Single(pair => pair.Key != failingKey).Value);
            templates
                .Keys.Should()
                .ContainSingle()
                .Which.Should()
                .Be(failingKey, "failed owners must not be discarded as successfully cleaned");
            var failures = failure.Should().BeOfType<AggregateException>().Which.InnerExceptions;
            if (timeout)
            {
                failures.Should().ContainSingle(error => error is TimeoutException);
            }
            else
            {
                failures.Should().Contain(original);
            }
            failures.Contains(containerFailure).Should().Be(containerFails);
        }
        finally
        {
            release.TrySetResult();
            await DrainOwners(owners, original);
        }
        // Late initializer cancellation still cleans precisely its generated database after container shutdown.
        deleted.Should().BeEquivalentTo(allocated.Values);
    }

    private static async Task DrainOwners(IEnumerable<OwnedTemplateDatabase> owners, Exception original)
    {
        foreach (var owner in owners)
        {
            try
            {
                await owner.Initialization;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ReferenceEquals(ex, original))
            {
                // Drain only these owned fake initializers; the test asserts their original failures.
            }
        }
    }

    [Fact]
    public async Task ExternallySuppliedServerDoesNotReleaseConnectionOrContainer()
    {
        var resets = 0;
        var deleted = 0;
        var templates = new ConcurrentDictionary<string, Lazy<OwnedTemplateDatabase>>();
        templates["external"] = new(() =>
            new OwnedTemplateDatabase(
                "Host=localhost;Database=postgres",
                (_, _) => Task.CompletedTask,
                (_, _) =>
                {
                    deleted++;
                    return Task.CompletedTask;
                }
            )
        );
        await templates["external"].Value.Initialization;
        await PostgresTestAssemblyFixture.DisposeOwnedAsync(templates, null, () => resets++);
        resets.Should().Be(0);
        deleted.Should().Be(1);
        templates.Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectionReleaseFailureStillAttemptsContainerCleanup()
    {
        var original = new InvalidOperationException("connection release failed");
        var container = new FakeContainer(() => ValueTask.CompletedTask);
        var dispose = () =>
            PostgresTestAssemblyFixture.DisposeOwnedAsync(new(), container, () => throw original).AsTask();
        var failure = (await dispose.Should().ThrowAsync<AggregateException>()).Which;
        failure.InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(original);
        container.Disposals.Should().Be(1);
    }

    private sealed class FakeContainer(Func<ValueTask> dispose) : IAsyncDisposable
    {
        internal int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return dispose();
        }
    }
}
