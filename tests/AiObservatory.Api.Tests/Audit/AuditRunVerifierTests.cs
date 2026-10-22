using AiObservatory.Api.Audit;
using AiObservatory.Audit;
using AiObservatory.Audit.Contracts;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests.Audit;

public sealed class AuditRunVerifierTests
{
    [Theory]
    [InlineData(1, "1.0.0", "1")]
    [InlineData(1, "1.0.0", "2")]
    [InlineData(1, "1.1.0", "1")]
    [InlineData(2, "1.1.0", "2")]
    public async Task IncompatiblePinnedIdentityIsRefusedBeforeVerification(int schema, string verifier, string rule)
    {
        var input = Input() with { SchemaVersion = schema, VerifierVersion = verifier, RuleVersion = rule };
        Func<Task> action = () => new AuditRunVerifier().VerifyAsync(input, TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<AuditValidationException>().WithMessage("incompatible_audit_verifier");
    }

    [Fact]
    public async Task ExplicitCurrentIdentityCanBeEvaluated()
    {
        var result = await new AuditRunVerifier().VerifyAsync(Input(), TestContext.Current.CancellationToken);
        result.VerifierVersion.Should().Be("1.1.0");
        result.RuleVersion.Should().Be("2");
    }

    private static AuditInput Input() =>
        new()
        {
            SchemaVersion = AuditVerifierIdentity.SchemaVersion,
            VerifierVersion = AuditVerifierIdentity.VerifierVersion,
            RuleVersion = AuditVerifierIdentity.RuleVersion,
            Scope = new(
                new(2026, 10, 20),
                new(2026, 10, 20),
                [new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "synthetic")],
                [CheckCode.PricingArithmetic],
                [],
                false
            ),
        };
}
