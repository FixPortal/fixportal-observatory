namespace AiObservatory.Api.Services.GitHub;

public static class GitHubBillingRegistration
{
    private static readonly Uri GitHubApi = new("https://api.github.com");

    /// <summary>
    /// Registers the GitHub billing arm when <c>GITHUB_TOKEN</c> and
    /// <c>GITHUB_BILLING_ORG</c> are both configured, and does nothing otherwise —
    /// <see cref="Intelligence.IntelligenceWorkerService"/> resolves the sync service
    /// optionally and skips the step when it is absent.
    /// <para>
    /// The token needs billing read ("Plan" read on a fine-grained PAT, or
    /// <c>admin:org</c> on a classic one) — a broader scope than the ingest worker's
    /// activity token, even though both read the same secret.
    /// </para>
    /// </summary>
    public static void AddGitHubBilling(this IServiceCollection services, IConfiguration configuration)
    {
        var token = configuration["GITHUB_TOKEN"];
        var org = configuration["GITHUB_BILLING_ORG"];

        if (!IsConfigured(token) || !IsConfigured(org))
        {
            return;
        }

        services.AddHttpClient(
            GitHubBillingClient.HttpClientName,
            c =>
            {
                c.BaseAddress = GitHubApi;
                c.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
                c.DefaultRequestHeaders.Add("User-Agent", "fpaiobs-api");
                c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                // Bounded well below the 100s default: this runs inside the daily background
                // cycle, and a hung GitHub must not hold that cycle open indefinitely.
                c.Timeout = TimeSpan.FromSeconds(30);
            }
        );

        services.AddScoped(sp => new GitHubBillingClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubBillingClient.HttpClientName),
            org!,
            sp.GetRequiredService<ILogger<GitHubBillingClient>>()
        ));

        services.AddScoped<GitHubBillingSyncService>();
    }

    /// <summary>
    /// A Key Vault reference App Service could not resolve is left as the literal
    /// "@Microsoft.KeyVault(...)" string. That is non-empty, so a plain null check would
    /// enable the arm with a garbage credential and log a 403 every day rather than staying
    /// off. Mirrors the same gate in AiObservatory.Ingest's composition root.
    /// </summary>
    private static bool IsConfigured(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase);
}
