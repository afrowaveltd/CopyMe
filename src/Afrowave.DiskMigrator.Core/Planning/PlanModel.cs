using Afrowave.DiskMigrator.Core.Model;

namespace Afrowave.DiskMigrator.Core.Planning;

/// <summary>How the content of one partition is transferred to the target.</summary>
public enum CopyStrategy
{
    /// <summary>Nothing to copy - the partition is recreated as an empty container (MSR).</summary>
    RecreateEmpty = 0,

    /// <summary>Sector-by-sector copy. Preserves everything byte for byte.</summary>
    RawClone = 1,

    /// <summary>
    /// NTFS-aware copy: all allocated clusters plus the boot sectors are copied to a
    /// partition of identical size. Byte-exact metadata, but skips gigabytes of free space.
    /// </summary>
    NtfsAllocationClone = 2,

    /// <summary>
    /// Capture to a WIM image with DISM, then apply to a freshly formatted volume.
    /// The only strategy that can shrink a partition.
    /// </summary>
    WimReimage = 3,
}

/// <summary>One partition of the proposed target layout.</summary>
public sealed class PlannedPartition
{
    public required int Index { get; init; }

    public required PartitionInfo Source { get; init; }

    public required PartitionRole Role { get; init; }

    public required CopyStrategy Strategy { get; init; }

    /// <summary>Byte offset of the copy source on the source device.</summary>
    public required long SourceStartByte { get; init; }

    public required long SourceSizeBytes { get; init; }

    /// <summary>Byte offset of the destination on the target device.</summary>
    public required long TargetStartByte { get; init; }

    public required long TargetSizeBytes { get; init; }

    public required Guid TargetTypeGuid { get; init; }

    public required Guid TargetUniqueGuid { get; init; }

    public required byte TargetMbrType { get; init; }

    public string? TargetName { get; init; }

    public ulong TargetAttributes { get; init; }

    public bool TargetIsActive { get; init; }

    public bool TargetIsLogical { get; init; }

    public long SourceEndByte => SourceStartByte + SourceSizeBytes - 1;

    public long TargetEndByte => TargetStartByte + TargetSizeBytes - 1;

    public bool IsResized => TargetSizeBytes != SourceSizeBytes;

    public bool IsShrunk => TargetSizeBytes < SourceSizeBytes;

    public FilesystemInfo? FileSystem => Source.FileSystem;

    /// <summary>Bytes that will physically be written to the target for this partition.</summary>
    public long EstimatedWriteBytes => Strategy switch
    {
        CopyStrategy.RecreateEmpty => 0,
        // Only the allocated clusters are moved.
        CopyStrategy.NtfsAllocationClone => Source.FileSystem?.Ntfs is { } ntfs
            ? Math.Min(SourceSizeBytes, ntfs.UsedBytes + (2 * 1024 * 1024))
            : SourceSizeBytes,
        // The image is captured and then applied, so the payload is roughly the used data.
        CopyStrategy.WimReimage => Source.FileSystem?.Ntfs is { } wim
            ? Math.Min(SourceSizeBytes, wim.UsedBytes + (2 * 1024 * 1024))
            : SourceSizeBytes,
        _ => SourceSizeBytes,
    };

    /// <summary>Very short strategy label for the progress line.</summary>
    public string StrategyShortDescription => Strategy switch
    {
        CopyStrategy.RecreateEmpty => "prázdný",
        CopyStrategy.RawClone => "sektorově",
        CopyStrategy.NtfsAllocationClone => "chytrá NTFS",
        CopyStrategy.WimReimage => "WIM (DISM)",
        _ => Strategy.ToString(),
    };

    public string StrategyDescription => Strategy switch
    {
        CopyStrategy.RecreateEmpty => "vytvořit prázdný oddíl",
        CopyStrategy.RawClone => "sektorová kopie 1:1",
        CopyStrategy.NtfsAllocationClone => "chytrá kopie NTFS (jen obsazené shluky)",
        CopyStrategy.WimReimage => "obraz WIM (DISM) – umožní zmenšení",
        _ => Strategy.ToString(),
    };
}

public enum BootMode
{
    None = 0,
    Uefi = 1,
    Bios = 2,
}

/// <summary>How the target disk will be made bootable.</summary>
public sealed class BootPlan
{
    public required BootMode Mode { get; init; }

    /// <summary>Index into <see cref="MigrationPlan.Partitions"/> of the EFI system partition.</summary>
    public int? EfiSystemIndex { get; init; }

    public int? WindowsIndex { get; init; }

    public int? RecoveryIndex { get; init; }

    public int? ActiveIndex { get; init; }

    public bool RequiresBcdboot { get; init; }

    public bool RequiresBootsect { get; init; }

    public bool RequiresWinReRegistration { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The complete, reviewable migration plan.</summary>
public sealed class MigrationPlan
{
    public required DiskIdentity SourceDisk { get; init; }

    public required DiskIdentity TargetDisk { get; init; }

    public required PartitionTable SourceTable { get; init; }

    public required IReadOnlyList<PlannedPartition> Partitions { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }

    public required BootPlan Boot { get; init; }

    public required Guid TargetDiskGuid { get; init; }

    public required uint TargetMbrSignature { get; init; }

    public required long TargetSizeBytes { get; init; }

    public required int TargetSectorSize { get; init; }

    public required long TargetUsableBytes { get; init; }

    public required long SourceSizeBytes { get; init; }

    /// <summary>Set when intelligent copying has to go through a temporary WIM file.</summary>
    public long RequiredTemporaryBytes { get; init; }

    public string? TemporaryRootPath { get; init; }

    public required FilesystemKind TemporaryFilesystem { get; init; }

    /// <summary>What the target disk inspection found, for the review screen.</summary>
    public string TargetSummary { get; init; } = string.Empty;

    public bool PreserveDiskIdentifiers { get; init; }

    public bool VerificationEnabled { get; init; }

    public IReadOnlyList<string> Preparation { get; init; } = [];

    public bool IsFeasible => Errors.Count == 0;

    public long TotalBytesToWrite => Partitions.Sum(p => p.EstimatedWriteBytes);

    public long PeakTemporaryBytes => RequiredTemporaryBytes;

    public bool UsesIntelligentCopy => Partitions.Any(p =>
        p.Strategy is CopyStrategy.NtfsAllocationClone or CopyStrategy.WimReimage);

    public bool UsesSectorCopy => Partitions.Any(p => p.Strategy == CopyStrategy.RawClone);

    /// <summary>Sum of the requested partition sizes, used for the fit check in the UI.</summary>
    public long TotalTargetPartitionBytes => Partitions.Sum(p => p.TargetSizeBytes);

    public long FreeBytesLeftOnTarget => TargetUsableBytes - TotalTargetPartitionBytes;
}
