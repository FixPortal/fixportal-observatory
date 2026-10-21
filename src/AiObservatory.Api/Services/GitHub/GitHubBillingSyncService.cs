using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using NodaTime;

namespace AiObservatory.Api.Services.GitHub;

/// <summary>Folds GitHub's own billed usage into retained observations and the spend ledger.</summary>
public class GitHubBillingSyncService(
    GitHubBillingClient client,
    BillingObservationWriter writer,
    IClock clock,
    ILogger<GitHubBillingSyncService> logger
)
{
    private const string Currency = "USD";

    private static readonly Dictionary<string, (string VendorKey, string CategoryKey)> ProductMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["actions"] = ("github-actions", "ci"),
        // Packages is storage/bandwidth, not Actions compute — booking it against the
        // github-actions vendor would misattribute it in every per-vendor breakdown.
        ["packages"] = ("github", "cloud"),
        ["code_quality"] = ("github", "code-review"),
        ["ghas"] = ("github", "subscription"),
    };

    private static readonly (string VendorKey, string CategoryKey) Fallback = ("github", "subscription");

    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var items = await FetchUsageItemsAsync(now.InUtc().Year, ct);

        if (items.Count == 0)
        {
            logger.LogInformation("GitHub billing: no usage items returned");
            return 0;
        }

        var written = 0;
        List<Exception>? failures = null;
        foreach (var line in Aggregate(items))
        {
            var (vendorKey, categoryKey) = ProductMap.GetValueOrDefault(line.Product, Fallback);
            try
            {
                var observation = ToObservation(line, now);
                var disposition = await writer.RecordAsync(observation, vendorKey, categoryKey, ct);
                if (
                    disposition != BillingWriteDisposition.Unchanged
                    && (observation.NetAmount != 0m || disposition == BillingWriteDisposition.Corrected)
                )
                {
                    written++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
                logger.LogError(
                    exception,
                    "GitHub billing: could not retain {Product}/{Sku} in {Month}",
                    line.Product,
                    line.Sku,
                    line.Month
                );
            }
        }

        logger.LogInformation("GitHub billing: {Written} entries written or updated", written);
        if (failures is not null)
        {
            throw new AggregateException("One or more GitHub billing lines could not be retained.", failures);
        }
        return written;
    }

    // Per-year isolation: a prior-year 403/404 must not abort the sync before the
    // current year is fetched — the open month's spend is the sync's whole purpose.
    // Only a current-year failure rethrows (an every-year failure lands there too,
    // since the current year is one of the two); a prior-year-only failure is
    // logged and the sync proceeds with the year GitHub did answer.
    private async Task<List<GitHubBillingUsageItem>> FetchUsageItemsAsync(int currentYear, CancellationToken ct)
    {
        var items = new List<GitHubBillingUsageItem>();
        GitHubBillingUnavailableException? currentYearFailure = null;
        foreach (var year in new[] { currentYear - 1, currentYear })
        {
            try
            {
                items.AddRange(await client.GetUsageAsync(year, ct));
            }
            catch (GitHubBillingUnavailableException exception)
            {
                currentYearFailure ??= year == currentYear ? exception : null;
                logger.LogWarning(
                    exception,
                    "GitHub billing: usage for {Year} is unavailable; continuing with the remaining years",
                    year
                );
            }
        }

        if (currentYearFailure is not null)
        {
            throw currentYearFailure;
        }

        return items;
    }

    // Coalesce here, not at the lookup: System.Text.Json binds a missing `product`/`sku`
    // to null despite the non-nullable record members, and a null key into ProductMap's
    // GetValueOrDefault throws ArgumentNullException outside the per-line failure guard.
    // The sentinel, not "": BillingObservationWriter rejects blank Service/Sku, so an empty
    // coalesce would die in the per-line catch and escalate to a source failure; "unknown"
    // passes validation and lands the row where an operator can see the data was incomplete.
    private const string UnknownSegment = "unknown";

    private static IEnumerable<BillingLine> Aggregate(IEnumerable<GitHubBillingUsageItem> items) =>
        items
            .GroupBy(item =>
                (
                    Month: LocalDate.FromDateOnly(item.Date).With(DateAdjusters.StartOfMonth),
                    Product: item.Product ?? UnknownSegment,
                    Sku: item.Sku ?? UnknownSegment
                )
            )
            .Select(group => new BillingLine(
                group.Key.Month,
                group.Key.Product,
                group.Key.Sku,
                group.Sum(item => item.GrossAmount),
                group.Sum(item => item.DiscountAmount),
                group.Sum(item => item.NetAmount)
            ))
            .OrderBy(line => line.Month)
            .ThenBy(line => line.Product, StringComparer.Ordinal)
            .ThenBy(line => line.Sku, StringComparer.Ordinal);

    private BillingObservation ToObservation(BillingLine line, Instant observedAt)
    {
        // Construct the ledger invariant rather than assert it: the writer throws unless
        // Gross + Credit = Net exactly, and three independent sums of GitHub's figures are not
        // guaranteed to balance. The constructed net always satisfies the invariant; when
        // GitHub's reported net disagrees, the divergence is logged and GitHub's own triple is
        // kept verbatim in RawPayload, so no information is lost either way.
        var grossAmount = line.GrossAmount;
        decimal netAmount;
        if (grossAmount == 0m && line.NetAmount != 0m)
        {
            // GitHub omitted grossAmount/discountAmount on every item of this line, so they
            // summed to 0m while the reported net stayed positive. Constructing net =
            // gross - discount would zero a real spend, and the writer treats a zero net as
            // "no spend" — deleting any existing spend row for the key. Take GitHub's
            // reported net as truth and reconstruct the gross around it so the invariant
            // still holds with the discount preserved.
            netAmount = line.NetAmount;
            grossAmount = line.NetAmount + line.DiscountAmount;
        }
        else
        {
            netAmount = grossAmount - line.DiscountAmount;
            if (netAmount != line.NetAmount)
            {
                logger.LogWarning(
                    "GitHub billing: {Product}/{Sku} in {Month} reports net {ReportedNet} but gross {GrossAmount} minus discount {DiscountAmount} is {ConstructedNet}; retaining the constructed figure (GitHub's triple stays in RawPayload)",
                    line.Product,
                    line.Sku,
                    line.Month,
                    line.NetAmount,
                    line.GrossAmount,
                    line.DiscountAmount,
                    netAmount
                );
            }
        }

        return new()
        {
            ProviderKey = "github",
            SourceId = UsageSourceIds.GitHubBillingApi,
            SourceKind = SourceKind.ProviderApi,
            UsageScope = UsageScope.Mixed,
            CostBasis = CostBasis.Billed,
            ObservationKey = ObservationKeyFor(line),
            OccurredOn = line.Month,
            BillingPeriod = line.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Service = line.Product,
            Sku = line.Sku,
            Currency = Currency,
            GrossAmount = grossAmount,
            // The ledger's invariant is Gross + Credit = Net (same as the Google arm), and
            // GitHub's discountAmount is positive, so it lands as a negative credit.
            CreditAmount = -line.DiscountAmount,
            NetAmount = netAmount,
            RawPayload = JsonSerializer.Serialize(
                new
                {
                    billingMonth = line.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    product = line.Product,
                    sku = line.Sku,
                    grossAmount = line.GrossAmount,
                    discountAmount = line.DiscountAmount,
                    netAmount = line.NetAmount,
                }
            ),
            ObservedAt = observedAt,
        };
    }

    private static string ObservationKeyFor(BillingLine line)
    {
        var month = line.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        // Keep the plain product:sku form byte-identical to the format every stored row was
        // written under: ApplyObservationAsync and FindSpendAsync match on the key verbatim,
        // so an unconditional format change orphans the whole ledger and double-counts every
        // historical month on the first sync after deploy. Length-prefix only when a segment
        // carries the ':' delimiter, which the plain form cannot disambiguate
        // (product="a:b"/sku="c" against product="a"/sku="b:c").
        var material =
            line.Product.Contains(':') || line.Sku.Contains(':')
                ? $"{Part(line.Product)}{Part(line.Sku)}"
                : $"{line.Product}:{line.Sku}";
        var readable = $"github:{month}:{material}";
        if (readable.Length <= 200)
        {
            return readable;
        }

        return $"github:{month}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
    }

    private static string Part(string value) => $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

    private sealed record BillingLine(
        LocalDate Month,
        string Product,
        string Sku,
        decimal GrossAmount,
        decimal DiscountAmount,
        decimal NetAmount
    );
}
