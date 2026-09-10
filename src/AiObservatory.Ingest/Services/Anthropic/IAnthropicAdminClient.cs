using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public interface IAnthropicAdminClient
{
    Task<IReadOnlyList<AnthropicUsageRecord>> GetMessageUsageAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<AnthropicCostRecord>> GetCostsAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<ClaudeCodeUsageRecord>> GetClaudeCodeUsageAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    );
}
