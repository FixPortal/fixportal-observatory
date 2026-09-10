namespace AiObservatory.Ingest;

public class IngestOptions
{
    public const string SectionName = "Ingest";

    // Setters are populated by Microsoft.Extensions.Configuration options binding.
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public int PollingIntervalMinutes { get; set; } = 60;
    public TimeSpan PollingInterval => TimeSpan.FromMinutes(PollingIntervalMinutes);

    // Each poll re-requests this many trailing days (ending yesterday) so a poll that was
    // down across a midnight — or ran before a provider's daily totals settled — backfills
    // the missed day on a later cycle. Already-recorded days are cheap no-ops (dedup).
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public int LookbackDays { get; set; } = 3;

    // owner/repo pairs to poll for PR/commit/CI activity. Empty disables the
    // GitHub Activity client entirely (see Program.cs) — there is no out-of-repo
    // hook holding this filter, unlike Claude Activity's project allowlist.
    //
    // Most repos in the allowlist are private, so the list is NOT committed to this
    // (public) repo. It is supplied at deploy time as a single delimited value —
    // see ResolveGitHubRepoAllowlist below and infra/modules/ingest.bicep.
    public string[] GitHubRepoAllowlist { get; set; } = [];

    public string? GoogleCloudCatalogApiKey { get; set; }
    public string? GoogleCloudCatalogServiceId { get; set; }

    public static void BindGoogleCloudCatalog(IConfiguration cfg, IngestOptions options)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(options);
        options.GoogleCloudCatalogApiKey = cfg["GOOGLE_CLOUD_CATALOG_API_KEY"];
        options.GoogleCloudCatalogServiceId = cfg["GOOGLE_CLOUD_CATALOG_SERVICE_ID"];
    }

    // Binds the allowlist from either shape: a JSON/indexed-env array
    // (Ingest:GitHubRepoAllowlist:0, :1, ...) for local development, or a single
    // comma/semicolon/newline-delimited string for the deployed Key Vault secret.
    // App Service surfaces a KV reference as one scalar app setting, so the array
    // form alone cannot express it.
    public static string[] ResolveGitHubRepoAllowlist(IConfiguration cfg)
    {
        var key = $"{SectionName}:{nameof(GitHubRepoAllowlist)}";

        var asArray = cfg.GetSection(key).Get<string[]>() ?? [];
        if (asArray.Length > 0)
        {
            return Clean(asArray);
        }

        var asScalar = cfg[key];
        return string.IsNullOrWhiteSpace(asScalar)
            ? []
            : Clean(asScalar.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
    }

    // Keeps only well-formed "owner/repo" entries. The exactly-two-segments rule is also
    // what discards an unresolved "@Microsoft.KeyVault(...)" reference, which App Service
    // leaves in place verbatim when the secret is absent or unreadable: the
    // VaultName/SecretName form contains no '/' at all, and the SecretUri form contains
    // several. Without that, splitting the literal on ',' would register the GitHub client
    // with garbage repos and 404 hourly forever.
    private static string[] Clean(IEnumerable<string> values) =>
        [.. values.Select(v => v.Trim()).Where(IsOwnerRepo).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static bool IsOwnerRepo(string value)
    {
        var parts = value.Split('/');
        return parts is [{ Length: > 0 }, { Length: > 0 }] && parts.All(part => !part.Any(char.IsWhiteSpace));
    }
}
