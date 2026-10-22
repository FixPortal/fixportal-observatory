using AiObservatory.Api.Audit;
using AiObservatory.Data.Audit;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests.Audit;

public sealed class AuditTemporaryBundleStreamTests
{
    [Fact]
    public async Task TemporarySpool_BoundsSyncAndAsyncWritesAndRemovesCancelledArtifact()
    {
        var file = new AuditTemporaryBundleStream(64);
        var path = file.Name;
        try
        {
            File.Exists(path).Should().BeTrue();
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            await file.WriteAsync(new byte[64], TestContext.Current.CancellationToken);
            var sync = () => file.WriteByte(1);
            sync.Should().Throw<AuditStoreException>().Which.StatusCode.Should().Be(413);
            var asyncWrite = async () => await file.WriteAsync(new byte[1], TestContext.Current.CancellationToken);
            (await asyncWrite.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(413);
            file.Length.Should().Be(64);
        }
        finally
        {
            await file.DisposeAsync();
        }
        File.Exists(path).Should().BeFalse();
    }
}
