using System.Runtime.CompilerServices;
using AwesomeAssertions;

namespace AiObservatory.Ingest.Tests.Pricing;

public sealed class TempDirectoryUsageTests
{
    // Guard, not behaviour: a raw recursive delete on a CreateTempSubdirectory() result has no
    // ownership proof and prints no path, and a directory created before its try block leaks when
    // setup throws. OwnedTempDirectory (tests/OwnedTempDirectory.cs) covers both.
    [Fact]
    public void IngestTestsCreateAndDeleteTempDirectoriesOnlyThroughOwnedTempDirectory()
    {
        var testsRoot = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
        var offenders = Directory
            .EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(file => !file.EndsWith(nameof(TempDirectoryUsageTests) + ".cs", StringComparison.Ordinal))
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("CreateTempSubdirectory", StringComparison.Ordinal)
                    || text.Contains("recursive: true", StringComparison.Ordinal);
            })
            .ToList();

        offenders.Should().BeEmpty();
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
