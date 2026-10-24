using System.Diagnostics;
using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

internal sealed class AuditExportDiagnostics(ILogger logger)
{
    private readonly Lock _gate = new();
    private readonly Guid _exportId = Guid.NewGuid();
    private readonly long _started = Stopwatch.GetTimestamp();
    private string? _phase;
    private long _phaseStarted;

    internal void Start(string phase)
    {
        lock (_gate)
        {
            Finish("completed");
            _phase = phase;
            _phaseStarted = Stopwatch.GetTimestamp();
            Emit("started");
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            Finish("completed");
        }
    }

    internal void Fail(Exception error)
    {
        lock (_gate)
        {
            Finish(error is OperationCanceledException ? "cancelled" : "failed");
        }
    }

    internal void CancellationRequested()
    {
        lock (_gate)
        {
            Emit("cancellation_requested");
        }
    }

    internal void Materialized(AuditExportMaterialization materialized) =>
        TryLog(() =>
            logger.LogInformation(
                "Audit export {ExportId} {Outcome}: snapshot_bytes={SnapshotBytes} result_bytes={ResultBytes} manifest_bytes={ManifestBytes} chunk_bytes={ChunkBytes} captures={CaptureCount} chunks={ChunkCount}",
                _exportId,
                "materialized",
                materialized.Snapshot.Bytes.LongLength,
                materialized.Result.LongLength,
                materialized.Captures.Sum(c => c.Manifest.LongLength),
                materialized.Captures.Sum(c => c.Chunks.Sum(b => b.LongLength)),
                materialized.Captures.Count,
                materialized.Captures.Sum(c => c.Chunks.Count)
            )
        );

    private void Finish(string outcome)
    {
        Emit(outcome);
        _phase = null;
    }

    private void Emit(string outcome)
    {
        if (_phase == null)
        {
            return;
        }
        TryLog(() =>
            logger.LogInformation(
                "Audit export {ExportId} phase={Phase} outcome={Outcome} elapsed_ms={ElapsedMilliseconds} phase_elapsed_ms={PhaseElapsedMilliseconds} process_allocated_bytes={ProcessAllocatedBytes} process_working_set_bytes={ProcessWorkingSetBytes}",
                _exportId,
                _phase,
                outcome,
                Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                Stopwatch.GetElapsedTime(_phaseStarted).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: false),
                Environment.WorkingSet
            )
        );
    }

    private void TryLog(Action write)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                write();
            }
        }
        catch (Exception)
        {
            // Diagnostic providers and process counters must never change evidence delivery.
        }
    }
}
