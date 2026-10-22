using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Serialization;

internal static class AuditContractValidation
{
    // An annotation belongs to the declaring property, not its name or a particular evidence value.
    // Lazy publication keeps concurrent readers from repeating reflection/attribute analysis.
    private static readonly ConcurrentDictionary<PropertyInfo, Lazy<bool>> NullableProperties = new();

    internal static void Validate(object? value) => Visit(value, null, 0);

    private static void Visit(object? value, PropertyInfo? property, int depth)
    {
        Require(depth <= 64, "contract_depth");
        if (value is null)
        {
            Require(
                property != null
                    && NullableProperties
                        .GetOrAdd(
                            property,
                            static member =>
                                new(() =>
                                    new NullabilityInfoContext().Create(member).ReadState == NullabilityState.Nullable
                                )
                        )
                        .Value,
                "required_value"
            );
            return;
        }
        switch (value)
        {
            case string text:
                ValidateString(text, property);
                return;
            case Guid id:
                Require(id != Guid.Empty, "empty_identity");
                return;
            case DateOnly date:
                Require(date != default, "invalid_date");
                return;
            case DateTimeOffset timestamp:
                Require(timestamp != default && timestamp.Offset == TimeSpan.Zero, "invalid_utc_timestamp");
                return;
            case Enum enumeration:
                Require(Enum.IsDefined(enumeration.GetType(), enumeration), "invalid_enum");
                return;
            case long count:
                Require(count >= 0, "negative_counter");
                return;
            case int number:
                Require(number >= 0, "negative_number");
                return;
            case decimal:
            case bool:
                return;
            case IEnumerable list:
                foreach (var item in list)
                {
                    Require(item != null, "null_collection_item");
                    Visit(item, property, depth + 1);
                }
                return;
        }
        Require(value.GetType().Namespace == typeof(AuditInput).Namespace, "unsupported_contract_type");
        ValidateObject(value);
        foreach (var member in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Visit(member.GetValue(value), member, depth + 1);
        }
    }

    private static void ValidateString(string text, PropertyInfo? property)
    {
        var name = property?.Name;
        if (IsEmptyAggregateModel(text, property))
        {
            return;
        }
        Require(text.Length is > 0 and <= 256, "invalid_string");
        if (
            name
            is "ContentHash"
                or "InputContentDigest"
                or "RevisionDigest"
                or "SnapshotHash"
                or "FrozenSnapshotHash"
                or "SummaryInputDigest"
                or "ChunkHashes"
        )
        {
            Require(
                text.Length == 64 && text.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'),
                "invalid_digest"
            );
        }
        else if (name is "Currency" or "FromCurrency" or "ToCurrency")
        {
            Require(IsCurrency(text), "invalid_currency");
        }
        else if (name is "Value" or "FormattedValue")
        {
            Require(text.Any(char.IsAsciiDigit) && text.All(IsNumericCharacter), "invalid_numeric_value");
        }
        else
        {
            var model = name is "Model" or "Aliases";
            Require(text.All(c => IsIdentifierCharacter(c, model)), "invalid_identifier");
            Require(
                !text.StartsWith('/')
                    && !text.Contains(":/", StringComparison.Ordinal)
                    && !text.Contains("..", StringComparison.Ordinal),
                "invalid_identifier"
            );
        }
    }

    private static void ValidateObject(object value)
    {
        switch (value)
        {
            case AuditScope scope:
                ValidateScope(AuditResultScope.FromScope(scope));
                break;
            case AuditResultScope scope:
                ValidateScope(scope);
                break;
            case AuditInput input:
                ValidateInput(input);
                break;
            case AuditResult result:
                Require(result.SchemaVersion == 1, "unsupported_schema");
                break;
            case SourceUsageRecord record:
                ValidateRecord(record);
                break;
            case ClosureEvidence closure:
                Require(
                    closure.State != ClosureState.ExplicitlyClosed
                        || closure.Timestamp != null && closure.Sequence != null,
                    "missing_closure_evidence"
                );
                break;
            case CaptureManifest capture:
                ValidateCapture(capture);
                break;
            case AuditCoverage coverage:
                Require(coverage.ComparedGroups <= coverage.TotalGroups, "invalid_coverage");
                break;
            case AuditOptions options:
                Require(options.PresentationDecimalPlaces == 2, "invalid_presentation_precision");
                break;
            case NamedTolerance tolerance:
                ValidateTolerance(tolerance);
                break;
            case FrozenSpend spend:
                Require(spend.FxRate > 0, "invalid_fx_rate");
                Require(
                    spend.PeriodFrom.HasValue == spend.PeriodTo.HasValue
                        && (spend.PeriodFrom == null || spend.PeriodFrom <= spend.PeriodTo),
                    "invalid_period"
                );
                break;
            case FrozenFx fx:
                Require(fx.Rate > 0, "invalid_fx_rate");
                break;
            case FrozenPriceRate rate:
                Require(
                    rate.UnitTokens > 0 && rate.UnitPrice >= 0 && rate.RegionMultiplier is null or > 0,
                    "invalid_price_rate"
                );
                Require(rate.EffectiveTo == null || rate.EffectiveTo >= rate.EffectiveFrom, "invalid_effective_window");
                break;
            case AuditValue typed:
                Require(
                    typed.Kind == AuditValueKind.Integer
                        ? long.TryParse(typed.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                        : CanonicalDecimalConverter.TryParse(typed.Value, out _),
                    "invalid_typed_value"
                );
                break;
        }
    }

    private static bool IsEmptyAggregateModel(string text, PropertyInfo? property) =>
        text.Length == 0 && property?.Name == "Model" && property.DeclaringType == typeof(AggregateKey);

    private static bool IsCurrency(string text) => text.Length == 3 && text.All(c => c is >= 'A' and <= 'Z');

    private static bool IsNumericCharacter(char c) =>
        char.IsAsciiDigit(c) || c is '-' or '.' or ',' or '$' or '\u00a3' or '\u20ac' or ' ';

    private static bool IsIdentifierCharacter(char c, bool model) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '@' or '|' or '=' || model && c == '/';

    internal static void ValidateInput(AuditInput input)
    {
        Require(input.SchemaVersion == 1, "unsupported_schema");
        Require(input.Records != null, "required_value");
        Require(
            input.Scope != null && input.Records.All(record => IsSelected(input.Scope, record)),
            "record_outside_scope"
        );
    }

    private static bool IsSelected(AuditScope scope, SourceUsageRecord? record) =>
        record != null
        && scope.Sources != null
        && scope.Sources.Any(source =>
            source != null && source.MachineId == record.MachineId && source.SourceId == record.SourceId
        );

    private static void ValidateRecord(SourceUsageRecord record)
    {
        Require(record.Cost is null or >= 0, "negative_source_cost");
        Require(record.Cost == null || record.Currency != null, "missing_currency");
        var requiresCounters =
            record.Kind is UsageRecordKind.MessageUsage or UsageRecordKind.CumulativeUsage or UsageRecordKind.Baseline;
        Require(!requiresCounters || record.Counters != null, "missing_usage_counters");
        Require(
            record.Counters == null
                || record.Tool == SourceTool.Claude && record.Counters is ClaudeCounters
                || record.Tool == SourceTool.Codex && record.Counters is CodexCounters,
            "counter_tool_mismatch"
        );
    }

    private static void ValidateScope(AuditResultScope scope)
    {
        var (from, to, sources, requested, excluded, _) = scope;
        Require(from != default && to >= from && to.DayNumber - from.DayNumber < 31, "invalid_scope_dates");
        Require(
            sources is { Count: > 0 } && requested is { Count: > 0 } && excluded != null,
            "invalid_scope_selection"
        );
        Require(!requested.Intersect(excluded).Any(), "conflicting_checks");
    }

    private static void ValidateCapture(CaptureManifest capture)
    {
        Require(capture.SchemaVersion == 1, "unsupported_schema");
        Require(
            capture.ScanStartedAt <= capture.ScanEndedAt && capture.ScanEndedAt <= capture.SealedAt,
            "invalid_capture_times"
        );
        Require(capture.RecordCount <= 1_000_000, "record_limit");
        Require(
            capture.ReadFiles <= capture.DiscoveredFiles
                && capture.SkippedFiles <= capture.DiscoveredFiles
                && capture.ChangedFiles <= capture.DiscoveredFiles,
            "invalid_scan_counts"
        );
        Require(
            capture.ObservedFrom.HasValue == capture.ObservedTo.HasValue
                && (capture.ObservedFrom == null || capture.ObservedFrom <= capture.ObservedTo),
            "invalid_observed_dates"
        );
        Require(
            capture.Scope != null
                && capture.Scope.Sources != null
                && capture.Scope.Sources.All(source => source != null && source.MachineId == capture.MachineId),
            "capture_machine_scope"
        );
    }

    private static void ValidateTolerance(NamedTolerance tolerance)
    {
        Require(
            tolerance.AbsoluteTolerance >= 0 && tolerance.StoredScale is null or >= 0 and <= 27,
            "invalid_tolerance"
        );
        if (tolerance.Unit != ValueUnit.Money)
        {
            Require(tolerance.AbsoluteTolerance == 0 && tolerance.StoredScale == null, "nonzero_count_tolerance");
            return;
        }
        Require(tolerance.Currency != null, "missing_currency");
        var expected = 0m;
        if (tolerance.StoredScale is { } scale)
        {
            expected = 0.5m;
            for (var i = 0; i < scale; i++)
            {
                expected /= 10m;
            }
        }
        Require(tolerance.AbsoluteTolerance == expected, "invalid_precision_tolerance");
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    {
        if (!condition)
        {
            throw new AuditValidationException(code);
        }
    }
}
