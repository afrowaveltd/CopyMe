using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Filesystems;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;

namespace Afrowave.DiskMigrator.Core.Analysis;

public sealed record WindowsInstallation(
    int PartitionNumber,
    string WindowsPath,
    string? Version,
    bool HasHibernationFile,
    bool HasPageFile);

/// <summary>Everything learned about a disk by reading it. Nothing is ever written here.</summary>
public sealed class DiskAnalysis
{
    public required PhysicalDisk Disk { get; init; }

    public PartitionTable? Table { get; init; }

    public required IReadOnlyList<PartitionInfo> Partitions { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public WindowsInstallation? Windows { get; init; }

    public bool IsUsable => Table is not null && Errors.Count == 0;

    public bool HasOperatingSystem => Windows is not null;

    public long UsedBytes => Partitions.Sum(p => p.FileSystem?.Ntfs?.UsedBytes ?? p.FileSystem?.TotalSectors * p.FileSystem?.BytesPerSector ?? 0);

    public bool ContainsContainerPartition => Partitions.Any(p => p.Role == PartitionRole.Container);
}

/// <summary>
/// Builds a <see cref="DiskAnalysis"/> for one disk: partition table, file systems,
/// partition roles and - when the platform can mount volumes - whether an operating
/// system lives on it.
/// </summary>
public static class DiskAnalyzer
{
    /// <summary>Partitions smaller than this are never worth mounting just to look inside.</summary>
    private const long MinimumProbeSize = 64L * 1024 * 1024;

    public static DiskAnalysis Analyze(PhysicalDisk disk, IDiskPlatform platform, bool probeContents)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var partitions = new List<PartitionInfo>();

        using var reader = platform.OpenReader(disk);
        var read = PartitionTableReader.Read(reader);
        errors.AddRange(read.Errors);
        warnings.AddRange(read.Warnings);

        if (read.Table is null)
        {
            return new DiskAnalysis
            {
                Disk = disk,
                Table = null,
                Partitions = [],
                Errors = errors,
                Warnings = warnings,
            };
        }

        var table = read.Table;
        int sectorSize = table.LogicalSectorSize;

        foreach (var partition in table.Partitions)
        {
            long offset = partition.StartLba * sectorSize;
            long size = partition.SectorCount * sectorSize;

            var filesystem = FilesystemDetector.Detect(reader, offset, size, sectorSize);

            if (filesystem.Kind == FilesystemKind.Ntfs)
            {
                try
                {
                    using var ntfs = NtfsVolume.Open(reader, offset, size);
                    filesystem = filesystem with { Ntfs = ntfs.Info, Label = ntfs.Info.VolumeLabel };

                    if (ntfs.Info.IsDirty)
                    {
                        warnings.Add(
                            $"Oddíl #{partition.Number} ({partition.DisplayName}) není čistě odpojen (NTFS dirty bit). " +
                            "Systém na cílovém disku pravděpodobně při prvním startu spustí kontrolu disku.");
                    }
                }
                catch (Exception ex) when (ex is NtfsAnalysisException or IOException)
                {
                    warnings.Add(
                        $"Oddíl #{partition.Number} vypadá jako NTFS, ale metadata nelze bezpečně přečíst ({ex.Message}). " +
                        "Bude použit sektorová kopie 1:1.");
                }
            }

            partition.FileSystem = filesystem;
            partition.Role = DetermineRole(partition, table);
            partitions.Add(partition);

            if (filesystem.IsEncrypted)
            {
                warnings.Add(
                    $"Oddíl #{partition.Number} ({partition.DisplayName}) je zašifrovaný ({filesystem.DisplayName}). " +
                    "Obsah bude přenesen sektorovou kopií 1:1, takže zůstane zašifrovaný a odemkne se stejným klíčem. " +
                    "Zmenšit zašifrovaný oddíl tento nástroj neumí – cílový disk musí být dost velký.");
            }
        }

        WindowsInstallation? windows = null;

        if (probeContents && platform.ContentProbe is not null)
        {
            windows = ProbeForOperatingSystem(platform, disk, partitions, table, warnings);
        }

        if (partitions.Count > 0 && partitions.All(p => p.Role == PartitionRole.Unknown))
        {
            warnings.Add("Nepodařilo se určit roli žádného oddílu.");
        }

        return new DiskAnalysis
        {
            Disk = disk,
            Table = table,
            Partitions = partitions,
            Errors = errors,
            Warnings = warnings,
            Windows = windows,
        };
    }

    private static PartitionRole DetermineRole(PartitionInfo partition, PartitionTable table)
    {
        if (partition.IsMicrosoftReserved) return PartitionRole.MicrosoftReserved;
        if (partition.IsEfiSystem) return PartitionRole.EfiSystem;
        if (partition.IsBiosBoot) return PartitionRole.BiosBoot;

        if (table.Kind == PartitionTableKind.Gpt)
        {
            if (partition.TypeGuid == GptPartitionTypes.MicrosoftRecovery) return PartitionRole.WindowsRecovery;
            if (GptPartitionTypes.IsContainerType(partition.TypeGuid)) return PartitionRole.Container;
        }
        else
        {
            if (partition.MbrType == MbrPartitionTypes.WindowsRe) return PartitionRole.WindowsRecovery;
            if (partition.MbrType is MbrPartitionTypes.Ldm or 0x42) return PartitionRole.Container;
        }

        if (partition.FileSystem is { IsEncrypted: true }) return PartitionRole.Basic;

        return PartitionRole.Unknown;
    }

    private static WindowsInstallation? ProbeForOperatingSystem(
        IDiskPlatform platform,
        PhysicalDisk disk,
        List<PartitionInfo> partitions,
        PartitionTable table,
        List<string> warnings)
    {
        int sectorSize = table.LogicalSectorSize;
        WindowsInstallation? found = null;

        foreach (var partition in partitions.OrderBy(p => p.StartLba))
        {
            long size = partition.SectorCount * sectorSize;
            if (size < MinimumProbeSize) continue;

            var filesystem = partition.FileSystem;
            if (filesystem is null) continue;

            bool worthProbing = filesystem.Kind is FilesystemKind.Ntfs or FilesystemKind.Fat32 or FilesystemKind.ExFat;
            if (!worthProbing) continue;

            PartitionContentProbe probe;
            try
            {
                probe = platform.ContentProbe.Probe(disk.Identity.Number, partition.StartLba * sectorSize);
            }
            catch (Exception ex)
            {
                warnings.Add($"Obsah oddílu #{partition.Number} nelze prozkoumat: {ex.Message}");
                continue;
            }

            if (!probe.Accessible)
            {
                if (filesystem.Kind == FilesystemKind.Ntfs)
                {
                    warnings.Add(
                        $"Do oddílu #{partition.Number} nelze nahlédnout ({probe.InaccessibleReason ?? "neznámý důvod"}). " +
                        "Případný operační systém zde nebude rozpoznán.");
                }

                if (partition.Role == PartitionRole.Unknown)
                {
                    partition.Role = PartitionRole.Basic;
                }

                continue;
            }

            if (probe.HasWindowsDirectory)
            {
                partition.Role = PartitionRole.OperatingSystem;
                found ??= new WindowsInstallation(
                    partition.Number,
                    @"\Windows",
                    probe.WindowsVersion,
                    probe.HasHibernationFile,
                    probe.HasPageFile);

                if (probe.HasHibernationFile)
                {
                    warnings.Add(
                        $"Windows na oddílu #{partition.Number} je v režimu hibernace nebo rychlého startu. " +
                        "Před migrací doporučujeme zdrojový disk řádně vypnout (ne hibernovat).");
                }

                continue;
            }

            if (probe.HasEfiBootFiles && partition.Role != PartitionRole.EfiSystem)
            {
                partition.Role = PartitionRole.EfiSystem;
                warnings.Add(
                    $"Oddíl #{partition.Number} obsahuje zaváděcí soubory UEFI – považuji jej za EFI System Partition.");
                continue;
            }

            if (probe.HasWinReImage && partition.Role != PartitionRole.WindowsRecovery)
            {
                partition.Role = PartitionRole.WindowsRecovery;
                continue;
            }

            if (partition.Role == PartitionRole.Unknown)
            {
                partition.Role = PartitionRole.Basic;
            }
        }

        if (found is not null)
        {
            foreach (var partition in partitions)
            {
                if (partition.Role == PartitionRole.Unknown)
                {
                    partition.Role = PartitionRole.Basic;
                }
            }
        }

        return found;
    }
}
