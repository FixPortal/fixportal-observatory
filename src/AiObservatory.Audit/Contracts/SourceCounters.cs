using System.Text.Json.Serialization;

namespace AiObservatory.Audit.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "counterKind")]
[JsonDerivedType(typeof(ClaudeCounters), "claude")]
[JsonDerivedType(typeof(CodexCounters), "codex")]
public abstract record SourceCounters;

/// <summary>Claude input excludes cache reads/creation. Five-minute and one-hour creation are subsets of creation.</summary>
public sealed record ClaudeCounters(
    [property: JsonPropertyName("input_tokens")] long? InputTokens,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens,
    [property: JsonPropertyName("cache_creation_input_tokens")] long? CacheCreationInputTokens,
    [property: JsonPropertyName("cache_read_input_tokens")] long? CacheReadInputTokens,
    [property: JsonPropertyName("ephemeral_5m_input_tokens")] long? CacheCreation5mInputTokens,
    [property: JsonPropertyName("ephemeral_1h_input_tokens")] long? CacheCreation1hInputTokens
) : SourceCounters;

/// <summary>Codex total_token_usage fields: input includes cached input and output includes reasoning output.</summary>
public sealed record CodexCounters(
    [property: JsonPropertyName("input_tokens")] long? InputTokens,
    [property: JsonPropertyName("cached_input_tokens")] long? CachedInputTokens,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens,
    [property: JsonPropertyName("reasoning_output_tokens")] long? ReasoningOutputTokens,
    [property: JsonPropertyName("total_tokens")] long? TotalTokens
) : SourceCounters
{
    [JsonPropertyName("last_token_usage")]
    public CodexTokenUsage? LastTokenUsage { get; init; }
}

/// <summary>Optional last_token_usage facts from the same observation, independent of cumulative totals.</summary>
public sealed record CodexTokenUsage(
    [property: JsonPropertyName("input_tokens")] long? InputTokens,
    [property: JsonPropertyName("cached_input_tokens")] long? CachedInputTokens,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens,
    [property: JsonPropertyName("reasoning_output_tokens")] long? ReasoningOutputTokens,
    [property: JsonPropertyName("total_tokens")] long? TotalTokens
);
