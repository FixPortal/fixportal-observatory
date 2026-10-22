using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Sources;

public static class AuditCapabilities
{
    public static IReadOnlyList<SourceAuditCapability> Sources { get; } =
        Array.AsReadOnly(
            new SourceAuditCapability[]
            {
                new(SourceTool.Claude, true, "claude-jsonl", "1", null),
                new(SourceTool.Claude, true, "claude-jsonl", "2", null),
                new(SourceTool.Codex, true, "codex-jsonl", "1", null),
                new(SourceTool.Copilot, false, null, null, "unsupported_copilot_accounting"),
                new(SourceTool.Gemini, false, null, null, "unsupported_gemini_accounting"),
                new(SourceTool.Kimi, false, null, null, "unsupported_kimi_accounting"),
                new(SourceTool.Grok, false, null, null, "unsupported_grok_accounting"),
                new(SourceTool.Pi, false, null, null, "unsupported_pi_accounting"),
                new(SourceTool.Antigravity, false, null, null, "unsupported_antigravity_accounting"),
            }
        );
}

public sealed record SourceAuditCapability(
    SourceTool Tool,
    bool Supported,
    string? Format,
    string? Version,
    string? ReasonCode
);
