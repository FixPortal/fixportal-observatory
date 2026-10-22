using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Serialization;

/// <summary>Evaluation identity retains evidence and policy while omitting operational run linkage.</summary>
public static class AuditComputationInput
{
    public static AuditInput Normalize(AuditInput input)
    {
        input = AuditCanonicalJson.Deserialize<AuditInput>(AuditCanonicalJson.Serialize(input));
        return input with
        {
            Scope = WithoutLinks(input.Scope),
            Captures = input.Captures.Select(c => c with { Scope = WithoutLinks(c.Scope) }).ToArray(),
        };
    }

    public static string ContentDigest(AuditInput input) =>
        AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(Normalize(input)));

    private static AuditScope WithoutLinks(AuditScope scope) =>
        scope with
        {
            PredecessorRunId = null,
            EvaluationOfRunId = null,
        };
}
