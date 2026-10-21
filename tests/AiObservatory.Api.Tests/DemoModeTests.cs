using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace AiObservatory.Api.Tests;

public class DemoModeTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("1", false)]
    public void IsEnabled_only_for_the_literal_word_true(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DemoMode.SettingName] = value })
            .Build();

        DemoMode.IsEnabled(configuration).Should().Be(expected);
    }

    [Theory]
    [InlineData("Host=fpaiodemo-db.postgres.database.azure.com;Database=aiobservatory;Username=u;Password=p")]
    [InlineData("Host=FPAIODEMO-DB.postgres.database.azure.com;Database=a;Username=u;Password=p")]
    [InlineData("Host=localhost;Database=a;Username=u;Password=p")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=a;Username=u;Password=p")]
    [InlineData("Host=db;Database=a;Username=u;Password=p")]
    public void EnsureSafeDatabase_accepts_demo_and_local_hosts(string connection)
    {
        var act = () => DemoMode.EnsureSafeDatabase(connection);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Host=fpaiobs-db.postgres.database.azure.com;Database=a;Username=u;Password=secret-pw")]
    [InlineData("Host=FPAIOBS-DB.postgres.database.azure.com;Database=a;Username=u;Password=secret-pw")]
    [InlineData(
        "Host=fpaiodemo-db.postgres.database.azure.com,fpaiobs-db.postgres.database.azure.com;Database=a;Username=u;Password=secret-pw"
    )]
    [InlineData("Database=a;Username=u;Password=secret-pw")]
    public void EnsureSafeDatabase_rejects_everything_else_without_leaking_the_password(string connection)
    {
        var act = () => DemoMode.EnsureSafeDatabase(connection);

        act.Should()
            .Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("OBSERVATORY_DEMO_MODE") && !e.Message.Contains("secret-pw"));
    }
}
