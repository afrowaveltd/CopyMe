using System.Text;
using Afrowave.DiskMigrator.Core.Filesystems;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Platform.Image;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>File system identification and the read-only NTFS metadata reader.</summary>
public static class FilesystemTests
{
    private const long MiB = 1024L * 1024;

    [Test("NTFS is recognised from the boot sector")]
    public static void DetectNtfs()
    {
        var device = new MemoryBlockDevice(4 * MiB);
        NtfsBootSector(device.Buffer, sectorsPerCluster: 8);

        var fs = FilesystemDetector.Detect(device, 0, 4 * MiB, 512);
        Assert.Equal(FilesystemKind.Ntfs, fs.Kind, "rozpoznaný systém");
        Assert.Equal(512, fs.BytesPerSector, "velikost sektoru");
    }

    [Test("FAT32 is recognised, including the volume label")]
    public static void DetectFat32()
    {
        var device = new MemoryBlockDevice(4 * MiB);
        var sector = device.Buffer.AsSpan(0, 512);
        sector[0] = 0xEB;
        sector[1] = 0x58;
        sector[2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(sector[3..]);
        BitConverter.TryWriteBytes(sector[0x0B..], (ushort)512);
        sector[0x0D] = 8;
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(sector[82..]);
        Encoding.ASCII.GetBytes("ZALOHA     ").CopyTo(sector[71..]);

        var fs = FilesystemDetector.Detect(device, 0, 4 * MiB, 512);
        Assert.Equal(FilesystemKind.Fat32, fs.Kind, "rozpoznaný systém");
        Assert.Equal("ZALOHA", fs.Label, "popisek svazku");
    }

    [Test("exFAT, BitLocker, ext4 and unknown file systems are distinguished")]
    public static void DetectVarious()
    {
        Assert.Equal(FilesystemKind.ExFat, Probe(bytes => Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(bytes.AsSpan(3))).Kind, "exFAT");
        Assert.Equal(FilesystemKind.BitLocker, Probe(bytes => Encoding.ASCII.GetBytes("-FVE-FS-").CopyTo(bytes.AsSpan(3))).Kind, "BitLocker");
        Assert.Equal(FilesystemKind.Ext2To4, Probe(bytes =>
        {
            bytes[1080] = 0x53;
            bytes[1081] = 0xEF;
        }).Kind, "ext4");

        var unknown = Probe(bytes => bytes[0] = 0x77);
        Assert.Equal(FilesystemKind.Unknown, unknown.Kind, "neznámý obsah");

        var encrypted = Probe(bytes => Encoding.ASCII.GetBytes("-FVE-FS-").CopyTo(bytes.AsSpan(3)));
        Assert.True(encrypted.IsEncrypted, "BitLocker musí být označen jako šifrovaný");
        Assert.False(encrypted.SupportsIntelligentCopy, "BitLocker nepodporuje chytrou kopii");
    }

    [Test("An all-zero partition is reported as unformatted")]
    public static void DetectUnformatted()
    {
        var device = new MemoryBlockDevice(2 * MiB);
        var fs = FilesystemDetector.Detect(device, 0, 2 * MiB, 512);
        Assert.Equal(FilesystemKind.Unformatted, fs.Kind, "neformátovaný oddíl");
    }

    [Test("The NTFS reader must decode geometry, label and allocation map")]
    public static void NtfsReaderBasics()
    {
        long[] dataClusters = [100, 101, 102, 500, 501];
        var image = SyntheticNtfs.Build(totalClusters: 2048, dataClusters: dataClusters);

        using var device = new MemoryBlockDeviceFrom(image);
        using var ntfs = NtfsVolume.Open(device, 0, image.Length);

        Assert.Equal(4096, ntfs.ClusterSize, "velikost clusteru");
        Assert.Equal(2048L, ntfs.Info.TotalClusters, "počet clusterů");
        Assert.Equal("TESTVOL", ntfs.Info.VolumeLabel, "popisek svazku");
        Assert.False(ntfs.Info.IsDirty, "svazek musí být čistý");

        long expectedUsed = SyntheticNtfs.MetadataClusters(2048).Distinct().Count() + dataClusters.Length;
        Assert.Equal(expectedUsed, ntfs.Info.UsedClusters, "počet obsazených clusterů");
        Assert.Equal(expectedUsed * 4096, ntfs.Info.UsedBytes, "obsazené bajty");

        foreach (long cluster in dataClusters)
        {
            Assert.True(ntfs.Allocations.IsClusterInUse(cluster), $"cluster {cluster} musí být obsazený");
        }

        Assert.False(ntfs.Allocations.IsClusterInUse(1500), "cluster 1500 musí být volný");
        Assert.False(ntfs.Allocations.IsClusterInUse(2047), "poslední cluster musí být volný");
    }

    [Test("NTFS boot sector layout matches the Windows disk format")]
    public static void NtfsBootSectorCanonicalOffsets()
    {
        var image = SyntheticNtfs.Build(totalClusters: 1024);

        Assert.Equal(8192UL, BitConverter.ToUInt64(image, 0x28), "0x28 = volume sectors");
        Assert.Equal((ulong)SyntheticNtfs.MftLcn, BitConverter.ToUInt64(image, 0x30), "0x30 = MFT LCN");
        Assert.True(BitConverter.ToUInt64(image, 0x38) != 0, "0x38 = MFT mirror LCN");
        Assert.Equal(unchecked((byte)(sbyte)-10), image[0x40], "0x40 = record size code");

        using var reader = new MemoryBlockDeviceFrom(image);
        using var volume = NtfsVolume.Open(reader, 0, image.Length);
        Assert.Equal(1024, SyntheticNtfs.RecordSize, "synthetic record size");
        Assert.Equal(1024L, volume.Info.TotalClusters, "allocation map from canonical MFT");
        Assert.False(volume.Info.IsDirty, "canonical header remains readable");
    }

    [Test("A malformed MFT record size cannot silently fall back to 1024 bytes")]
    public static void NtfsInvalidRecordSizeRejected()
    {
        var image = SyntheticNtfs.Build(totalClusters: 1024);
        image[0x40] = 0; // invalid: no FILE-record size specified
        using var reader = new MemoryBlockDeviceFrom(image);

        Assert.Throws<NtfsAnalysisException>(
            () => NtfsVolume.Open(reader, 0, image.Length).Dispose(),
            "unknown record size must not be guessed");
    }

    [Test("The NTFS dirty flag must be detected")]
    public static void NtfsDirtyFlag()
    {
        var image = SyntheticNtfs.Build(totalClusters: 1024, dirty: true);
        using var device = new MemoryBlockDeviceFrom(image);
        using var ntfs = NtfsVolume.Open(device, 0, image.Length);
        Assert.True(ntfs.Info.IsDirty, "špinavý svazek musí být rozpoznán");
    }

    [Test("A partition that only pretends to be NTFS must be rejected, not guessed")]
    public static void NtfsRejectsGarbage()
    {
        var device = new MemoryBlockDevice(4 * MiB);
        NtfsBootSector(device.Buffer, sectorsPerCluster: 8);
        // The boot sector claims NTFS but there is no record table behind it.

        Assert.Throws<NtfsAnalysisException>(
            () => NtfsVolume.Open(device, 0, 4 * MiB).Dispose(),
            "nečitelná NTFS metadata musí skončit výjimkou");
    }

    // ---------------------------------------------------------------- helpers

    private static FilesystemInfo Probe(Action<byte[]> mutate)
    {
        var device = new MemoryBlockDevice(4 * MiB);
        mutate(device.Buffer);
        return FilesystemDetector.Detect(device, 0, 4 * MiB, 512);
    }

    private static void NtfsBootSector(byte[] buffer, int sectorsPerCluster)
    {
        var boot = buffer.AsSpan(0, 512);
        boot[0] = 0xEB;
        boot[1] = 0x52;
        boot[2] = 0x90;
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(boot[3..]);
        BitConverter.TryWriteBytes(boot[0x0B..], (ushort)512);
        boot[0x0D] = (byte)sectorsPerCluster;
        boot[0x15] = 0xF8;
        BitConverter.TryWriteBytes(boot[0x28..], (ulong)(buffer.Length / 512));
        BitConverter.TryWriteBytes(boot[0x30..], 8UL);
        BitConverter.TryWriteBytes(boot[0x38..], 16UL);
        boot[0x40] = unchecked((byte)(sbyte)-10);
        boot[0x1FE] = 0x55;
        boot[0x1FF] = 0xAA;
    }
}
