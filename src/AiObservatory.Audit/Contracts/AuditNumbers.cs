namespace AiObservatory.Audit.Contracts;

public static class AuditNumbers
{
    public static long AddCounters(long left, long right)
    {
        if (left < 0 || right < 0)
        {
            throw new AuditValidationException("negative_counter");
        }
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            throw new AuditValidationException("counter_overflow");
        }
    }
}
