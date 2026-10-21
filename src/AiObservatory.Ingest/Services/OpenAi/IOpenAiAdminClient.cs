using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

public interface IOpenAiAdminClient
{
    Task<IReadOnlyList<OpenAiUsageRecord>> GetUsageAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<OpenAiCostRecord>> GetCostsAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    );
}
