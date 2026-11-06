using Afrowave.DiskMigrator.Core.Model;

namespace Afrowave.DiskMigrator.Core.Abstractions;

/// <summary>A physical disk as reported by the platform, before any content is read.</summary>
public sealed class PhysicalDisk
{
    public required DiskIdentity Identity { get; init; }

    /// <summary>True when the platform reports the disk as offline, read-only or otherwise unusable.</summary>
    public bool IsOffline { get; init; }

    /// <summary>True when the disk is reported read-only by the platform.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>True when the disk is a dynamic disk, Storage Spaces member or similar container.</summary>
    public bool IsContainerMember { get; init; }

    /// <summary>Reason why disk status could not be established; blocks both roles without calling it a container.</summary>
    public string? AvailabilityProblem { get; init; }

    public string? ContainerReason { get; init; }

    public override string ToString() => Identity.ShortLabel;
}

/// <summary>How a mounted volume participates in the running service operating system.</summary>
[Flags]
public enum VolumeUsage
{
    None = 0,
    /// <summary>Volume that holds %SystemRoot% / %SystemDrive% of the running OS.</summary>
    RunningOs = 1,
    /// <summary>Volume that hosts the running executable.</summary>
    ExecutableHost = 2,
    /// <summary>Volume that carries an active page file, hibernation file or crash dump.</summary>
    PagingCarrier = 4,
    /// <summary>
    /// Volume that contains a \Windows directory. Informational only: an offline Windows
    /// installation (for example the customer's disk) is exactly what this tool is meant to
    /// migrate, so this flag must never protect a disk.
    /// </summary>
    ContainsWindows = 8,
    /// <summary>Volume currently in use by another process (open handles detected).</summary>
    Busy = 16,
}

/// <summary>A volume mounted by the running operating system, mapped to its physical disk.</summary>
public sealed record MountedVolume(
    string RootPath,
    char? DriveLetter,
    int DiskNumber,
    long PartitionStartBytes,
    string FileSystemName,
    long TotalBytes,
    long FreeBytes,
    VolumeUsage Usage,
    string? Label)
{
    /// <summary>
    /// True when the volume is spread over more than one physical disk (spanned, striped or a
    /// Storage Spaces member). Such a disk cannot be handled safely on its own.
    /// </summary>
    public bool SpansMultipleDisks { get; init; }
}

/// <summary>Why a disk must never be touched.</summary>
public sealed record ProtectedDisk(int DiskNumber, string Reason);

/// <summary>
/// Facts the safety policy needs about the machine the tool is running on.
/// Collected by the platform layer so that the policy itself stays pure and testable.
/// </summary>
public sealed class SafetyEnvironment
{
    public required IReadOnlyList<ProtectedDisk> ProtectedDisks { get; init; }

    public required IReadOnlyList<MountedVolume> MountedVolumes { get; init; }

    /// <summary>Directory of the running executable (used to locate the host volume).</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>%SystemDrive% of the running OS, e.g. "C:\".</summary>
    public string? SystemDrive { get; init; }

    /// <summary>%SystemRoot%, e.g. "C:\Windows".</summary>
    public string? SystemRoot { get; init; }

    /// <summary>Disk numbers reported by the platform as unusable containers.</summary>
    public IReadOnlyList<ProtectedDisk> ContainerDisks { get; init; } = [];

    /// <summary>Set when the running OS boots from a non-standard configuration we cannot reason about.</summary>
    public string? UnknownLayoutReason { get; init; }

    /// <summary>
    /// Smallest disk that may be used as a migration target. Guards against selecting a
    /// memory card or a tiny USB stick by mistake.
    /// </summary>
    public long MinimumTargetBytes { get; init; } = Safety.DiskSafetyPolicy.DefaultMinimumTargetBytes;
}

/// <summary>Result of running an external system tool.</summary>
public sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    public string CombinedOutput
    {
        get
        {
            if (string.IsNullOrEmpty(StandardError)) return StandardOutput;
            if (string.IsNullOrEmpty(StandardOutput)) return StandardError;
            return StandardOutput + Environment.NewLine + StandardError;
        }
    }
}

/// <summary>Runs bundled Windows tools (DISM, bcdboot, bootsect, reg, mountvol...).</summary>
public interface IToolRunner
{
    /// <summary>Resolves a system tool path, or null when it is not available.</summary>
    string? ResolveTool(string fileName);

    ToolResult Run(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of looking inside a partition without modifying it.</summary>
public sealed record PartitionContentProbe(
    bool Accessible,
    string? InaccessibleReason,
    bool HasWindowsDirectory,
    string? WindowsVersion,
    bool HasHibernationFile,
    bool HasPageFile,
    bool HasWinReImage,
    string? VolumeLabel,
    IReadOnlyList<string> TopLevelEntries)
{
    /// <summary>True when the partition holds EFI boot loader files.</summary>
    public bool HasEfiBootFiles { get; init; }
}

/// <summary>
/// Mounts a partition read-only just long enough to see whether it carries an
/// operating system or a recovery image.
/// </summary>
public interface IPartitionContentProbe
{
    PartitionContentProbe Probe(int diskNumber, long partitionStartBytes);
}

/// <summary>Mounts volumes of the migration target/source to temporary drive letters.</summary>
public interface IVolumeService
{
    /// <summary>Assigns a free drive letter to the volume starting at the given byte offset of a disk.</summary>
    char MountVolume(int diskNumber, long partitionStartBytes, bool readOnly);

    /// <summary>Removes the drive letter previously assigned by <see cref="MountVolume"/>.</summary>
    void DismountVolume(char driveLetter);

    /// <summary>Asks the OS to re-read the partition table of a disk.</summary>
    void RefreshPartitionTable(int diskNumber);

    /// <summary>Waits until the OS has published volumes for the given disk, or times out.</summary>
    bool WaitForVolumes(int diskNumber, TimeSpan timeout);

    /// <summary>Free bytes available on an existing volume root such as "C:\".</summary>
    long GetFreeBytes(string rootPath);

    /// <summary>
    /// Forces the operating system to release a volume (flush + lock + dismount) so that
    /// raw sector writes below it cannot race with a mounted file system.
    /// </summary>
    bool TryDismountVolume(int diskNumber, long partitionStartBytes);

    /// <summary>Removes any drive letter or mount point of a volume on the target disk.</summary>
    void RemoveAllMountPoints(int diskNumber);
}

/// <summary>The platform under the tool: a real Windows machine, or a simulated image-disk set.</summary>
public interface IDiskPlatform
{
    string PlatformName { get; }

    /// <summary>False when the platform could not initialise (missing privilege, unsupported OS...).</summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    bool IsElevated { get; }

    IReadOnlyList<PhysicalDisk> EnumerateDisks();

    IBlockReader OpenReader(PhysicalDisk disk);

    /// <summary>Opens the device writable. Only valid for a confirmed migration target.</summary>
    IBlockDevice OpenDevice(PhysicalDisk disk);

    SafetyEnvironment GetSafetyEnvironment();

    IVolumeService Volumes { get; }

    IPartitionContentProbe ContentProbe { get; }

    IToolRunner Tools { get; }

    IWimReimageService Wim { get; }

    IBootConfigurator Boot { get; }

    /// <summary>Takes a mounted source disk away from Windows for read-only use.</summary>
    ISourceTakeoverService? SourceTakeover { get; }

    /// <summary>Best-effort: marks a whole disk read-only at the device level.</summary>
    bool TrySetDiskReadOnly(PhysicalDisk disk, bool readOnly);

    /// <summary>Re-reads the disk list (used for fingerprint re-verification).</summary>
    void Rescan();
}
