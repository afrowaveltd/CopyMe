using System.Text;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Model;

namespace Afrowave.DiskMigrator.Core.Filesystems;

/// <summary>
/// Identifies the file system living in a partition by probing its boot sector and the
/// first few metadata blocks. Everything is decided from raw bytes - nothing is mounted
/// and nothing is written.
/// </summary>
public static class FilesystemDetector
{
    /// <summary>Enough to reach the NTFS/exFAT BPB, the ext/btrfs superblocks and the ISO9660 identifier.</summary>
    private const int ProbeLength = 192 * 1024;

    private static readonly byte[] LuksMagic = [0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE];

    public static FilesystemInfo Detect(
        IBlockReader device,
        long partitionOffsetBytes,
        long partitionSizeBytes,
        int logicalSectorSize)
    {
        int available = (int)Math.Min(ProbeLength, Math.Max(0, Math.Min(partitionSizeBytes, device.Length - partitionOffsetBytes)));
        if (available < 512)
        {
            return Unformatted(logicalSectorSize);
        }

        var buffer = new byte[available];
        device.ReadAt(partitionOffsetBytes, buffer);
        var span = buffer.AsSpan();

        if (HasAscii(span, 3, "NTFS    "))
        {
            int bytesPerSector = ReadUInt16(span, 0x0B);
            if (bytesPerSector is not (512 or 1024 or 2048 or 4096))
            {
                bytesPerSector = logicalSectorSize;
            }

            return new FilesystemInfo(
                FilesystemKind.Ntfs, "NTFS", null, bytesPerSector, 0, 0, null);
        }

        if (HasAscii(span, 3, "EXFAT   "))
        {
            return new FilesystemInfo(FilesystemKind.ExFat, "exFAT", null, ReadUInt16(span, 0x0B), 0, 0, null);
        }

        if (HasAscii(span, 3, "ReFS    "))
        {
            return new FilesystemInfo(FilesystemKind.ReFs, "ReFS", null, ReadUInt16(span, 0x0B), 0, 0, null);
        }

        if (HasAscii(span, 3, "-FVE-FS-"))
        {
            return new FilesystemInfo(FilesystemKind.BitLocker, "BitLocker (zašifrováno)", null, 512, 0, 0, null);
        }

        if (HasBytes(span, 0, LuksMagic))
        {
            return new FilesystemInfo(FilesystemKind.Luks, "LUKS (zašifrováno)", null, 512, 0, 0, null);
        }

        if (HasAscii(span, 0, "XFSB"))
        {
            return new FilesystemInfo(FilesystemKind.Xfs, "XFS", null, 512, 0, 0, null);
        }

        if (HasAscii(span, 32, "NXSB"))
        {
            return new FilesystemInfo(FilesystemKind.Apfs, "APFS", null, 4096, 0, 0, null);
        }

        if (span.Length >= 0x43A && ReadUInt16(span, 0x438) == 0xEF53)
        {
            return new FilesystemInfo(FilesystemKind.Ext2To4, "ext2/3/4", null, 1024, 0, 1024, null);
        }

        if (span.Length >= 0x10048 && HasAscii(span, 0x10040, "_BHRfS_M"))
        {
            return new FilesystemInfo(FilesystemKind.Btrfs, "Btrfs", null, 4096, 0, 65536, null);
        }

        if (span.Length >= 1082)
        {
            ushort hfsSignature = ReadUInt16(span, 1024);
            if (hfsSignature == 0x482B || 0x4858 == hfsSignature)
            {
                return new FilesystemInfo(FilesystemKind.HfsPlus, "HFS+", null, 512, 0, 1024, null);
            }
        }

        if (span.Length > 0x8006 && HasAscii(span, 0x8001, "CD001"))
        {
            return new FilesystemInfo(FilesystemKind.Iso9660, "ISO9660", null, 2048, 0, 0, null);
        }

        if (LooksLikeLinuxSwap(span))
        {
            return new FilesystemInfo(FilesystemKind.LinuxSwap, "Linux swap", null, 4096, 0, 0, null);
        }

        if (LooksLikeFat(span, out var fatKind, out var fatBytesPerSector, out string? fatLabel))
        {
            string name = fatKind switch
            {
                FilesystemKind.Fat12 => "FAT12",
                FilesystemKind.Fat16 => "FAT16",
                _ => "FAT32",
            };

            return new FilesystemInfo(fatKind, name, fatLabel, fatBytesPerSector, 0, 0, null);
        }

        if (IsAllZero(span))
        {
            return Unformatted(logicalSectorSize);
        }

        return new FilesystemInfo(FilesystemKind.Unknown, "neznámý souborový systém", null, logicalSectorSize, 0, 0, null);
    }

    private static FilesystemInfo Unformatted(int logicalSectorSize) =>
        new(FilesystemKind.Unformatted, "neformátováno", null, logicalSectorSize, 0, 0, null);

    private static bool LooksLikeLinuxSwap(ReadOnlySpan<byte> span)
    {
        // The swap signature sits in the last ten bytes of the first swap page.
        foreach (int pageSize in (ReadOnlySpan<int>)[4096, 8192, 16384, 32768, 65536])
        {
            if (span.Length >= pageSize && HasAscii(span, pageSize - 10, "SWAPSPACE2"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeFat(ReadOnlySpan<byte> span, out FilesystemKind kind, out int bytesPerSector, out string? label)
    {
        kind = FilesystemKind.Unknown;
        bytesPerSector = 512;
        label = null;

        if (span.Length < 512) return false;

        byte jump = span[0];
        if (jump != 0xEB && jump != 0xE9) return false;
        if (jump == 0xEB && span[2] != 0x90) return false;
        if (ReadUInt16(span, 0x0B) is not (512 or 1024 or 2048 or 4096)) return false;
        if (span[0x0D] == 0) return false;

        if (HasAscii(span, 54, "FAT12   ")) kind = FilesystemKind.Fat12;
        else if (HasAscii(span, 54, "FAT16   ")) kind = FilesystemKind.Fat16;
        else if (HasAscii(span, 82, "FAT32   ")) kind = FilesystemKind.Fat32;
        else if (HasAscii(span, 54, "FAT     ")) kind = FilesystemKind.Fat16;
        else return false;

        bytesPerSector = ReadUInt16(span, 0x0B);

        int labelOffset = kind == FilesystemKind.Fat32 ? 71 : 43;
        if (span.Length > labelOffset + 11)
        {
            string raw = Encoding.ASCII.GetString(span.Slice(labelOffset, 11)).TrimEnd(' ', '\0');
            if (raw.Length > 0 && raw != "NO NAME") label = raw;
        }

        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span)
        {
            if (b != 0) return false;
        }

        return true;
    }

    private static bool HasAscii(ReadOnlySpan<byte> span, int offset, string ascii)
    {
        if (offset < 0 || offset + ascii.Length > span.Length) return false;

        for (int i = 0; i < ascii.Length; i++)
        {
            if (span[offset + i] != (byte)ascii[i]) return false;
        }

        return true;
    }

    private static bool HasBytes(ReadOnlySpan<byte> span, int offset, ReadOnlySpan<byte> expected)
    {
        if (offset < 0 || offset + expected.Length > span.Length) return false;
        return span.Slice(offset, expected.Length).SequenceEqual(expected);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> span, int offset) =>
        offset + 2 <= span.Length ? BitConverter.ToUInt16(span.Slice(offset, 2)) : (ushort)0;

    private static ulong ReadUInt64(ReadOnlySpan<byte> span, int offset) =>
        offset + 8 <= span.Length ? BitConverter.ToUInt64(span.Slice(offset, 8)) : 0UL;
}
