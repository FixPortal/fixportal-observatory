using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;
using AiObservatory.Data;
using AiObservatory.Data.Audit;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Audit;

public sealed class AuditReadinessService(
    AiObservatoryDbContext db,
    AuditStore store,
    IClock clock,
    IOptions<AuditOptions> options
)
{
    public async Task<AuditReadinessDto> GetAsync(AuditScope scope, CancellationToken ct)
    {
        if (scope.To < scope.From || scope.To.DayNumber - scope.From.DayNumber >= options.Value.MaxRunWindowDays)
        {
            throw new AuditStoreException("invalid_scope_dates", 400);
        }
        var captures = await store.AvailableCapturesAsync(ct);
        var sourceIds = scope.Sources.Select(s => s.SourceId).Distinct().ToArray();
        var statuses = await db
            .SourceSyncStates.AsNoTracking()
            .Where(s => sourceIds.Contains(s.SourceId))
            .ToArrayAsync(ct);
        var result = new List<AuditSourceReadinessDto>();
        foreach (var selection in scope.Sources)
        {
            var matching = captures
                .Where(c => selection.MachineId == Guid.Empty || c.MachineId == selection.MachineId)
                .Select(c => (Capture: c, Manifest: AuditCanonicalJson.Deserialize<CaptureManifest>(c.Manifest)))
                .Where(c =>
                    c.Manifest.Scope.Sources.Any(s => s.SourceId == selection.SourceId)
                    && c.Manifest.Scope.From <= scope.From
                    && c.Manifest.Scope.To >= scope.To
                )
                .ToArray();
            var diagnostics = new List<string>();
            if (matching.Length == 0)
            {
                diagnostics.Add("capture_missing");
            }
            diagnostics.AddRange(matching.SelectMany(c => CaptureDiagnostics(c.Manifest, selection.SourceId)));
            var status = statuses.FirstOrDefault(s => s.SourceId == selection.SourceId);
            diagnostics.AddRange(StatusDiagnostics(status));
            diagnostics.Add("revision_unverified");
            diagnostics.Add("session_closure_unverified");
            result.Add(
                new(
                    selection.SourceId,
                    selection.MachineId == Guid.Empty ? null : selection.MachineId,
                    matching.Select(c => c.Capture.Id).ToArray(),
                    diagnostics.Distinct().Order(StringComparer.Ordinal).ToArray(),
                    status?.LastSuccessAt?.ToDateTimeOffset()
                )
            );
        }
        var activePricing = await db
            .PricingSnapshots.AsNoTracking()
            .Where(p => p.IsActive)
            .GroupBy(p => p.Provider)
            .Select(g => new { Provider = g.Key, RetrievedAt = g.Max(p => p.RetrievedAt) })
            .ToArrayAsync(ct);
        var fresh = activePricing
            .Where(p => p.RetrievedAt >= clock.GetCurrentInstant().Minus(Duration.FromHours(48)))
            .Select(p => p.Provider)
            .ToHashSet();
        result = result
            .Select(r =>
                r with
                {
                    DiagnosticCodes = [.. r.DiagnosticCodes, .. PricingDiagnostics(r.SourceId, captures, fresh)],
                }
            )
            .ToList();
        return new(1, result, options.Value.MaxRunWindowDays, options.Value.MaxSnapshotBytes);
    }

    private static IEnumerable<string> PricingDiagnostics(
        string source,
        IReadOnlyList<AvailableAuditCapture> captures,
        HashSet<Provider> fresh
    )
    {
        var providers = captures
            .Select(c => AuditCanonicalJson.Deserialize<CaptureManifest>(c.Manifest))
            .SelectMany(c => c.SourceFormats)
            .Where(f => f.SourceId == source)
            .Select(f =>
                f.Format switch
                {
                    "claude-jsonl" => (Provider?)Provider.Anthropic,
                    "codex-jsonl" => Provider.OpenAI,
                    _ => null,
                }
            )
            .OfType<Provider>()
            .ToHashSet();
        if (source == UsageSourceIds.ClaudeLocal)
        {
            providers.Add(Provider.Anthropic);
        }
        if (source == UsageSourceIds.CodexLocal)
        {
            providers.Add(Provider.OpenAI);
        }
        if (providers.Count == 0)
        {
            yield return "pricing_unverified";
        }
        else if (providers.Any(p => !fresh.Contains(p)))
        {
            yield return "pricing_stale";
        }
    }

    private IEnumerable<string> StatusDiagnostics(AiObservatory.Data.Entities.SourceSyncState? status)
    {
        var diagnostics = new List<string>();
        if (status?.IsAvailable == false)
        {
            diagnostics.Add("source_unavailable");
        }
        if (status?.ConsecutiveFailureCount > 0)
        {
            diagnostics.Add("source_refresh_failed");
        }
        if (
            status?.LastSuccessAt == null
            || status.LastSuccessAt.Value.Plus(
                Duration.FromSeconds(Math.Clamp(status.ExpectedRefreshIntervalSeconds, 1, 86400) * 2)
            ) < clock.GetCurrentInstant()
        )
        {
            diagnostics.Add("source_stale");
        }
        return diagnostics;
    }

    private static IEnumerable<string> CaptureDiagnostics(CaptureManifest manifest, string source)
    {
        if (manifest.SkippedFiles > 0 || manifest.ChangedFiles > 0 || manifest.ReadFiles != manifest.DiscoveredFiles)
        {
            yield return "incomplete_capture";
        }
        foreach (var diagnostic in manifest.Diagnostics.Where(d => d.Count > 0))
        {
            yield return diagnostic.Code;
        }
        if (
            manifest.SourceFormats.Any(f =>
                f.SourceId == source
                && !AuditCapabilities.Sources.Any(c => c.Supported && c.Format == f.Format && c.Version == f.Version)
            )
        )
        {
            yield return "unsupported_source_format";
        }
    }
}
