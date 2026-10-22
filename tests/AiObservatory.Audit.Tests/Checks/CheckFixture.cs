using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Tests.Checks;

internal static class CheckFixture
{
    internal static readonly DateOnly Day = new(2026, 10, 20);
    internal static readonly Guid Machine = Guid.Parse("00000000-0000-0000-0000-000000000001");
    internal static readonly string Digest = new('a', 64);

    internal static AuditInput Input(params FrozenEvent[] events) =>
        new()
        {
            SchemaVersion = 1,
            VerifierVersion = "test-1",
            RuleVersion = "test-1",
            Scope = new(Day, Day.AddDays(1), [new(Machine, "local")], [CheckCode.EventAggregation], [], false),
            Events = events,
        };

    internal static FrozenEvent Event(int id = 1) =>
        new(
            new Guid(id, 0, 0, new byte[8]),
            null,
            "local",
            new(SourceKind.LocalTelemetry, UsageScope.Subscription, AccountingBasis.Notional),
            new(2026, 10, 20, 23, 0, 0, TimeSpan.Zero),
            new(2026, 10, 22, 1, 0, 0, TimeSpan.Zero),
            new(2026, 10, 22, 1, 1, 0, TimeSpan.Zero),
            "provider",
            null,
            new(10, 20, null, 3, null, 7),
            1.25m,
            -0.25m,
            null,
            null,
            null,
            null,
            Digest,
            null
        );

    internal static FrozenAggregate Aggregate() =>
        new(
            new(
                Day,
                "provider",
                "",
                "local",
                SourceKind.LocalTelemetry,
                UsageScope.Subscription,
                AccountingBasis.Notional
            ),
            10,
            20,
            0,
            3,
            0,
            1.25m,
            0,
            -0.25m,
            0,
            1
        );

    internal static FrozenSpend Spend(int id, string currency, decimal amount, decimal gbp) =>
        new(
            new Guid(id, 0, 0, new byte[8]),
            "local",
            null,
            null,
            null,
            Machine,
            Machine,
            Day,
            null,
            null,
            new(SourceKind.Manual, UsageScope.Api, AccountingBasis.Billed),
            currency,
            amount,
            gbp,
            0.8m,
            null,
            new(2026, 10, 22, 1, 0, 0, TimeSpan.Zero),
            new(2026, 10, 22, 1, 0, 0, TimeSpan.Zero),
            Digest
        );
}
