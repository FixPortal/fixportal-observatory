using AiObservatory.Api.Services.Suggestions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AiObservatory.Api.Tests.Suggestions;

public class SuggestionOptionsAndWorkerTests
{
    [Fact]
    public void The_defaults_are_valid()
    {
        new SuggestionOptions().Validate().Should().BeNull();
        new SuggestionOptionsValidator().Validate(null, new SuggestionOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(SuggestionOptions.SeatWindowDays), 0)]
    [InlineData(nameof(SuggestionOptions.MinDetailedRuns), -1)]
    [InlineData(nameof(SuggestionOptions.PlanMinActiveDays), 0)]
    [InlineData(nameof(SuggestionOptions.FalsePositiveThreshold), 0)]
    [InlineData(nameof(SuggestionOptions.FalsePositiveThreshold), 1.5)]
    [InlineData(nameof(SuggestionOptions.ReopenChangeFraction), 2)]
    [InlineData(nameof(SuggestionOptions.CostOutlierMultiple), 0)]
    public void An_unusable_value_fails_validation_naming_its_key(string key, double value)
    {
        var options = new SuggestionOptions();
        var property = typeof(SuggestionOptions).GetProperty(key)!;
        property.SetValue(options, Convert.ChangeType(value, property.PropertyType));

        var result = new SuggestionOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain($"Suggestions:{key}");
    }

    private static (SuggestionWorkerService Worker, SuggestionService Service) CreateWorker(
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        bool enabled = true
    )
    {
        var service = Substitute.For<SuggestionService>(null, null, null, null, null);
        var services = new ServiceCollection().AddSingleton(service).BuildServiceProvider();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 19, 12, 0));
        var worker = new SuggestionWorkerService(
            services.GetRequiredService<IServiceScopeFactory>(),
            clock,
            Options.Create(new SuggestionOptions { WorkerEnabled = enabled }),
            NullLogger<SuggestionWorkerService>.Instance
        )
        {
            DelayAsync = delay ?? ((_, _) => Task.CompletedTask),
        };
        return (worker, service);
    }

    [Fact]
    public async Task A_failing_evaluation_is_logged_and_does_not_stop_the_worker()
    {
        var (worker, service) = CreateWorker();
        service.EvaluateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => worker.RunOnceAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_disabled_worker_never_evaluates()
    {
        var (worker, service) = CreateWorker(enabled: false);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        await service.DidNotReceive().EvaluateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_worker_evaluates_at_startup_then_parks_until_the_next_utc_midnight()
    {
        var parked = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (worker, service) = CreateWorker(
            async (delay, token) =>
            {
                parked.TrySetResult(delay);
                await Task.Delay(Timeout.Infinite, token);
            }
        );
        service.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RuleOutcome>>([]));

        await worker.StartAsync(TestContext.Current.CancellationToken);
        var delay = await parked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        delay.Should().Be(TimeSpan.FromHours(12));
        await service.Received(1).EvaluateAsync(Arg.Any<CancellationToken>());
    }
}
