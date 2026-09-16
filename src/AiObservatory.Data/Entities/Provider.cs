using System.Text.Json.Serialization;

namespace AiObservatory.Data.Entities;

// OpenAI is the persisted provider identifier (HasConversion<string> stores the member
// name); changing its casing would break the established external contract.
// ReSharper disable once InconsistentNaming
public enum Provider
{
    Anthropic,
    Copilot,
    Google,

    /// <remarks>
    /// The JSON name is pinned lower-case because the global
    /// <c>JsonStringEnumConverter(CamelCase)</c> renders this member as <c>openAI</c> —
    /// every other member is single-word and camel-cases to the all-lower form the
    /// frontend keys on, so only this one diverged. `AggregatesEndpoints` sidestepped it
    /// by hand-lowercasing its own projection, which left the two paths disagreeing:
    /// aggregates said <c>openai</c>, vendors and subscriptions said <c>openAI</c>, and
    /// the frontend's PROVIDERS lookup (keyed <c>openai</c>) silently failed on the
    /// latter, rendering "No provider" for a vendor that had one. Pinning the name here
    /// fixes every endpoint at once rather than per-call-site.
    /// </remarks>
    [JsonStringEnumMemberName("openai")]
    OpenAI,

    Moonshot,

    /// <remarks>
    /// xAI reaches this estate through the Grok CLI only: the models page publishes API
    /// prices, but the API channel needs an <c>XAI_API_KEY</c> this machine does not hold.
    /// The provider therefore carries a pricing snapshot and CLI telemetry, not API usage.
    /// </remarks>
    Xai,

    /// <remarks>
    /// Meta is the model vendor; OpenRouter is the route that actually bills. Filing the
    /// provider under the vendor matches Moonshot (vendor) being fed by <c>kimi-local</c>
    /// (route), and keeps the route visible where it belongs — in the source id.
    /// </remarks>
    Meta,
}
