using System.Globalization;
using System.Text;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Serialization;

public sealed class ProjectionContractTests
{
    /// <summary>Frozen projections preserve signed spend, every aggregate dimension, pricing and frontend observations.</summary>
    [Fact]
    public void Serialize_CompleteFrozenInput_RoundTripsAndSorts()
    {
        var input = CompleteInput();
        var shuffled = input with
        {
            Events = input.Events.Reverse().ToArray(),
            Aggregates = input.Aggregates.Reverse().ToArray(),
            Spend = input.Spend.Reverse().ToArray(),
            PricingCatalogs = input.PricingCatalogs.Reverse().ToArray(),
            FxRates = input.FxRates.Reverse().ToArray(),
        };
        var bytes = AuditCanonicalJson.Serialize(input);
        AuditCanonicalJson.Serialize(shuffled).Should().Equal(bytes);
        AuditCanonicalJson.Serialize(AuditCanonicalJson.Deserialize<AuditInput>(bytes)).Should().Equal(bytes);
        Encoding.UTF8.GetString(bytes).Should().Contain("\"amount\":\"-1.23\"").And.Contain("\"cacheWrite1hTokens\":1");
        AuditCanonicalJson
            .Serialize(
                input with
                {
                    Aggregates =
                    [
                        input.Aggregates[0],
                        input.Aggregates[0] with
                        {
                            Key = input.Aggregates[0].Key with { CostBasis = AccountingBasis.Notional },
                        },
                    ],
                }
            )
            .Should()
            .NotBeEmpty();
    }

    /// <summary>Every frozen identity rejects conflicting rows instead of choosing whichever arrived last.</summary>
    [Fact]
    public void Serialize_ConflictingFrozenIdentities_RejectsInput()
    {
        var input = CompleteInput();
        AuditInput[] conflicts =
        [
            input with
            {
                Events = [input.Events[0], input.Events[0] with { CostUsd = 4m }],
            },
            input with
            {
                Aggregates = [input.Aggregates[0], input.Aggregates[0] with { RequestCount = 4 }],
            },
            input with
            {
                Spend = [input.Spend[0], input.Spend[0] with { Amount = 4m }],
            },
            input with
            {
                PricingCatalogs = [input.PricingCatalogs[0], input.PricingCatalogs[0] with { Currency = "GBP" }],
            },
            input with
            {
                FxRates = [input.FxRates[0], input.FxRates[0] with { Rate = 4m }],
            },
        ];
        foreach (var conflict in conflicts)
        {
            var action = () => AuditCanonicalJson.Serialize(conflict);
            action.Should().Throw<AuditValidationException>().Which.Code.Should().Be("conflicting_identity");
        }
    }

    /// <summary>Root record collections obey the same stable identity contract as AuditInput records.</summary>
    [Fact]
    public void Serialize_RootCollectionConflict_RejectsInput()
    {
        var record = CanonicalJsonTests.Record();
        var action = () => AuditCanonicalJson.Serialize(new[] { record, record with { Cost = 7m } });
        action.Should().Throw<AuditValidationException>().Which.Code.Should().Be("conflicting_identity");
    }

    /// <summary>Named tolerances encode exact token checks and half-unit money precision, never a guessed epsilon.</summary>
    [Fact]
    public void Serialize_InvalidTolerance_RejectsInput()
    {
        NamedTolerance[] invalid =
        [
            new("tokens", ValueUnit.Tokens, null, null, 1m),
            new("money", ValueUnit.Money, "USD", null, .01m),
            new("money", ValueUnit.Money, "USD", 2, .01m),
            new("money", ValueUnit.Money, "USD", 29, 0m),
            new("money", ValueUnit.Money, null, 2, .005m),
        ];
        foreach (var tolerance in invalid)
        {
            var action = () => AuditCanonicalJson.Serialize(tolerance);
            action.Should().Throw<AuditValidationException>();
        }
        AuditCanonicalJson.Serialize(new NamedTolerance("usd", ValueUnit.Money, "USD", 2, .005m)).Should().NotBeEmpty();
        AuditCanonicalJson.Serialize(new NamedTolerance("usd", ValueUnit.Money, "USD", null, 0m)).Should().NotBeEmpty();
    }

    /// <summary>Money lexemes that require rounding, exponent interpretation, or numeric coercion are rejected.</summary>
    [Theory]
    [InlineData("\"1.00000000000000000000000000001\"")]
    [InlineData("\"1e2\"")]
    [InlineData("\".5\"")]
    [InlineData("1.23")]
    public void Deserialize_InexactMoney_RejectsInput(string money)
    {
        var json = Encoding.UTF8.GetString(
            AuditCanonicalJson.Serialize(CanonicalJsonTests.Record() with { Cost = 1.23m })
        );
        json = json.Replace("\"cost\":\"1.23\"", $"\"cost\":{money}", StringComparison.Ordinal);
        var action = () => AuditCanonicalJson.Deserialize<SourceUsageRecord>(Encoding.UTF8.GetBytes(json));
        action.Should().Throw<AuditValidationException>();
    }

    /// <summary>Finding numeric strings canonicalize without retaining insignificant decimal zeros.</summary>
    [Fact]
    public void Serialize_NumericFindingValues_NormalizesLexemes()
    {
        var finding = Finding() with
        {
            Expected = new(AuditValueKind.Decimal, "1.2300"),
            Actual = new(AuditValueKind.Integer, "0002"),
        };
        var json = Encoding.UTF8.GetString(AuditCanonicalJson.Serialize(finding));
        json.Should().Contain("\"value\":\"1.23\"").And.Contain("\"value\":\"2\"").And.NotContain("1.2300");
    }

    /// <summary>UTC timestamps require an explicit offset so local machine timezone never changes evidence.</summary>
    [Fact]
    public void Deserialize_TimestampWithoutOffset_RejectsInput()
    {
        var json = Encoding
            .UTF8.GetString(AuditCanonicalJson.Serialize(CanonicalJsonTests.Record()))
            .Replace("2026-10-01T00:00:00.0000000Z", "2026-10-01T00:00:00", StringComparison.Ordinal);
        var action = () => AuditCanonicalJson.Deserialize<SourceUsageRecord>(Encoding.UTF8.GetBytes(json));
        action.Should().Throw<AuditValidationException>();
    }

    /// <summary>Capture chunks retain index ordering while manifest identity and date bounds are validated.</summary>
    [Fact]
    public void Serialize_Manifest_ValidatesCountsAndPreservesChunkOrder()
    {
        var manifest = Manifest();
        AuditCanonicalJson
            .Serialize(manifest)
            .Should()
            .NotEqual(
                AuditCanonicalJson.Serialize(manifest with { ChunkHashes = manifest.ChunkHashes.Reverse().ToArray() })
            );
        CaptureManifest[] invalid =
        [
            manifest with
            {
                ReadFiles = 4,
            },
            manifest with
            {
                ChangedFiles = -1,
            },
            manifest with
            {
                RecordCount = 1_000_001,
            },
            manifest with
            {
                SealedAt = manifest.ScanStartedAt.AddSeconds(-1),
            },
            manifest with
            {
                ObservedTo = null,
            },
            manifest with
            {
                MachineId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            },
        ];
        foreach (var candidate in invalid)
        {
            var action = () => AuditCanonicalJson.Serialize(candidate);
            action.Should().Throw<AuditValidationException>();
        }
    }

    /// <summary>Result findings and checks sort deterministically while retaining coverage diagnostics.</summary>
    [Fact]
    public void Serialize_ResultFindings_ShuffledEvidenceProducesSameBytes()
    {
        var finding = Finding() with { EvidenceRefs = ["evidence-b", "evidence-a"] };
        var other = finding with { Field = "outputTokens" };
        var result = new AuditResult(
            1,
            AuditResultScope.FromScope(CanonicalJsonTests.Scope()),
            Hash,
            "v1",
            "r1",
            [new(CheckCode.SourceAccounting, CheckVerdict.Mismatched, new(1, 1, []), [finding, other])],
            new(1, 1, []),
            [finding, other],
            CheckVerdict.Mismatched
        );
        var shuffled = result with { Findings = [other, finding with { EvidenceRefs = ["evidence-a", "evidence-b"] }] };
        AuditCanonicalJson.Serialize(result).Should().Equal(AuditCanonicalJson.Serialize(shuffled));
    }

    /// <summary>Missing production models retain their empty aggregate-key dimension rather than dropping rows.</summary>
    [Fact]
    public void Serialize_EmptyAggregateModel_PreservesUnknownModelDimension()
    {
        var aggregate = CompleteInput().Aggregates[0];
        var bytes = AuditCanonicalJson.Serialize(aggregate with { Key = aggregate.Key with { Model = "" } });
        Encoding.UTF8.GetString(bytes).Should().Contain("\"model\":\"\"");
        AuditCanonicalJson.Serialize(AuditCanonicalJson.Deserialize<FrozenAggregate>(bytes)).Should().Equal(bytes);
    }

    /// <summary>Usage facts require counters, but session metadata may truthfully carry none.</summary>
    [Fact]
    public void Serialize_MissingUsageCounters_RejectsInput()
    {
        var record = CanonicalJsonTests.Record() with { Counters = null };
        var action = () => AuditCanonicalJson.Serialize(record);
        action.Should().Throw<AuditValidationException>();
        AuditCanonicalJson.Serialize(record with { Kind = UsageRecordKind.SessionMetadata }).Should().NotBeEmpty();
    }

    /// <summary>Codex retains both total_token_usage and last_token_usage from one source observation.</summary>
    [Fact]
    public void Serialize_CodexLastUsage_RetainsIndependentSourceFacts()
    {
        var counters = new CodexCounters(100, 40, 20, 5, 120) { LastTokenUsage = new(10, 4, 2, 1, 12) };
        var record = CanonicalJsonTests.Record() with { Counters = counters };
        var bytes = AuditCanonicalJson.Serialize(record);
        Encoding
            .UTF8.GetString(bytes)
            .Should()
            .Contain("\"last_token_usage\":")
            .And.Contain("\"input_tokens\":100")
            .And.Contain("\"input_tokens\":10");
        AuditCanonicalJson.Serialize(AuditCanonicalJson.Deserialize<SourceUsageRecord>(bytes)).Should().Equal(bytes);
        var action = () =>
            AuditCanonicalJson.Serialize(
                record with
                {
                    Counters = counters with { LastTokenUsage = new(-1, null, null, null, null) },
                }
            );
        action.Should().Throw<AuditValidationException>();
    }

    /// <summary>Only genuine numeric production summary observations can enter presentation evidence.</summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("---")]
    public void Serialize_NonnumericPresentationValue_RejectsInput(string formatted)
    {
        var action = () =>
            AuditCanonicalJson.Serialize(new PresentationSummaryValue("cost", "all", 1m, formatted, "USD"));
        action.Should().Throw<AuditValidationException>();
    }

    private static readonly string Hash = new('a', 64);
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
        "2026-10-01T00:00:00Z",
        CultureInfo.InvariantCulture
    );

    private static ObservationProvenance Provenance() =>
        new(SourceKind.LocalTelemetry, UsageScope.Subscription, AccountingBasis.ListPriceEstimate);

    private static AuditFinding Finding() =>
        new(
            CheckCode.SourceAccounting,
            "source.tokens",
            "group-a",
            ["evidence-a"],
            "inputTokens",
            new(AuditValueKind.Integer, "1"),
            new(AuditValueKind.Integer, "2"),
            new(AuditValueKind.Integer, "1"),
            ValueUnit.Tokens,
            null,
            AccountingBasis.None,
            "token_mismatch"
        );

    private static CaptureManifest Manifest() =>
        new(
            1,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            CanonicalJsonTests.Record().MachineId,
            "generation-a",
            "extractor-1",
            Timestamp,
            Timestamp.AddSeconds(1),
            CanonicalJsonTests.Scope(),
            [new("codex-local", "codex-jsonl", "1")],
            3,
            2,
            1,
            0,
            1,
            new(2026, 10, 1),
            new(2026, 10, 1),
            [new("skipped_metadata", 1)],
            [Hash, new('b', 64)],
            Timestamp.AddSeconds(2)
        );

    private static AuditInput CompleteInput()
    {
        var first = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var second = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var dimensions = new PricingDimensions(ServiceTier.Standard, false, null, null);
        var row = new FrozenEvent(
            first,
            "event-a",
            "codex-local",
            Provenance(),
            Timestamp,
            Timestamp,
            Timestamp,
            "openai",
            "gpt-6",
            new(10, 1, null, 0, 0, null),
            1.23m,
            null,
            "codex",
            "session-a",
            null,
            null,
            Hash,
            new("price-a", Hash, "rate-a", dimensions, new(2026, 10, 1))
        );
        var aggregate = new FrozenAggregate(
            new(
                new(2026, 10, 1),
                "openai",
                "gpt-6",
                "codex-local",
                SourceKind.LocalTelemetry,
                UsageScope.Subscription,
                AccountingBasis.ListPriceEstimate
            ),
            10,
            1,
            0,
            1,
            1,
            1.23m,
            0,
            0,
            1,
            1
        );
        var spend = new FrozenSpend(
            first,
            "billing-api",
            "entry-a",
            "account-a",
            "period-a",
            first,
            second,
            new(2026, 10, 1),
            new(2026, 10, 1),
            new(2026, 10, 7),
            new(SourceKind.ProviderApi, UsageScope.Subscription, AccountingBasis.Billed),
            "USD",
            -1.23m,
            -.984m,
            .8m,
            "fx-a",
            Timestamp,
            Timestamp,
            Hash
        );
        var price = new FrozenPricing(
            "pricing-api",
            "price-a",
            Hash,
            "openai",
            "USD",
            Timestamp,
            [
                new(
                    "rate-a",
                    "gpt-6",
                    ["gpt-6-alias"],
                    new(2026, 10, 1),
                    null,
                    true,
                    PriceMeter.Input,
                    dimensions,
                    null,
                    1_000_000,
                    1.23m,
                    null
                ),
            ]
        );
        var fx = new FrozenFx("fx-provider", "fx-a", Hash, new(2026, 10, 1), "USD", "GBP", .8m, Timestamp);
        return CanonicalJsonTests.Input(CanonicalJsonTests.Record()) with
        {
            Captures = [Manifest()],
            Events = [row, row with { RowId = second }],
            Aggregates = [aggregate, aggregate with { Key = aggregate.Key with { Model = "gpt-6-fast" } }],
            Spend = [spend, spend with { RowId = second }],
            PricingCatalogs = [price, price with { Id = "price-b" }],
            FxRates = [fx, fx with { Id = "fx-b" }],
            SourceStatuses = [new("codex-local", true, true, 60, Timestamp, Timestamp, Timestamp, null, 0, null)],
            Tolerances =
            [
                new("event.costUsd", ValueUnit.Money, "USD", null, 0m),
                new("tokens", ValueUnit.Tokens, null, null, 0m),
            ],
            PresentationProbe = new(Hash, "build-a", "commit-a", Hash, [new("costUsd", "all", 1.23m, "$1.23", "USD")]),
        };
    }
}
