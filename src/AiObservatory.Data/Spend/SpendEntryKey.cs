using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NodaTime;

namespace AiObservatory.Data.Spend;

/// <summary>
/// Derives the idempotency key for an imported spend row, so re-importing a file lands
/// nothing rather than doubling a total.
/// </summary>
public static class SpendEntryKey
{
    /// <param name="occurrence">
    /// Zero-based index among the rows of the SAME import that share every other input.
    /// Load-bearing: without it, two genuine identical charges on one day collide and the
    /// second is silently dropped. Scoped to one file, which is a known and accepted limit
    /// (spec §6) — identical charges split across two imports still collide, and the fix
    /// is to differentiate the description.
    /// </param>
    public static string Derive(
        LocalDate occurredOn,
        string vendorKey,
        decimal amount,
        string currency,
        string? description,
        int occurrence
    )
    {
        // Invariant culture throughout: a machine with a comma decimal separator must not
        // derive a different key for the same charge. Each field is length-prefixed, so no
        // combination of field contents can produce the same material as a different
        // combination, pipes or other characters in the values included.
        var material = string.Join(
            '|',
            Part(occurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            Part(vendorKey.Trim().ToLowerInvariant()),
            Part(amount.ToString("F4", CultureInfo.InvariantCulture)),
            Part(currency.Trim().ToUpperInvariant()),
            Part((description ?? string.Empty).Trim()),
            Part(occurrence.ToString(CultureInfo.InvariantCulture))
        );

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash); // 64 chars, comfortably inside varchar(200)
    }

    private static string Part(string s) => $"{s.Length}:{s}";
}
