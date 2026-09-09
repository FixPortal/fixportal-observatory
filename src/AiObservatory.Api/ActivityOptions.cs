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
        if (string.IsNullOrWhiteSpace(asScalar))
        {
            return [];
        }

        // An unresolved Key Vault reference must be rejected BEFORE the delimited split: App
        // Service leaves "@Microsoft.KeyVault(VaultName=v;SecretName=s)" in place verbatim when
        // the secret is unreadable, and ';' is in the split set, so splitting first would yield
        // two slash-free, whitespace-free fragments that both look like owners. Empty means
        // allow-everything here, which fails open: the tabs show data instead of going blank,
        // and the disallowed-projects cleanup (which negates this list and deletes) becomes a
        // no-op instead of matching every stored session.
        var trimmed = asScalar.Trim();
        if (IsUnresolvedKeyVaultReference(trimmed))
        {
            return [];
        }

        return Clean(trimmed.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
    }

    // The no-'/' rule alone does NOT discard an unresolved "@Microsoft.KeyVault(...)" reference:
    // only the SecretUri form contains slashes; the VaultName/SecretName form has none. Without
    // an explicit check the literal would be registered as an owner and would quietly filter
    // every real session out of the dashboard — or, negated by the disallowed-projects cleanup,
    // match every session for deletion.
    private static bool IsUnresolvedKeyVaultReference(string value) =>
        value.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase);

    private static string[] Clean(IEnumerable<string> values) =>
        [.. values.Select(v => v.Trim()).Where(IsOwnerName).Distinct(StringComparer.Ordinal)];

    // An owner is a single path segment: non-empty, no '/', no whitespace, and not an
    // unresolved Key Vault reference (see above — the array shape can carry one too when an
    // indexed environment variable holds it).
    private static bool IsOwnerName(string value) =>
        value.Length > 0
        && !value.Contains('/')
        && !value.Any(char.IsWhiteSpace)
        && !IsUnresolvedKeyVaultReference(value);
}
