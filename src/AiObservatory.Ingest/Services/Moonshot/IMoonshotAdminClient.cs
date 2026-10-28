using NodaTime;

namespace AiObservatory.Ingest.Services.Moonshot;

public interface IMoonshotAdminClient
{
    /// <summary>Reads the daily bills for the inclusive bill-date range, flattened to billed lines.</summary>
    Task<IReadOnlyList<MoonshotBillRecord>> GetDailyBillsAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    );
}
