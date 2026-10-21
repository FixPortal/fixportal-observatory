using System.Xml.Linq;
using AiObservatory.Audit.Serialization;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Architecture;

public sealed class DependencyBoundaryTests
{
    /// <summary>The independent verifier cannot reuse production parsers, accounting, transport, or database code.</summary>
    [Fact]
    public void Audit_HasNoProductionOrInfrastructureDependencies()
    {
        var assembly = typeof(AuditCanonicalJson).Assembly;
        assembly
            .GetReferencedAssemblies()
            .Select(x => x.Name)
            .Should()
            .NotContain(x =>
                x != null
                && (
                    x.StartsWith("AiObservatory.", StringComparison.Ordinal)
                    || x.StartsWith("NodaTime", StringComparison.Ordinal)
                    || x.Contains("EntityFramework", StringComparison.Ordinal)
                    || x.Contains("Npgsql", StringComparison.Ordinal)
                    || x.Contains("Http", StringComparison.Ordinal)
                )
            );
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AiObservatory.slnx")))
        {
            root = root.Parent;
        }
        root.Should().NotBeNull();
        var project = XDocument.Load(
            Path.Combine(root.FullName, "src", "AiObservatory.Audit", "AiObservatory.Audit.csproj")
        );
        project.Descendants("ProjectReference").Should().BeEmpty();
        project
            .Descendants("Compile")
            .Should()
            .BeEmpty("production source must not be linked or copied through explicit compile entries");
        project
            .Descendants("PackageReference")
            .Select(x => (string?)x.Attribute("Include"))
            .Should()
            .OnlyContain(x => x == "FixPortal.CodeStyle");
        var props = XDocument.Load(Path.Combine(root.FullName, "Directory.Build.props"));
        props
            .Descendants("PackageReference")
            .Select(x => (string?)x.Attribute("Include"))
            .Should()
            .OnlyContain(x => x == "FixPortal.CodeStyle");
    }
}
