using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public class BudgetAlertServiceTests
{
    private readonly IUsageRepository _repo = Substitute.For<IUsageRepository>();
    private readonly IAlertNotifier _notifier = Substitute.For<IAlertNotifier>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 2, 10, 0));

    [Fact]
    public async Task CheckAndAlert_triggers_rule_when_billed_spend_exceeds_threshold()
    {
        var rule = Rule(BillingPeriod.Daily);
        StubRules(rule);
        StubBilledSpend(rule, 10.01m);
        StubSuccessfulDelivery(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .GetOrCreateBudgetAlertAsync(
                rule.Id,
                new LocalDate(2026, 6, 1),
                new LocalDate(2026, 6, 1),
                rule.ThresholdGbp,
                10.01m,
                Arg.Is<Insight>(i =>
                    i.InsightType == InsightType.BudgetAlert
                    && i.Title.Contains("billed spend")
                    && i.Data.Contains("thresholdGbp")
                ),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            );
        await _notifier
            .Received(1)
            .NotifyAsync(
                Arg.Is<BudgetAlertPayload>(p => p.ThresholdGbp == rule.ThresholdGbp && p.ActualSpendGbp == 10.01m),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_does_not_alert_when_billed_spend_is_below_threshold_despite_higher_estimates()
    {
        var rule = Rule(BillingPeriod.Daily, provider: Provider.Anthropic);
        StubRules(rule);
        StubBilledSpend(rule, 4m);
        _repo
            .GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new DailyAggregate
                {
                    Date = new LocalDate(2026, 6, 1),
                    Provider = Provider.Anthropic,
                    Model = "legacy-estimate",
                    CostUsd = 20m,
                    InputTokens = 0,
                    OutputTokens = 0,
                    RequestCount = 1,
                },
            ]);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.DidNotReceive().NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_evaluates_all_completed_daily_periods_from_rule_boundary_in_one_read()
    {
        var evaluationStartsOn = new LocalDate(2026, 4, 1);
        var rule = Rule(BillingPeriod.Daily, evaluationStartsOn: evaluationStartsOn);
        StubRules(rule);
        StubBilledSpend(rule, 0m);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .GetDailyBilledSpendGbpAsync(
                evaluationStartsOn,
                new LocalDate(2026, 6, 1),
                null,
                Arg.Any<CancellationToken>()
            );
        await _repo
            .DidNotReceive()
            .GetBilledSpendGbpAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_reconsiders_a_late_daily_entry_older_than_seven_days()
    {
        var lateDate = new LocalDate(2026, 5, 1);
        var rule = Rule(BillingPeriod.Daily, evaluationStartsOn: lateDate);
        StubRules(rule);
        _repo
            .GetDailyBilledSpendGbpAsync(
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                rule.Provider,
                Arg.Any<CancellationToken>()
            )
            .Returns([new DailyBilledSpend(lateDate, 15m)]);
        StubSuccessfulDelivery(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .GetOrCreateBudgetAlertAsync(
                rule.Id,
                lateDate,
                lateDate,
                10m,
                15m,
                Arg.Any<Insight>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            );
        await _notifier.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_rescans_a_daily_rule_only_from_the_grace_window_behind_its_last_trigger()
    {
        var rule = Rule(
            BillingPeriod.Daily,
            lastTriggeredAt: Instant.FromUtc(2026, 5, 20, 8, 0),
            evaluationStartsOn: new LocalDate(2026, 4, 1)
        );
        StubRules(rule);
        StubBilledSpend(rule, 0m);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        // Days at or before the last trigger already have their claim; the rescan lower-bounds
        // at a 7-day grace window behind that watermark instead of the rule's whole lifetime.
        await _repo
            .Received(1)
            .GetDailyBilledSpendGbpAsync(
                new LocalDate(2026, 5, 13),
                new LocalDate(2026, 6, 1),
                null,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_does_not_replay_daily_history_before_deployment_boundary()
    {
        var rule = Rule(
            BillingPeriod.Daily,
            lastTriggeredAt: Instant.FromUtc(2026, 5, 20, 8, 0),
            evaluationStartsOn: new LocalDate(2026, 6, 2)
        );
        StubRules(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .DidNotReceive()
            .GetDailyBilledSpendGbpAsync(
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Provider?>(),
                Arg.Any<CancellationToken>()
            );
        await _repo
            .DidNotReceive()
            .GetOrCreateBudgetAlertAsync(
                Arg.Any<Guid>(),
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<decimal>(),
                Arg.Any<decimal>(),
                Arg.Any<Insight>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_WhenNotifierTemporarilyFails_RetriesSameMessageThenMarksSent()
    {
        var claimId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var ruleId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var email = new BudgetAlertEmail(
            claimId,
            ruleId,
            Provider.Anthropic,
            BillingPeriod.Daily,
            new LocalDate(2026, 6, 1),
            new LocalDate(2026, 6, 1),
            10m,
            15m,
            Instant.FromUtc(2026, 6, 2, 0, 1)
        );
        _repo.GetDeliverableBudgetAlertEmailsAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>()).Returns([email]);
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                claimId,
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<AlertDeliveryResult>(new InvalidOperationException("SMTP unreachable")),
                Task.FromResult(AlertDeliveryResult.Sent)
            );

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);
        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        var expectedMessageId = $"budget-alert-{claimId:N}@{MessageIdDomain}";
        await _notifier
            .Received(2)
            .NotifyAsync(
                Arg.Is<BudgetAlertPayload>(payload => payload.MessageId == expectedMessageId),
                Arg.Any<CancellationToken>()
            );
        await _repo
            .Received(1)
            .ReleaseBudgetAlertEmailLeaseAsync(claimId, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repo
            .Received(1)
            .MarkBudgetAlertEmailSentAsync(claimId, Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The Message-Id domain is configuration. It used to be a hardcoded maintainer domain, which
    /// every self-hoster would have stamped on their own outgoing alert mail. The sender's own
    /// domain is the right default because that is the address the mail is actually sent from;
    /// an explicit setting overrides it.
    /// </summary>
    [Theory]
    // Explicit setting wins.
    [InlineData("mail.example.test", "alerts@sender.example", "mail.example.test")]
    // No explicit setting: derived from the configured sender.
    [InlineData(null, "alerts@sender.example", "sender.example")]
    // Neither configured: a neutral literal, never a domain belonging to this project.
    [InlineData(null, null, "observatory.local")]
    public async Task CheckAndAlert_DerivesTheMessageIdDomainFromConfiguration(
        string? explicitDomain,
        string? sender,
        string expectedDomain
    )
    {
        var claimId = Guid.Parse("10000000-0000-0000-0000-0000000000aa");
        var email = new BudgetAlertEmail(
            claimId,
            Guid.Parse("20000000-0000-0000-0000-0000000000aa"),
            Provider.Anthropic,
            BillingPeriod.Daily,
            new LocalDate(2026, 6, 1),
            new LocalDate(2026, 6, 1),
            10m,
            15m,
            Instant.FromUtc(2026, 6, 2, 0, 1)
        );
        _repo.GetDeliverableBudgetAlertEmailsAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>()).Returns([email]);
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                claimId,
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(AlertDeliveryResult.Sent);

        var settings = new Dictionary<string, string?>();
        if (explicitDomain is not null)
        {
            settings["BUDGET_ALERT_MESSAGE_ID_DOMAIN"] = explicitDomain;
        }
        if (sender is not null)
        {
            settings["BUDGET_ALERT_EMAIL_FROM"] = sender;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var sut = new BudgetAlertService(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance, config);

        await sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier
            .Received(1)
            .NotifyAsync(
                Arg.Is<BudgetAlertPayload>(payload =>
                    payload.MessageId == $"budget-alert-{claimId:N}@{expectedDomain}"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_replays_a_pending_email_from_durable_state_despite_period_suppression()
    {
        var claimId = Guid.NewGuid();
        var rule = Rule(BillingPeriod.Weekly, lastTriggeredAt: Instant.FromUtc(2026, 6, 1, 8, 0));
        StubRules(rule);
        _repo
            .GetDeliverableBudgetAlertEmailsAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([
                new BudgetAlertEmail(
                    claimId,
                    rule.Id,
                    Provider.Anthropic,
                    BillingPeriod.Weekly,
                    new LocalDate(2026, 5, 27),
                    new LocalDate(2026, 6, 2),
                    10m,
                    15m,
                    Instant.FromUtc(2026, 6, 2, 0, 1)
                ),
            ]);
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                claimId,
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AlertDeliveryResult.Sent));

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier
            .Received(1)
            .NotifyAsync(
                Arg.Is<BudgetAlertPayload>(payload =>
                    payload.Provider == "Anthropic"
                    && payload.Period == "Weekly"
                    && payload.ThresholdGbp == 10m
                    && payload.ActualSpendGbp == 15m
                ),
                Arg.Any<CancellationToken>()
            );
        await _repo
            .DidNotReceive()
            .GetBilledSpendGbpAsync(
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Provider?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_uses_fresh_lease_and_completion_times_for_each_backlogged_email()
    {
        var startedAt = _clock.GetCurrentInstant();
        var firstClaimId = Guid.NewGuid();
        var secondClaimId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        BudgetAlertEmail Email(Guid claimId) =>
            new(
                claimId,
                ruleId,
                null,
                BillingPeriod.Daily,
                new LocalDate(2026, 6, 1),
                new LocalDate(2026, 6, 1),
                10m,
                15m,
                startedAt
            );

        _repo
            .GetDeliverableBudgetAlertEmailsAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([Email(firstClaimId), Email(secondClaimId)]);
        var acquisitions = new List<(Guid ClaimId, Instant AcquiredAt, Instant LeaseExpiredBefore)>();
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                acquisitions.Add((call.ArgAt<Guid>(0), call.ArgAt<Instant>(2), call.ArgAt<Instant>(3)));
                return true;
            });
        var completions = new List<(Guid ClaimId, Instant SentAt)>();
        _repo
            .MarkBudgetAlertEmailSentAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                completions.Add((call.ArgAt<Guid>(0), call.ArgAt<Instant>(2)));
                return Task.CompletedTask;
            });
        var delivery = 0;
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (delivery++ == 0)
                {
                    _clock.Advance(Duration.FromMinutes(16));
                }
                return Task.FromResult(AlertDeliveryResult.Sent);
            });

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        acquisitions
            .Should()
            .Equal(
                (firstClaimId, startedAt, startedAt.Minus(Duration.FromMinutes(15))),
                (secondClaimId, startedAt.Plus(Duration.FromMinutes(16)), startedAt.Plus(Duration.FromMinutes(1)))
            );
        completions
            .Should()
            .Equal(
                (firstClaimId, startedAt.Plus(Duration.FromMinutes(16))),
                (secondClaimId, startedAt.Plus(Duration.FromMinutes(16)))
            );
    }

    [Fact]
    public async Task CheckAndAlert_creates_claims_before_one_deterministic_bounded_delivery_pass()
    {
        var rules = Enumerable.Range(1, 52).Select(_ => Rule(BillingPeriod.Daily)).ToArray();
        var claimIds = Enumerable
            .Range(1, rules.Length)
            .Select(index => Guid.ParseExact($"{index:x32}", "N"))
            .ToArray();
        var claimIdByRule = rules
            .Select((rule, index) => (rule.Id, claimIds[index]))
            .ToDictionary(pair => pair.Id, pair => pair.Item2);
        var pending = new Dictionary<Guid, BudgetAlertEmail>();
        var sent = new HashSet<Guid>();
        var attempts = new List<Guid>();

        StubRules(rules.Reverse().ToArray());
        StubBilledSpend(rules[0], 15m);
        _repo
            .GetOrCreateBudgetAlertAsync(
                Arg.Any<Guid>(),
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<decimal>(),
                Arg.Any<decimal>(),
                Arg.Any<Insight>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                var ruleId = call.ArgAt<Guid>(0);
                var claimId = claimIdByRule[ruleId];
                pending.TryAdd(
                    claimId,
                    new BudgetAlertEmail(
                        claimId,
                        ruleId,
                        null,
                        BillingPeriod.Daily,
                        call.ArgAt<LocalDate>(1),
                        call.ArgAt<LocalDate>(2),
                        call.ArgAt<decimal>(3),
                        call.ArgAt<decimal>(4),
                        call.ArgAt<Instant>(6)
                    )
                );
                return new BudgetAlertClaimResult(
                    claimId,
                    true,
                    call.ArgAt<decimal>(3),
                    call.ArgAt<decimal>(4),
                    call.ArgAt<Instant>(6)
                );
            });
        _repo
            .GetDeliverableBudgetAlertEmailsAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
                pending
                    .Values.Where(email => !sent.Contains(email.ClaimId))
                    .OrderBy(email => email.CreatedAt)
                    .ThenBy(email => email.ClaimId)
                    .Take(50)
                    .ToArray()
            );
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call => !sent.Contains(call.ArgAt<Guid>(0)));
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var messageId = call.ArgAt<BudgetAlertPayload>(0).MessageId;
                var encodedClaimId = messageId["budget-alert-".Length..^$"@{MessageIdDomain}".Length];
                attempts.Add(Guid.ParseExact(encodedClaimId, "N"));
                return Task.FromResult(AlertDeliveryResult.Sent);
            });
        _repo
            .MarkBudgetAlertEmailSentAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                sent.Add(call.ArgAt<Guid>(0));
                return Task.CompletedTask;
            });

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        attempts.Should().Equal(claimIds.Take(50));

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        attempts.Should().Equal(claimIds);
    }

    [Fact]
    public async Task CheckAndAlert_WhenOneRulesNotifierThrows_SiblingRulesStillEvaluated()
    {
        var failing = Rule(BillingPeriod.Daily);
        var healthy = Rule(BillingPeriod.Daily, provider: Provider.Google);
        StubRules(failing, healthy);
        StubBilledSpend(failing, 15m);
        StubBilledSpend(healthy, 15m);
        _notifier
            .NotifyAsync(Arg.Is<BudgetAlertPayload>(p => p.Provider == "all"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AlertDeliveryResult>(new InvalidOperationException("SMTP unreachable")));
        _notifier
            .NotifyAsync(Arg.Is<BudgetAlertPayload>(p => p.Provider != "all"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AlertDeliveryResult.Sent));

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .GetOrCreateBudgetAlertAsync(
                healthy.Id,
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<decimal>(),
                Arg.Any<decimal>(),
                Arg.Any<Insight>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_WhenRuleIsProviderScoped_OnlyThatProvidersBilledSpendCounts()
    {
        var rule = Rule(BillingPeriod.Daily, provider: Provider.Anthropic);
        StubRules(rule);
        StubBilledSpend(rule, 4m);
        _repo
            .GetDailyBilledSpendGbpAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>())
            .Returns([new DailyBilledSpend(new LocalDate(2026, 6, 1), 20m)]);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.DidNotReceive().NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(BillingPeriod.Weekly, 5, 31)]
    [InlineData(BillingPeriod.Monthly, 6, 2)]
    public async Task CheckAndAlert_does_not_read_before_a_periodic_rules_evaluation_boundary(
        BillingPeriod period,
        int startMonth,
        int startDay
    )
    {
        var evaluationStartsOn = new LocalDate(2026, startMonth, startDay);
        var rule = Rule(period, evaluationStartsOn: evaluationStartsOn);
        StubRules(rule);
        StubBilledSpend(rule, 0m);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .GetBilledSpendGbpAsync(evaluationStartsOn, new LocalDate(2026, 6, 2), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_skips_a_periodic_rule_whose_evaluation_boundary_is_in_the_future()
    {
        var rule = Rule(BillingPeriod.Weekly, evaluationStartsOn: new LocalDate(2026, 6, 3));
        StubRules(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .DidNotReceive()
            .GetBilledSpendGbpAsync(
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Provider?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_skips_weekly_rule_already_triggered_within_window()
    {
        var rule = Rule(BillingPeriod.Weekly, lastTriggeredAt: Instant.FromUtc(2026, 5, 28, 8, 0));
        StubRules(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.DidNotReceive().NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_fires_weekly_rule_last_triggered_before_current_window()
    {
        var rule = Rule(BillingPeriod.Weekly, lastTriggeredAt: Instant.FromUtc(2026, 5, 20, 8, 0));
        StubRules(rule);
        StubBilledSpend(rule, 15m);
        StubSuccessfulDelivery(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_skips_monthly_rule_already_triggered_this_month()
    {
        var rule = Rule(BillingPeriod.Monthly, lastTriggeredAt: Instant.FromUtc(2026, 6, 1, 8, 0));
        StubRules(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.DidNotReceive().NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_fires_monthly_rule_last_triggered_in_a_prior_month()
    {
        var rule = Rule(BillingPeriod.Monthly, lastTriggeredAt: Instant.FromUtc(2026, 5, 15, 8, 0));
        StubRules(rule);
        StubBilledSpend(rule, 15m);
        StubSuccessfulDelivery(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(AlertDeliveryResult.NoRecipientConfigured)]
    [InlineData(AlertDeliveryResult.Failed)]
    public async Task CheckAndAlert_does_not_mark_sent_and_releases_the_lease_when_no_channel_delivered(
        AlertDeliveryResult outcome
    )
    {
        var claimId = Guid.NewGuid();
        var rule = Rule(BillingPeriod.Daily);
        StubRules(rule);
        StubBilledSpend(rule, 0m);
        _repo
            .GetDeliverableBudgetAlertEmailsAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([
                new BudgetAlertEmail(
                    claimId,
                    rule.Id,
                    null,
                    BillingPeriod.Daily,
                    new LocalDate(2026, 6, 1),
                    new LocalDate(2026, 6, 1),
                    10m,
                    15m,
                    Instant.FromUtc(2026, 6, 2, 0, 1)
                ),
            ]);
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                claimId,
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(outcome));

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .DidNotReceive()
            .MarkBudgetAlertEmailSentAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            );
        await _repo
            .Received(1)
            .ReleaseBudgetAlertEmailLeaseAsync(claimId, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_marks_sent_when_any_channel_reports_delivery()
    {
        var claimId = Guid.NewGuid();
        var rule = Rule(BillingPeriod.Daily);
        StubRules(rule);
        StubBilledSpend(rule, 0m);
        _repo
            .GetDeliverableBudgetAlertEmailsAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([
                new BudgetAlertEmail(
                    claimId,
                    rule.Id,
                    null,
                    BillingPeriod.Daily,
                    new LocalDate(2026, 6, 1),
                    new LocalDate(2026, 6, 1),
                    10m,
                    15m,
                    Instant.FromUtc(2026, 6, 2, 0, 1)
                ),
            ]);
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                claimId,
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AlertDeliveryResult.Sent));

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .MarkBudgetAlertEmailSentAsync(claimId, Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _repo
            .DidNotReceive()
            .ReleaseBudgetAlertEmailLeaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Lease cleanup must not hide the original delivery failure, including when cleanup itself fails.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAndAlert_logs_delivery_failure_even_when_lease_release_fails(bool releaseFails)
    {
        var rule = Rule(BillingPeriod.Daily);
        StubRules(rule);
        StubBilledSpend(rule, 15m);
        StubSuccessfulDelivery(rule);
        var deliveryFailure = new InvalidOperationException("SMTP unreachable");
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AlertDeliveryResult>(deliveryFailure));
        var cleanupFailure = new IOException("Lease release failed");
        _repo
            .ReleaseBudgetAlertEmailLeaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(releaseFails ? Task.FromException(cleanupFailure) : Task.CompletedTask);
        var logger = Substitute.For<ILogger<BudgetAlertService>>();
        var sut = new BudgetAlertService(_repo, _clock, _notifier, logger, EmptyConfig);

        Func<Task> act = () => sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        if (releaseFails)
        {
            (await act.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(cleanupFailure);
        }
        else
        {
            await act.Should().NotThrowAsync();
        }
        logger
            .ReceivedCalls()
            .Select(call => call.GetArguments())
            .Should()
            .ContainSingle(args => (LogLevel)args[0]! == LogLevel.Error && ReferenceEquals(args[3], deliveryFailure));
    }

    // No alert sender configured, so the Message-Id domain falls back to its neutral literal.
    private const string MessageIdDomain = "observatory.local";
    private static readonly IConfiguration EmptyConfig = new ConfigurationBuilder().Build();

    private BudgetAlertService Sut() =>
        new(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance, EmptyConfig);

    private static BudgetRule Rule(
        BillingPeriod period,
        Provider? provider = null,
        Instant? lastTriggeredAt = null,
        LocalDate? evaluationStartsOn = null
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            Period = period,
            Provider = provider,
            ThresholdGbp = 10m,
            EvaluationStartsOn = evaluationStartsOn ?? new LocalDate(2026, 6, 1),
            LastTriggeredAt = lastTriggeredAt,
        };

    private void StubRules(params BudgetRule[] rules) =>
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns(rules);

    private void StubBilledSpend(BudgetRule rule, decimal amount)
    {
        if (rule.Period == BillingPeriod.Daily)
        {
            _repo
                .GetDailyBilledSpendGbpAsync(
                    Arg.Any<LocalDate>(),
                    Arg.Any<LocalDate>(),
                    rule.Provider,
                    Arg.Any<CancellationToken>()
                )
                .Returns(amount == 0m ? [] : [new DailyBilledSpend(new LocalDate(2026, 6, 1), amount)]);
            return;
        }

        _repo
            .GetBilledSpendGbpAsync(
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                rule.Provider,
                Arg.Any<CancellationToken>()
            )
            .Returns(amount);
    }

    private void StubSuccessfulDelivery(BudgetRule rule)
    {
        BudgetAlertEmail? pending = null;
        _repo
            .GetOrCreateBudgetAlertAsync(
                Arg.Any<Guid>(),
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<decimal>(),
                Arg.Any<decimal>(),
                Arg.Any<Insight>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                var claimId = Guid.NewGuid();
                pending = new BudgetAlertEmail(
                    claimId,
                    rule.Id,
                    rule.Provider,
                    rule.Period,
                    call.ArgAt<LocalDate>(1),
                    call.ArgAt<LocalDate>(2),
                    call.ArgAt<decimal>(3),
                    call.ArgAt<decimal>(4),
                    call.ArgAt<Instant>(6)
                );
                return new BudgetAlertClaimResult(
                    claimId,
                    true,
                    call.ArgAt<decimal>(3),
                    call.ArgAt<decimal>(4),
                    call.ArgAt<Instant>(6)
                );
            });
        _repo
            .GetDeliverableBudgetAlertEmailsAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(_ => pending is null ? [] : [pending]);
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AlertDeliveryResult.Sent));
    }
}
