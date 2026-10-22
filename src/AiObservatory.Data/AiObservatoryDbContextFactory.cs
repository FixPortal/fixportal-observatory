using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiObservatory.Data;

// Used only by `dotnet ef` tooling (migrations, scaffolding). Never called at runtime.
// ReSharper disable once UnusedType.Global
public class AiObservatoryDbContextFactory : IDesignTimeDbContextFactory<AiObservatoryDbContext>
{
    public AiObservatoryDbContext CreateDbContext(string[] args)
    {
        var connection = args is ["--connection", var explicitConnection]
            ? explicitConnection
            : "Host=localhost;Database=aiobservatory;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.UseNodaTime())
            .Options;
        return new AiObservatoryDbContext(options);
    }
}
