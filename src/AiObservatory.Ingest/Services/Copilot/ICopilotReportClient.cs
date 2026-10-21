namespace AiObservatory.Ingest.Services.Copilot;

public interface ICopilotReportClient
{
    Task<IReadOnlyList<CopilotDailyReportRecord>> GetLatestOrganizationReportAsync(
        CancellationToken cancellationToken = default
    );
}
