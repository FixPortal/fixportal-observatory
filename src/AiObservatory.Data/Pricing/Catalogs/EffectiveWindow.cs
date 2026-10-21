using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

/// <summary>
/// Effective-date window selection shared by every catalog. An entry whose
/// <c>EffectiveDateIsProviderDeclared</c> is false carries the retrieval date as its
/// <c>EffectiveFrom</c> — a fetch stamp, not the price's real start — so when no window
/// genuinely covers the usage date, the earliest assumed window is treated as open-ended
/// backwards. Without that, every event predating the first bundled fetch (all bundled
/// catalogs start at their 2026-08-24 retrieval date) would be unpriceable history.
/// Provider-declared dates always gate: a provider-announced future price never applies early.
/// </summary>
internal static class EffectiveWindow
{
    public static IReadOnlyList<T> ApplicableAt<T>(
        IEnumerable<T> matches,
        LocalDate usageDate,
        Func<T, LocalDate> effectiveFrom,
        Func<T, bool> isProviderDeclared
    )
    {
        var applicable = matches.Where(entry => effectiveFrom(entry) <= usageDate).ToList();
        if (applicable.Count > 0)
        {
            return applicable;
        }

        var assumed = matches.Where(entry => !isProviderDeclared(entry)).ToList();
        if (assumed.Count == 0)
        {
            return [];
        }

        var earliest = assumed.Min(effectiveFrom);
        return assumed.Where(entry => effectiveFrom(entry) == earliest).ToList();
    }
}
