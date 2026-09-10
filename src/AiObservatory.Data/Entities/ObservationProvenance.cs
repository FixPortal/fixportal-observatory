namespace AiObservatory.Data.Entities;

public enum SourceKind
{
    ProviderApi,
    LocalTelemetry,
    Manual,
    Synthetic,
    Legacy,
}

public enum UsageScope
{
    Api,
    Subscription,
    Mixed,
    Unknown,
}

public enum CostBasis
{
    Billed,
    ProviderEstimated,
    ListPriceEstimate,
    Notional,
    None,
    Unknown,
}

public static class UsageSourceIds
{
    public const string LegacyApi = "legacy-api";
    public const string LegacySpend = "legacy-spend";
    public const string ManualLedger = "manual-ledger";
    public const string DemoSeed = "demo-seed";
    public const string GitHubBillingApi = "github-billing-api";
    public const string GitHubActivityApi = "github-activity-api";
    public const string OpenAiUsageApi = "openai-usage-api";
    public const string OpenAiCostsApi = "openai-costs-api";
    public const string CodexLocal = "codex-local";
    public const string AnthropicUsageApi = "anthropic-usage-api";
    public const string AnthropicCostReport = "anthropic-cost-report";
    public const string ClaudeCodeUsageApi = "claude-code-usage-api";
    public const string ClaudeLocal = "claude-local";
    public const string CopilotOrgReport = "copilot-org-report";
    public const string CopilotLocal = "copilot-local";
    public const string GoogleCloudBillingExport = "google-cloud-billing-export";
    public const string KimiLocal = "kimi-local";
}
