using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace AiObservatory.Data.Pricing;

public static class PricingCatalogJson
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    }.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    public static string Serialize<T>(T catalog) => JsonSerializer.Serialize(catalog, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException("The normalized pricing catalog is null.");
}
