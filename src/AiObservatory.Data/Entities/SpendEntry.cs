using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// One billed charge. Carries NO account, card, counterparty, invoice number or bank
/// transaction id as a typed property — see spec §3. Note the limits of the guard:
/// <c>ArchitectureTests.SpendEntry_must_not_carry_bank_linkage</c> checks property <i>names</i>
/// only, never content, and <see cref="RawPayload"/> stores the provider billing API response
/// verbatim, so the no-bank-linkage boundary rests on what providers choose to include, not
/// on the test. Treat any new provenance field — or any new writer of <see cref="RawPayload"/>
/// — with the same suspicion.
/// </summary>
public sealed class SpendEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The charge date. Drives both the reporting period and the FX rate used.</summary>
    public LocalDate OccurredOn { get; set; }

    public Guid VendorId { get; set; }
    public Guid CategoryId { get; set; }

    /// <summary>
    /// Amount as charged, in <see cref="Currency"/>. <b>Signed</b>: positive is a charge,
    /// negative is a refund or credit. Never zero — a zero-value charge is a data-entry
    /// mistake, and <c>CK_SpendEntry_Amount_NonZero</c> rejects it.
    /// <para>
    /// Signed rather than a separate refund flag so that <see cref="AmountGbp"/> stays the
    /// one column every aggregate sums, unconditionally. A flag would oblige every total,
    /// breakdown and time series to remember to subtract, and the one that forgot would
    /// silently overstate billed spend — the exact failure this project exists to correct.
    /// </para>
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217, upper case.</summary>
    public string Currency { get; set; } = "GBP";

    /// <summary>
    /// <see cref="Amount"/> in GBP, converted once at write using the rate on
    /// <see cref="OccurredOn"/> and never recomputed. Totals sum this column. Converting at
    /// render instead — the convention used for token costs — would make a historical
    /// charge show a different figure every day and an annual total drift with the market.
    /// Carries <see cref="Amount"/>'s sign, so a plain <c>SUM</c> nets refunds off charges.
    /// </summary>
    public decimal AmountGbp { get; set; }

    /// <summary>The rate actually applied, so every conversion is auditable. 1 when
    /// <see cref="Currency"/> is GBP.</summary>
    public decimal FxRate { get; set; }

    public string? Description { get; set; }

    public SpendSource Source { get; set; }

    /// <summary>
    /// Idempotency key, unique per source. Null for manual entries: a person typing the
    /// same charge twice is a mistake worth showing them, not one to silence.
    /// </summary>
    public string? EntryKey { get; set; }

    public Instant RecordedAt { get; set; }

    /// <summary>
    /// The provider billing API response body, persisted verbatim and unredacted (validated
    /// only as parseable JSON). This is content the bank-linkage rule in the class doc does
    /// NOT cover — do not surface it to share-link/read-only consumers without review.
    /// </summary>
    public string RawPayload { get; set; } = "{}";
    public string SourceId { get; set; } = UsageSourceIds.LegacySpend;
    public SourceKind SourceKind { get; set; } = SourceKind.Legacy;
    public UsageScope UsageScope { get; set; } = UsageScope.Unknown;
    public CostBasis CostBasis { get; set; } = CostBasis.Unknown;
    public Instant ObservedAt { get; set; }
}
