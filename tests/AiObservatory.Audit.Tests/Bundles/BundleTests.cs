using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AiObservatory.Audit.Bundles;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Tests.Sources;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Bundles;

public sealed class BundleTests
{
    [Fact]
    public async Task VersionTwo_BundleIsDeterministicAndContainsBothIndependentBands()
    {
        var bundle = VersionTwoFixture();
        bundle.Result.VerifierVersion.Should().Be("1.1.0");
        bundle.Result.RuleVersion.Should().Be("2");
        bundle.Result.Verdict.Should().Be(CheckVerdict.Matched);
        using var first = await Write(bundle);
        using var second = await Write(bundle);
        first.ToArray().Should().Equal(second.ToArray());
        using var golden = new MemoryStream(
            await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit", "golden-v2.zip"),
                TestContext.Current.CancellationToken
            )
        );
        AssertCanonicalEntriesEqual(first, golden);
        AuditCanonicalJson
            .Serialize(bundle.Result)
            .Should()
            .Equal(
                await File.ReadAllBytesAsync(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit", "golden-v2.result.json"),
                    TestContext.Current.CancellationToken
                )
            );
        var goldenReplay = await AuditBundleReader.ReadBundleAsync(
            golden,
            new(),
            TestContext.Current.CancellationToken
        );
        AuditCanonicalJson
            .Serialize(new AuditVerifier().Verify(goldenReplay.Input))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(bundle.Result));
        var replay = await AuditBundleReader.ReadBundleAsync(first, new(), TestContext.Current.CancellationToken);
        var reconstruction = new AiObservatory.Audit.Sources.ClaudeAuditAdapter().Reconstruct(
            replay.Input.Records,
            replay.Input.Scope
        );
        reconstruction
            .CompatibleGroups.Select(g => g.Key.Pricing.Context)
            .Should()
            .BeEquivalentTo("prompt_le_100000", "prompt_gt_100000");
        AuditCanonicalJson
            .Serialize(new AuditVerifier().Verify(replay.Input))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(bundle.Result));
    }

    private static void AssertCanonicalEntriesEqual(MemoryStream actual, MemoryStream expected)
    {
        using (var archive = new ZipArchive(actual, ZipArchiveMode.Read, true))
        using (var golden = new ZipArchive(expected, ZipArchiveMode.Read, true))
        {
            archive.Entries.Select(e => e.FullName).Should().Equal(golden.Entries.Select(e => e.FullName));
            foreach (var entry in archive.Entries)
            {
                using var source = entry.Open();
                using var reference = golden.GetEntry(entry.FullName)!.Open();
                using var bytes = new MemoryStream();
                using var referenceBytes = new MemoryStream();
                source.CopyTo(bytes);
                reference.CopyTo(referenceBytes);
                bytes
                    .ToArray()
                    .Should()
                    .Equal(referenceBytes.ToArray(), "canonical entry {0} must be unchanged", entry.FullName);
            }
        }
        actual.Position = 0;
        expected.Position = 0;
    }

    [Fact]
    public async Task VersionOne_GoldenBytesStayPinnedAndDefaultReaderRefusesOldIdentity()
    {
        var zip = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit", "golden-v1.zip"),
            TestContext.Current.CancellationToken
        );
        var result = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit", "golden-v1.result.json"),
            TestContext.Current.CancellationToken
        );
        AuditCanonicalJson
            .ContentHash(zip)
            .Should()
            .Be("adbf69fa7d1b54d9752ab6fd15cb7b7d62f8ac552603ef35e6cb04dbd841f014");
        AuditCanonicalJson
            .ContentHash(result)
            .Should()
            .Be("5a194bb9f85af8731a076e0fbea4baad6a57e0f8a32c5c746f3cea90d9a78f5b");
        using var source = new MemoryStream(zip);
        Func<Task> read = () => AuditBundleReader.ReadBundleAsync(source, new(), TestContext.Current.CancellationToken);
        await read.Should().ThrowAsync<AuditValidationException>().WithMessage("incompatible_bundle_verifier");
    }

    /// <summary>Preserves exact retained capture bytes, integer precision and complete fact membership.</summary>
    [Fact]
    public async Task Roundtrip_PreservesEvidenceAndDeterministicResult()
    {
        var bundle = Fixture();
        using var zip = await Write(bundle);
        var read = await AuditBundleReader.ReadBundleAsync(zip, new(), CancellationToken.None);
        read.Captures[0].Chunks[0].Ndjson.Should().Equal(bundle.Captures[0].Chunks[0].Ndjson);
        ((ClaudeCounters)read.Input.Records[0].Counters!).InputTokens.Should().Be(9007199254740993);
        AuditCanonicalJson.Serialize(read.Input).Should().Equal(AuditCanonicalJson.Serialize(bundle.Input));
        AuditCanonicalJson
            .Serialize(new AuditVerifier().Verify(read.Input))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(bundle.Result));
        using var again = await Write(bundle);
        zip.ToArray().Should().Equal(again.ToArray());
    }

    [Fact]
    public async Task Roundtrip_PreservesOptionalQualificationAndFinancialFacts()
    {
        var bundle = Fixture();
        var input = bundle.Input with
        {
            PartialEvidenceAcknowledged = true,
            SourceQualifications = [new("claude-local", "partial_capture_acknowledged", false)],
            FinancialObservations = [],
        };
        bundle = bundle with { Input = input, Result = new AuditVerifier().Verify(input) };
        using var zip = await Write(bundle);
        var read = await AuditBundleReader.ReadBundleAsync(zip, new(), TestContext.Current.CancellationToken);
        AuditCanonicalJson.Serialize(read.Input).Should().Equal(AuditCanonicalJson.Serialize(input));
    }

    [Fact]
    public async Task Roundtrip_LargeFinancialFactsDoNotConsumeTheMetadataBudget()
    {
        var bundle = Fixture();
        var facts = Enumerable
            .Range(1, 5000)
            .Select(i => new FrozenFinancialObservation(
                new Guid(i, 0, 0, new byte[8]),
                "billing",
                "provider",
                "observation",
                new(2026, 10, 20),
                null,
                new(SourceKind.ProviderApi, UsageScope.Api, AccountingBasis.Billed),
                "USD",
                1m,
                0m,
                1m,
                new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)
            ))
            .ToArray();
        var input = bundle.Input with { FinancialObservations = facts };
        bundle = bundle with { Input = input, Result = new AuditVerifier().Verify(input) };
        using var zip = await Write(bundle);
        var read = await AuditBundleReader.ReadBundleAsync(zip, new(), TestContext.Current.CancellationToken);
        read.Input.FinancialObservations.Should().HaveCount(5000);
        AuditCanonicalJson.Serialize(read.Input).Should().Equal(AuditCanonicalJson.Serialize(input));
    }

    /// <summary>ZIP directory order cannot change replay computation.</summary>
    [Fact]
    public async Task Read_ReorderedEntries_PreservesResult()
    {
        using var zip = await Write(Fixture());
        using var reversed = Rewrite(zip, entries => entries.AsEnumerable().Reverse());
        var input = await AuditBundleReader.ReadAsync(reversed, new(), CancellationToken.None);
        AuditCanonicalJson
            .Serialize(new AuditVerifier().Verify(input))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(Fixture().Result));
    }

    /// <summary>Old evidence requires reevaluation permission; reading preserves its recorded bytes and identity.</summary>
    [Fact]
    public async Task Read_ReorderedGolden_MatchesCommittedResult()
    {
        using var zip = new MemoryStream(
            await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit", "golden-v1.zip"),
                TestContext.Current.CancellationToken
            )
        );
        using var reversed = Rewrite(zip, entries => entries.AsEnumerable().Reverse());
        var bundle = await AuditBundleReader.ReadBundleAsync(
            reversed,
            new() { AllowReevaluation = true },
            TestContext.Current.CancellationToken
        );
        bundle.Input.VerifierVersion.Should().Be("1.0.0");
        bundle.Input.RuleVersion.Should().Be("1");
        AuditCanonicalJson
            .Serialize(bundle.Result)
            .Should()
            .Equal(
                await File.ReadAllBytesAsync(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit", "golden-v1.result.json"),
                    TestContext.Current.CancellationToken
                )
            );
    }

    /// <summary>Rejects all non-declared archive paths before any filesystem extraction or execution.</summary>
    [Theory]
    [InlineData("../escape.json")]
    [InlineData("/absolute.json")]
    [InlineData("captures\\escape.json")]
    [InlineData("C:/escape.json")]
    [InlineData("execute.exe")]
    [InlineData("snapshot/unknown.ndjson")]
    [InlineData("snapshot/")]
    [InlineData("Snapshot/events.ndjson")]
    public async Task Read_UnknownOrUnsafeEntry_Rejects(string name)
    {
        using var zip = await Write(Fixture());
        using var changed = Rewrite(zip, entries => entries.Append(new(name, [1])));
        await Reject(changed);
    }

    /// <summary>Archive declarations cannot conceal duplicates, missing bytes or corrupt payloads.</summary>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("unknown-field")]
    [InlineData("schema")]
    [InlineData("content-hash")]
    public async Task Read_InvalidArchive_Rejects(string kind)
    {
        using var zip = await Write(Fixture());
        using var changed = Rewrite(zip, entries => Corrupt(entries, kind));
        await Reject(changed);
    }

    /// <summary>Caller limits can tighten but cannot lift the hard 512 MiB protocol ceiling.</summary>
    [Theory]
    [InlineData("total")]
    [InlineData("entry")]
    [InlineData("chunk")]
    [InlineData("entries")]
    [InlineData("ratio")]
    public async Task Read_ExceededBound_Rejects(string kind)
    {
        using var zip = await Write(Fixture());
        var limits = kind switch
        {
            "total" => new BundleLimits { MaxTotalBytes = 32 },
            "entry" => new BundleLimits { MaxEntryBytes = 32 },
            "chunk" => new BundleLimits { MaxChunkBytes = 32 },
            "entries" => new BundleLimits { MaxEntries = 2 },
            _ => new BundleLimits { MaxCompressionRatio = 1 },
        };
        using var compressed = Rewrite(zip, entries => entries);
        await Reject(compressed, limits);
    }

    /// <summary>Capture association must come from retained chunks, with original hash and record count.</summary>
    [Theory]
    [InlineData("chunk-hash")]
    [InlineData("count")]
    [InlineData("union")]
    [InlineData("machine")]
    [InlineData("generation")]
    public async Task Write_InvalidCaptureEvidence_Rejects(string kind)
    {
        var bundle = Fixture();
        var capture = bundle.Captures[0];
        var record = bundle.Input.Records[0];
        bundle = kind switch
        {
            "chunk-hash" => bundle with { Captures = [capture with { Chunks = [new(0, [1])] }] },
            "count" => bundle with
            {
                Captures = [capture with { Manifest = capture.Manifest with { RecordCount = 2 } }],
            },
            "union" => bundle with { Input = bundle.Input with { Records = [] } },
            "machine" => WithRecord(
                bundle,
                record with
                {
                    MachineId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                }
            ),
            _ => WithRecord(bundle, record with { Generation = "another-generation" }),
        };
        bundle = bundle with { Result = new AuditVerifier().Verify(bundle.Input) };
        Func<Task> action = async () =>
        {
            using var invalid = await Write(bundle);
        };
        await action.Should().ThrowAsync<AuditValidationException>();
    }

    /// <summary>A declared unsupported verifier is incompatible until reevaluation is explicitly requested.</summary>
    [Fact]
    public async Task Read_VerifierMismatch_RequiresReevaluation()
    {
        var bundle = Fixture();
        var old = bundle.Input with { VerifierVersion = "0.9.0" };
        using var zip = await Write(bundle with { Input = old, Result = new AuditVerifier().Verify(old) });
        await Reject(zip);
        zip.Position = 0;
        var read = await AuditBundleReader.ReadAsync(zip, new() { AllowReevaluation = true }, CancellationToken.None);
        read.VerifierVersion.Should().Be("0.9.0");
    }

    /// <summary>Rehashed container declarations cannot bypass semantic capture, result or strict DTO validation.</summary>
    [Theory]
    [InlineData("chunk-hash")]
    [InlineData("record-count")]
    [InlineData("missing-chunk")]
    [InlineData("extra-chunk")]
    [InlineData("capture-id")]
    [InlineData("fractional-counter")]
    [InlineData("overflow-counter")]
    [InlineData("unknown-record")]
    [InlineData("unknown-option")]
    [InlineData("duplicate-json-key")]
    [InlineData("missing-fixed-entry")]
    [InlineData("result-binding")]
    [InlineData("result-verdict")]
    [InlineData("option-version")]
    [InlineData("input-schema")]
    [InlineData("duplicate-declaration")]
    public async Task Read_RehashedInvalidEvidence_Rejects(string kind)
    {
        using var zip = await Write(Fixture());
        using var changed = Rewrite(zip, entries => Reseal(Tamper(entries, kind), kind == "duplicate-declaration"));
        await Reject(changed);
    }

    /// <summary>Oversized central-directory lengths are rejected before decompression or allocation.</summary>
    [Fact]
    public async Task Read_DeclaredExpansionAboveHardMaximum_RejectsWithoutExpanding()
    {
        using var zip = await Write(Fixture());
        var bytes = zip.ToArray();
        var found = false;
        for (var index = 0; index + 46 < bytes.Length; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == 0x02014b50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 24, 4), 512 * 1024 * 1024 + 1);
                found = true;
                break;
            }
        }
        found.Should().BeTrue();
        using var bomb = new MemoryStream(bytes);
        await Reject(bomb);
        using var original = new MemoryStream(zip.ToArray());
        await Reject(original, new() { MaxTotalBytes = 512L * 1024 * 1024 + 1 });
    }

    /// <summary>Highly compressible payloads are bounded independently of their small ZIP file size.</summary>
    [Fact]
    public async Task Read_CompressedBomb_Rejects()
    {
        using var zip = await Write(Fixture());
        using var bomb = Rewrite(
            zip,
            entries =>
                entries.Select(e => e.Key == "snapshot/events.ndjson" ? new(e.Key, new byte[2 * 1024 * 1024]) : e)
        );
        await Reject(bomb);
    }

    /// <summary>ZIP metadata expansion is rejected before constructing thousands of entry objects.</summary>
    [Fact]
    public async Task Read_MetadataBomb_RejectsBeforeDirectoryMaterialization()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            for (var index = 0; index < 150; index++)
            {
                archive.CreateEntry("options.json").Comment = new string('x', 60_000);
            }
        }
        bytes.Position = 0;
        Func<Task> read = () => AuditBundleReader.ReadAsync(bytes, new(), CancellationToken.None);
        await read.Should().ThrowAsync<AuditValidationException>().WithMessage("bundle_directory_limit");
    }

    /// <summary>Two retained captures can contain the same fact while preserving each original chunk and count.</summary>
    [Fact]
    public async Task Roundtrip_OverlappingCaptures_PreservesMembershipAndDeduplicatesUnion()
    {
        var bundle = Fixture();
        var first = bundle.Captures[0];
        var second = first with
        {
            Manifest = first.Manifest with { CaptureId = Guid.Parse("10000000-0000-0000-0000-000000000002") },
        };
        var input = bundle.Input with { Captures = [first.Manifest, second.Manifest] };
        using var zip = await Write(new(input, new AuditVerifier().Verify(input), [first, second]));
        var read = await AuditBundleReader.ReadBundleAsync(zip, new(), CancellationToken.None);
        read.Input.Records.Should().HaveCount(1);
        read.Captures.Should().HaveCount(2);
        read.Captures.Select(c => c.Manifest.RecordCount).Should().Equal(1, 1);
        read.Captures[1].Chunks[0].Ndjson.Should().Equal(first.Chunks[0].Ndjson);
    }

    /// <summary>Malformed or truncated ZIP inputs give sanitized protocol failures.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public async Task Read_TruncatedZip_Rejects(int length)
    {
        using var zip = await Write(Fixture());
        using var truncated = new MemoryStream(zip.ToArray()[..length]);
        await Reject(truncated);
    }

    /// <summary>The writer must not emit an options envelope that its own bounded reader refuses.</summary>
    [Fact]
    public async Task Write_OversizedOptions_RejectsBeforeWritingArchive()
    {
        var bundle = Fixture();
        var sources = Enumerable
            .Range(1, 4000)
            .Select(index => new SourceSelection(SourceFixture.Machine, "s" + new string('x', 240) + index))
            .Prepend(bundle.Input.Scope.Sources[0])
            .ToArray();
        var input = bundle.Input with
        {
            Scope = bundle.Input.Scope with { Sources = sources, RequestedChecks = [CheckCode.PricingArithmetic] },
        };
        bundle = bundle with { Input = input, Result = new AuditVerifier().Verify(input) };
        using var destination = new MemoryStream();
        Func<Task> write = () => AuditBundleWriter.WriteAsync(destination, bundle, CancellationToken.None);
        await write.Should().ThrowAsync<AuditValidationException>().WithMessage("bundle_metadata_limit");
        destination.Length.Should().Be(0);
    }

    private static List<KeyValuePair<string, byte[]>> Tamper(List<KeyValuePair<string, byte[]>> entries, string kind)
    {
        var capture = entries
            .Single(e =>
                e.Key.StartsWith("captures/", StringComparison.Ordinal)
                && e.Key.EndsWith("manifest.json", StringComparison.Ordinal)
            )
            .Key;
        var chunk = entries.Single(e => e.Key.Contains("/chunks/", StringComparison.Ordinal)).Key;
        var path = TargetPath(kind, capture, chunk);
        if (kind is "missing-chunk" or "missing-fixed-entry")
        {
            return entries.Where(e => e.Key != path).ToList();
        }
        if (kind == "extra-chunk")
        {
            return entries
                .Append(new(chunk.Replace("000000.ndjson", "000001.ndjson", StringComparison.Ordinal), []))
                .ToList();
        }
        return entries
            .Select(e =>
                e.Key == path
                    ? new(e.Key, Encoding.UTF8.GetBytes(TamperJson(Encoding.UTF8.GetString(e.Value), kind)))
                    : e
            )
            .ToList();
    }

    private static string TargetPath(string kind, string capture, string chunk) =>
        kind switch
        {
            "chunk-hash" or "record-count" or "capture-id" => capture,
            "fractional-counter" or "overflow-counter" or "unknown-record" or "missing-chunk" => chunk,
            "result-binding" or "result-verdict" => "result.json",
            "duplicate-declaration" => "manifest.json",
            _ => "options.json",
        };

    private static string TamperJson(string json, string kind) =>
        kind switch
        {
            "chunk-hash" => json.Replace(
                Fixture().Input.Captures[0].ChunkHashes[0],
                new string('b', 64),
                StringComparison.Ordinal
            ),
            "record-count" => json.Replace("\"recordCount\":1", "\"recordCount\":2", StringComparison.Ordinal),
            "capture-id" => json.Replace(
                "10000000-0000-0000-0000-000000000001",
                "10000000-0000-0000-0000-000000000002",
                StringComparison.Ordinal
            ),
            "fractional-counter" => json.Replace("9007199254740993", "9007199254740993.5", StringComparison.Ordinal),
            "overflow-counter" => json.Replace("9007199254740993", "9223372036854775808", StringComparison.Ordinal),
            "unknown-record" or "unknown-option" => "{\"url\":\"https://invalid.example/run\"," + json[1..],
            "duplicate-json-key" => "{\"schemaVersion\":1," + json[1..],
            "result-binding" => json.Replace(
                "\"inputContentDigest\":\"",
                "\"inputContentDigest\":\"0",
                StringComparison.Ordinal
            ),
            "result-verdict" => json.Replace(
                "\"verdict\":\"mismatched\"",
                "\"verdict\":\"matched\"",
                StringComparison.Ordinal
            ),
            "option-version" => json.Replace(AuditVerifierIdentity.VerifierVersion, "9.0.0", StringComparison.Ordinal),
            "input-schema" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            _ => json,
        };

    private static IEnumerable<KeyValuePair<string, byte[]>> Reseal(
        List<KeyValuePair<string, byte[]>> entries,
        bool duplicate = false
    )
    {
        var manifestEntry = entries.Single(e => e.Key == "manifest.json");
        var manifest = JsonNode.Parse(manifestEntry.Value)!;
        var declarations = new JsonArray(
            entries
                .Where(e => e.Key != "manifest.json")
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["length"] = e.Value.Length,
                            ["path"] = e.Key,
                            ["sha256"] = AuditCanonicalJson.ContentHash(e.Value),
                        }
                )
                .ToArray()
        );
        manifest["entries"] = declarations;
        manifest["contentHash"] = AuditCanonicalJson.ContentHash(Encoding.UTF8.GetBytes(declarations.ToJsonString()));
        // Keep a deliberately repeated declaration for its separate regression case.
        if (duplicate)
        {
            declarations.Add(declarations[0]!.DeepClone());
        }
        return entries.Select(e =>
            e.Key == "manifest.json" ? new(e.Key, Encoding.UTF8.GetBytes(manifest.ToJsonString())) : e
        );
    }

    internal static AuditBundle Fixture()
    {
        var record = SourceFixture.Claude() with { Counters = new ClaudeCounters(9007199254740993, 0, 0, 0, 0, 0) };
        var chunk = AuditCanonicalJson.Serialize(record).Concat(new byte[] { 10 }).ToArray();
        var input = SourceFixture.Input(record) with
        {
            VerifierVersion = AuditVerifierIdentity.VerifierVersion,
            RuleVersion = AuditVerifierIdentity.RuleVersion,
        };
        var manifest = input.Captures[0] with
        {
            CaptureId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            RequestId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
            ChunkHashes = [AuditCanonicalJson.ContentHash(chunk)],
        };
        input = input with { Captures = [manifest] };
        return new(input, new AuditVerifier().Verify(input), [new(manifest, [new(0, chunk)])]);
    }

    private static AuditBundle VersionTwoFixture()
    {
        var lower = SourceFixture.Claude() with
        {
            SourceVersion = "2",
            Model = "claude-haiku-5-5",
            Counters = new ClaudeCounters(70000, 20, 0, 30000, null, null),
        };
        var upper = lower with
        {
            Sequence = 2,
            MessageId = "m2",
            Counters = new ClaudeCounters(70000, 37, 1, 30000, null, null),
        };
        var records = new[] { lower, upper };
        var chunk = records.SelectMany(r => AuditCanonicalJson.Serialize(r).Append((byte)10)).ToArray();
        var input = SourceFixture.Input(records) with
        {
            VerifierVersion = AuditVerifierIdentity.VerifierVersion,
            RuleVersion = AuditVerifierIdentity.RuleVersion,
        };
        var manifest = input.Captures[0] with
        {
            CaptureId = Guid.Parse("10000000-0000-0000-0000-000000000002"),
            RequestId = Guid.Parse("20000000-0000-0000-0000-000000000002"),
            ExtractorVersion = "1.1.0",
            SourceFormats = [new(lower.SourceId, lower.SourceFormat, "2")],
            ChunkHashes = [AuditCanonicalJson.ContentHash(chunk)],
        };
        input = input with
        {
            Captures = [manifest],
            Events = records
                .Select(
                    (r, i) =>
                    {
                        var counters = (ClaudeCounters)r.Counters!;
                        var e = SourceFixture.Event(
                            r,
                            new(
                                counters.InputTokens!.Value,
                                counters.OutputTokens!.Value,
                                counters.CacheReadInputTokens,
                                counters.CacheCreationInputTokens,
                                null,
                                null
                            )
                        );
                        var dimensions = r.Pricing with { Context = i == 0 ? "prompt_le_100000" : "prompt_gt_100000" };
                        return e with
                        {
                            SourceDimensions = dimensions,
                            PricingApplication = e.PricingApplication! with { Dimensions = dimensions },
                        };
                    }
                )
                .ToArray(),
        };
        return new(input, new AuditVerifier().Verify(input), [new(manifest, [new(0, chunk)])]);
    }

    private static IEnumerable<KeyValuePair<string, byte[]>> Corrupt(
        List<KeyValuePair<string, byte[]>> entries,
        string kind
    ) =>
        kind switch
        {
            "duplicate" => entries.Append(entries[0]),
            "missing" => entries.Where(e => e.Key != "options.json"),
            "corrupt" => entries.Select(e => e.Key == "options.json" ? new(e.Key, [1]) : e),
            _ => entries.Select(e => e.Key == "manifest.json" ? new(e.Key, ChangeManifest(e.Value, kind)) : e),
        };

    private static byte[] ChangeManifest(byte[] value, string kind)
    {
        var json = Encoding.UTF8.GetString(value);
        return Encoding.UTF8.GetBytes(
            kind switch
            {
                "unknown-field" => "{\"executable\":\"https://invalid.example/run\"," + json[1..],
                "schema" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
                _ => json.Replace("\"contentHash\":\"", "\"contentHash\":\"0", StringComparison.Ordinal),
            }
        );
    }

    private static AuditBundle WithRecord(AuditBundle bundle, SourceUsageRecord record)
    {
        var chunk = AuditCanonicalJson.Serialize(record).Concat(new byte[] { 10 }).ToArray();
        var capture = bundle.Captures[0];
        var manifest = capture.Manifest with { ChunkHashes = [AuditCanonicalJson.ContentHash(chunk)] };
        var input = bundle.Input with
        {
            Captures = [manifest],
            Records = [record],
            Scope = bundle.Input.Scope with
            {
                Sources = bundle
                    .Input.Scope.Sources.Append(new(record.MachineId, record.SourceId))
                    .Distinct()
                    .ToArray(),
            },
        };
        return bundle with { Captures = [new(manifest, [new(0, chunk)])], Input = input };
    }

    private static async Task<MemoryStream> Write(AuditBundle bundle)
    {
        var stream = new MemoryStream();
        await AuditBundleWriter.WriteAsync(stream, bundle, CancellationToken.None);
        stream.Position = 0;
        return stream;
    }

    private static async Task Reject(Stream stream, BundleLimits? limits = null)
    {
        Func<Task> action = () => AuditBundleReader.ReadAsync(stream, limits ?? new(), CancellationToken.None);
        await action.Should().ThrowAsync<AuditValidationException>();
    }

    private static MemoryStream Rewrite(
        MemoryStream source,
        Func<List<KeyValuePair<string, byte[]>>, IEnumerable<KeyValuePair<string, byte[]>>> transform
    )
    {
        source.Position = 0;
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, true);
        var entries = archive
            .Entries.Select(e =>
            {
                using var input = e.Open();
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                return new KeyValuePair<string, byte[]>(e.FullName, bytes.ToArray());
            })
            .ToList();
        var result = new MemoryStream();
        using (var output = new ZipArchive(result, ZipArchiveMode.Create, true))
        {
            foreach (var entry in transform(entries))
            {
                using var destination = output.CreateEntry(entry.Key).Open();
                destination.Write(entry.Value);
            }
        }
        result.Position = 0;
        return result;
    }
}
