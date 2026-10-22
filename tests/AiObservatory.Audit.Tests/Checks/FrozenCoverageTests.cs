using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Checks;

public sealed class FrozenCoverageTests
{
    [Fact]
    public void IndependentAggregateProof_UnrelatedSourceGapDoesNotInvalidateRequestedCheck()
    {
        var input = CheckFixture.Input(CheckFixture.Event()) with
        {
            Aggregates = [CheckFixture.Aggregate()],
            SourceQualifications = [new("local", "capture_missing", false)],
        };
        var result = new AuditVerifier().Verify(input);
        result.Verdict.Should().Be(CheckVerdict.Matched);
        result.Checks.Single().Verdict.Should().Be(CheckVerdict.Matched);
    }

    [Theory]
    [InlineData(false, CheckVerdict.Incomplete)]
    [InlineData(true, CheckVerdict.Mismatched)]
    public void PartialProceed_CannotCertifyWholeRunButIndependentMismatchWins(bool mismatch, CheckVerdict verdict)
    {
        var input = CheckFixture.Input(CheckFixture.Event()) with
        {
            Aggregates = [CheckFixture.Aggregate() with { InputTokens = mismatch ? 11 : 10 }],
            SourceQualifications = [new("local", "capture_missing", false)],
            PartialEvidenceAcknowledged = true,
        };
        var result = new AuditVerifier().Verify(input);
        result.Verdict.Should().Be(verdict);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "partial_capture_acknowledged");
        AuditCanonicalJson
            .Serialize(
                new AuditVerifier().Verify(
                    AuditCanonicalJson.Deserialize<AuditInput>(AuditCanonicalJson.Serialize(input))
                )
            )
            .Should()
            .Equal(AuditCanonicalJson.Serialize(result));
    }
}
