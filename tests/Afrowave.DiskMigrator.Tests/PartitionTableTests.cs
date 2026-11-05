using System.Text;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>
/// Partition table parsing and writing. These tests are the first line of defence: if the
/// tool misreads a partition table, everything downstream is wrong.
/// </summary>
public static class PartitionTableTests
{
    private const long MiB = 1024L * 1024;
    private const int Sector = 512;

    [Test("CRC-32 must match the published IEEE test vector")]
    public static void Crc32KnownVector()
    {
        uint crc = Crc32.Compute(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xCBF43926u, crc, "CRC-32 standardního vektoru");
        Assert.Equal(0u, Crc32.Compute([]), "CRC-32 prázdných dat");
    }

    [Test("GPT written by the tool must parse back identically")]
    public static void GptRoundTrip()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        var esp = Guid.NewGuid();
        var data = Guid.NewGuid();
        var diskGuid = Guid.NewGuid();

        PartitionTableWriter.WriteGpt(device, new GptWriteSpec
        {
            DiskGuid = diskGuid,
            Entries =
            [
                new PartitionWriteEntry
                {
                    Number = 1,
                    StartLba = 2048,
                    SectorCount = 20 * 1024 * 1024 / Sector,
                    TypeGuid = GptPartitionTypes.EfiSystem,
                    UniqueGuid = esp,
                    Name = "SYSTEM",
                },
                new PartitionWriteEntry
                {
                    Number = 2,
                    StartLba = (2048 + (20 * 1024 * 1024 / Sector) + 2048),
                    SectorCount = 4096,
                    TypeGuid = GptPartitionTypes.MicrosoftBasicData,
                    UniqueGuid = data,
                    Name = "Data",
                    Attributes = 0x8000000000000000,
                },
            ],
        });

        var read = PartitionTableReader.Read(device);
        Assert.Empty(read.Errors, "chyby při čtení zapsané GPT");
        Assert.NotEqual<PartitionTable?>(null, read.Table, "tabulka musí být načtena");

        var table = read.Table!;
        Assert.Equal(PartitionTableKind.Gpt, table.Kind, "typ tabulky");
        Assert.Equal(diskGuid, table.DiskGuid, "GUID disku");
        Assert.Equal(2, table.Partitions.Count, "počet oddílů");
        Assert.Equal(esp, table.Partitions[0].UniqueGuid, "GUID prvního oddílu");
        Assert.Equal("SYSTEM", table.Partitions[0].Name, "název prvního oddílu");
        Assert.Equal(GptPartitionTypes.EfiSystem, table.Partitions[0].TypeGuid, "typ prvního oddílu");
        Assert.Equal(0x8000000000000000UL, table.Partitions[1].Attributes, "atributy druhého oddílu");
    }

    [Test("A hand-built GPT (independent of the writer) must parse")]
    public static void GptHandCraftedBytes()
    {
        const int sectors = 128 * 1024;            // 64 MiB
        var image = new byte[sectors * Sector];

        // --- protective MBR
        image[446 + 4] = 0xEE;
        BitConverter.TryWriteBytes(image.AsSpan(446 + 8, 4), 1u);
        BitConverter.TryWriteBytes(image.AsSpan(446 + 12, 4), (uint)(sectors - 1));
        image[510] = 0x55;
        image[511] = 0xAA;

        // --- partition entry array (LBA 2)
        var entries = new byte[128 * 128];
        var typeGuid = GptPartitionTypes.MicrosoftBasicData;
        var uniqueGuid = Guid.NewGuid();
        typeGuid.TryWriteBytes(entries.AsSpan(0, 16));
        uniqueGuid.TryWriteBytes(entries.AsSpan(16, 16));
        BitConverter.TryWriteBytes(entries.AsSpan(32, 8), 2048UL);
        BitConverter.TryWriteBytes(entries.AsSpan(40, 8), 4095UL);
        Encoding.Unicode.GetBytes("Ruční".ToCharArray()).CopyTo(entries.AsSpan(56, 72));
        entries.AsSpan(2048, 128).Clear();
        uint entriesCrc = Crc32.Compute(entries);
        entries.CopyTo(image, 2 * Sector);

        // --- headers
        var primary = BuildHeader(1, sectors - 1, 34, sectors - 34, Guid.NewGuid(), 2, entriesCrc);
        primary.CopyTo(image, 1 * Sector);
        var backup = BuildHeader(sectors - 1, 1, 34, sectors - 34, Guid.Parse("00000000-0000-0000-0000-000000000000"), 2, entriesCrc);
        _ = backup;

        var read = PartitionTableReader.Read(new MemoryBlockDeviceFrom(image));
        Assert.Empty(read.Errors, "chyby při čtení ručně sestavené GPT");
        Assert.Equal(1, read.Table!.Partitions.Count, "počet oddílů");
        Assert.Equal(2048L, read.Table.Partitions[0].StartLba, "první LBA");
        Assert.Equal(uniqueGuid, read.Table.Partitions[0].UniqueGuid, "GUID oddílu");
        Assert.Equal("Ruční", read.Table.Partitions[0].Name, "název oddílu");
    }

    [Test("A GPT with a broken header checksum must be refused")]
    public static void GptCorruptHeaderRefused()
    {
        var device = new MemoryBlockDevice(32 * MiB);
        PartitionTableWriter.WriteGpt(device, new GptWriteSpec
        {
            DiskGuid = Guid.NewGuid(),
            Entries =
            [
                new PartitionWriteEntry
                {
                    Number = 1,
                    StartLba = 2048,
                    SectorCount = 4096,
                    TypeGuid = GptPartitionTypes.MicrosoftBasicData,
                    UniqueGuid = Guid.NewGuid(),
                },
            ],
        });

        // Break both the primary and the backup header so no fallback is possible.
        device.Buffer[1 * Sector + 30] ^= 0xFF;
        int lastLba = device.Buffer.Length / Sector - 1;
        device.Buffer[lastLba * Sector + 30] ^= 0xFF;

        var read = PartitionTableReader.Read(device);
        Assert.NotEmpty(read.Errors, "poškozená GPT musí být odmítnuta");
        Assert.Equal<PartitionTable?>(null, read.Table, "tabulka nesmí být vrácena");
    }

    [Test("A damaged primary GPT must fall back to the backup copy")]
    public static void GptBackupFallback()
    {
        var device = new MemoryBlockDevice(32 * MiB);
        PartitionTableWriter.WriteGpt(device, new GptWriteSpec
        {
            DiskGuid = Guid.NewGuid(),
            Entries =
            [
                new PartitionWriteEntry
                {
                    Number = 1,
                    StartLba = 2048,
                    SectorCount = 4096,
                    TypeGuid = GptPartitionTypes.MicrosoftBasicData,
                    UniqueGuid = Guid.NewGuid(),
                },
            ],
        });

        device.Buffer[1 * Sector + 30] ^= 0xFF;   // corrupt the primary header only

        var read = PartitionTableReader.Read(device);
        Assert.Empty(read.Errors, "záložní GPT musí umožnit čtení");
        Assert.NotEqual<PartitionTable?>(null, read.Table, "tabulka ze zálohy");
        Assert.NotEmpty(read.Warnings, "musí být vydáno varování o použité záloze");
    }

    [Test("MBR with four primary partitions parses, including the active flag")]
    public static void MbrPrimaries()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        PartitionTableWriter.WriteMbr(device, new MbrWriteSpec
        {
            DiskSignature = 0xAABBCCDD,
            BootCode = new byte[446],
            Entries =
            [
                new PartitionWriteEntry { Number = 1, StartLba = 2048, SectorCount = 20000, MbrType = 0x07, IsActive = true },
                new PartitionWriteEntry { Number = 2, StartLba = 22048, SectorCount = 20000, MbrType = 0x0C },
                new PartitionWriteEntry { Number = 3, StartLba = 42048, SectorCount = 10000, MbrType = 0x83 },
            ],
        });

        var read = PartitionTableReader.Read(device);
        Assert.Empty(read.Errors, "chyby čtení MBR");
        var table = read.Table!;
        Assert.Equal(PartitionTableKind.Mbr, table.Kind, "typ tabulky");
        Assert.Equal(0xAABBCCDDu, table.MbrDiskSignature, "signatura disku");
        Assert.Equal(3, table.Partitions.Count, "počet oddílů");
        Assert.True(table.Partitions[0].IsActive, "první oddíl musí být aktivní");
        Assert.Equal<byte>(0x07, table.Partitions[0].MbrType, "typ prvního oddílu");
        Assert.False(table.Partitions[1].IsActive, "druhý oddíl nesmí být aktivní");
    }

    [Test("MBR extended partition with logical drives parses in order")]
    public static void MbrLogicalPartitions()
    {
        var device = new MemoryBlockDevice(128 * MiB);
        long primarySectors = 40960;

        PartitionTableWriter.WriteMbr(device, new MbrWriteSpec
        {
            DiskSignature = 0x11223344,
            BootCode = new byte[446],
            Entries =
            [
                new PartitionWriteEntry { Number = 1, StartLba = 2048, SectorCount = primarySectors, MbrType = 0x07, IsActive = true },
                new PartitionWriteEntry { Number = 2, StartLba = 43008, SectorCount = 20480, MbrType = 0x07, IsLogical = true },
                new PartitionWriteEntry { Number = 3, StartLba = 63488, SectorCount = 20480, MbrType = 0x07, IsLogical = true },
            ],
        });

        var read = PartitionTableReader.Read(device);
        Assert.Empty(read.Errors, "chyby čtení logických oddílů");
        var table = read.Table!;
        Assert.Equal(3, table.Partitions.Count, "počet oddílů");
        Assert.False(table.Partitions[0].IsLogical, "první oddíl je primární");
        Assert.True(table.Partitions[1].IsLogical, "druhý oddíl je logický");
        Assert.True(table.Partitions[2].IsLogical, "třetí oddíl je logický");
        Assert.Equal(43008L, table.Partitions[1].StartLba, "start prvního logického oddílu");
        Assert.Equal(63488L, table.Partitions[2].StartLba, "start druhého logického oddílu");
        Assert.Equal(20480L, table.Partitions[1].SectorCount, "velikost logického oddílu");
    }

    [Test("A disk without a boot signature must be reported, not guessed")]
    public static void MbrWithoutSignature()
    {
        var device = new MemoryBlockDevice(16 * MiB);
        var read = PartitionTableReader.Read(device);
        Assert.NotEmpty(read.Errors, "prázdný disk musí skončit chybou");
        Assert.Equal<PartitionTable?>(null, read.Table, "žádná tabulka");
    }

    [Test("Overlapping partitions must be refused outright")]
    public static void OverlappingPartitionsRefused()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        PartitionTableWriter.ClearPartitionAreas(device, gpt: false);

        // Build the MBR by hand so the writer's own validation does not kick in.
        var sector = device.Buffer;
        WriteMbrEntry(sector, 0, 0x07, 2048, 40000);
        WriteMbrEntry(sector, 1, 0x07, 30000, 40000); // overlaps the first one
        sector[510] = 0x55;
        sector[511] = 0xAA;

        var read = PartitionTableReader.Read(device);
        Assert.NotEmpty(read.Errors, "překrývající se oddíly musí být odmítnuty");
        Assert.Contains("překrývají", string.Join("; ", read.Errors), "chyba musí mluvit o překryvu");
    }

    [Test("A partition beyond the end of the disk must be refused")]
    public static void PartitionBeyondDiskRefused()
    {
        var device = new MemoryBlockDevice(16 * MiB);
        var sector = device.Buffer;
        WriteMbrEntry(sector, 0, 0x07, 2048, 100_000); // way past 32768 sectors
        sector[510] = 0x55;
        sector[511] = 0xAA;

        var read = PartitionTableReader.Read(device);
        Assert.NotEmpty(read.Errors, "oddíl za koncem disku musí být odmítnut");
    }

    [Test("The writer must refuse to lay out partitions that do not fit")]
    public static void WriterRefusesOverflow()
    {
        var device = new MemoryBlockDevice(16 * MiB);
        Assert.Throws<InvalidOperationException>(() =>
            PartitionTableWriter.WriteGpt(device, new GptWriteSpec
            {
                DiskGuid = Guid.NewGuid(),
                Entries =
                [
                    new PartitionWriteEntry
                    {
                        Number = 1,
                        StartLba = 2048,
                        SectorCount = 1_000_000,
                        TypeGuid = GptPartitionTypes.MicrosoftBasicData,
                        UniqueGuid = Guid.NewGuid(),
                    },
                ],
            }), "zápis oddílu za konec disku musí selhat");
    }

    [Test("4 KiB sector disks must be laid out and parsed correctly")]
    public static void FourKilobyteSectors()
    {
        var device = new MemoryBlockDevice(128 * 1024 * 1024, sectorSize: 4096);
        var unique = Guid.NewGuid();

        PartitionTableWriter.WriteGpt(device, new GptWriteSpec
        {
            DiskGuid = Guid.NewGuid(),
            Entries =
            [
                new PartitionWriteEntry
                {
                    Number = 1,
                    StartLba = 256,               // 1 MiB
                    SectorCount = 25600,           // 100 MiB
                    TypeGuid = GptPartitionTypes.EfiSystem,
                    UniqueGuid = unique,
                    Name = "ESP",
                },
            ],
        });

        var read = PartitionTableReader.Read(device);
        Assert.Empty(read.Errors, "chyby u 4K sektorů");
        Assert.Equal(unique, read.Table!.Partitions[0].UniqueGuid, "GUID oddílu na 4K disku");
        Assert.Equal(256L, read.Table.Partitions[0].StartLba, "start na 4K disku");
        Assert.Equal(4096, read.Table.LogicalSectorSize, "velikost sektoru");
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] BuildHeader(long myLba, long alternateLba, long firstUsable, long lastUsable, Guid diskGuid, long entriesLba, uint entriesCrc)
    {
        var header = new byte[Sector];
        "EFI PART"u8.CopyTo(header.AsSpan(0, 8));
        BitConverter.TryWriteBytes(header.AsSpan(8, 4), 0x00010000u);
        BitConverter.TryWriteBytes(header.AsSpan(12, 4), 92u);
        BitConverter.TryWriteBytes(header.AsSpan(24, 8), (ulong)myLba);
        BitConverter.TryWriteBytes(header.AsSpan(32, 8), (ulong)alternateLba);
        BitConverter.TryWriteBytes(header.AsSpan(40, 8), (ulong)firstUsable);
        BitConverter.TryWriteBytes(header.AsSpan(48, 8), (ulong)lastUsable);
        diskGuid.TryWriteBytes(header.AsSpan(56, 16));
        BitConverter.TryWriteBytes(header.AsSpan(72, 8), (ulong)entriesLba);
        BitConverter.TryWriteBytes(header.AsSpan(80, 4), 128u);
        BitConverter.TryWriteBytes(header.AsSpan(84, 4), 128u);
        BitConverter.TryWriteBytes(header.AsSpan(88, 4), entriesCrc);
        BitConverter.TryWriteBytes(header.AsSpan(16, 4), Crc32.Compute(header.AsSpan(0, 92)));
        return header;
    }

    private static void WriteMbrEntry(byte[] sector, int slot, byte type, long startLba, long sectorCount)
    {
        int offset = 446 + (slot * 16);
        sector[offset] = 0x00;
        sector[offset + 4] = type;
        BitConverter.TryWriteBytes(sector.AsSpan(offset + 8, 4), (uint)startLba);
        BitConverter.TryWriteBytes(sector.AsSpan(offset + 12, 4), (uint)sectorCount);
    }
}

/// <summary>Wraps an existing byte array as a read-only block device.</summary>
public sealed class MemoryBlockDeviceFrom : Core.Abstractions.IBlockDevice
{
    private readonly byte[] _data;

    public MemoryBlockDeviceFrom(byte[] data)
    {
        _data = data;
        SectorSize = 512;
    }

    public string Name => "memory-from";

    public long Length => _data.Length;

    public int SectorSize { get; }

    public void ReadAt(long offset, Span<byte> buffer) => _data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);

    public void WriteAt(long offset, ReadOnlySpan<byte> buffer) => buffer.CopyTo(_data.AsSpan((int)offset));

    public void Flush()
    {
    }

    public void Dispose()
    {
    }
}
