namespace AiObservatory.Audit.Contracts;

/// <summary>Only stable codes cross error boundaries; private rejected values never enter the message.</summary>
public sealed class AuditValidationException(string code) : ArgumentException(code)
{
    public string Code { get; } = code;
}
