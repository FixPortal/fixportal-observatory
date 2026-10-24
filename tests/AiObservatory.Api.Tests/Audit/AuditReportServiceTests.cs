using System.Text;
using AiObservatory.Api.Audit;
using AiObservatory.Audit;
using AiObservatory.Audit.Bundles;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace AiObservatory.Api.Tests.Audit;

public sealed class AuditReportServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExportDiagnostics_ReportsOrderedPhasesAndExactCountsWithoutChangingBytes()
    {
        var (retained, bundle) = Fixture();
        var logs = new ExportLogs();
        var diagnostics = new AuditExportDiagnostics(logs);
        diagnostics.Start("materialize");
        diagnostics.Materialized(retained);
        diagnostics.Complete();
        using var expected = new MemoryStream();
        using var actual = new MemoryStream();
        await AuditBundleWriter.WriteAsync(expected, bundle, Ct);
        await AuditReportService.WriteRetainedBundleAsync(actual, retained, Ct, diagnostics);
        actual.ToArray().Should().Equal(expected.ToArray());
        logs.Rows.Where(r => (string)r["Outcome"]! == "started")
            .Select(r => r["Phase"])
            .Should()
            .Equal("materialize", "detached_read", "validation", "entry_serialization", "zip_write");
        var counts = logs.Rows.Single(r => r.ContainsKey("SnapshotBytes"));
        counts["SnapshotBytes"].Should().Be(retained.Snapshot.Bytes.LongLength);
        counts["ResultBytes"].Should().Be(retained.Result.LongLength);
        counts["ManifestBytes"].Should().Be(retained.Captures.Sum(c => c.Manifest.LongLength));
        counts["ChunkBytes"].Should().Be(0L);
        counts["CaptureCount"].Should().Be(1);
        counts["ChunkCount"].Should().Be(1);
        logs.Rows.Where(r => r.ContainsKey("Phase"))
            .Should()
            .OnlyContain(r =>
                (double)r["ElapsedMilliseconds"]! >= 0
                && (long)r["ProcessAllocatedBytes"]! >= 0
                && (long)r["ProcessWorkingSetBytes"]! > 0
            );
        logs.Text.Should()
            .NotContain(retained.RunId.ToString())
            .And.NotContain(retained.ResultHash)
            .And.NotContain("synthetic")
            .And.NotContain(retained.Snapshot.InputContentDigest);
        var allowed = new[]
        {
            "ExportId",
            "Outcome",
            "Phase",
            "ElapsedMilliseconds",
            "PhaseElapsedMilliseconds",
            "ProcessAllocatedBytes",
            "ProcessWorkingSetBytes",
            "SnapshotBytes",
            "ResultBytes",
            "ManifestBytes",
            "ChunkBytes",
            "CaptureCount",
            "ChunkCount",
            "{OriginalFormat}",
        };
        logs.Rows.SelectMany(r => r.Keys).Should().OnlyContain(key => allowed.Contains(key));
    }

    [Theory]
    [InlineData("snapshot_hash", "detached_read")]
    [InlineData("result_verdict", "validation")]
    public async Task ExportDiagnostics_ReportsFailingPhaseWithoutEvidenceOrExceptionText(
        string corruption,
        string phase
    )
    {
        var (retained, _) = Fixture();
        var logs = new ExportLogs();
        using var actual = new MemoryStream();
        var action = () =>
            AuditReportService.WriteRetainedBundleAsync(actual, Corrupt(retained, corruption), Ct, new(logs));
        await action.Should().ThrowAsync<AuditStoreException>();
        logs.Rows[^1]["Phase"].Should().Be(phase);
        logs.Rows[^1]["Outcome"].Should().Be("failed");
        logs.Text.Should().NotContain("retained_evidence_integrity");
    }

    [Fact]
    public void ExportDiagnostics_CancellationReportsActivePhaseWithoutCompletingIt()
    {
        var logs = new ExportLogs();
        var diagnostics = new AuditExportDiagnostics(logs);
        diagnostics.Start("validation");
        diagnostics.CancellationRequested();
        diagnostics.Fail(new OperationCanceledException("secret/path/payload"));
        diagnostics.Fail(new IOException("duplicate"));
        logs.Rows.Select(r => r["Outcome"]).Should().Equal("started", "cancellation_requested", "cancelled");
        logs.Text.Should().NotContain("secret").And.NotContain("duplicate");
    }

    [Fact]
    public async Task ExportDiagnostics_LoggingFailureDoesNotChangeExport()
    {
        var (retained, bundle) = Fixture();
        var diagnostics = new AuditExportDiagnostics(new ExportLogs { Throw = true });
        diagnostics.Start("materialize");
        diagnostics.Materialized(retained);
        diagnostics.Complete();
        using var expected = new MemoryStream();
        using var actual = new MemoryStream();
        await AuditBundleWriter.WriteAsync(expected, bundle, Ct);
        await AuditReportService.WriteRetainedBundleAsync(actual, retained, Ct, diagnostics);
        actual.ToArray().Should().Equal(expected.ToArray());
    }

    [Fact]
    public async Task ExportDiagnostics_OutputFailureIsAttributedToZipWrite()
    {
        var (retained, _) = Fixture();
        var logs = new ExportLogs();
        using var output = new MemoryStream([], writable: false);
        var action = () => AuditReportService.WriteRetainedBundleAsync(output, retained, Ct, new(logs));
        await action.Should().ThrowAsync<ArgumentException>();
        logs.Rows[^1]["Phase"].Should().Be("zip_write");
        logs.Rows[^1]["Outcome"].Should().Be("failed");
    }

    private sealed class ExportLogs : ILogger
    {
        public List<Dictionary<string, object?>> Rows { get; } = [];
        public string Text => string.Join("\n", Rows.SelectMany(r => r).Select(p => $"{p.Key}={p.Value}"));
        public bool Throw { get; init; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (Throw)
            {
                throw new InvalidOperationException("logger unavailable");
            }
            exception.Should().BeNull();
            Rows.Add(
                ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(p => p.Key, p => p.Value)
            );
        }
    }

    [Fact]
    public async Task RetainedExport_PreservesCanonicalBundleBytes()
    {
        var (retained, bundle) = Fixture();
        using var expected = new MemoryStream();
        using var actual = new MemoryStream();
        await AuditBundleWriter.WriteAsync(expected, bundle, Ct);
        await AuditReportService.WriteRetainedBundleAsync(actual, retained, Ct);
        actual.ToArray().Should().Equal(expected.ToArray());
    }

    [Theory]
    [InlineData("snapshot_hash")]
    [InlineData("result_hash")]
    [InlineData("digest_bridge")]
    [InlineData("result_verdict")]
    [InlineData("capture_manifest")]
    [InlineData("capture_chunk")]
    public async Task RetainedExport_RejectsCorruptionWithIntegrityConflict(string corruption)
    {
        var (retained, _) = Fixture();
        retained = Corrupt(retained, corruption);
        using var destination = new MemoryStream();
        var action = () => AuditReportService.WriteRetainedBundleAsync(destination, retained, Ct);
        var error = (await action.Should().ThrowAsync<AuditStoreException>()).Which;
        error.Code.Should().Be("retained_evidence_integrity");
        error.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task RetainedExport_ObservesCancellationBeforeReadingCorruptEvidence()
    {
        var (retained, _) = Fixture();
        retained = retained with { Snapshot = retained.Snapshot with { Bytes = [123], Sha256 = new('0', 64) } };
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var destination = new MemoryStream();
        var action = () => AuditReportService.WriteRetainedBundleAsync(destination, retained, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        destination.Length.Should().Be(0);
    }

    [Fact]
    public void PublicEvidenceValidation_StillRejectsForgedSnapshotAndResultDigestAgreement()
    {
        var (retained, _) = Fixture();
        var forged = Corrupt(retained, "digest_bridge");
        var action = () => AuditReportService.Validate(forged);
        var error = action.Should().Throw<AuditStoreException>().Which;
        error.Code.Should().Be("retained_evidence_integrity");
        error.StatusCode.Should().Be(409);
    }

    private static AuditExportMaterialization Corrupt(AuditExportMaterialization retained, string corruption)
    {
        var result = AuditCanonicalJson.Deserialize<AuditResult>(retained.Result);
        var capture = retained.Captures.Single();
        return corruption switch
        {
            "snapshot_hash" => retained with { Snapshot = retained.Snapshot with { Sha256 = new('0', 64) } },
            "result_hash" => retained with { ResultHash = new('0', 64) },
            "digest_bridge" => WithResult(retained, result with { InputContentDigest = new('0', 64) }) with
            {
                Snapshot = retained.Snapshot with { InputContentDigest = new('0', 64) },
            },
            "result_verdict" => WithResult(
                retained,
                result with
                {
                    Verdict = result.Verdict == CheckVerdict.Matched ? CheckVerdict.Incomplete : CheckVerdict.Matched,
                }
            ),
            "capture_manifest" => retained with
            {
                Captures =
                [
                    capture with
                    {
                        Manifest = AuditCanonicalJson.Serialize(
                            AuditCanonicalJson.Deserialize<CaptureManifest>(capture.Manifest) with
                            {
                                Generation = "changed",
                            }
                        ),
                    },
                ],
            },
            "capture_chunk" => retained with
            {
                Captures = [capture with { Chunks = [Encoding.UTF8.GetBytes("{}\n")] }],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
    }

    private static AuditExportMaterialization WithResult(AuditExportMaterialization retained, AuditResult result)
    {
        var bytes = AuditCanonicalJson.Serialize(result);
        return retained with { Result = bytes, ResultHash = AuditCanonicalJson.ContentHash(bytes) };
    }

    private static (AuditExportMaterialization Retained, AuditBundle Bundle) Fixture()
    {
        var machine = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var scope = new AuditScope(
            new(2026, 10, 20),
            new(2026, 10, 20),
            [new(machine, "synthetic")],
            [CheckCode.PricingArithmetic],
            [],
            false
        );
        var stamp = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        var manifest = new CaptureManifest(
            1,
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Guid.Parse("00000000-0000-0000-0000-000000000003"),
            machine,
            "synthetic",
            "1.1.0",
            stamp,
            stamp.AddSeconds(1),
            scope,
            [new("synthetic", "claude-jsonl", "2")],
            1,
            1,
            0,
            0,
            0,
            null,
            null,
            [],
            [AuditCanonicalJson.ContentHash([])],
            stamp.AddSeconds(2)
        );
        var input = new AuditInput
        {
            SchemaVersion = 1,
            VerifierVersion = AuditVerifierIdentity.VerifierVersion,
            RuleVersion = AuditVerifierIdentity.RuleVersion,
            Scope = scope,
            Captures = [manifest],
        };
        var result = new AuditVerifier().Verify(input);
        var snapshot = AuditCanonicalJson.Serialize(input);
        var resultBytes = AuditCanonicalJson.Serialize(result);
        var capture = new AuditCaptureEvidence(manifest, [new(0, [])]);
        return (
            new(
                Guid.Parse("00000000-0000-0000-0000-000000000004"),
                0,
                resultBytes,
                AuditCanonicalJson.ContentHash(resultBytes),
                new(snapshot, AuditCanonicalJson.ContentHash(snapshot), result.InputContentDigest, null),
                [
                    new(
                        AuditCanonicalJson.Serialize(manifest),
                        [
                            [],
                        ],
                        null
                    ),
                ],
                Instant.FromUnixTimeSeconds(0),
                null
            ),
            new(input, result, [capture])
        );
    }
}
