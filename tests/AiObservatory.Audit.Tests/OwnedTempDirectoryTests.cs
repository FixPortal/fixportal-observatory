using AiObservatory.Testing;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests;

public sealed class OwnedTempDirectoryTests
{
    [Fact]
    public void Dispose_RemovesTheDirectoryItCreated()
    {
        var owned = OwnedTempDirectory.Create("audit-owned-test-");
        File.WriteAllText(Path.Join(owned.Path, "child.txt"), "x");
        owned.Delete();
        Directory.Exists(owned.Path).Should().BeFalse();
    }

    [Fact]
    public void Dispose_RefusesAndKeepsTheDirectoryWhenTheMarkerIsNotTheCreators()
    {
        var owned = OwnedTempDirectory.Create("audit-owned-test-");
        try
        {
            File.WriteAllText(Path.Join(owned.Path, ".owned-by-test"), "someone else");
            var delete = owned.Delete;
            delete.Should().Throw<InvalidOperationException>().WithMessage("*not the directory this test created*");
            Directory.Exists(owned.Path).Should().BeTrue();
        }
        finally
        {
            // The helper refused on purpose, so this test removes its own directory; validate the path first.
            owned.Path.Should().StartWith(Path.GetFullPath(Path.GetTempPath()));
            Path.GetFileName(owned.Path).Should().StartWith("audit-owned-test-");
            Console.WriteLine($"Deleting {owned.Path}");
            Directory.Delete(owned.Path, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAfterAsync_DeletesTheDirectoryEvenWhenTeardownThrows()
    {
        var owned = OwnedTempDirectory.Create("audit-owned-test-");
        var teardown = () => owned.DisposeAfterAsync(() => throw new IOException("drop failed"));
        await teardown.Should().ThrowAsync<IOException>().WithMessage("drop failed");
        Directory.Exists(owned.Path).Should().BeFalse();
    }
}
