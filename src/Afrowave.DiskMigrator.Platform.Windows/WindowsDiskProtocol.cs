using System.Buffers.Binary;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>Disk attribute bits as defined by winioctl.h (NOT the Win32 file attributes).</summary>
public readonly record struct WindowsDiskAttributes(ulong RawAttributes)
{
    public bool IsOffline => (RawAttributes & WindowsDiskProtocol.Offline) != 0;
    public bool IsReadOnly => (RawAttributes & WindowsDiskProtocol.ReadOnly) != 0;
}

/// <summary>
/// Platform-independent wire-format encoding for GET_DISK_ATTRIBUTES and SET_DISK_ATTRIBUTES.
/// SET.Version is sizeof(GET_DISK_ATTRIBUTES) = 16, but SET's full buffer is 40 bytes.
/// </summary>
public static class WindowsDiskProtocol
{
    public const ulong Offline = 0x1;
    public const ulong ReadOnly = 0x2;
    public const int GetLength = 16;
    public const int SetLength = 40;

    public static byte[] EncodeSetReadOnly(bool readOnly)
    {
        // 4 Version + 1 Persist + 3 reserved + 8 attributes + 8 mask + 16 reserved
        // All reserved fields and Persist are zero; the OFFLINE bit is never touched.
        var buffer = new byte[SetLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), GetLength);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), readOnly ? ReadOnly : 0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16, 8), ReadOnly);
        return buffer;
    }

    public static bool TryDecodeGet(ReadOnlySpan<byte> data, out WindowsDiskAttributes attributes)
    {
        attributes = default;
        if (data.Length < GetLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[..4]) < GetLength)
            return false;

        attributes = new WindowsDiskAttributes(
            BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8, 8)));
        return true;
    }
}

/// <summary>Parser for STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR (IOCTL_STORAGE_QUERY_PROPERTY).</summary>
public static class WindowsStorageAlignmentProtocol
{
    public const uint StorageAccessAlignmentProperty = 6;
    public const int DescriptorLength = 28;

    public static byte[] CreateQuery()
    {
        // STORAGE_PROPERTY_QUERY: PropertyId=6, QueryType=PropertyStandardQuery(0).
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), StorageAccessAlignmentProperty);
        return bytes;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out int logical, out int physical)
    {
        logical = 0;
        physical = 0;
        if (data.Length < DescriptorLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[..4]) < DescriptorLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) < DescriptorLength)
            return false;

        uint l = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4));
        uint p = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4));
        if (l is < 512 or > 65536 || p < l || p > 65536 ||
            (l & (l - 1)) != 0 || (p & (p - 1)) != 0 || p % l != 0)
            return false;
        logical = checked((int)l);
        physical = checked((int)p);
        return true;
    }
}

/// <summary>
/// Cross-platform decoder for the Win32 DISK_GEOMETRY_EX prefix (DISK_GEOMETRY + DiskSize).
/// DISK_GEOMETRY: Cylinders [0..7], MediaType [8..11], TracksPerCylinder [12..15],
/// SectorsPerTrack [16..19], BytesPerSector [20..23], then DiskSize [24..31].
/// </summary>
public static class WindowsDiskGeometryProtocol
{
    public const int MinimumLength = 32;

    public static bool TryDecode(ReadOnlySpan<byte> data, out long diskSize, out int logicalSectorSize)
    {
        diskSize = 0;
        logicalSectorSize = 0;

        if (data.Length < MinimumLength) return false;

        // Offset 16 is SectorsPerTrack (often 63) -- never use it as BytesPerSector.
        int sectorSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(20, 4));
        long size = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(24, 8));

        if (sectorSize is < 512 or > 65536 ||
            (sectorSize & (sectorSize - 1)) != 0 ||
            size <= 0 || size % sectorSize != 0)
            return false;

        diskSize = size;
        logicalSectorSize = sectorSize;
        return true;
    }
}

