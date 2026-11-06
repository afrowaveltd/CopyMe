using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Platform.Image;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>
/// The target disk must define no partitions at all. These tests pin down exactly what counts
/// as "empty" and - more importantly - what must be refused because it cannot be interpreted
/// with certainty.
/// </summary>
public static class TargetDiskTests
{
    private const long MiB = Fixtures.MiB;

    [Test("A completely blank disk is an acceptable target")]
    public static void BlankDiskIsEmpty()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        var inspection = TargetDiskInspector.Inspect(device);

        Assert.Equal(TargetDiskState.Empty, inspection.State, "prázdný disk");
        Assert.True(inspection.IsEmpty, "disk musí být použitelný jako cíl");
    }

    [Test("Invalid 256-byte sector geometry is rejected without indexing past the MBR")]
    public static void InvalidSectorGeometryRefused()
    {
        var device = new MemoryBlockDevice(64 * MiB, sectorSize: 256);

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Uncertain, inspection.State, "neplatná geometrie nesmí projít kontrolou");

        var read = PartitionTableReader.Read(device);
        Assert.False(read.Success, "neplatná geometrie nesmí způsobit výjimku ani přijetí disku");
    }

    [Test("A GPT disk with partitions is refused as a target")]
    public static void GptWithPartitionsRefused()
    {
        var device = new MemoryBlockDevice(128 * MiB);
        PartitionTableWriter.WriteGpt(device, new GptWriteSpec
        {
            DiskGuid = Guid.NewGuid(),
            Entries =
            [
                new PartitionWriteEntry
                {
                    Number = 1,
                    StartLba = 2048,
                    SectorCount = 20000,
                    TypeGuid = GptPartitionTypes.MicrosoftBasicData,
                    UniqueGuid = Guid.NewGuid(),
                },
            ],
        });

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Occupied, inspection.State, "GPT s oddílem");
        Assert.False(inspection.IsEmpty, "disk nesmí být použitelný jako cíl");
        Assert.Equal(1, inspection.DefinedPartitionCount, "počet oddílů");
        Assert.Contains("obsahuje", string.Join(" | ", inspection.Reasons), "důvod musí být vysvětlen");
    }

    [Test("A GPT disk without partitions is an acceptable target")]
    public static void GptWithoutPartitionsIsEmpty()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        PartitionTableWriter.WriteGpt(device, new GptWriteSpec
        {
            DiskGuid = Guid.NewGuid(),
            Entries = [],
        });

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Empty, inspection.State, "GPT bez oddílů");
    }

    [Test("An MBR disk with partitions is refused as a target")]
    public static void MbrWithPartitionsRefused()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        PartitionTableWriter.WriteMbr(device, new MbrWriteSpec
        {
            DiskSignature = 0x12345678,
            BootCode = new byte[446],
            Entries =
            [
                new PartitionWriteEntry { Number = 1, StartLba = 2048, SectorCount = 20000, MbrType = 0x07 },
            ],
        });

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Occupied, inspection.State, "MBR s oddílem");
        Assert.Equal(1, inspection.DefinedPartitionCount, "počet oddílů");
    }

    [Test("An extended MBR container counts as a defined partition")]
    public static void MbrExtendedContainerRefused()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        PartitionTableWriter.WriteMbr(device, new MbrWriteSpec
        {
            DiskSignature = 0x22222222,
            BootCode = new byte[446],
            Entries =
            [
                new PartitionWriteEntry { Number = 1, StartLba = 2048, SectorCount = 10000, MbrType = 0x07 },
                new PartitionWriteEntry { Number = 2, StartLba = 12048, SectorCount = 8000, MbrType = 0x07, IsLogical = true },
            ],
        });

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Occupied, inspection.State, "MBR s rozšířeným oddílem");
    }

    [Test("An MBR disk with only empty entries is an acceptable target")]
    public static void MbrWithEmptyEntriesIsEmpty()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        PartitionTableWriter.WriteMbr(device, new MbrWriteSpec
        {
            DiskSignature = 0x11111111,
            BootCode = new byte[446],
            Entries = [],
        });

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Empty, inspection.State, "MBR bez oddílů");
    }

    [Test("A disk whose partition table cannot be parsed must be refused")]
    public static void CorruptTableRefused()
    {
        var device = new MemoryBlockDevice(64 * MiB);
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

        // Break both GPT headers so neither the primary nor the backup can be trusted.
        device.Buffer[1 * 512 + 30] ^= 0xFF;
        int lastLba = device.Buffer.Length / 512 - 1;
        device.Buffer[lastLba * 512 + 30] ^= 0xFF;

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Uncertain, inspection.State, "poškozená tabulka");
        Assert.False(inspection.IsEmpty, "nejistý stav se nesmí použít jako cíl");
    }

    [Test("A stray GPT header without a protective MBR is ambiguous and refused")]
    public static void StrayGptHeaderRefused()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        PartitionTableWriter.WriteGpt(device, new GptWriteSpec
        {
            DiskGuid = Guid.NewGuid(),
            Entries = [],
        });

        // Remove the protective MBR signature but leave the GPT behind.
        device.Buffer[510] = 0;
        device.Buffer[511] = 0;

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Uncertain, inspection.State, "GPT bez ochranného MBR");
    }

    [Test("A leftover backup GPT without a primary table is refused")]
    public static void StaleBackupGptRefused()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        PartitionTableWriter.WriteGpt(device, new GptWriteSpec
        {
            DiskGuid = Guid.NewGuid(),
            Entries = [],
        });

        // Zero the head of the disk: the primary GPT disappears, the backup at the end survives.
        device.Buffer.AsSpan(0, 64 * 512).Clear();

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Uncertain, inspection.State, "zůstala jen záložní GPT");
    }

    [Test("A hybrid MBR (GPT plus legacy entries) is refused")]
    public static void HybridMbrRefused()
    {
        var device = new MemoryBlockDevice(64 * MiB);
        PartitionTableWriter.WriteGpt(device, new GptWriteSpec
        {
            DiskGuid = Guid.NewGuid(),
            Entries = [],
        });

        // Add a legacy entry next to the protective one.
        int offset = 446 + 16;
        device.Buffer[offset + 4] = 0x07;
        BitConverter.TryWriteBytes(device.Buffer.AsSpan(offset + 8, 4), 2048u);
        BitConverter.TryWriteBytes(device.Buffer.AsSpan(offset + 12, 4), 4000u);

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Uncertain, inspection.State, "hybridní MBR");
    }

    [Test("A file system written directly on the disk counts as empty, with a note")]
    public static void SuperFloppyIsEmptyWithNote()
    {
        var device = new MemoryBlockDevice(16 * MiB);
        var boot = device.Buffer.AsSpan(0, 512);
        boot[0] = 0xEB;
        boot[1] = 0x58;
        boot[2] = 0x90;
        System.Text.Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(boot[3..]);
        BitConverter.TryWriteBytes(boot[0x0B..], (ushort)512);
        boot[0x0D] = 8;
        System.Text.Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot[82..]);
        boot[510] = 0x55;
        boot[511] = 0xAA;

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Empty, inspection.State, "superfloppy nemá oddíly");
        Assert.Contains("FAT32", string.Join(" | ", inspection.Notes), "poznámka musí zmínit souborový systém");
    }

    [Test("A partition larger than the disk is still reported as occupied")]
    public static void PartitionBeyondDiskReported()
    {
        var device = new MemoryBlockDevice(16 * MiB);
        var sector = device.Buffer.AsSpan();
        int offset = 446;
        sector[offset + 4] = 0x07;
        BitConverter.TryWriteBytes(sector.Slice(offset + 8, 4), 2048u);
        BitConverter.TryWriteBytes(sector.Slice(offset + 12, 4), 900_000u);
        sector[510] = 0x55;
        sector[511] = 0xAA;

        var inspection = TargetDiskInspector.Inspect(device);
        Assert.Equal(TargetDiskState.Occupied, inspection.State, "přesahující oddíl je stále definovaný");
    }
}
