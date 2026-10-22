using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using AiObservatory.Audit.Bundles;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AwesomeAssertions;

namespace AiObservatory.Audit.Cli.Tests;

public sealed class ReplayCommandTests
{
    /// <summary>The real built process deterministically replays without credentials or package restoration.</summary>
    [Fact]
    public async Task Replay_VersionTwoFixture_WritesDeterministicExpectedResultOffline()
    {
        using var directory = new TestDirectory();
        var first = Path.Combine(directory.Path, "first");
        var second = Path.Combine(directory.Path, "second");
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden-v2.zip");
        var run1 = await Run("replay", fixture, "--output", first);
        var run2 = await Run("replay", fixture, "--output", second);
        run1.ExitCode.Should().Be(0, run1.Error);
        run2.ExitCode.Should().Be(0, run2.Error);
        var expected = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden-v2.result.json"),
            TestContext.Current.CancellationToken
        );
        (await File.ReadAllBytesAsync(Path.Combine(first, "result.json"), TestContext.Current.CancellationToken))
            .Should()
            .Equal(expected);
        (await File.ReadAllBytesAsync(Path.Combine(second, "result.json"), TestContext.Current.CancellationToken))
            .Should()
            .Equal(expected);
        (await File.ReadAllTextAsync(Path.Combine(first, "result.sha256"), TestContext.Current.CancellationToken))
            .Trim()
            .Should()
            .Be(Convert.ToHexStringLower(SHA256.HashData(expected)));
        (await File.ReadAllTextAsync(Path.Combine(first, "report.txt"), TestContext.Current.CancellationToken))
            .Should()
            .Contain("Matched")
            .And.Contain("Coverage");
    }

    [Fact]
    public async Task Review64_ReadableReportIdentifiesScopeAndAvailableObservationContext()
    {
        using var directory = new TestDirectory();
        var bundle = await Golden();
        var output = Path.Combine(directory.Path, "scope");
        var path = await Write(directory, bundle);
        (await Run("replay", path, "--output", output)).ExitCode.Should().Be(3);
        var report = await File.ReadAllTextAsync(
            Path.Combine(output, "report.txt"),
            TestContext.Current.CancellationToken
        );
        report
            .Should()
            .Contain(bundle.Input.Scope.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .And.Contain(bundle.Input.Scope.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .And.Contain(bundle.Input.Scope.Sources[0].SourceId)
            .And.Contain("Requested checks:")
            .And.Contain("Excluded checks:")
            .And.Contain(bundle.Result.InputContentDigest)
            .And.Contain("Server cutoff: unavailable")
            .And.Contain("Completion time: unavailable");
    }

    /// <summary>Unknown versions and options fail with a sanitized exit code and create no outputs.</summary>
    [Theory]
    [InlineData("--verifier-version", "9.0.0")]
    [InlineData("--download", "https://invalid.example/tool")]
    public async Task Replay_InvalidOption_ExitsFour(string option, string value)
    {
        using var directory = new TestDirectory();
        var output = Path.Combine(directory.Path, "result");
        var result = await Run(
            "replay",
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden-v1.zip"),
            "--output",
            output,
            option,
            value
        );
        result.ExitCode.Should().Be(4);
        Directory.Exists(output).Should().BeFalse();
    }

    /// <summary>Process exit status represents numeric match or difference for explicitly supported aggregate scope.</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public async Task Replay_SupportedAggregateScope_ReturnsVerdictExitCode(bool mismatch, int expectedExit)
    {
        const long expectedInputTokens = 9_007_199_254_740_993;
        const long actualInputTokens = 9_007_199_254_740_992;
        using var directory = new TestDirectory();
        var bundle = await Golden();
        var input = bundle.Input with
        {
            Scope = bundle.Input.Scope with { RequestedChecks = [CheckCode.EventAggregation], ExcludedChecks = [] },
            Aggregates = mismatch
                ? [bundle.Input.Aggregates[0] with { InputTokens = actualInputTokens }]
                : bundle.Input.Aggregates,
        };
        var path = await Write(directory, bundle with { Input = input, Result = new AuditVerifier().Verify(input) });
        var output = Path.Combine(directory.Path, "output");
        var run = await Run(
            "replay",
            path,
            "--output",
            output,
            "--verifier-version",
            AuditVerifierIdentity.VerifierVersion
        );
        run.ExitCode.Should().Be(expectedExit, run.Error);
        var result = AuditCanonicalJson.Deserialize<AuditResult>(
            await File.ReadAllBytesAsync(Path.Combine(output, "result.json"), TestContext.Current.CancellationToken)
        );
        result.Verdict.Should().Be(mismatch ? CheckVerdict.Mismatched : CheckVerdict.Matched);
        if (mismatch)
        {
            (await File.ReadAllTextAsync(Path.Combine(output, "report.txt"), TestContext.Current.CancellationToken))
                .Should()
                .Contain(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"inputTokens: expected={expectedInputTokens} actual={actualInputTokens} delta=-1"
                    )
                );
        }
    }

    /// <summary>Explicit reevaluation keeps the recorded prior result and creates a distinct current evaluation identity.</summary>
    [Fact]
    public async Task Replay_PriorVersion_RequiresFlagAndPreservesOriginalResult()
    {
        using var directory = new TestDirectory();
        var bundle = await Golden();
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden-v1.zip");
        await using (var source = File.OpenRead(path))
        {
            bundle = await AuditBundleReader.ReadBundleAsync(
                source,
                new() { AllowReevaluation = true },
                TestContext.Current.CancellationToken
            );
        }
        var originalBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var rejected = await Run("replay", path, "--output", Path.Combine(directory.Path, "rejected"));
        rejected.ExitCode.Should().Be(4);
        Directory.Exists(Path.Combine(directory.Path, "rejected")).Should().BeFalse();
        var output = Path.Combine(directory.Path, "reevaluated");
        var run = await Run(
            "replay",
            path,
            "--output",
            output,
            "--reevaluate",
            "--verifier-version",
            AuditVerifierIdentity.VerifierVersion
        );
        run.ExitCode.Should().Be(3, run.Error);
        (
            await File.ReadAllBytesAsync(
                Path.Combine(output, "recorded-result.json"),
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Equal(AuditCanonicalJson.Serialize(bundle.Result));
        var reevaluated = AuditCanonicalJson.Deserialize<AuditResult>(
            await File.ReadAllBytesAsync(Path.Combine(output, "result.json"), TestContext.Current.CancellationToken)
        );
        reevaluated.VerifierVersion.Should().Be("1.1.0");
        reevaluated.RuleVersion.Should().Be("2");
        reevaluated.InputContentDigest.Should().NotBe(bundle.Result.InputContentDigest);
        (await File.ReadAllTextAsync(Path.Combine(output, "evaluation.json"), TestContext.Current.CancellationToken))
            .Should()
            .Contain("reevaluation")
            .And.Contain(bundle.Result.InputContentDigest);
        (await File.ReadAllTextAsync(Path.Combine(output, "report.txt"), TestContext.Current.CancellationToken))
            .Should()
            .StartWith("Reevaluation\n");
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).Should().Equal(originalBytes);
    }

    /// <summary>Invalid archives and missing arguments fail without exposing private rejected paths.</summary>
    [Fact]
    public async Task Replay_InvalidBundleOrArguments_ReturnsSanitizedFailure()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "private-evidence.zip");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        var invalid = await Run("replay", path, "--output", Path.Combine(directory.Path, "output"));
        invalid.ExitCode.Should().Be(4);
        invalid.Error.Should().NotContain(path);
        Directory.Exists(Path.Combine(directory.Path, "output")).Should().BeFalse();
        (await Run()).ExitCode.Should().Be(4);
    }

    private static async Task<AuditBundle> Golden()
    {
        await using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden-v1.zip"));
        var recorded = await AuditBundleReader.ReadBundleAsync(
            source,
            new() { AllowReevaluation = true },
            TestContext.Current.CancellationToken
        );
        var input = recorded.Input with
        {
            VerifierVersion = AuditVerifierIdentity.VerifierVersion,
            RuleVersion = AuditVerifierIdentity.RuleVersion,
        };
        return recorded with { Input = input, Result = new AuditVerifier().Verify(input) };
    }

    private static async Task<string> Write(TestDirectory directory, AuditBundle bundle)
    {
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "input.zip");
        await using var destination = File.Create(path);
        await AuditBundleWriter.WriteAsync(destination, bundle, TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<(int ExitCode, string Error)> Run(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AiObservatory.Audit.Cli.dll"));
        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Clear();
        foreach (
            var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "DOTNET_ROOT", "HOME", "USERPROFILE" }
        )
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                start.Environment[name] = value;
            }
        }
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:1";
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
        await output;
        return (process.ExitCode, await error);
    }

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "audit-cli-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
