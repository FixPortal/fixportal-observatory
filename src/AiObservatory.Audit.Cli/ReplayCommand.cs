using System.Globalization;
using System.Text;
using AiObservatory.Audit.Bundles;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;

namespace AiObservatory.Audit.Cli;

public static class ReplayCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        try
        {
            var command = Parse(args);
            await using var source = File.OpenRead(command.Bundle);
            var bundle = await AuditBundleReader.ReadBundleAsync(
                source,
                new() { AllowReevaluation = command.Reevaluate },
                ct
            );
            var input = bundle.Input;
            if (command.Reevaluate)
            {
                input = input with
                {
                    VerifierVersion = AuditVerifierIdentity.VerifierVersion,
                    RuleVersion = AuditVerifierIdentity.RuleVersion,
                };
            }
            var result = new AuditVerifier().Verify(input);
            var canonical = AuditCanonicalJson.Serialize(result);
            var digest = AuditCanonicalJson.ContentHash(canonical);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["result.json"] = canonical,
                ["result.sha256"] = Encoding.UTF8.GetBytes(digest + "\n"),
                ["report.txt"] = Encoding.UTF8.GetBytes(Report(result, digest, command.Reevaluate)),
            };
            if (command.Reevaluate)
            {
                files.Add("recorded-result.json", AuditCanonicalJson.Serialize(bundle.Result));
                files.Add(
                    "evaluation.json",
                    Encoding.UTF8.GetBytes(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{{\"evaluationIdentity\":\"{digest}\",\"evaluationInputDigest\":\"{result.InputContentDigest}\",\"mode\":\"reevaluation\",\"recordedInputDigest\":\"{bundle.Result.InputContentDigest}\",\"recordedResultSha256\":\"{AuditCanonicalJson.ContentHash(files["recorded-result.json"])}\",\"ruleVersion\":\"{AuditVerifierIdentity.RuleVersion}\",\"verifierVersion\":\"{AuditVerifierIdentity.VerifierVersion}\"}}"
                        )
                    )
                );
            }
            await WriteOutputs(command.Output, files, ct);
            return result.Verdict switch
            {
                CheckVerdict.Matched => 0,
                CheckVerdict.Mismatched => 2,
                _ => 3,
            };
        }
        catch (AuditValidationException error)
        {
            await Console.Error.WriteLineAsync(error.Code);
            return 4;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
                        or OverflowException
                        or OperationCanceledException
            )
        {
            await Console.Error.WriteLineAsync("replay_execution_failure");
            return 4;
        }
    }

    private static ReplayArguments Parse(string[] args)
    {
        if (args.Length < 4 || args[0] != "replay")
        {
            throw new AuditValidationException("usage_replay_bundle_output_directory");
        }
        string? output = null;
        string? version = null;
        var reevaluate = false;
        var index = 2;
        while (index < args.Length)
        {
            switch (args[index])
            {
                case "--output" when output is null && index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--verifier-version" when version is null && index + 1 < args.Length:
                    version = args[++index];
                    break;
                case "--reevaluate" when !reevaluate:
                    reevaluate = true;
                    break;
                default:
                    throw new AuditValidationException("invalid_replay_option");
            }
            index++;
        }
        if (string.IsNullOrWhiteSpace(output) || version != null && version != AuditVerifierIdentity.VerifierVersion)
        {
            throw new AuditValidationException("incompatible_replay_verifier_or_output");
        }
        return new(args[1], output, reevaluate);
    }

    private static string Report(AuditResult result, string digest, bool reevaluate)
    {
        var text = new StringBuilder();
        text.Append(reevaluate ? "Reevaluation\n" : "Replay\n");
        text.Append(
            CultureInfo.InvariantCulture,
            $"Verdict: {result.Verdict}\nVerifier: {result.VerifierVersion}; rules: {result.RuleVersion}\nResult SHA-256: {digest}\n"
        );
        text.Append(
            CultureInfo.InvariantCulture,
            $"Coverage: {result.Coverage.ComparedGroups}/{result.Coverage.TotalGroups}\n"
        );
        foreach (var check in result.Checks)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"{check.Code}: {check.Verdict}; coverage {check.Coverage.ComparedGroups}/{check.Coverage.TotalGroups}\n"
            );
        }
        foreach (var gap in result.Coverage.Gaps)
        {
            text.Append(CultureInfo.InvariantCulture, $"Gap: {gap.DiagnosticCode}\n");
        }
        foreach (var exclusion in result.Coverage.Exclusions)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"Excluded field: {exclusion.Field}; {exclusion.DiagnosticCode}\n"
            );
        }
        text.Append(CultureInfo.InvariantCulture, $"Findings: {result.Findings.Count}\n");
        foreach (var finding in result.Findings)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"{finding.CheckCode}/{finding.RuleCode} {finding.Field}: expected={finding.Expected?.Value ?? "unknown"} actual={finding.Actual?.Value ?? "unknown"} delta={finding.Delta?.Value ?? "unknown"}; unit={finding.Unit} currency={finding.Currency ?? "none"} basis={finding.Basis}; {finding.DiagnosticCode}\n"
            );
        }
        return text.ToString();
    }

    private static async Task WriteOutputs(string directory, Dictionary<string, byte[]> files, CancellationToken ct)
    {
        if (files.Keys.Any(name => File.Exists(Path.Combine(directory, name))))
        {
            throw new AuditValidationException("replay_output_exists");
        }
        Directory.CreateDirectory(directory);
        foreach (var (name, bytes) in files)
        {
            await using var destination = new FileStream(
                Path.Combine(directory, name),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            );
            await destination.WriteAsync(bytes, ct);
        }
    }

    private sealed record ReplayArguments(string Bundle, string Output, bool Reevaluate);
}
