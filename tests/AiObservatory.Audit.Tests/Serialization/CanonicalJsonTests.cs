using System.Text;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Serialization;

public sealed class CanonicalJsonTests
{
    /// <summary>Canonical evidence preserves integer precision, nulls, money, and set ordering.</summary>
    [Fact]
    public void OrderAndPrecisionAreStable()
    {
        var record = Record() with
        {
            Counters = new CodexCounters(9007199254740993, null, 0, null, null),
            Cost = 1.2300m,
        };
        var other = Record() with { FileId = "file-b", Sequence = 2 };
        var input = Input(record, other);
        var shuffled = Input(other, record) with
        {
            Scope = Scope() with { Sources = [new(Machine, "codex-local"), new(Machine, "codex-local")] },
        };

        var bytes = AuditCanonicalJson.Serialize(input);
        bytes.Should().Equal(AuditCanonicalJson.Serialize(shuffled));
        var json = Encoding.UTF8.GetString(bytes);
        json.Should()
            .Contain("9007199254740993")
            .And.Contain("\"cost\":\"1.23\"")
            .And.Contain("\"cached_input_tokens\":null");
        bytes
            .Should()
            .NotEqual(
                AuditCanonicalJson.Serialize(
                    Input(record with { Counters = new CodexCounters(9007199254740993, 0, 0, null, null) }, other)
                )
            );
        AuditCanonicalJson.Serialize(AuditCanonicalJson.Deserialize<AuditInput>(bytes)).Should().Equal(bytes);
    }

    /// <summary>Conflicting stable record identities cannot silently overwrite reference facts.</summary>
    [Fact]
    public void Serialize_ConflictingRecordIdentity_RejectsInput()
    {
        var action = () => AuditCanonicalJson.Serialize(Input(Record(), Record() with { Cost = 2m }));
        action.Should().Throw<AuditValidationException>().Which.Code.Should().Be("conflicting_identity");
        AuditCanonicalJson
            .Serialize(Input(Record(), Record()))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(Input(Record())));
    }

    /// <summary>Opaque identifiers and schema allowlisting prevent payload and path leakage.</summary>
    [Theory]
    [InlineData("rawPayload", "{}")]
    [InlineData("transcript", "secret-text")]
    [InlineData("environment", "secret-text")]
    public void Deserialize_UnknownField_RejectsInput(string field, string value)
    {
        var json = Encoding.UTF8.GetString(AuditCanonicalJson.Serialize(Record()));
        json = json.Insert(1, $"\"{field}\":\"{value}\",");
        var action = () => AuditCanonicalJson.Deserialize<SourceUsageRecord>(Encoding.UTF8.GetBytes(json));
        action.Should().Throw<AuditValidationException>().Which.Message.Should().NotContain(value);
    }

    /// <summary>Duplicate object properties cannot disguise a conflicting or forbidden value.</summary>
    [Fact]
    public void Deserialize_DuplicateProperty_RejectsInput()
    {
        var json = Encoding.UTF8.GetString(AuditCanonicalJson.Serialize(Record())).Insert(1, "\"sequence\":999,");
        var action = () => AuditCanonicalJson.Deserialize<SourceUsageRecord>(Encoding.UTF8.GetBytes(json));
        action.Should().Throw<AuditValidationException>().Which.Code.Should().Be("duplicate_property");
    }

    /// <summary>Unsupported schemas fail before independent checks consume their meaning.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Serialize_UnsupportedSchema_RejectsInput(int schema)
    {
        var action = () => AuditCanonicalJson.Serialize(Input(Record()) with { SchemaVersion = schema });
        action.Should().Throw<AuditValidationException>();
    }

    /// <summary>Scope dates are inclusive and limited to thirty-one days, with explicit disjoint checks.</summary>
    [Fact]
    public void Serialize_InvalidScope_RejectsInput()
    {
        AuditScope[] invalid =
        [
            Scope() with
            {
                From = default,
            },
            Scope() with
            {
                To = Scope().From.AddDays(-1),
            },
            Scope() with
            {
                To = Scope().From.AddDays(31),
            },
            Scope() with
            {
                Sources = [],
            },
            Scope() with
            {
                RequestedChecks = [],
            },
            Scope() with
            {
                ExcludedChecks = [CheckCode.SourceAccounting],
            },
            Scope() with
            {
                RequestedChecks = [(CheckCode)99],
            },
        ];
        foreach (var scope in invalid)
        {
            var action = () => AuditCanonicalJson.Serialize(scope);
            action.Should().Throw<AuditValidationException>();
        }
        AuditCanonicalJson.Serialize(Scope() with { To = Scope().From.AddDays(30) }).Should().NotBeEmpty();
    }

    /// <summary>Source counts, UTC dates, and opaque identities are validated on both wire boundaries.</summary>
    [Fact]
    public void Serialize_InvalidSourceFacts_RejectsInput()
    {
        SourceUsageRecord[] invalid =
        [
            Record() with
            {
                Sequence = -1,
            },
            Record() with
            {
                Counters = new CodexCounters(-1, null, null, null, null),
            },
            Record() with
            {
                Timestamp = DateTimeOffset.Parse(
                    "2026-10-01T01:00:00+01:00",
                    System.Globalization.CultureInfo.InvariantCulture
                ),
            },
            Record() with
            {
                Timestamp = default,
            },
            Record() with
            {
                FileId = "C:\\private\\session.jsonl",
            },
            Record() with
            {
                SessionId = "/home/private/session",
            },
            Record() with
            {
                SourceId = " ",
            },
            Record() with
            {
                SourceVersion = "",
            },
            Record() with
            {
                Tool = (SourceTool)99,
            },
            Record() with
            {
                MachineId = Guid.Empty,
            },
            Record() with
            {
                Currency = "usd",
            },
            Record() with
            {
                Cost = -1m,
            },
            Record() with
            {
                Counters = new ClaudeCounters(1, 1, null, null, null, null),
            },
        ];
        foreach (var record in invalid)
        {
            var action = () => AuditCanonicalJson.Serialize(record);
            action.Should().Throw<AuditValidationException>();
        }
    }

    /// <summary>Wire enums are exact lower camel case; numeric or case-ambiguous values are invalid.</summary>
    [Theory]
    [InlineData("\"CODEX\"")]
    [InlineData("99")]
    [InlineData("\"unknownTool\"")]
    public void Deserialize_InvalidEnum_RejectsInput(string replacement)
    {
        var json = Encoding
            .UTF8.GetString(AuditCanonicalJson.Serialize(Record()))
            .Replace("\"tool\":\"codex\"", $"\"tool\":{replacement}", StringComparison.Ordinal);
        var action = () => AuditCanonicalJson.Deserialize<SourceUsageRecord>(Encoding.UTF8.GetBytes(json));
        action.Should().Throw<AuditValidationException>();
    }

    /// <summary>Fractional, exponent-form, and overflowing tokens cannot be coerced into plausible counts.</summary>
    [Theory]
    [InlineData("1.5")]
    [InlineData("1e2")]
    [InlineData("9223372036854775808")]
    [InlineData("-1")]
    public void Deserialize_InvalidInteger_RejectsInput(string number)
    {
        var json = Encoding
            .UTF8.GetString(AuditCanonicalJson.Serialize(Record()))
            .Replace("\"sequence\":1", $"\"sequence\":{number}", StringComparison.Ordinal);
        var action = () => AuditCanonicalJson.Deserialize<SourceUsageRecord>(Encoding.UTF8.GetBytes(json));
        action.Should().Throw<AuditValidationException>();
    }

    /// <summary>Explicit version identities and missing required values are never inferred from a clock.</summary>
    [Fact]
    public void Deserialize_MissingRequiredOrNullValue_RejectsInput()
    {
        var json = Encoding.UTF8.GetString(AuditCanonicalJson.Serialize(Input(Record())));
        foreach (
            var invalid in new[]
            {
                json.Replace(",\"verifierVersion\":\"v1\"", "", StringComparison.Ordinal),
                json.Replace("\"verifierVersion\":\"v1\"", "\"verifierVersion\":null", StringComparison.Ordinal),
            }
        )
        {
            var action = () => AuditCanonicalJson.Deserialize<AuditInput>(Encoding.UTF8.GetBytes(invalid));
            action.Should().Throw<AuditValidationException>();
        }
    }

    /// <summary>Operational linkage never changes deterministic result bytes.</summary>
    [Fact]
    public void Serialize_ResultScope_OmitsOperationalRunIdentity()
    {
        var result = new AuditResult(
            1,
            AuditResultScope.FromScope(Scope() with { PredecessorRunId = Machine, EvaluationOfRunId = Machine }),
            new string('a', 64),
            "v1",
            "r1",
            [],
            new(0, 0, []),
            [],
            CheckVerdict.Incomplete
        );
        var json = Encoding.UTF8.GetString(AuditCanonicalJson.Serialize(result));
        json.Should()
            .NotContain("predecessor")
            .And.NotContain("evaluationOf")
            .And.NotContain("elapsed")
            .And.NotContain("runId");
    }

    /// <summary>Content digests use SHA-256 of the exact canonical bytes.</summary>
    [Fact]
    public void ContentHash_KnownBytes_ReturnsSha256()
    {
        AuditCanonicalJson
            .ContentHash("abc"u8)
            .Should()
            .Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    /// <summary>Checked token arithmetic reports a sanitized stable diagnostic rather than wrapping.</summary>
    [Fact]
    public void AddCounters_Overflow_RejectsInput()
    {
        AuditNumbers.AddCounters(9007199254740993, 1).Should().Be(9007199254740994);
        var action = () => AuditNumbers.AddCounters(long.MaxValue, 1);
        action.Should().Throw<AuditValidationException>().Which.Code.Should().Be("counter_overflow");
    }

    /// <summary>Model aliases may contain provider separators but cannot carry absolute filesystem paths.</summary>
    [Fact]
    public void Serialize_AbsolutePathInModel_RejectsInput()
    {
        var action = () => AuditCanonicalJson.Serialize(Record() with { Model = "/home/private/model" });
        action.Should().Throw<AuditValidationException>();
        AuditCanonicalJson.Serialize(Record() with { Model = "provider/model" }).Should().NotBeEmpty();
    }

    private static readonly Guid Machine = Guid.Parse("11111111-1111-1111-1111-111111111111");

    internal static AuditScope Scope() =>
        new(new(2026, 10, 1), new(2026, 10, 7), [new(Machine, "codex-local")], [CheckCode.SourceAccounting], [], false);

    internal static SourceUsageRecord Record() =>
        new()
        {
            MachineId = Machine,
            SourceId = "codex-local",
            SourceFormat = "codex-jsonl",
            SourceVersion = "1",
            Tool = SourceTool.Codex,
            Kind = UsageRecordKind.CumulativeUsage,
            Generation = "generation-a",
            FileId = "file-a",
            SessionId = "session-a",
            Sequence = 1,
            Timestamp = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            Model = "gpt-6",
            Provider = "openai",
            Counters = new CodexCounters(10, null, 1, null, 11),
            Currency = "USD",
            Closure = new(ClosureState.Open, null, null),
            Pricing = new(ServiceTier.Standard, false, null, null),
        };

    internal static AuditInput Input(params SourceUsageRecord[] records) =>
        new()
        {
            SchemaVersion = 1,
            VerifierVersion = "v1",
            RuleVersion = "r1",
            Scope = Scope(),
            Records = records,
        };
}
