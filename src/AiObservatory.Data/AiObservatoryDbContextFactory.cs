using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiObservatory.Data;

// Used only by `dotnet ef` tooling (migrations, scaffolding). Never called at runtime.
// ReSharper disable once UnusedType.Global
public class AiObservatoryDbContextFactory : IDesignTimeDbContextFactory<AiObservatoryDbContext>
{
    public AiObservatoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=aiobservatory;Username=postgres;Password=postgres",
                npgsql => npgsql.UseNodaTime()
            )
            .Options;
        return new AiObservatoryDbContext(options);
    }
}
