using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// A user-managed spend category ("Code Review", "Credits", "CI"). Categories are data
/// rather than an enum so a new spend type needs no migration or deploy.
/// </summary>
public sealed class SpendCategory
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Stable slug ("code-review"). Imports and the portal feed reference this, so
    /// renaming <see cref="DisplayName"/> never breaks a feed.</summary>
    public string Key { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>CSS custom-property name used for this category's colour in charts.</summary>
    public string ColorVar { get; set; } = "";

    public int SortOrder { get; set; }

    /// <summary>Soft delete. A retired category is hidden from pickers but must still
    /// resolve for the historical rows that reference it.</summary>
    public Instant? ArchivedAt { get; set; }
}
