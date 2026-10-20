using AiObservatory.Api.Services.Suggestions;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Api.Tests.Suggestions;

public class SuggestionServiceFxOrderingTests
{
    [Fact]
    public async Task The_live_fx_lookup_runs_before_the_store_takes_its_lock()
    {
        var lockHeld = false;
        bool? lockHeldDuringFx = null;

        var fx = Substitute.For<FxRateProvider>(null, null, null);
        fx.TryGetUsdToGbpAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                lockHeldDuringFx = lockHeld;
                return Task.FromResult<decimal?>(0.79m);
            });

        var usage = Substitute.For<IUsageRepository>();
        usage.GetActiveSubscriptionsAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        usage.GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        var reviews = Substitute.For<IAdversarialReviewRepository>();
        reviews
            .GetRunsRecordedBetweenAsync(Arg.Any<Instant>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdversarialReviewRun>>([]));
        reviews
            .GetFindingsForRunsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdversarialReviewFinding>>([]));
        var options = Options.Create(new SuggestionOptions());
        var loader = new SuggestionInputsLoader(
            usage,
            reviews,
            fx,
            new FakeClock(Instant.FromUtc(2026, 10, 19, 12, 0)),
            options
        );

        // Stands in for the real store: it holds the lock for the duration of the delegate.
        var store = Substitute.For<SuggestionStore>(null, null);
        store
            .EvaluateAndSaveAsync(
                Arg.Any<Func<CancellationToken, Task<IReadOnlyList<RuleOutcome>>>>(),
                Arg.Any<SuggestionOptions>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async call =>
            {
                lockHeld = true;
                try
                {
                    return await call.Arg<Func<CancellationToken, Task<IReadOnlyList<RuleOutcome>>>>()(
                        call.Arg<CancellationToken>()
                    );
                }
                finally
                {
                    lockHeld = false;
                }
            });
        var service = new SuggestionService(loader, [], store, options, NullLogger<SuggestionService>.Instance);

        await service.EvaluateAsync(TestContext.Current.CancellationToken);

        lockHeldDuringFx.Should().BeFalse("the lookup can run for the HTTP timeout and must not hold the lock");
    }
}
