using System.Text;
using AiObservatory.Api.Audit;
using AiObservatory.Audit;
using AiObservatory.Audit.Bundles;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Audit;

public sealed class AuditReportServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
