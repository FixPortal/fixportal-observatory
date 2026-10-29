namespace AiObservatory.Data.Spend;

/// <summary>
/// A billed amount that cannot be stored because it rounds to zero GBP at the ledger's four-decimal precision.
/// A subtype of <see cref="InvalidOperationException"/> so existing handlers still match; a source that can see
/// amounts this small catches it to skip one observation instead of aborting the whole poll.
/// </summary>
public sealed class LedgerPrecisionException(string message) : InvalidOperationException(message);
