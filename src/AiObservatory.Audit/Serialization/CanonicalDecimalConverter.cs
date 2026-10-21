using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiObservatory.Audit.Serialization;

internal sealed class CanonicalDecimalConverter : JsonConverter<decimal>
{
    internal static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        if (
            text is not { Length: > 0 and <= 64 }
            || !decimal.TryParse(
                text,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out value
            )
        )
        {
            return false;
        }
        var unsigned = text.StartsWith('-') ? text[1..] : text;
        var parts = unsigned.Split('.');
        if (parts.Length > 2 || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
        {
            return false;
        }
        var whole = parts[0].TrimStart('0');
        whole = whole.Length == 0 ? "0" : whole;
        var fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
        var normalized = fraction.Length == 0 ? whole : $"{whole}.{fraction}";
        if (text.StartsWith('-') && normalized != "0")
        {
            normalized = "-" + normalized;
        }
        // decimal.TryParse may round an over-precise string. Only an exact value is valid evidence.
        return normalized == Format(value);
    }

    internal static string Format(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException();

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));
}
