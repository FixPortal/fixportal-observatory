using Npgsql;

namespace AiObservatory.Api;

/// <summary>
/// Demo mode turns the API into a disposable, synthetic-data instance: seed and reset routes
/// exist, every other write is refused, the background worker does not run. It must never be
/// switched on against real data, so enabling it also requires the database to look like a
/// demo (or local) database.
/// </summary>
internal static class DemoMode
{
    public const string SettingName = "OBSERVATORY_DEMO_MODE";

    private const string DemoHostPrefix = "fpaiodemo-";

    // Loopback for local runs and CI Testcontainers; `db` is the Compose service name.
    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1", "db"];

    public static bool IsEnabled(IConfiguration configuration) =>
        string.Equals(configuration[SettingName], "true", StringComparison.OrdinalIgnoreCase);

    public static void EnsureSafeDatabase(string connectionString)
    {
        var hostValue = new NpgsqlConnectionStringBuilder(connectionString).Host ?? string.Empty;
        var hosts = hostValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Every listed host must qualify: one safe host in a failover list must not vouch for
        // an unsafe one beside it. Only the host is reported, never the credentials.
        if (hosts.Length > 0 && hosts.All(IsDemoOrLocal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{SettingName}=true refused to start: database host '{hostValue}' is not a demo database. "
                + $"Demo hosts start with '{DemoHostPrefix}'; local runs may use {string.Join(", ", LocalHosts)}."
        );
    }

    private static bool IsDemoOrLocal(string host) =>
        host.StartsWith(DemoHostPrefix, StringComparison.OrdinalIgnoreCase)
        || LocalHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
}
