using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// A user-managed vendor. Distinct from <see cref="Provider"/>, which means "a provider
/// whose tokens we can meter" — vendors include CodeRabbit, Gitar and GitHub Actions,
/// which have no tokens at all.
/// </summary>
public sealed class SpendVendor
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Stable slug ("coderabbit").</summary>
    public string Key { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Optional link to a token provider. The ONLY join between billed spend and the
    /// estimate, so variance is possible exactly where an estimate exists and structurally
    /// impossible where it does not. Null for CodeRabbit, Gitar, GitHub Actions.
    /// </summary>
    public Provider? Provider { get; set; }

    /// <summary>Pre-fills the entry form and lets a CSV omit the category column.
    /// A default, never a constraint — Anthropic spend lands in several categories.</summary>
    public Guid? DefaultCategoryId { get; set; }

    public Instant? ArchivedAt { get; set; }
}
