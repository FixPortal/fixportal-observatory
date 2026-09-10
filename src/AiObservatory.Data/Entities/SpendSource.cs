namespace AiObservatory.Data.Entities;

/// <summary>
/// How a <see cref="SpendEntry"/> reached the ledger. Provenance only — never a bank reference.
/// <para>
/// Stored as text, so the member NAMES are the persisted values and must not be renamed;
/// adding a member is free, reordering is harmless, renaming one orphans every row that
/// carries it (including its half of the unique <c>(Source, EntryKey)</c> index).
/// </para>
/// </summary>
public enum SpendSource
{
    Manual,
    Csv,
    Portal,

    /// <summary>
    /// Pulled from a vendor's own billing API rather than a statement — currently GitHub's
    /// org billing usage. Distinct from <see cref="Csv"/> because the vendor is the
    /// authority here: the figure is what GitHub says it billed, not what a bank export
    /// happened to capture.
    /// </summary>
    Api,
}
