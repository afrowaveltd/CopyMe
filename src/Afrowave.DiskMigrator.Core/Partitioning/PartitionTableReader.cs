using System.Text;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Model;

namespace Afrowave.DiskMigrator.Core.Partitioning;

public sealed class PartitionTableReadResult
{
    public PartitionTable? Table { get; init; }

    /// <summary>Conditions that make migration unsafe. Any entry blocks the operation.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool Success => Table is not null && Errors.Count == 0;
}

/// <summary>
/// Reads and validates GPT and MBR partition tables straight from a block device.
/// Nothing is trusted: CRCs are verified, overlaps are rejected and the backup GPT is
/// used only when the primary one is demonstrably corrupt.
/// </summary>
public static class PartitionTableReader
{
    private const int MbrSignatureOffset = 510;
    private const int MbrEntryOffset = 446;
    private const int MbrEntrySize = 16;
    private const int MaxLogicalPartitions = 128;

    private static readonly byte[] GptSignature = "EFI PART"u8.ToArray();

    public static PartitionTableReadResult Read(IBlockReader device)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        int sectorSize = device.SectorSize;
        long diskSectors = device.Length / sectorSize;

        if (device.Length % sectorSize != 0)
        {
            errors.Add($"Velikost zařízení ({device.Length} B) není násobkem velikosti sektoru ({sectorSize} B).");
            return new PartitionTableReadResult { Errors = errors, Warnings = warnings };
        }

        if (diskSectors < 2)
        {
            errors.Add("Zařízení je příliš malé na to, aby obsahovalo rozpoznatelnou tabulku oddílů.");
            return new PartitionTableReadResult { Errors = errors, Warnings = warnings };
        }

        var mbr = new byte[sectorSize];
        device.ReadAt(0, mbr);

        if (mbr[MbrSignatureOffset] != 0x55 || mbr[MbrSignatureOffset + 1] != 0xAA)
        {
            errors.Add("Sektor 0 neobsahuje platný podpis 0x55AA – disk nemá rozpoznatelnou tabulku oddílů.");
            return new PartitionTableReadResult { Errors = errors, Warnings = warnings };
        }

        var entries = ParseMbrEntries(mbr);
        bool hasProtective = entries.Any(e => e.Type == MbrPartitionTypes.GptProtective);

        if (hasProtective)
        {
            return ReadGpt(device, entries, warnings, errors);
        }

        return ReadMbr(device, entries, warnings, errors);
    }

    private readonly record struct MbrRawEntry(byte Status, byte Type, uint StartLba, uint SectorCount)
    {
        public bool IsEmpty => Type == MbrPartitionTypes.Empty || SectorCount == 0;
    }

    private static MbrRawEntry[] ParseMbrEntries(ReadOnlySpan<byte> sector)
    {
        var result = new MbrRawEntry[4];
        for (int i = 0; i < 4; i++)
        {
            var e = sector.Slice(MbrEntryOffset + (i * MbrEntrySize), MbrEntrySize);
            result[i] = new MbrRawEntry(
                e[0],
                e[4],
                BitConverter.ToUInt32(e[8..12]),
                BitConverter.ToUInt32(e[12..16]));
        }

        return result;
    }

    // ---------------------------------------------------------------- GPT

    private static PartitionTableReadResult ReadGpt(
        IBlockReader device,
        MbrRawEntry[] mbrEntries,
        List<string> warnings,
        List<string> errors)
    {
        int sectorSize = device.SectorSize;
        long diskSectors = device.Length / sectorSize;

        GptHeader? primary = null;
        string? primaryProblem = null;
        try
        {
            primary = ReadGptHeader(device, 1);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            primaryProblem = ex.Message;
        }

        GptHeader? header = primary;
        bool usedBackup = false;

        if (primary is null || !primary.IsValid)
        {
            primaryProblem ??= primary?.Problem;
            GptHeader? backup = null;
            try
            {
                backup = ReadGptHeader(device, diskSectors - 1);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                errors.Add($"Záložní GPT nelze přečíst: {ex.Message}");
            }

            if (backup is not null && backup.IsValid)
            {
                header = backup;
                usedBackup = true;
                warnings.Add("Primární GPT hlavička je poškozená, byla použita záložní kopie z konce disku.");
            }
        }

        if (header is null || !header.IsValid)
        {
            errors.Add($"GPT nelze přečíst: {primaryProblem ?? header?.Problem ?? "neznámá chyba"}");
            return new PartitionTableReadResult { Errors = errors, Warnings = warnings };
        }

        if (header.NumberOfPartitionEntries is 0 or > 4096)
        {
            errors.Add($"GPT uvádí nepoužitelný počet položek ({header.NumberOfPartitionEntries}).");
            return new PartitionTableReadResult { Errors = errors, Warnings = warnings };
        }

        if (header.SizeOfPartitionEntry is < 128 or > 4096 || header.SizeOfPartitionEntry % 8 != 0)
        {
            errors.Add($"GPT uvádí nepoužitelnou velikost položky ({header.SizeOfPartitionEntry} B).");
            return new PartitionTableReadResult { Errors = errors, Warnings = warnings };
        }

        int entriesBytes = checked((int)((long)header.NumberOfPartitionEntries * header.SizeOfPartitionEntry));
        var entryBuffer = new byte[entriesBytes];
        device.ReadAt((long)header.PartitionEntryLba * sectorSize, entryBuffer);

        uint actualCrc = Crc32.Compute(entryBuffer);
        if (actualCrc != header.PartitionEntryArrayCrc32)
        {
            errors.Add("Kontrolní součet pole položek GPT nesouhlasí – tabulka oddílů je poškozená.");
            return new PartitionTableReadResult { Errors = errors, Warnings = warnings };
        }

        var partitions = new List<PartitionInfo>();
        for (int i = 0; i < (int)header.NumberOfPartitionEntries; i++)
        {
            var entry = entryBuffer.AsSpan(i * (int)header.SizeOfPartitionEntry, (int)header.SizeOfPartitionEntry);
            var typeGuid = GuidUtil.FromBytes(entry[..16]);
            if (typeGuid == GptPartitionTypes.Unused) continue;

            var uniqueGuid = GuidUtil.FromBytes(entry.Slice(16, 16));
            ulong firstLba = BitConverter.ToUInt64(entry.Slice(32, 8));
            ulong lastLba = BitConverter.ToUInt64(entry.Slice(40, 8));
            ulong attributes = BitConverter.ToUInt64(entry.Slice(48, 8));
            string name = Encoding.Unicode.GetString(entry.Slice(56, 72)).TrimEnd('\0', ' ');

            if (lastLba < firstLba)
            {
                errors.Add($"GPT položka {i + 1} má neplatný rozsah (first={firstLba}, last={lastLba}).");
                continue;
            }

            if (lastLba >= (ulong)diskSectors)
            {
                errors.Add($"GPT položka {i + 1} přesahuje konec disku ({lastLba + 1} > {diskSectors} LBA).");
                continue;
            }

            if (firstLba < header.FirstUsableLba)
            {
                errors.Add($"GPT položka {i + 1} začíná v oblasti vyhrazené pro GPT (LBA {firstLba}).");
                continue;
            }

            if (lastLba > header.LastUsableLba)
            {
                errors.Add($"GPT položka {i + 1} končí za poslední použitelnou LBA ({lastLba} > {header.LastUsableLba}).");
                continue;
            }

            partitions.Add(new PartitionInfo
            {
                Number = partitions.Count + 1,
                TableKind = PartitionTableKind.Gpt,
                StartLba = (long)firstLba,
                SectorCount = (long)(lastLba - firstLba + 1),
                TypeGuid = typeGuid,
                UniqueGuid = uniqueGuid,
                Name = name.Length == 0 ? null : name,
                Attributes = attributes,
            });
        }

        int legacyEntries = mbrEntries.Count(e => !e.IsEmpty && e.Type != MbrPartitionTypes.GptProtective);
        bool hybrid = legacyEntries > 0;

        if (hybrid)
        {
            warnings.Add("Disk má hybridní MBR (protective + klasické položky). Bude použita GPT, MBR položky budou zahozeny.");
        }

        var table = new PartitionTable
        {
            Kind = PartitionTableKind.Gpt,
            LogicalSectorSize = sectorSize,
            DiskSectors = diskSectors,
            Partitions = partitions,
            DiskGuid = header.DiskGuid,
            MbrDiskSignature = 0,
            IsHybridMbr = hybrid,
            EntryCapacity = (int)header.NumberOfPartitionEntries,
        };

        ValidateLayout(table, errors, warnings, usedBackup);

        return new PartitionTableReadResult { Table = errors.Count == 0 ? table : null, Errors = errors, Warnings = warnings };
    }

    private sealed class GptHeader
    {
        public bool IsValid => Problem is null;
        public string? Problem { get; init; }
        public ulong MyLba { get; init; }
        public ulong AlternateLba { get; init; }
        public ulong FirstUsableLba { get; init; }
        public ulong LastUsableLba { get; init; }
        public Guid DiskGuid { get; init; }
        public ulong PartitionEntryLba { get; init; }
        public uint NumberOfPartitionEntries { get; init; }
        public uint SizeOfPartitionEntry { get; init; }
        public uint PartitionEntryArrayCrc32 { get; init; }
    }

    private static GptHeader ReadGptHeader(IBlockReader device, long lba)
    {
        int sectorSize = device.SectorSize;
        var sector = new byte[sectorSize];
        device.ReadAt(lba * sectorSize, sector);

        if (!sector.AsSpan(0, 8).SequenceEqual(GptSignature))
        {
            return new GptHeader { Problem = $"na LBA {lba} není podpis \"EFI PART\"" };
        }

        uint headerSize = BitConverter.ToUInt32(sector.AsSpan(12, 4));
        if (headerSize is < 92 or > 4096 || headerSize > sectorSize)
        {
            return new GptHeader { Problem = $"neplatná velikost hlavičky ({headerSize} B)" };
        }

        uint storedCrc = BitConverter.ToUInt32(sector.AsSpan(16, 4));
        var copy = sector.AsSpan(0, (int)headerSize).ToArray();
        BitConverter.GetBytes(0u).CopyTo(copy, 16);
        uint computedCrc = Crc32.Compute(copy);
        if (storedCrc != computedCrc)
        {
            return new GptHeader { Problem = $"kontrolní součet hlavičky nesouhlasí (uloženo 0x{storedCrc:X8}, spočteno 0x{computedCrc:X8})" };
        }

        var header = new GptHeader
        {
            MyLba = BitConverter.ToUInt64(sector.AsSpan(24, 8)),
            AlternateLba = BitConverter.ToUInt64(sector.AsSpan(32, 8)),
            FirstUsableLba = BitConverter.ToUInt64(sector.AsSpan(40, 8)),
            LastUsableLba = BitConverter.ToUInt64(sector.AsSpan(48, 8)),
            DiskGuid = GuidUtil.FromBytes(sector.AsSpan(56, 16)),
            PartitionEntryLba = BitConverter.ToUInt64(sector.AsSpan(72, 8)),
            NumberOfPartitionEntries = BitConverter.ToUInt32(sector.AsSpan(80, 4)),
            SizeOfPartitionEntry = BitConverter.ToUInt32(sector.AsSpan(84, 4)),
            PartitionEntryArrayCrc32 = BitConverter.ToUInt32(sector.AsSpan(88, 4)),
        };

        if (header.MyLba != (ulong)lba)
        {
            return new GptHeader { Problem = $"hlavička tvrdí, že je na LBA {header.MyLba}, ale nalezena byla na LBA {lba}" };
        }

        return header;
    }

    // ---------------------------------------------------------------- MBR

    private static PartitionTableReadResult ReadMbr(
        IBlockReader device,
        MbrRawEntry[] entries,
        List<string> warnings,
        List<string> errors)
    {
        int sectorSize = device.SectorSize;
        long diskSectors = device.Length / sectorSize;

        var mbrSector = new byte[sectorSize];
        device.ReadAt(0, mbrSector);
        uint diskSignature = BitConverter.ToUInt32(mbrSector.AsSpan(440, 4));

        var partitions = new List<PartitionInfo>();
        var extended = new List<MbrRawEntry>();

        foreach (var entry in entries)
        {
            if (entry.IsEmpty) continue;

            if (MbrPartitionTypes.IsExtended(entry.Type))
            {
                extended.Add(entry);
                continue;
            }

            AddPrimary(entry);
        }

        void AddPrimary(MbrRawEntry entry)
        {
            ulong end = (ulong)entry.StartLba + entry.SectorCount - 1;
            if (end >= (ulong)diskSectors)
            {
                errors.Add($"MBR oddíl typu 0x{entry.Type:X2} přesahuje konec disku.");
                return;
            }

            if (entry.StartLba == 0)
            {
                errors.Add($"MBR oddíl typu 0x{entry.Type:X2} začíná na LBA 0, což je neplatné.");
                return;
            }

            if (entry.Type == 0x42)
            {
                warnings.Add("Disk obsahuje dynamický disk (LDM, typ 0x42).");
            }

            partitions.Add(new PartitionInfo
            {
                Number = partitions.Count + 1,
                TableKind = PartitionTableKind.Mbr,
                StartLba = entry.StartLba,
                SectorCount = entry.SectorCount,
                MbrType = entry.Type,
                IsActive = entry.Status == 0x80,
            });
        }

        if (extended.Count > 1)
        {
            errors.Add("MBR obsahuje více než jeden rozšířený oddíl, což je neplatná konfigurace.");
        }

        foreach (var ext in extended)
        {
            ReadLogicalChain(device, ext, diskSectors, partitions, errors, warnings);
        }

        var table = new PartitionTable
        {
            Kind = PartitionTableKind.Mbr,
            LogicalSectorSize = sectorSize,
            DiskSectors = diskSectors,
            Partitions = partitions,
            MbrDiskSignature = diskSignature,
            EntryCapacity = 4,
            DiskGuid = Guid.Empty,
        };

        ValidateLayout(table, errors, warnings, false);

        return new PartitionTableReadResult { Table = errors.Count == 0 ? table : null, Errors = errors, Warnings = warnings };
    }

    private static void ReadLogicalChain(
        IBlockReader device,
        MbrRawEntry extended,
        long diskSectors,
        List<PartitionInfo> partitions,
        List<string> errors,
        List<string> warnings)
    {
        int sectorSize = device.SectorSize;
        long extendedEnd = (long)extended.StartLba + extended.SectorCount - 1;
        long ebrLba = extended.StartLba;
        var visited = new HashSet<long>();

        for (int guard = 0; guard <= MaxLogicalPartitions; guard++)
        {
            if (ebrLba <= 0 || ebrLba >= diskSectors)
            {
                errors.Add($"Řetězec rozšířených oddílů ukazuje mimo disk (EBR na LBA {ebrLba}).");
                return;
            }

            if (!visited.Add(ebrLba))
            {
                errors.Add("Řetězec rozšířených oddílů obsahuje smyčku.");
                return;
            }

            var sector = new byte[sectorSize];
            device.ReadAt(ebrLba * sectorSize, sector);

            if (sector[MbrSignatureOffset] != 0x55 || sector[MbrSignatureOffset + 1] != 0xAA)
            {
                errors.Add($"EBR na LBA {ebrLba} nemá platný podpis.");
                return;
            }

            var entries = ParseMbrEntries(sector);
            var first = entries[0];

            if (!first.IsEmpty)
            {
                long start = ebrLba + first.StartLba;
                long end = start + first.SectorCount - 1;
                if (end > extendedEnd)
                {
                    errors.Add($"Logický oddíl na LBA {start} přesahuje rozšířený oddíl.");
                    return;
                }

                if (end >= diskSectors)
                {
                    errors.Add($"Logický oddíl na LBA {start} přesahuje konec disku.");
                    return;
                }

                partitions.Add(new PartitionInfo
                {
                    Number = partitions.Count + 1,
                    TableKind = PartitionTableKind.Mbr,
                    StartLba = start,
                    SectorCount = first.SectorCount,
                    MbrType = first.Type,
                    IsActive = first.Status == 0x80,
                    IsLogical = true,
                });
            }

            var next = entries[1];
            if (next.IsEmpty) break;

            long nextEbr = extended.StartLba + next.StartLba;
            if (nextEbr == ebrLba) break;
            ebrLba = nextEbr;
        }

        if (partitions.Count >= MaxLogicalPartitions)
        {
            warnings.Add("Byl dosažen maximální počet logických oddílů, řetězec může být neúplný.");
        }
    }

    // ---------------------------------------------------------------- shared validation

    private static void ValidateLayout(
        PartitionTable table,
        List<string> errors,
        List<string> warnings,
        bool usedBackupGpt)
    {
        var ordered = table.Partitions.OrderBy(p => p.StartLba).ToList();

        for (int i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            if (current.StartLba <= previous.EndLba)
            {
                errors.Add(
                    $"Oddíly se překrývají: #{previous.Number} (LBA {previous.StartLba}..{previous.EndLba}) " +
                    $"a #{current.Number} (LBA {current.StartLba}..{current.EndLba}).");
            }
        }

        foreach (var p in table.Partitions)
        {
            if (p.StartLba < 0 || p.SectorCount <= 0)
            {
                errors.Add($"Oddíl #{p.Number} má neplatnou geometrii.");
            }

            if (p.EndLba >= table.DiskSectors)
            {
                errors.Add($"Oddíl #{p.Number} končí za koncem disku.");
            }
        }

        if (table.Kind == PartitionTableKind.Gpt)
        {
            long firstUsable = GptLayout.FirstUsableLba(table.LogicalSectorSize, table.EntryCapacity, 128);
            if (ordered.Count > 0 && ordered[0].StartLba < firstUsable)
            {
                warnings.Add("První oddíl začíná před doporučenou hranicí zarovnání.");
            }
        }

        int activeCount = table.Partitions.Count(p => p.IsActive);
        if (table.Kind == PartitionTableKind.Mbr && activeCount > 1)
        {
            warnings.Add($"MBR označuje {activeCount} oddílů jako aktivní, což je neobvyklé.");
        }

        if (usedBackupGpt)
        {
            warnings.Add("Tabulka oddílů byla obnovena ze zálohy – doporučujeme po migraci ověřit obsah.");
        }
    }
}

/// <summary>GPT area arithmetic, shared by the reader, the writer and the planner.</summary>
public static class GptLayout
{
    public const int DefaultEntryCount = 128;
    public const int DefaultEntrySize = 128;

    /// <summary>Number of sectors occupied by the partition entry array.</summary>
    public static int EntryArraySectors(int sectorSize, int entryCount = DefaultEntryCount, int entrySize = DefaultEntrySize)
        => (entryCount * entrySize + sectorSize - 1) / sectorSize;

    /// <summary>First LBA that may hold partition data.</summary>
    public static long FirstUsableLba(int sectorSize, int entryCount = DefaultEntryCount, int entrySize = DefaultEntrySize)
        => 2 + EntryArraySectors(sectorSize, entryCount, entrySize);

    /// <summary>Last LBA that may hold partition data.</summary>
    public static long LastUsableLba(long diskSectors, int sectorSize, int entryCount = DefaultEntryCount, int entrySize = DefaultEntrySize)
        => diskSectors - 1 - EntryArraySectors(sectorSize, entryCount, entrySize);
}
