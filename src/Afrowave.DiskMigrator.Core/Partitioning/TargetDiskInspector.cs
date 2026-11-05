using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Filesystems;
using Afrowave.DiskMigrator.Core.Model;

namespace Afrowave.DiskMigrator.Core.Partitioning;

public enum TargetDiskState
{
    /// <summary>No partition table entries at all - safe to initialise.</summary>
    Empty = 0,

    /// <summary>The disk defines at least one partition.</summary>
    Occupied = 1,

    /// <summary>The disk contains records we cannot interpret with certainty.</summary>
    Uncertain = 2,
}

/// <summary>Result of checking whether a target disk is free of partition definitions.</summary>
public sealed class TargetInspection
{
    public required TargetDiskState State { get; init; }

    public PartitionTableKind TableKind { get; init; } = PartitionTableKind.Unknown;

    public int DefinedPartitionCount { get; init; }

    /// <summary>Why the disk is not usable as a target (localised, user facing).</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Neutral one-line statement about the disk, always safe to display.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Caveats that the operator should see even though the disk is acceptable.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool IsEmpty => State == TargetDiskState.Empty;

    public string ShortDescription => State switch
    {
        TargetDiskState.Empty => "bez oddílů",
        TargetDiskState.Occupied => $"obsahuje {DefinedPartitionCount} oddílů",
        _ => "nejednoznačný stav",
    };

    public static TargetInspection Empty(string summary, IReadOnlyList<string>? notes = null) =>
        new() { State = TargetDiskState.Empty, Summary = summary, Notes = notes ?? [] };

    public static TargetInspection Occupied(PartitionTableKind kind, int count, IReadOnlyList<string> reasons) =>
        new()
        {
            State = TargetDiskState.Occupied,
            TableKind = kind,
            DefinedPartitionCount = count,
            Reasons = reasons,
        };

    public static TargetInspection Uncertain(IReadOnlyList<string> reasons, IReadOnlyList<string>? notes = null) =>
        new() { State = TargetDiskState.Uncertain, Reasons = reasons, Notes = notes ?? [] };
}

/// <summary>
/// Decides whether a disk may be used as a migration target. The rule is deliberately blunt:
/// the target must define no partitions at all, and anything that cannot be interpreted with
/// certainty is refused. The tool never deletes partitions to make a disk eligible - the
/// operator has to choose a suitable disk.
/// </summary>
public static class TargetDiskInspector
{
    private const int MbrSignatureOffset = 510;
    private const int MbrEntryOffset = 446;
    private const int MbrEntrySize = 16;

    private static readonly byte[] GptSignature = "EFI PART"u8.ToArray();

    public static TargetInspection Inspect(IBlockReader device)
    {
        int sectorSize = device.SectorSize;
        long diskSectors = device.Length / sectorSize;

        if (diskSectors < 2)
        {
            return TargetInspection.Uncertain(["Disk je příliš malý na to, aby mohl obsahovat tabulku oddílů."]);
        }

        var first = new byte[sectorSize];
        device.ReadAt(0, first);

        bool hasMbrSignature = first[MbrSignatureOffset] == 0x55 && first[MbrSignatureOffset + 1] == 0xAA;
        bool hasPrimaryGpt = StartsWithGptSignature(device, 1, sectorSize);

        long lastLba = diskSectors - 1;
        bool hasBackupGpt = lastLba > 1 && StartsWithGptSignature(device, lastLba, sectorSize);

        // ---------------------------------------------------- completely blank disk
        if (!hasMbrSignature && !hasPrimaryGpt && !hasBackupGpt)
        {
            return TargetInspection.Empty("disk neobsahuje žádnou tabulku oddílů");
        }

        // ---------------------------------------------------- leftover GPT structures
        if (hasBackupGpt && !hasPrimaryGpt)
        {
            return TargetInspection.Uncertain(
                ["Na konci disku zůstala záložní GPT, ale primární tabulka chybí. Stav disku je nejednoznačný."]);
        }

        if (hasPrimaryGpt && !hasMbrSignature)
        {
            return TargetInspection.Uncertain(
                ["Disk obsahuje GPT hlavičku, ale chybí ochranný záznam v MBR. Stav disku je nejednoznačný."]);
        }

        // ---------------------------------------------------- GPT disk
        if (hasPrimaryGpt)
        {
            return InspectGpt(device, sectorSize, lastLba);
        }

        // ---------------------------------------------------- classic MBR disk
        return InspectMbr(device, first, sectorSize);
    }

    private static TargetInspection InspectGpt(IBlockReader device, int sectorSize, long lastLba)
    {
        var read = PartitionTableReader.Read(device);

        if (read.Table is null)
        {
            return TargetInspection.Uncertain(
                ["Tabulku oddílů disku nelze spolehlivě přečíst:", .. read.Errors]);
        }

        if (read.Table.IsHybridMbr)
        {
            return TargetInspection.Uncertain(
                ["Disk má hybridní MBR (GPT i klasické položky). Stav disku je nejednoznačný."]);
        }

        if (read.Table.Partitions.Count > 0)
        {
            var reasons = new List<string>
            {
                $"Cílový disk už obsahuje {read.Table.Partitions.Count} definovaných oddílů (GPT).",
            };

            foreach (var partition in read.Table.Partitions.Take(8))
            {
                reasons.Add(
                    $"  • oddíl #{partition.Number}: {partition.TypeDescription}, " +
                    $"{partition.SectorCount * sectorSize / (1024 * 1024)} MiB");
            }

            reasons.Add("Nástroj oddíly nikdy nemaže, aby cílový disk uvolnil. Vyberte jiný disk.");

            return TargetInspection.Occupied(PartitionTableKind.Gpt, read.Table.Partitions.Count, reasons);
        }

        // The primary table is empty - make sure the backup copy does not disagree.
        var backup = TryReadBackupPartitionCount(device, lastLba, sectorSize);
        if (backup is > 0)
        {
            return TargetInspection.Uncertain(
                ["Primární GPT neobsahuje oddíly, ale záložní GPT na konci disku ano. Stav disku je nejednoznačný."]);
        }

        var notes = new List<string>();
        if (backup is null)
        {
            notes.Add("Záložní GPT na konci disku chybí nebo je poškozená – bude znovu vytvořena.");
        }

        return TargetInspection.Empty("disk má platnou GPT, ale žádné oddíly", notes);
    }

    private static TargetInspection InspectMbr(IBlockReader device, byte[] firstSector, int sectorSize)
    {
        int defined = 0;
        var descriptions = new List<string>();

        for (int i = 0; i < 4; i++)
        {
            var entry = firstSector.AsSpan(MbrEntryOffset + (i * MbrEntrySize), MbrEntrySize);
            byte type = entry[4];
            uint startLba = BitConverter.ToUInt32(entry[8..12]);
            uint sectorCount = BitConverter.ToUInt32(entry[12..16]);

            if (type == MbrPartitionTypes.Empty || sectorCount == 0) continue;

            defined++;
            descriptions.Add($"  • MBR položka {i + 1}: {MbrPartitionTypes.Describe(type)}, LBA {startLba}+{sectorCount}");
        }

        if (defined > 0)
        {
            var reasons = new List<string> { $"Cílový disk už obsahuje {defined} definovaných oddílů (MBR)." };
            reasons.AddRange(descriptions);
            reasons.Add("Nástroj oddíly nikdy nemaže, aby cílový disk uvolnil. Vyberte jiný disk.");
            return TargetInspection.Occupied(PartitionTableKind.Mbr, defined, reasons);
        }

        // No partitions defined. Find out whether a file system sits directly on the disk,
        // which is common for removable media; that is still "no partitions", only worth a note.
        var filesystem = FilesystemDetector.Detect(device, 0, device.Length, sectorSize);
        var notes = new List<string>();

        if (filesystem.Kind is not (FilesystemKind.Unformatted or FilesystemKind.Unknown))
        {
            notes.Add(
                $"Na začátku disku je rozpoznán souborový systém {filesystem.DisplayName}, ale disk " +
                "nedefinuje žádné oddíly. Obsah bude přepsán.");
        }

        return TargetInspection.Empty("tabulka oddílů MBR je prázdná", notes);
    }

    /// <summary>Number of partitions in the backup GPT, or null when it is missing or unreadable.</summary>
    private static int? TryReadBackupPartitionCount(IBlockReader device, long lastLba, int sectorSize)
    {
        if (!StartsWithGptSignature(device, lastLba, sectorSize)) return null;

        try
        {
            var header = new byte[sectorSize];
            device.ReadAt(lastLba * sectorSize, header);

            ulong entries = BitConverter.ToUInt64(header.AsSpan(72, 8));
            uint count = BitConverter.ToUInt32(header.AsSpan(80, 4));
            uint entrySize = BitConverter.ToUInt32(header.AsSpan(84, 4));

            if (count is 0 or > 4096 || entrySize is < 128 or > 4096) return null;
            if ((long)entries * sectorSize + ((long)count * entrySize) > device.Length) return null;

            var buffer = new byte[count * entrySize];
            device.ReadAt((long)entries * sectorSize, buffer);

            int defined = 0;
            for (int i = 0; i < count; i++)
            {
                var type = new Guid(buffer.AsSpan(i * (int)entrySize, 16));
                if (type != GptPartitionTypes.Unused) defined++;
            }

            return defined;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static bool StartsWithGptSignature(IBlockReader device, long lba, int sectorSize)
    {
        if (lba < 0 || (lba * sectorSize) + 8 > device.Length) return false;

        var probe = new byte[8];
        device.ReadAt(lba * sectorSize, probe);
        return probe.AsSpan().SequenceEqual(GptSignature);
    }
}
