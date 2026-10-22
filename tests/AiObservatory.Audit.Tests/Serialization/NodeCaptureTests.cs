using System.Diagnostics;
using System.Text;
using AiObservatory.Audit.Bundles;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Serialization;

public sealed class NodeCaptureTests
{
    /// <summary>Actual Node extraction must produce exact .NET canonical evidence and valid retained bundle bytes.</summary>
    [Fact]
    public async Task NodeCapture_CanonicalRecordsAndManifest_RoundtripThroughBundle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "audit-node-synthetic-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Generate(directory);
            var cancellation = TestContext.Current.CancellationToken;
            var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"), cancellation);
            var manifest = AuditCanonicalJson.Deserialize<CaptureManifest>(manifestBytes);
            AuditCanonicalJson.Serialize(manifest).Should().Equal(manifestBytes);
            var chunks = new List<AuditCaptureChunk>();
            var records = new List<SourceUsageRecord>();
            for (var index = 0; index < manifest.ChunkHashes.Count; index++)
            {
                var bytes = await File.ReadAllBytesAsync(
                    Path.Combine(
                        directory,
                        "chunks",
                        index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".ndjson"
                    ),
                    cancellation
                );
                AuditCanonicalJson.ContentHash(bytes).Should().Be(manifest.ChunkHashes[index]);
                bytes[^1].Should().Be((byte)'\n');
                foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var record = AuditCanonicalJson.Deserialize<SourceUsageRecord>(Encoding.UTF8.GetBytes(line));
                    AuditCanonicalJson.Serialize(record).Should().Equal(Encoding.UTF8.GetBytes(line));
                    records.Add(record);
                }
                chunks.Add(new(index, bytes));
            }
            (
                (ClaudeCounters)
                    records.First(record => record.Tool == SourceTool.Claude && record.Counters != null).Counters!
            )
                .InputTokens.Should()
                .Be(9007199254740993);
            records.First(record => record.Tool == SourceTool.Claude).Cost.Should().Be(1.230000000000000001m);
            records
                .First(record => record.Tool == SourceTool.Claude)
                .Timestamp.Ticks.Should()
                .Be(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).Ticks + 1234567);
            var input = new AuditInput
            {
                SchemaVersion = 1,
                VerifierVersion = AuditVerifierIdentity.VerifierVersion,
                RuleVersion = AuditVerifierIdentity.RuleVersion,
                Scope = manifest.Scope,
                Captures = [manifest],
                Records = records,
            };
            var bundle = new AuditBundle(input, new AuditVerifier().Verify(input), [new(manifest, chunks)]);
            using var destination = new MemoryStream();
            await AuditBundleWriter.WriteAsync(destination, bundle, cancellation);
            destination.Position = 0;
            var replay = await AuditBundleReader.ReadBundleAsync(destination, new(), cancellation);
            replay.Captures[0].Chunks.Should().HaveCount(chunks.Count);
            for (var index = 0; index < chunks.Count; index++)
            {
                replay.Captures[0].Chunks[index].Ndjson.Should().Equal(chunks[index].Ndjson);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static async Task Generate(string output)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "clients", "observatory-audit.mjs")))
        {
            root = root.Parent;
        }
        root.Should().NotBeNull();
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root.FullName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("clients/audit/fixtures/capture-synthetic.mjs");
        start.ArgumentList.Add(output);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "USERPROFILE" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                start.Environment[name] = value;
            }
        }
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
        await stdout;
        process.ExitCode.Should().Be(0, await error);
    }
}
