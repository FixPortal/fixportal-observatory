using System.Text.Json;

namespace AiObservatory.Ingest.Services;

internal static class AdminResponseJson
{
    public static string? RequireCursor(JsonElement root, bool hasMore, string provider)
    {
        if (!root.TryGetProperty("next_page", out var value))
        {
            throw new InvalidDataException($"{provider} response is missing next_page.");
        }
        if (!hasMore)
        {
            if (value.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidDataException($"{provider} final page has an unexpected cursor.");
            }
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{provider} response requires a non-empty next_page cursor.");
        }
        return value.GetString();
    }

    public static string RequireNonBlankString(JsonElement element, string propertyName, string provider)
    {
        if (
            !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
        )
        {
            throw new InvalidDataException($"{provider} response is missing {propertyName}.");
        }
        return value.GetString()!;
    }
}
