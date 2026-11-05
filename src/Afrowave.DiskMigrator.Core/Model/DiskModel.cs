namespace Afrowave.DiskMigrator.Core.Model;

public enum PartitionTableKind
{
    Unknown = 0,
    Mbr = 1,
    Gpt = 2,
}

/// <summary>Semantic role of a partition, derived from its type and/or contents.</summary>
public enum PartitionRole
{
    Unknown = 0,
    EfiSystem,
    BiosBoot,
    MicrosoftReserved,
    WindowsRecovery,
    /// <summary>Partition that holds a bootable operating system.</summary>
    OperatingSystem,
    /// <summary>Ordinary data partition.</summary>
    Basic,
    /// <summary>Container we refuse to reinterpret (LVM, Storage Spaces, dynamic disks...).</summary>
    Container,
}

public enum FilesystemKind
{
    Unknown = 0,
    Ntfs,
    ReFs,
    Fat12,
    Fat16,
    Fat32,
    ExFat,
    Ext2To4,
    Btrfs,
    Xfs,
    Apfs,
    HfsPlus,
    BitLocker,
    Luks,
    LinuxSwap,
    Iso9660,
    Udf,
    Unformatted,
}

/// <summary>Detailed NTFS geometry collected from the volume's own metadata files.</summary>
public sealed record NtfsVolumeInfo(
    int BytesPerSector,
    int SectorsPerCluster,
    long TotalSectors,
    long TotalClusters,
    long FreeClusters,
    bool IsDirty,
    bool NeedsLogFileReplay,
    string? VolumeLabel,
    int MajorVersion,
    int MinorVersion)
{
    public int ClusterSize => BytesPerSector * SectorsPerCluster;

    public long UsedClusters => TotalClusters - FreeClusters;

    /// <summary>Bytes actually occupied by file data and metadata (rounded up to clusters).</summary>
    public long UsedBytes => UsedClusters * (long)ClusterSize;

    public long TotalBytes => TotalClusters * (long)ClusterSize;
}

public sealed record FilesystemInfo(
    FilesystemKind Kind,
    string DisplayName,
    string? Label,
    int BytesPerSector,
    long TotalSectors,
    long OffsetWithinPartition,
    NtfsVolumeInfo? Ntfs)
{
    public bool IsNtfs => Kind == FilesystemKind.Ntfs;

    public bool IsEncrypted => Kind is FilesystemKind.BitLocker or FilesystemKind.Luks;

    /// <summary>File systems whose content this tool can copy intelligently (not blind sector copy).</summary>
    public bool SupportsIntelligentCopy => Kind == FilesystemKind.Ntfs && Ntfs is not null;
}

/// <summary>One partition as read from the on-disk partition table.</summary>
public sealed class PartitionInfo
{
    /// <summary>1-based index in disk order (primary entries first, then logical ones).</summary>
    public required int Number { get; init; }

    public required PartitionTableKind TableKind { get; init; }

    public required long StartLba { get; init; }

    public required long SectorCount { get; init; }

    public long EndLba => StartLba + SectorCount - 1;

    /// <summary>GPT partition type GUID, or <see cref="Guid.Empty"/> for MBR tables.</summary>
    public Guid TypeGuid { get; init; }

    /// <summary>GPT unique partition GUID, or <see cref="Guid.Empty"/> for MBR tables.</summary>
    public Guid UniqueGuid { get; init; }

    /// <summary>MBR partition type byte, or 0 for GPT tables.</summary>
    public byte MbrType { get; init; }

    /// <summary>GPT partition name (may be null/empty).</summary>
    public string? Name { get; init; }

    /// <summary>GPT attribute flags.</summary>
    public ulong Attributes { get; init; }

    /// <summary>MBR active ("bootable") flag.</summary>
    public bool IsActive { get; init; }

    /// <summary>True for a logical partition inside an extended MBR partition.</summary>
    public bool IsLogical { get; init; }

    public Guid? LdmContainerGuid { get; init; }

    public PartitionRole Role { get; set; } = PartitionRole.Unknown;

    public FilesystemInfo? FileSystem { get; set; }

    public bool IsMicrosoftReserved => TableKind == PartitionTableKind.Gpt && TypeGuid == Partitioning.GptPartitionTypes.MicrosoftReserved;

    public bool IsEfiSystem =>
        (TableKind == PartitionTableKind.Gpt && TypeGuid == Partitioning.GptPartitionTypes.EfiSystem) ||
        (TableKind == PartitionTableKind.Mbr && MbrType == Partitioning.MbrPartitionTypes.EfiSystem);

    public bool IsBiosBoot => TableKind == PartitionTableKind.Gpt && TypeGuid == Partitioning.GptPartitionTypes.BiosBoot;

    public bool IsExtendedContainer => TableKind == PartitionTableKind.Mbr && Partitioning.MbrPartitionTypes.IsExtended(MbrType);

    public string TypeDescription => TableKind == PartitionTableKind.Gpt
        ? Partitioning.GptPartitionTypes.Describe(TypeGuid)
        : Partitioning.MbrPartitionTypes.Describe(MbrType);

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name!;
            if (FileSystem?.Label is { Length: > 0 } label) return label;
            return TableKind == PartitionTableKind.Gpt && TypeGuid != Guid.Empty
                ? Partitioning.GptPartitionTypes.Describe(TypeGuid)
                : Partitioning.MbrPartitionTypes.Describe(MbrType);
        }
    }

    public override string ToString() => $"#{Number} LBA {StartLba}..{EndLba} ({TypeDescription})";
}

/// <summary>Complete partition table of one disk, plus geometry needed to interpret it.</summary>
public sealed class PartitionTable
{
    public required PartitionTableKind Kind { get; init; }

    public required int LogicalSectorSize { get; init; }

    public required long DiskSectors { get; init; }

    public required IReadOnlyList<PartitionInfo> Partitions { get; init; }

    /// <summary>GPT disk GUID (zero for MBR).</summary>
    public Guid DiskGuid { get; init; }

    /// <summary>MBR disk signature (0 for GPT unless a protective MBR carries one).</summary>
    public uint MbrDiskSignature { get; init; }

    /// <summary>Set when the MBR holds both a protective entry and legacy entries.</summary>
    public bool IsHybridMbr { get; init; }

    /// <summary>Number of MBR partition slots (4 for MBR, 128 for GPT).</summary>
    public int EntryCapacity { get; init; }

    public long DiskBytes => DiskSectors * LogicalSectorSize;

    public IEnumerable<PartitionInfo> Ordered => Partitions.OrderBy(p => p.StartLba);

    public override string ToString() => $"{Kind}, {Partitions.Count} oddílů, {DiskSectors} LBA";
}

/// <summary>Stable identity of a physical disk, independent of drive letters or volume names.</summary>
public sealed record DiskIdentity(
    int Number,
    string DevicePath,
    string Model,
    string SerialNumber,
    long SizeBytes,
    int LogicalSectorSize,
    int PhysicalSectorSize,
    bool IsRemovable,
    string BusType)
{
    /// <summary>Short stable label used in confirmations and logs.</summary>
    public string ShortLabel
    {
        get
        {
            string model = string.IsNullOrWhiteSpace(Model) ? "neznámý model" : Model;
            if (model.Length > 40) model = model[..40];
            return $"Disk {Number}: {model}";
        }
    }
}
