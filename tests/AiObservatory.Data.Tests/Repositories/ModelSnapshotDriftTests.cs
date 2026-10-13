using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiObservatory.Data.Tests.Repositories;

/// <summary>
/// An entity or index change merged without a migration leaves the deployed schema behind the
/// model (CHECK constraints, the unique indexes ON CONFLICT relies on). Migrations apply cleanly
/// against a stale snapshot, so only comparing the current model with the snapshot catches it.
/// The comparison needs no database connection.
/// </summary>
public class ModelSnapshotDriftTests
{
    [Fact]
    public void CurrentModelMatchesTheMigrationSnapshot()
    {
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql("Host=unused;Database=unused", o => o.UseNodaTime())
            .Options;
        using var db = new AiObservatoryDbContext(options);

        db.Database.GetMigrations().Should().NotBeEmpty("an empty migration set would make the comparison vacuous");
        db.Database.HasPendingModelChanges()
            .Should()
            .BeFalse("the model has changes no migration captures; add a migration");
    }
}
