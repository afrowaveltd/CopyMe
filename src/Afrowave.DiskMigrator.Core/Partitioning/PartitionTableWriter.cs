using System.Text;
using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Core.Partitioning;

/// <summary>One partition entry to be written into a target partition table.</summary>
public sealed class PartitionWriteEntry
{
    public required int Number { get; init; }

    public required long StartLba { get; init; }

    public required long SectorCount { get; init; }

    public long EndLba => StartLba + SectorCount - 1;

    public Guid TypeGuid { get; init; }

    public Guid UniqueGuid { get; init; }

    public byte MbrType { get; init; }

    public string? Name { get; init; }

    public ulong Attributes { get; init; }

    public bool IsActive { get; init; }

    public bool IsLogical { get; init; }
}

public sealed class GptWriteSpec
{
    public required Guid DiskGuid { get; init; }

    public required IReadOnlyList<PartitionWriteEntry> Entries { get; init; }
}

public sealed class MbrWriteSpec
{
    public required uint DiskSignature { get; init; }

    /// <summary>446 bytes of boot code copied from the source disk.</summary>
    public required byte[] BootCode { get; init; }

    public required IReadOnlyList<PartitionWriteEntry> Entries { get; init; }
}

/// <summary>
/// Writes partition tables to a target block device. Everything is computed from scratch
/// and the resulting table is verified by the caller reading it back.
/// </summary>
public static class PartitionTableWriter
{
    private const int MaxGptEntries = GptLayout.DefaultEntryCount;
    private const int GptEntrySize = GptLayout.DefaultEntrySize;
    private const int MbrSignatureOffset = 510;
    private const int MbrEntryOffset = 446;
    private const int MbrEntrySize = 16;

    /// <summary>
    /// Clears the sectors that carry the old partition table plus both GPT areas.
    /// Called before the new table is written so a half-destroyed old table cannot be found
    /// by the firmware later on.
    /// </summary>
    public static void ClearPartitionAreas(IBlockDevice device, bool gpt)
    {
        int ss = device.SectorSize;
        long diskSectors = device.Length / ss;

        long headSectors = gpt
            ? GptLayout.FirstUsableLba(ss)
            : 1;

        var zeros = new byte[Math.Max(ss, 64 * 1024)];
        long remaining = headSectors * ss;
        long offset = 0;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, zeros.Length);
            Array.Clear(zeros, 0, chunk);
            device.WriteAt(offset, zeros.AsSpan(0, chunk));
            offset += chunk;
            remaining -= chunk;
        }

        if (gpt)
        {
            long tailStart = GptLayout.LastUsableLba(diskSectors, ss) + 1;
            long tailBytes = (diskSectors - tailStart) * ss;
            long position = tailStart * ss;
            while (tailBytes > 0)
            {
                int chunk = (int)Math.Min(tailBytes, zeros.Length);
                Array.Clear(zeros, 0, chunk);
                device.WriteAt(position, zeros.AsSpan(0, chunk));
                position += chunk;
                tailBytes -= chunk;
            }
        }

        device.Flush();
    }

    public static void WriteGpt(IBlockDevice device, GptWriteSpec spec)
    {
        int ss = device.SectorSize;
        long diskSectors = device.Length / ss;
        long lastLba = diskSectors - 1;

        long firstUsable = GptLayout.FirstUsableLba(ss);
        long lastUsable = GptLayout.LastUsableLba(diskSectors, ss);

        foreach (var entry in spec.Entries)
        {
            if (entry.StartLba < firstUsable)
                throw new InvalidOperationException($"Oddíl {entry.Number} začíná před první použitelnou LBA ({entry.StartLba} < {firstUsable}).");
            if (entry.EndLba > lastUsable)
                throw new InvalidOperationException($"Oddíl {entry.Number} končí za poslední použitelnou LBA ({entry.EndLba} > {lastUsable}).");
            if (entry.SectorCount <= 0)
                throw new InvalidOperationException($"Oddíl {entry.Number} má nulovou velikost.");
        }

        for (int i = 1; i < spec.Entries.Count; i++)
        {
            if (spec.Entries[i].StartLba <= spec.Entries[i - 1].EndLba)
                throw new InvalidOperationException("Navržené oddíly se překrývají.");
        }

        // --- partition entry array
        var entryArray = new byte[MaxGptEntries * GptEntrySize];
        for (int i = 0; i < spec.Entries.Count && i < MaxGptEntries; i++)
        {
            var source = spec.Entries[i];
            var span = entryArray.AsSpan(i * GptEntrySize, GptEntrySize);
            source.TypeGuid.TryWriteBytes(span[..16]);
            source.UniqueGuid.TryWriteBytes(span.Slice(16, 16));
            BitConverter.TryWriteBytes(span.Slice(32, 8), (ulong)source.StartLba);
            BitConverter.TryWriteBytes(span.Slice(40, 8), (ulong)source.EndLba);
            BitConverter.TryWriteBytes(span.Slice(48, 8), source.Attributes);

            string name = source.Name ?? string.Empty;
            if (name.Length > 36) name = name[..36];
            var nameBytes = new byte[72];
            Encoding.Unicode.GetBytes(name, nameBytes);
            nameBytes.CopyTo(span.Slice(56, 72));
        }

        uint entriesCrc = Crc32.Compute(entryArray);

        long primaryEntriesLba = 2;
        long backupEntriesLba = lastLba - GptLayout.EntryArraySectors(ss);

        WriteSectors(device, primaryEntriesLba, entryArray);

        var primaryHeader = BuildGptHeader(
            ss, myLba: 1, alternateLba: lastLba, firstUsable, lastUsable,
            spec.DiskGuid, primaryEntriesLba, entriesCrc);
        WriteSectors(device, 1, primaryHeader);

        WriteSectors(device, backupEntriesLba, entryArray);

        var backupHeader = BuildGptHeader(
            ss, myLba: lastLba, alternateLba: 1, firstUsable, lastUsable,
            spec.DiskGuid, backupEntriesLba, entriesCrc);
        WriteSectors(device, lastLba, backupHeader);

        WriteProtectiveMbr(device, diskSectors);

        device.Flush();
    }

    private static byte[] BuildGptHeader(
        int sectorSize,
        long myLba,
        long alternateLba,
        long firstUsable,
        long lastUsable,
        Guid diskGuid,
        long entriesLba,
        uint entriesCrc)
    {
        var sector = new byte[sectorSize];
        var span = sector.AsSpan();

        "EFI PART"u8.CopyTo(span[..8]);
        BitConverter.TryWriteBytes(span.Slice(8, 4), 0x00010000u);      // revision 1.0
        BitConverter.TryWriteBytes(span.Slice(12, 4), 92u);            // header size
        // CRC at 16 is filled in below.
        BitConverter.TryWriteBytes(span.Slice(20, 4), 0u);             // reserved
        BitConverter.TryWriteBytes(span.Slice(24, 8), (ulong)myLba);
        BitConverter.TryWriteBytes(span.Slice(32, 8), (ulong)alternateLba);
        BitConverter.TryWriteBytes(span.Slice(40, 8), (ulong)firstUsable);
        BitConverter.TryWriteBytes(span.Slice(48, 8), (ulong)lastUsable);
        diskGuid.TryWriteBytes(span.Slice(56, 16));
        BitConverter.TryWriteBytes(span.Slice(72, 8), (ulong)entriesLba);
        BitConverter.TryWriteBytes(span.Slice(80, 4), (uint)MaxGptEntries);
        BitConverter.TryWriteBytes(span.Slice(84, 4), (uint)GptEntrySize);
        BitConverter.TryWriteBytes(span.Slice(88, 4), entriesCrc);

        uint headerCrc = Crc32.Compute(span[..92]);
        BitConverter.TryWriteBytes(span.Slice(16, 4), headerCrc);

        return sector;
    }

    private static void WriteProtectiveMbr(IBlockDevice device, long diskSectors)
    {
        int ss = device.SectorSize;
        var sector = new byte[ss];

        var entry = sector.AsSpan(MbrEntryOffset, MbrEntrySize);
        entry[0] = 0x00;
        WriteChs(entry.Slice(1, 3), 0);
        entry[4] = MbrPartitionTypes.GptProtective;
        WriteChs(entry.Slice(5, 3), uint.MaxValue);
        BitConverter.TryWriteBytes(entry.Slice(8, 4), 1u);

        long sectorsCovered = Math.Min(diskSectors - 1, uint.MaxValue);
        BitConverter.TryWriteBytes(entry.Slice(12, 4), (uint)sectorsCovered);

        sector[MbrSignatureOffset] = 0x55;
        sector[MbrSignatureOffset + 1] = 0xAA;

        WriteSectors(device, 0, sector);
    }

    public static void WriteMbr(IBlockDevice device, MbrWriteSpec spec)
    {
        int ss = device.SectorSize;
        long diskSectors = device.Length / ss;

        foreach (var entry in spec.Entries)
        {
            if (entry.StartLba < 1)
                throw new InvalidOperationException($"Oddíl {entry.Number} začíná na LBA 0, což není možné.");
            if (entry.EndLba >= diskSectors)
                throw new InvalidOperationException($"Oddíl {entry.Number} přesahuje konec disku.");
            if (entry.StartLba > uint.MaxValue || entry.SectorCount > uint.MaxValue || entry.EndLba > uint.MaxValue)
                throw new InvalidOperationException($"Oddíl {entry.Number} přesahuje limity MBR (2 TiB / 32bitová LBA).");
        }

        var primaries = spec.Entries.Where(e => !e.IsLogical).ToList();
        var logicals = spec.Entries.Where(e => e.IsLogical).OrderBy(e => e.StartLba).ToList();

        if (primaries.Count > 4)
            throw new InvalidOperationException("MBR unese nejvýše 4 primární oddíly.");

        if (logicals.Count > 0 && primaries.Count > 3)
            throw new InvalidOperationException("Pro logické oddíly musí zůstat volný alespoň jeden slot MBR.");

        var sector = new byte[ss];
        var bootCode = spec.BootCode;
        if (bootCode.Length != 446)
            throw new ArgumentException("Boot kód MBR musí mít přesně 446 bajtů.", nameof(spec));
        bootCode.CopyTo(sector, 0);

        BitConverter.TryWriteBytes(sector.AsSpan(440, 4), spec.DiskSignature);
        sector[444] = 0;
        sector[445] = 0;

        int slot = 0;
        foreach (var primary in primaries)
        {
            WriteMbrEntry(sector.AsSpan(MbrEntryOffset + (slot * MbrEntrySize), MbrEntrySize),
                primary.IsActive ? (byte)0x80 : (byte)0x00, primary.MbrType, primary.StartLba, primary.SectorCount);
            slot++;
        }

        long? extendedStart = null;
        if (logicals.Count > 0)
        {
            extendedStart = logicals[0].StartLba - 1;
            if (extendedStart < 1)
                throw new InvalidOperationException("Pro logické oddíly nelze umístit EBR před první oddíl.");

            long extendedEnd = logicals[^1].EndLba;
            WriteMbrEntry(sector.AsSpan(MbrEntryOffset + (slot * MbrEntrySize), MbrEntrySize),
                0x00, MbrPartitionTypes.ExtendedLba, extendedStart.Value, extendedEnd - extendedStart.Value + 1);
            slot++;
        }

        sector[MbrSignatureOffset] = 0x55;
        sector[MbrSignatureOffset + 1] = 0xAA;

        WriteSectors(device, 0, sector);

        if (logicals.Count > 0 && extendedStart is not null)
        {
            long extendedEnd = logicals[^1].EndLba;

            for (int i = 0; i < logicals.Count; i++)
            {
                var logical = logicals[i];
                long ebrLba = i == 0 ? extendedStart.Value : logical.StartLba - 1;

                var ebr = new byte[ss];
                WriteMbrEntry(ebr.AsSpan(MbrEntryOffset, MbrEntrySize),
                    logical.IsActive ? (byte)0x80 : (byte)0x00, logical.MbrType,
                    logical.StartLba - ebrLba, logical.SectorCount);

                if (i + 1 < logicals.Count)
                {
                    long nextEbr = logicals[i + 1].StartLba - 1;
                    WriteMbrEntry(ebr.AsSpan(MbrEntryOffset + MbrEntrySize, MbrEntrySize),
                        0x00, MbrPartitionTypes.ExtendedLba,
                        nextEbr - extendedStart.Value,
                        extendedEnd - nextEbr + 1);
                }

                ebr[MbrSignatureOffset] = 0x55;
                ebr[MbrSignatureOffset + 1] = 0xAA;

                WriteSectors(device, ebrLba, ebr);
            }
        }

        device.Flush();
    }

    private static void WriteMbrEntry(Span<byte> entry, byte status, byte type, long startLba, long sectorCount)
    {
        entry[0] = status;
        WriteChs(entry.Slice(1, 3), (ulong)startLba);
        entry[4] = type;
        WriteChs(entry.Slice(5, 3), (ulong)(startLba + sectorCount - 1));
        BitConverter.TryWriteBytes(entry.Slice(8, 4), (uint)startLba);
        BitConverter.TryWriteBytes(entry.Slice(12, 4), (uint)sectorCount);
    }

    private static void WriteChs(Span<byte> destination, ulong lba)
    {
        const int heads = 255;
        const int sectorsPerTrack = 63;

        ulong cylinder = lba / ((ulong)heads * sectorsPerTrack);
        if (cylinder > 1023)
        {
            destination[0] = 0xFE;
            destination[1] = 0xFF;
            destination[2] = 0xFF;
            return;
        }

        ulong remainder = lba % ((ulong)heads * sectorsPerTrack);
        ulong head = remainder / sectorsPerTrack;
        ulong sector = remainder % sectorsPerTrack + 1;

        destination[0] = (byte)head;
        destination[1] = (byte)((sector & 0x3F) | ((cylinder >> 2) & 0xC0));
        destination[2] = (byte)(cylinder & 0xFF);
    }

    private static void WriteSectors(IBlockDevice device, long lba, ReadOnlySpan<byte> data)
    {
        int ss = device.SectorSize;
        if (data.Length % ss != 0)
            throw new ArgumentException("Data pro zápis musí být násobkem velikosti sektoru.");

        device.WriteAt(lba * ss, data);
    }
}
