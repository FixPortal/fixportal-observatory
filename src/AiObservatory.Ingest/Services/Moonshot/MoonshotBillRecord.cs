using NodaTime;

namespace AiObservatory.Ingest.Services.Moonshot;

/// <summary>
/// One billed line of a Moonshot daily bill, in USD. <see cref="ModelId"/> and <see cref="TokenType"/> are
/// null on the residual line that carries charges the per-model breakdown does not list.
/// </summary>
public sealed record MoonshotBillRecord(
    LocalDate Date,
    string? ModelId,
    string? TokenType,
    string? KeyId,
    long? Tokens,
    decimal TotalUsd,
    decimal CashUsd,
    decimal VoucherUsd,
    string RawJson
)
{
    public const string ResidualSku = "non-token charges";

    public string Sku => ModelId is null ? ResidualSku : $"{ModelId}/{TokenType}";
}
