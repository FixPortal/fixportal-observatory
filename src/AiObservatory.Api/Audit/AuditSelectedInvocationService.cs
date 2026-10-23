namespace AiObservatory.Api.Audit;

public interface IAuditSelectedInvocationService
{
    Task<SelectedInvocationOutcome> InvokeAsync(
        SelectedInvocationAuthority authority,
        SelectedInvocationRequest request,
        CancellationToken ct
    );
}

/// <summary>One awaited core call; exceptions cannot establish which admission phase committed.</summary>
public sealed class AuditSelectedInvocationService(
    AuditSelectedRunService core,
    IAuditSelectedInvocationMetadataReader metadata
) : IAuditSelectedInvocationService
{
    public async Task<SelectedInvocationOutcome> InvokeAsync(
        SelectedInvocationAuthority authority,
        SelectedInvocationRequest request,
        CancellationToken ct
    )
    {
        if (
            request.SchemaVersion != 1
            || request.ExpectedRevision != authority.Binding.ExpectedRevision
            || request.FullScopeDigest != authority.Binding.ScopeDigest
        )
        {
            return new("conflict", "selected_binding_conflict");
        }
        try
        {
            var result = await core.ExecuteAsync(authority.Binding, Guid.NewGuid(), authority.ExpiresAt, ct);
            ct.ThrowIfCancellationRequested();
            if (result == "pending")
            {
                return new("pending", "selected_pending");
            }
            if (result == "busy")
            {
                return new("busy", "selected_busy");
            }
            if (result != "attempt_finished")
            {
                return Ambiguous();
            }
            var current = await metadata.ReadAsync(authority.Binding, ct);
            ct.ThrowIfCancellationRequested();
            if (
                current == null
                || current.RunId != authority.Binding.RunId
                || current.FullScopeDigest != authority.Binding.ScopeDigest
            )
            {
                return Ambiguous();
            }
            var state = new SelectedCompactRunState(
                current.CurrentRevision,
                current.State,
                current.Stage,
                current.Attempts,
                current.DiagnosticCode,
                current.ResultAvailable,
                current.EvidenceExpiresAt?.ToDateTimeOffset(),
                current.SummaryExpiresAt?.ToDateTimeOffset()
            );
            return current.State == "completed"
                ? new("completed", "selected_completed", state)
                : new("admitted", "selected_admitted", state);
        }
        catch (Exception)
        {
            // Includes cancellation, rollback/commit uncertainty and familiar store codes.
            // No code or CLR type proves a pre-admission origin after ExecuteAsync begins.
            return Ambiguous();
        }
    }

    private static SelectedInvocationOutcome Ambiguous() => new("ambiguous", "selected_ambiguous");
}
