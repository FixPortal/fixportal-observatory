// ReSharper disable RedundantUsingDirective
using System.Globalization;
// ReSharper restore RedundantUsingDirective
using System.Net;
using System.Text.Json.Serialization;

namespace AiObservatory.Api.Services.GitHub;

/// <summary>
/// Reads an organisation's billed usage from GitHub's enhanced billing platform:
/// <c>GET /organizations/{org}/settings/billing/usage</c>.
/// <para>
/// This is the only authoritative source for GitHub spend. The org bill is not paid from
/// the account the spend CSV exports, so before this existed the ledger understated GitHub
/// by the entire org bill — every penny of Actions, Advanced Security and Code Quality AI
/// Credits was invisible.
/// </para>
/// <para>
/// The token needs read access to the org's billing ("Plan" read on a fine-grained PAT, or
/// <c>admin:org</c> on a classic one). That is a different scope from the
/// <c>contents/pull-requests/actions</c> set the activity ingester uses, so the same
/// <c>GITHUB_TOKEN</c> secret must carry both for this arm to work.
/// </para>
/// </summary>
public class GitHubBillingClient(HttpClient http, string org, ILogger<GitHubBillingClient> logger)
{
    /// <summary>
    /// Named-client key. The org is a constructor argument, which a typed client cannot
    /// supply, so the HttpClient is configured by name and the instance is built by a
    /// factory registration.
    /// </summary>
    public const string HttpClientName = "github-billing";

    /// <summary>
    /// Usage for one calendar year, or an empty list when the org has no billed usage that
    /// year. An empty list is ONLY the 200-with-no-rows case: a 403/404 is not an empty year
    /// and throws (see remarks), so "the year predates the billing history" and "the token
    /// cannot see this year" are never conflated.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="GitHubBillingUnavailableException"/> on 403/404 — a token missing
    /// the billing scope must surface as a source failure, not as an empty (and therefore
    /// "successful") sync: the two are indistinguishable downstream and the status surface
    /// would report the source healthy while the entire org bill went missing. The worker
    /// isolates this arm already, and the sync isolates each YEAR, so a throw for one year
    /// neither takes the rest of the daily cycle down nor skips the other year's fetch.
    /// </remarks>
    /// <exception cref="GitHubBillingUnavailableException">
    /// The token cannot see the org's billing (403/404 — most often a missing billing scope).
    /// </exception>
    public virtual async Task<IReadOnlyList<GitHubBillingUsageItem>> GetUsageAsync(
        int year,
        CancellationToken ct = default
    )
    {
        var url =
            $"/organizations/{Uri.EscapeDataString(org)}/settings/billing/usage"
            + $"?year={year.ToString(CultureInfo.InvariantCulture)}";

        using var response = await http.GetAsync(url, ct);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // 403 = token lacks the billing scope; 404 = org invisible to this token, which
            // GitHub also returns for "exists but you may not know that". Same remedy either
            // way, and neither is retryable — so throw (like the 401 path already does via
            // EnsureSuccessStatusCode) and let the caller record a source failure rather
            // than mistaking it for a genuinely empty year.
            logger.LogWarning(
                "GitHub billing usage unavailable for {Org} ({Status}) — token likely lacks billing read scope",
                org,
                (int)response.StatusCode
            );
            throw new GitHubBillingUnavailableException(
                $"GitHub billing usage unavailable for org '{org}' ({(int)response.StatusCode}) — "
                    + "the token likely lacks billing read scope"
            );
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<GitHubBillingUsageResponse>(ct);
        return payload?.UsageItems ?? [];
    }

    private sealed record GitHubBillingUsageResponse(List<GitHubBillingUsageItem>? UsageItems);
}

/// <param name="Date">
/// Start of the usage month. GitHub reports this platform's usage monthly, so this is a
/// period marker, not the date of an individual charge — hence <see cref="DateOnly"/> rather
/// than a timestamp. See <see cref="GitHubBillingDateConverter"/> for why it needs a
/// converter at all (the docs and the live API disagree about the wire format).
/// </param>
/// <param name="Product">Coarse billing product, e.g. <c>actions</c>, <c>ghas</c>, <c>code_quality</c>.</param>
/// <param name="Sku">Human-readable line, e.g. <c>Code Quality AI Credits</c>.</param>
/// <param name="GrossAmount">USD before the discount — the full metered price of the line.</param>
/// <param name="DiscountAmount">
/// USD GitHub took off, e.g. the included-minutes allowance given back on the same invoice.
/// Positive, as GitHub sends it.
/// </param>
/// <param name="NetAmount">
/// USD actually billed: <c>grossAmount</c> minus <c>discountAmount</c>. All three are kept so
/// gross-versus-credit views match the Google arm instead of showing GitHub gross as zero-credit
/// net.
/// </param>
public sealed record GitHubBillingUsageItem(
    [property: JsonConverter(typeof(GitHubBillingDateConverter))] DateOnly Date,
    string Product,
    string Sku,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal NetAmount
);

/// <summary>
/// The GitHub token cannot see the organisation's billing (403/404 from the usage
/// endpoint). Distinct from "no usage this year": this must be recorded as a source
/// failure so the status surface does not report an unscoped token as a healthy sync.
/// </summary>
public sealed class GitHubBillingUnavailableException(string message) : Exception(message);
