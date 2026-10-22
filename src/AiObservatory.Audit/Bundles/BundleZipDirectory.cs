using System.Buffers.Binary;

namespace AiObservatory.Audit.Bundles;

/// <summary>Preflight the bounded v1 directory before ZipArchive materializes entry metadata. ZIP64 is unnecessary under v1 limits.</summary>
internal static class BundleZipDirectory
{
    internal static void Validate(MemoryStream stream, BundleLimits limits)
    {
        var bytes = stream.GetBuffer().AsSpan(0, (int)stream.Length);
        var end = FindEnd(bytes);
        var header = bytes[end..];
        BundleCodec.Require(U16(header, 4) == 0 && U16(header, 6) == 0, "unsupported_bundle_multidisk");
        var count = U16(header, 10);
        var length = U32(header, 12);
        var offset = U32(header, 16);
        BundleCodec.Require(
            count != ushort.MaxValue && length != uint.MaxValue && offset != uint.MaxValue,
            "unsupported_bundle_zip64"
        );
        BundleCodec.Require(
            count > 0 && count <= limits.MaxEntries && length <= 8 * 1024 * 1024,
            "bundle_directory_limit"
        );
        BundleCodec.Require(U16(header, 8) == count && (long)offset + length == end, "invalid_bundle_directory");
        ValidateEntries(bytes.Slice((int)offset, (int)length), count);
    }

    private static int FindEnd(ReadOnlySpan<byte> bytes)
    {
        for (var index = bytes.Length - 22; index >= Math.Max(0, bytes.Length - 65557); index--)
        {
            if (U32(bytes, index) == 0x06054b50 && index + 22 + U16(bytes, index + 20) == bytes.Length)
            {
                return index;
            }
        }
        throw new Contracts.AuditValidationException("invalid_bundle_zip");
    }

    private static void ValidateEntries(ReadOnlySpan<byte> directory, int count)
    {
        var offset = 0;
        for (var index = 0; index < count; index++)
        {
            BundleCodec.Require(directory.Length - offset >= 46, "invalid_bundle_directory");
            var record = directory[offset..];
            BundleCodec.Require(U32(record, 0) == 0x02014b50, "invalid_bundle_directory");
            BundleCodec.Require(
                U32(record, 20) != uint.MaxValue
                    && U32(record, 24) != uint.MaxValue
                    && U32(record, 42) != uint.MaxValue,
                "unsupported_bundle_zip64"
            );
            BundleCodec.Require(U16(record, 34) == 0, "unsupported_bundle_multidisk");
            var length = 46 + U16(record, 28) + U16(record, 30) + U16(record, 32);
            BundleCodec.Require(length <= directory.Length - offset, "invalid_bundle_directory");
            offset += length;
        }
        BundleCodec.Require(offset == directory.Length, "invalid_bundle_directory");
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
}
