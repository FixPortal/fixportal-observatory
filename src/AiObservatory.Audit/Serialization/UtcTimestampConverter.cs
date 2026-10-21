using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiObservatory.Audit.Serialization;

internal sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (
            reader.TokenType != JsonTokenType.String
            || !HasUtcOffset(reader.GetString())
            || !reader.TryGetDateTimeOffset(out var value)
            || value.Offset != TimeSpan.Zero
        )
        {
            throw new JsonException();
        }
        return value;
    }

    private static bool HasUtcOffset(string? value) =>
        value != null && (value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal));

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
}
