using NodaTime;

namespace AiObservatory.Data.Audit;

/// <summary>Finite retention supports at most 36,500 days; null explicitly requests indefinite retention.</summary>
public static class AuditRetentionPolicy
{
    public const int MaxFiniteDays = 36_500;

    public static bool IsSupportedDays(int? days) => days is null or > 0 and <= MaxFiniteDays;

    public static bool TryGetExpiry(Instant from, int? days, out Instant? expiry)
    {
        expiry = null;
        if (!IsSupportedDays(days))
        {
            return false;
        }
        if (days == null)
        {
            return true;
        }
        // The finite bound makes Duration construction safe. Compare remaining representable
        // time before adding, so even a supported duration cannot overflow the actual Instant.
        var duration = Duration.FromDays(days.Value);
        if (duration > Instant.MaxValue - from)
        {
            return false;
        }
        expiry = from.Plus(duration);
        return true;
    }
}
