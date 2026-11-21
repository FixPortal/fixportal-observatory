namespace AiObservatory.Testing;

/// <summary>
/// A temporary directory that proves it owns itself before it deletes itself. Creation writes a marker holding a
/// random token; deletion refuses unless the path is still a non-link directory under the temp root, carries the
/// creator's prefix, and holds the same marker. The resolved path is printed to the test output before deleting.
/// </summary>
public sealed class OwnedTempDirectory
{
    private const string MarkerName = ".owned-by-test";
    private readonly string _prefix;
    private readonly string _token = Guid.NewGuid().ToString("N");

    private OwnedTempDirectory(string prefix)
    {
        _prefix = prefix;
        Path = System.IO.Path.GetFullPath(
            System.IO.Path.Join(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"))
        );
    }

    public string Path { get; }

    public static OwnedTempDirectory Create(string prefix)
    {
        var owned = new OwnedTempDirectory(prefix);
        Directory.CreateDirectory(owned.Path);
        File.WriteAllText(System.IO.Path.Join(owned.Path, MarkerName), owned._token);
        return owned;
    }

    /// <summary>Runs <paramref name="teardown"/>, then deletes the directory whether or not it threw.</summary>
    public async Task DisposeAfterAsync(Func<Task> teardown)
    {
        try
        {
            await teardown();
        }
        finally
        {
            Delete();
        }
    }

    public void Delete()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }
        var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        var info = new DirectoryInfo(Path);
        var marker = System.IO.Path.Join(Path, MarkerName);
        if (
            !Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !info.Name.StartsWith(_prefix, StringComparison.Ordinal)
            || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || !File.Exists(marker)
            || File.ReadAllText(marker) != _token
        )
        {
            throw new InvalidOperationException(
                $"Refusing to delete '{Path}': it is not the directory this test created."
            );
        }
        TestContext.Current.SendDiagnosticMessage($"Deleting owned temp directory {Path}");
        Directory.Delete(Path, recursive: true);
    }
}
