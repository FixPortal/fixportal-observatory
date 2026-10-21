using NodaTime;

namespace AiObservatory.Api.Services;

internal static class BudgetAlertEmailLease
{
    internal static readonly Duration Duration = Duration.FromMinutes(15);
}
