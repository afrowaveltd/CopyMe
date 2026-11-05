namespace Afrowave.DiskMigrator.Core.Abstractions;

/// <summary>Outcome of taking a source disk over for read-only use.</summary>
public sealed class SourceTakeoverReport
{
    public required bool Success { get; init; }

    /// <summary>What was actually done, in order - shown to the technician.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>Why the disk could not be taken over. Empty on success.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Things that worked but deserve attention.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>True when trying again later has a realistic chance of succeeding.</summary>
    public bool CanRetry { get; init; }
}

/// <summary>Outcome of handing the disk back to Windows.</summary>
public sealed class SourceReleaseReport
{
    public required bool Success { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>What could not be restored, with the concrete remedy.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>
/// A source disk exclusively held by this tool for reading. Disposing releases every lock and
/// restores whatever was changed, so the lifecycle is always closed by a <c>using</c> or
/// <c>finally</c> block rather than by the happy path only.
/// </summary>
public interface ISourceTakeoverSession : IDisposable
{
    /// <summary>Restores the disk to the state it was in before the takeover.</summary>
    SourceReleaseReport Release();
}

/// <summary>
/// Takes an already mounted source disk away from Windows for the duration of a migration.
///
/// The sequence matters. Pending writes must be flushed and the file system detached while the
/// disk is still writable - setting the disk read-only first would make those writes fail and
/// leave an unclean file system behind on the customer's disk.
/// </summary>
public interface ISourceTakeoverService
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>
    /// Attempts the takeover. On success <paramref name="session"/> must be released by the caller.
    /// On failure nothing is left held: locks are released and attributes are restored.
    /// </summary>
    SourceTakeoverReport TakeOver(PhysicalDisk disk, out ISourceTakeoverSession? session);
}
