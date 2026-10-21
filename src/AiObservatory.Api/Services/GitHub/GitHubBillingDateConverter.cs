using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiObservatory.Api.Services.GitHub;

/// <summary>
/// Reads GitHub's billing <c>date</c> field, which is a calendar month marker rather than a
/// moment in time.
/// <para>
/// A converter is needed because the documentation and the live API disagree about the
/// format. <see href="https://docs.github.com/en/rest/billing/usage">The REST reference</see>
/// describes a date-only <c>YYYY-MM-DD</c> value, while the endpoint currently returns a full
/// ISO 8601 timestamp (<c>2026-07-01T00:00:00Z</c>) — verified against the FixPortal org on
/// 2026-07-27. A plain <c>DateOnly</c> property rejects the timestamp and a plain
/// <c>DateTimeOffset</c> rejects the date-only value, so binding to either alone would break
/// the moment GitHub moved to the other. Accepting both costs nothing and cannot be caught
/// out either way.
/// </para>
/// <para>
/// A rejected payload matters more here than the JSON exception suggests: the sync's only
/// caller catches broadly, so a deserialization failure would degrade to "no GitHub spend
/// recorded" — silently reintroducing the exact understatement this arm exists to fix.
/// </para>
/// </summary>
public sealed class GitHubBillingDateConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString() ?? throw new JsonException("GitHub billing date was null");

        // Date-only first: it is the documented shape, and the cheaper parse.
        if (
            DateOnly.TryParseExact(
                raw,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateOnly
            )
        )
        {
            return dateOnly;
        }

        // AssumeUniversal, not RoundtripKind: a timestamp carrying no offset at all
        // ("2026-07-01T00:00:00") is otherwise read as MACHINE-LOCAL and then converted to
        // UTC below, which shifts a midnight month marker into the previous month anywhere
        // east of Greenwich — 2026-07-01 becomes 2026-06-30 on a BST machine, filing a whole
        // month's GitHub spend under the wrong period, and only on some machines. GitHub
        // currently always sends the Z, so this is latent rather than live; an explicit
        // offset is still honoured exactly as before.
        if (
            DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp
            )
        )
        {
            // UtcDateTime, not LocalDateTime: the value is UTC-anchored, and reading it
            // through the machine's time zone could shift a month-start marker into the
            // previous month west of Greenwich — which would file a whole month's GitHub
            // spend under the wrong period.
            return DateOnly.FromDateTime(timestamp.UtcDateTime);
        }

        throw new JsonException($"Unrecognised GitHub billing date format: {raw}");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}
