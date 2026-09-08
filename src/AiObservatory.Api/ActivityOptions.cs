namespace AiObservatory.Api;

/// <summary>
/// Which GitHub accounts count as "real" projects for the Activity and GitHub tabs.
/// </summary>
public class ActivityOptions
{
    public const string SectionName = "Activity";

    /// <summary>
    /// Owner names whose projects and repositories appear in the Activity and GitHub tabs.
    /// Everything else — scratch folders, other orgs, non-git directories falling back to a leaf
    /// folder name — is ingestion noise and stays out of the project breakdown and treemap.
    /// <para>
    /// EMPTY MEANS ALLOW EVERYTHING, and empty is the default. This list used to be the hardcoded
    /// pair <c>["FixPortal", "fix-portal"]</c>, which meant a self-hoster got two of six tabs
    /// silently blank with nothing in the UI or the docs to explain it. A filter nobody set must
    /// not be a filter that hides everything.
    /// </para>
    /// <para>
    /// Matching is ORDINAL, so casing must match the value the producer actually writes: for the
    /// FixPortal deployment that is the display form <c>FixPortal</c> (plus the legacy
    /// <c>fix-portal</c> so historical rows stay visible). That deployment's value must remain a
    /// subset of the producer-side allowlist in the out-of-repo observe-sweep.ps1 hook.
    /// </para>
    /// </summary>
    // Setter is populated by Microsoft.Extensions.Configuration options binding.
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string[] ProjectOwners { get; set; } = [];

    /// <summary>
    /// Binds the owner list from either shape: a JSON/indexed-env array
    /// (<c>Activity:ProjectOwners:0</c>, <c>:1</c>, …) for local development and Compose, or a
    /// single comma/semicolon/newline-delimited string, which is the only shape App Service can
    /// surface for a Key Vault reference. Mirrors
    /// <c>IngestOptions.ResolveGitHubRepoAllowlist</c> deliberately — same problem, same answer.
    /// </summary>
    public static string[] ResolveProjectOwners(IConfiguration cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var key = $"{SectionName}:{nameof(ProjectOwners)}";

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

    // An owner is a single path segment. The no-'/' rule is also what discards an unresolved
    // "@Microsoft.KeyVault(...)" reference, which App Service leaves in place verbatim when the
    // secret is absent or unreadable: the SecretUri form contains several slashes, and the
    // VaultName/SecretName form contains one. Without that, the literal would be registered as an
    // owner and would quietly filter every real session out of the dashboard.
    private static string[] Clean(IEnumerable<string> values) =>
        [.. values.Select(v => v.Trim()).Where(IsOwnerName).Distinct(StringComparer.Ordinal)];

    private static bool IsOwnerName(string value) =>
        value.Length > 0 && !value.Contains('/') && !value.Any(char.IsWhiteSpace);
}
