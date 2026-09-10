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
}
