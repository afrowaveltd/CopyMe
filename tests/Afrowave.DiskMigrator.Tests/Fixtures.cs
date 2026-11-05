using System.Text;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Analysis;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Planning;
using Afrowave.DiskMigrator.Platform.Image;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>A scratch directory that removes itself when the test finishes.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string? name = null)
    {
        string folder = name ?? "run";
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "afrowave-disktest-" + Guid.NewGuid().ToString("N")[..10],
            folder);

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort - a leftover temp directory must never fail a test run.
        }
    }
}

/// <summary>Helpers for building disk images and analysis objects in tests.</summary>
public static class Fixtures
{
    public const long MiB = 1024L * 1024;
    public const long GiB = 1024L * 1024 * 1024;

    public static DiskIdentity Identity(int number, string path, long sizeBytes, int sectorSize = 512, string? serial = null) =>
        new(
            number,
            path,
            $"SIMULOVANÝ DISK {number}",
            serial ?? $"SN{number:0000000000}",
            sizeBytes,
            sectorSize,
            4096,
            false,
            "Virtual");

    public static SimulatedDisk Simulated(int number, string path, long sizeBytes, int sectorSize = 512, string? serial = null) =>
        new()
        {
            ImagePath = path,
            Identity = Identity(number, path, sizeBytes, sectorSize, serial),
        };

    public static SafetyEnvironment Safety(params int[] protectedDisks) =>
        new()
        {
            ProtectedDisks = protectedDisks.Select(n => new ProtectedDisk(n, $"disk {n} je chráněn testem")).ToList(),
            MountedVolumes = [],
            ExecutablePath = @"C:\Tools\AfrowaveDiskMigrator.exe",
            SystemDrive = @"C:\",
            SystemRoot = @"C:\Windows",
            // The test suite works on small image files, so the "suspiciously small disk"
            // guard is lowered. Production keeps its 1 GiB default.
            MinimumTargetBytes = 1 * MiB,
        };

    public static NtfsVolumeInfo Ntfs(long usedBytes, long totalBytes, bool dirty = false, long clusterSize = 4096)
    {
        long totalClusters = totalBytes / clusterSize;
        long usedClusters = usedBytes / clusterSize;
        return new NtfsVolumeInfo(
            BytesPerSector: 512,
            SectorsPerCluster: (int)(clusterSize / 512),
            TotalSectors: totalBytes / 512,
            TotalClusters: totalClusters,
            FreeClusters: totalClusters - usedClusters,
            IsDirty: dirty,
            NeedsLogFileReplay: dirty,
            VolumeLabel: "DATA",
            MajorVersion: 3,
            MinorVersion: 1);
    }

    /// <summary>Builds a <see cref="PartitionInfo"/> with a file system attached, for planner tests.</summary>
    public static PartitionInfo Partition(
        int number,
        PartitionTableKind kind,
        long startLba,
        long sectorCount,
        Guid typeGuid,
        Guid uniqueGuid,
        string? name,
        PartitionRole role,
        FilesystemKind fsKind,
        NtfsVolumeInfo? ntfs = null,
        bool active = false,
        byte mbrType = 0)
    {
        return new PartitionInfo
        {
            Number = number,
            TableKind = kind,
            StartLba = startLba,
            SectorCount = sectorCount,
            TypeGuid = typeGuid,
            UniqueGuid = uniqueGuid,
            MbrType = mbrType,
            Name = name,
            IsActive = active,
            Role = role,
            FileSystem = new FilesystemInfo(fsKind, fsKind.ToString(), null, 512, 0, 0, ntfs),
        };
    }

    public static DiskAnalysis Analysis(
        SimulatedDisk disk,
        PartitionTableKind kind,
        IEnumerable<PartitionInfo> partitions,
        Guid diskGuid,
        uint mbrSignature = 0xDEADBEEF,
        WindowsInstallation? windows = null,
        int sectorSize = 512) =>
        new()
        {
            Disk = disk.ToPhysicalDisk(),
            Table = new PartitionTable
            {
                Kind = kind,
                LogicalSectorSize = sectorSize,
                DiskSectors = disk.Identity.SizeBytes / sectorSize,
                Partitions = partitions.ToList(),
                DiskGuid = diskGuid,
                MbrDiskSignature = mbrSignature,
                EntryCapacity = kind == PartitionTableKind.Gpt ? 128 : 4,
            },
            Partitions = partitions.ToList(),
            Errors = [],
            Warnings = [],
            Windows = windows,
        };

    /// <summary>A canonical Windows 10/11 GPT layout: ESP + MSR + Windows + WinRE.</summary>
    public static (DiskAnalysis Analysis, SimulatedDisk Disk) WindowsGptLayout(
        string imagePath,
        long diskBytes,
        long windowsSizeBytes,
        long windowsUsedBytes,
        long espSize = 100 * MiB,
        long msrSize = 16 * MiB,
        long winReSize = 700 * MiB,
        Guid? espGuid = null,
        Guid? diskGuid = null,
        long sectorSize = 512)
    {
        long cursor = 1 * MiB;
        var partitions = new List<PartitionInfo>();
        Guid espUnique = espGuid ?? Guid.NewGuid();
        int number = 1;

        partitions.Add(Partition(number++, PartitionTableKind.Gpt, cursor / sectorSize, (espSize) / sectorSize,
            GptPartitionTypes.EfiSystem, espUnique, "EFI system partition", PartitionRole.EfiSystem, FilesystemKind.Fat32));
        cursor += espSize;
        cursor = Align(cursor, MiB);

        partitions.Add(Partition(number++, PartitionTableKind.Gpt, cursor / sectorSize, msrSize / sectorSize,
            GptPartitionTypes.MicrosoftReserved, Guid.NewGuid(), "Microsoft reserved partition", PartitionRole.MicrosoftReserved, FilesystemKind.Unknown));
        cursor += msrSize;
        cursor = Align(cursor, MiB);

        partitions.Add(Partition(number++, PartitionTableKind.Gpt, cursor / sectorSize, windowsSizeBytes / sectorSize,
            GptPartitionTypes.MicrosoftBasicData, Guid.NewGuid(), "Základní datový oddíl", PartitionRole.OperatingSystem,
            FilesystemKind.Ntfs, Ntfs(windowsUsedBytes, windowsSizeBytes)));
        cursor += windowsSizeBytes;
        cursor = Align(cursor, MiB);

        partitions.Add(Partition(number, PartitionTableKind.Gpt, cursor / sectorSize, winReSize / sectorSize,
            GptPartitionTypes.MicrosoftRecovery, Guid.NewGuid(), "Windows Recovery", PartitionRole.WindowsRecovery,
            FilesystemKind.Ntfs, Ntfs(600 * MiB, winReSize)));
        cursor += winReSize;

        var simulated = Simulated(1, imagePath, diskBytes, (int)sectorSize);
        var analysis = Analysis(
            simulated,
            PartitionTableKind.Gpt,
            partitions,
            diskGuid ?? Guid.NewGuid(),
            windows: new WindowsInstallation(3, @"\Windows", "10.0.22631", false, true),
            sectorSize: (int)sectorSize);

        return (analysis, simulated);
    }

    public static long Align(long value, long alignment) =>
        value % alignment == 0 ? value : value + (alignment - (value % alignment));

    /// <summary>Compares two byte ranges and reports the first difference.</summary>
    public static string? CompareRanges(byte[] expected, long expectedOffset, byte[] actual, long actualOffset, long length)
    {
        for (long i = 0; i < length; i++)
        {
            if (expected[expectedOffset + i] != actual[actualOffset + i])
            {
                return $"rozdíl na offsetu {i} (relativně): 0x{expected[expectedOffset + i]:X2} vs 0x{actual[actualOffset + i]:X2}";
            }
        }

        return null;
    }

    /// <summary>A reporter that records everything, for assertions on the narration.</summary>
    public sealed class RecordingReporter : IMigrationReporter
    {
        public List<string> Phases { get; } = [];

        public List<string> Details { get; } = [];

        public List<string> Warnings { get; } = [];

        public void Phase(string message) => Phases.Add(message);

        public void Detail(string message) => Details.Add(message);

        public void Notice(string message) => Details.Add(message);

        public void Warning(string message) => Warnings.Add(message);

        public void PartitionStarted(PlannedPartition partition) => Details.Add($"start #{partition.Source.Number}");

        public void Progress(PlannedPartition partition, CopyProgress progress)
        {
        }

        public void PartitionCompleted(PlannedPartition partition, long bytesWritten) =>
            Details.Add($"done #{partition.Source.Number} ({bytesWritten} B)");
    }
}
