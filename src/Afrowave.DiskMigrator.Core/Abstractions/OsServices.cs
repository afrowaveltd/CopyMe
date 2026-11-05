using Afrowave.DiskMigrator.Core.Planning;

namespace Afrowave.DiskMigrator.Core.Abstractions;

/// <summary>Request to transfer one partition through a WIM image using DISM.</summary>
public sealed record WimReimageRequest(
    int SourceDiskNumber,
    long SourceStartBytes,
    long SourceSizeBytes,
    int TargetDiskNumber,
    long TargetStartBytes,
    long TargetSizeBytes,
    /// <summary>1-based index of the partition on the target disk (as written in the table).</summary>
    int TargetPartitionNumber,
    int TargetSectorSize,
    string TemporaryRootPath,
    string? VolumeLabel,
    string Description);

public sealed record WimReimageResult(bool Success, long BytesWritten, string? Error);

/// <summary>Windows-only: capture a partition into a WIM and apply it to a resized target.</summary>
public interface IWimReimageService
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    WimReimageResult Execute(
        WimReimageRequest request,
        ProgressCallback? onProgress,
        Action<string>? onOutput,
        CancellationToken cancellationToken = default);
}

/// <summary>Request to make the freshly written target disk bootable.</summary>
public sealed record BootConfigurationRequest(
    BootMode Mode,
    int TargetDiskNumber,
    /// <summary>EFI system partition (UEFI) or the active system partition (BIOS).</summary>
    long? SystemPartitionStartBytes,
    long? WindowsPartitionStartBytes,
    long? RecoveryPartitionStartBytes,
    bool RunBootsect,
    bool RegisterWinRe);

public sealed record BootConfigurationResult(
    bool Success,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Errors,
    bool BootWasActuallyTested = false);

/// <summary>Rewrites the boot configuration on the target disk using Microsoft's own tools.</summary>
public interface IBootConfigurator
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    BootConfigurationResult Configure(BootConfigurationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one migration run.</summary>
public sealed record MigrationResult(
    bool Success,
    bool CompletedAllSteps,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> VerificationNotes,
    bool BootTested,
    TimeSpan Elapsed,
    long BytesWritten)
{
    public static MigrationResult Fail(string error, IReadOnlyList<string>? warnings = null) =>
        new(false, false, [error], warnings ?? [], [], false, TimeSpan.Zero, 0);
}
