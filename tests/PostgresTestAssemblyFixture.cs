using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(AiObservatory.Testing.PostgresTestAssemblyFixture))]

namespace AiObservatory.Testing;

/// <summary>
/// Supplies PostgreSQL automatically for Visual Studio and command-line test runs. CI or a
/// developer can still provide TEST_DB_CONNECTION explicitly; in that case no container is
/// created and the supplied database server remains entirely caller-owned.
/// </summary>
public sealed class PostgresTestAssemblyFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public async ValueTask InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")))
        {
            return;
        }

        _container = new PostgreSqlBuilder("postgres:17").WithDatabase("postgres").Build();

        try
        {
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start PostgreSQL for tests. Start Docker or set TEST_DB_CONNECTION.",
                ex
            );
        }
        Environment.SetEnvironmentVariable("TEST_DB_CONNECTION", _container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is null)
        {
            return;
        }

        Environment.SetEnvironmentVariable("TEST_DB_CONNECTION", null);
        await _container.DisposeAsync();
    }
}
