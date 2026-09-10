using AiObservatory.Api.Services;
using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public class IntelligenceWorkerExecutionTests
{
    [Fact]
    public void GitHubBillingStatusErrorsAreSingleLineBoundedAndDropQueryStrings()
    {
        var error = $"failed at https://example.test/billing?token=secret\r\n{new string('x', 600)}";

        var sanitized = IntelligenceWorkerService.SanitizeError(error);

        sanitized.Should().HaveLength(500).And.NotContain("secret").And.NotContain("\r").And.NotContain("\n");
    }

    [Fact]
    public async Task AnalysisFailureDoesNotPreventTheBudgetArmOrStopTheWorker()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock(Instant.FromUtc(2026, 7, 30, 9, 0));
        var repository = Substitute.For<IUsageRepository>();
        repository
            .GetLatestInsightPeriodEndAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<LocalDate?>(new InvalidOperationException("failed")));
        var budgetCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = Budget(repository, clock, () => budgetCalled.TrySetResult());
        var services = new ServiceCollection().AddSingleton(repository).AddSingleton(budget).BuildServiceProvider();
        var logger = new CapturingLogger();
        var worker = new IntelligenceWorkerService(services.GetRequiredService<IServiceScopeFactory>(), clock, logger);

        await worker.StartAsync(ct);
        try
        {
            await budgetCalled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            logger.Messages.Should().Contain(m => m.Contains("catchup failed"));
            worker.ExecuteTask.Should().NotBeNull();
            worker.ExecuteTask.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CatchupProcessesAtMostTheLatestSevenCompletedDays()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock(Instant.FromUtc(2026, 7, 30, 9, 0));
        var repository = Substitute.For<IUsageRepository>();
        repository.GetLatestInsightPeriodEndAsync(Arg.Any<CancellationToken>()).Returns(new LocalDate(2026, 7, 1));
        var generatedDates = new List<LocalDate>();
        var generator = Substitute.For<IInsightGenerator>();
        generator
            .GenerateForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                generatedDates.Add(call.Arg<LocalDate>());
                return new InsightGenerationResult(0, 0);
            });
        var budgetCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = Budget(repository, clock, () => budgetCalled.TrySetResult());
        var services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(generator)
            .AddSingleton(budget)
            .BuildServiceProvider();
        var worker = new IntelligenceWorkerService(
            services.GetRequiredService<IServiceScopeFactory>(),
            clock,
            NullLogger<IntelligenceWorkerService>.Instance
        );

        await worker.StartAsync(ct);
        try
        {
            await budgetCalled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            generatedDates
                .Should()
                .Equal(
                    new LocalDate(2026, 7, 23),
                    new LocalDate(2026, 7, 24),
                    new LocalDate(2026, 7, 25),
                    new LocalDate(2026, 7, 26),
                    new LocalDate(2026, 7, 27),
                    new LocalDate(2026, 7, 28),
                    new LocalDate(2026, 7, 29)
                );
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExactMidnightSchedulesTheNextCycleForTomorrowInsteadOfSpinning()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock(Instant.FromUtc(2026, 7, 30, 0, 0));
        var repository = Substitute.For<IUsageRepository>();
        repository.GetLatestInsightPeriodEndAsync(Arg.Any<CancellationToken>()).Returns(new LocalDate(2026, 7, 29));
        var firstBudgetCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBudgetCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var budget = Budget(
            repository,
            clock,
            () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstBudgetCall.TrySetResult();
                }
                else
                {
                    secondBudgetCall.TrySetResult();
                }
            }
        );
        var services = new ServiceCollection().AddSingleton(repository).AddSingleton(budget).BuildServiceProvider();
        // Park the worker inside a controlled delay: observing the requested delay proves the
        // schedule deterministically, where racing a real 100ms timer only proved the worker
        // had not YET re-run (a loaded machine passes a spinning worker the same way).
        var delayRequested = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new IntelligenceWorkerService(
            services.GetRequiredService<IServiceScopeFactory>(),
            clock,
            NullLogger<IntelligenceWorkerService>.Instance
        )
        {
            DelayAsync = (delay, token) =>
            {
                delayRequested.TrySetResult(delay);
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };

        await worker.StartAsync(ct);
        try
        {
            await firstBudgetCall.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            var requested = await delayRequested.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            requested
                .Should()
                .Be(
                    TimeSpan.FromHours(24),
                    "at exact midnight the next cycle is tomorrow's midnight, not an immediate re-run"
                );
            secondBudgetCall
                .Task.IsCompleted.Should()
                .BeFalse("the worker is parked inside the day-long delay and cannot spin");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static BudgetAlertService Budget(IUsageRepository repository, IClock clock, Action onCall)
    {
        var budget = Substitute.For<BudgetAlertService>(
            repository,
            clock,
            Substitute.For<IAlertNotifier>(),
            NullLogger<BudgetAlertService>.Instance,
            new ConfigurationBuilder().Build()
        );
        budget
            .CheckAndAlertAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                onCall();
                return Task.CompletedTask;
            });
        return budget;
    }

    private sealed class CapturingLogger : ILogger<IntelligenceWorkerService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
