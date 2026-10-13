namespace AiObservatory.Api.Services;

public static class SlackAlertNotifierRegistration
{
    /// <summary>
    /// The webhook URL is the credential (its path is the whole auth), so this client's
    /// HttpClientFactory logging handlers are removed -- otherwise every budget alert writes the
    /// full URL to the logs and App Insights. Matches the Ingest secret-carrying registrations.
    /// Program.cs and the DI composition tests both call this, so the tests pin the real wiring.
    /// </summary>
    public static IServiceCollection AddSlackAlertNotifier(this IServiceCollection services)
    {
        services
            .AddHttpClient<SlackAlertNotifier>()
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers();
        services.AddKeyedTransient<IAlertNotifier>("slack", (sp, _) => sp.GetRequiredService<SlackAlertNotifier>());
        return services;
    }
}
