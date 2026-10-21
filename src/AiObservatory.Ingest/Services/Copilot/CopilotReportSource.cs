using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Ingest.Sources;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Ingest.Services.Copilot;

public sealed class CopilotReportSource(
    ICopilotReportClient client,
    AiObservatoryDbContext db,
    IClock clock,
    ILogger<CopilotReportSource> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.CopilotOrgReport;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        var records = await client.GetLatestOrganizationReportAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var inRange = records.Where(record => record.Day >= from && record.Day <= through).ToArray();
        if (inRange.Length == 0)
        {
            return new SourceIngestionResult(null);
        }

        var acquisitionAt = clock.GetCurrentInstant();
        var persistedObservationTimes = new List<Instant>(inRange.Length);
        foreach (var record in inRange)
        {
            var reportKey = BuildReportKey(record.OrganizationId, record.Day);
            var observedAt = record.ObservedAt ?? acquisitionAt;
            var existing = await db.CopilotDailyReports.SingleOrDefaultAsync(
                row => row.SourceId == SourceId && row.ReportKey == reportKey,
                cancellationToken
            );
            if (existing is null)
            {
                db.CopilotDailyReports.Add(NewEntity(record, reportKey, observedAt));
                persistedObservationTimes.Add(observedAt);
                continue;
            }
            if (SameProviderFacts(existing, record))
            {
                persistedObservationTimes.Add(existing.ObservedAt);
                continue;
            }

            existing.Day = record.Day;
            existing.DailyActiveUsers = record.DailyActiveUsers;
            existing.WeeklyActiveUsers = record.WeeklyActiveUsers;
            existing.MonthlyActiveUsers = record.MonthlyActiveUsers;
            existing.UserInitiatedInteractionCount = record.UserInitiatedInteractionCount;
            existing.CodeGenerationActivityCount = record.CodeGenerationActivityCount;
            existing.CodeAcceptanceActivityCount = record.CodeAcceptanceActivityCount;
            existing.RawPayload = record.RawJson;
            existing.ObservedAt = observedAt;
            persistedObservationTimes.Add(observedAt);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
        logger.LogInformation("Copilot: retained {Count} organization report days", inRange.Length);
        return new SourceIngestionResult(persistedObservationTimes.Max());
    }

    private static CopilotDailyReport NewEntity(
        CopilotDailyReportRecord record,
        string reportKey,
        Instant observedAt
    ) =>
        new()
        {
            Day = record.Day,
            ReportKey = reportKey,
            DailyActiveUsers = record.DailyActiveUsers,
            WeeklyActiveUsers = record.WeeklyActiveUsers,
            MonthlyActiveUsers = record.MonthlyActiveUsers,
            UserInitiatedInteractionCount = record.UserInitiatedInteractionCount,
            CodeGenerationActivityCount = record.CodeGenerationActivityCount,
            CodeAcceptanceActivityCount = record.CodeAcceptanceActivityCount,
            RawPayload = record.RawJson,
            ObservedAt = observedAt,
        };

    private static bool SameProviderFacts(CopilotDailyReport entity, CopilotDailyReportRecord record)
    {
        using var stored = JsonDocument.Parse(entity.RawPayload);
        using var incoming = JsonDocument.Parse(record.RawJson);
        return entity.Day == record.Day
            && entity.DailyActiveUsers == record.DailyActiveUsers
            && entity.WeeklyActiveUsers == record.WeeklyActiveUsers
            && entity.MonthlyActiveUsers == record.MonthlyActiveUsers
            && entity.UserInitiatedInteractionCount == record.UserInitiatedInteractionCount
            && entity.CodeGenerationActivityCount == record.CodeGenerationActivityCount
            && entity.CodeAcceptanceActivityCount == record.CodeAcceptanceActivityCount
            && JsonElement.DeepEquals(stored.RootElement, incoming.RootElement);
    }

    private static string BuildReportKey(string organizationId, LocalDate day)
    {
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var material = $"{organizationId.Length.ToString(CultureInfo.InvariantCulture)}:{organizationId}{date}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return $"copilot-org:{date}:{hash}";
    }
}
