using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

/// <summary>Private, atomically created, self-removing ZIP spool with a hard on-disk byte ceiling.</summary>
internal sealed class AuditTemporaryBundleStream : FileStream
{
    private const long PortableEncodedLimit = 528L * 1024 * 1024;
    private readonly long _maxBytes;

    public AuditTemporaryBundleStream(long maxBytes = PortableEncodedLimit)
        : base(Path.Combine(Path.GetTempPath(), $"aiobs-audit-{Guid.NewGuid():N}.zip"), Options())
    {
        _maxBytes = Math.Clamp(maxBytes, 1, PortableEncodedLimit);
    }

    public static AuditTemporaryBundleStream Create()
    {
        try
        {
            return new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuditStoreException("bundle_spool_unavailable", 503);
        }
    }

    private static FileStreamOptions Options()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return options;
    }

    private void Bound(long length)
    {
        if (length > _maxBytes)
        {
            throw new AuditStoreException("bundle_size_limit", 413);
        }
    }

    public override void WriteByte(byte value)
    {
        Bound(Position + 1);
        base.WriteByte(value);
    }

    public override void SetLength(long value)
    {
        Bound(value);
        base.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Bound(Position + count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Bound(Position + buffer.Length);
        base.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Bound(Position + count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Bound(Position + buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }
}
