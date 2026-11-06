using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>Read-only flag control on a physical disk, used by the source takeover.</summary>
internal interface IDiskWriteProtection
{
    bool TrySetReadOnlyState(int diskNumber, bool readOnly, out string? error);

    bool TryGetAttributes(int diskNumber, out WindowsDiskAttributes attributes, out string? error);
}

/// <summary>
/// Takes an already mounted customer disk away from Windows before anything is read from it.
///
/// Why this order, and why not "just set the disk read-only":
///
///  * A disk that Windows has mounted has a live file system with possibly pending writes.
///    Setting the disk read-only first would make those writes fail mid-flight and leave the
///    file system behind in an unclean state - on the customer's disk.
///  * So first every volume is opened exclusively, locked with FSCTL_LOCK_VOLUME and detached
///    with FSCTL_DISMOUNT_VOLUME. That flushes pending writes and takes the file system out of
///    the picture while the disk is still writable.
///  * Only then is the disk switched to read-only (IOCTL_DISK_SET_DISK_ATTRIBUTES) and verified.
///
/// Volumes are deliberately NOT permanently locked and drive letters are never deleted: once the
/// disk is read-only, any remount Windows performs is necessarily read-only too, and leaving the
/// drive letters alone means there is nothing to restore afterwards. It also means a crashed
/// process leaves no lock behind - only a read-only flag that a reboot clears (Persist = 0).
/// </summary>
internal sealed class WindowsSourceTakeover : ISourceTakeoverService
{
    private readonly IWindowsTakeoverVolumes _volumes;
    private readonly IDiskWriteProtection _protection;

    public WindowsSourceTakeover(IWindowsTakeoverVolumes volumes, IDiskWriteProtection protection)
    {
        _volumes = volumes;
        _protection = protection;
    }

    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? UnavailableReason => IsAvailable ? null : "Převzetí zdrojového disku je dostupné jen na Windows.";

    public SourceTakeoverReport TakeOver(PhysicalDisk disk, out ISourceTakeoverSession? session)
    {
        session = null;

        var steps = new List<string>();
        var problems = new List<string>();
        var warnings = new List<string>();

        int diskNumber = disk.Identity.Number;
        var allVolumes = _volumes.EnumerateForTakeover();

        // ---------------------------------------------------------- refused configurations

        var spanning = allVolumes
            .Where(v => v.SpansMultipleDisks && v.Extents.Any(e => e.DiskNumber == diskNumber))
            .ToList();

        if (spanning.Count > 0)
        {
            problems.Add(
                $"Disk je součástí svazku rozloženého přes více fyzických disků " +
                $"({spanning.Count} takových svazků). Takový disk nelze bezpečně převzít ani přenést.");
            return Fail(steps, problems, warnings, canRetry: false);
        }

        var volumes = allVolumes
            .Where(v => v.Extents.Count > 0 && v.Extents.All(e => e.DiskNumber == diskNumber))
            .ToList();

        if (volumes.Count == 0)
        {
            steps.Add("Na disku nejsou připojené žádné svazky – není co odpojovat.");
        }

        // Query actual attributes. A failed writable CreateFile is NOT proof of read-only.
        if (!_protection.TryGetAttributes(diskNumber, out var original, out var readError))
        {
            problems.Add("Nelze zjistit skutečné atributy zdrojového disku: " + readError);
            return Fail(steps, problems, warnings, canRetry: true);
        }

        if (original.IsOffline)
        {
            problems.Add("Zdrojový disk je OFFLINE; nebudeme měnit jeho stav ani pokračovat.");
            return Fail(steps, problems, warnings, canRetry: false);
        }

        if (original.IsReadOnly)
        {
            steps.Add("Zdroj byl prokazatelně READ_ONLY už před spuštěním.");
            warnings.Add("Původní režim READ_ONLY zůstane zachován.");
            session = new Session(_volumes, _protection, diskNumber, [], changedByUs: false);
            return Success(steps, problems, warnings);
        }

        // Keep successful volume locks until the physical-disk attribute is verified.
        // Without the lock, Windows could remount a writable volume between operations.
        var locks = new List<ISourceVolumeLock>();
        var dismounted = new List<VolumeRecord>();
        try
        {
            foreach (var volume in volumes)
            {
                string name = volume.RootPath ?? volume.GuidPath;
                if (!volume.HasFileSystem)
                {
                    steps.Add($"{name}: bez připojeného souborového systému.");
                    continue;
                }

                var result = _volumes.FlushAndLock(volume, out var held);
                if (!result.Success || held is null)
                {
                    held?.Dispose();
                    problems.Add($"{name}: {result.Message}.");
                    break;
                }

                locks.Add(held);
                dismounted.Add(volume);
                steps.Add($"{name}: {result.Message}.");
            }

            if (problems.Count > 0)
                return Fail(steps, problems, warnings, canRetry: true);

            if (!_protection.TrySetReadOnlyState(diskNumber, true, out string? setError))
            {
                // Even a failed IOCTL may have changed the state. Query and roll back
                // rather than leaving a customer disk unexpectedly read-only.
                if (_protection.TryGetAttributes(diskNumber, out var uncertain, out _) &&
                    uncertain.IsReadOnly &&
                    !_protection.TrySetReadOnlyState(diskNumber, false, out var undoError))
                {
                    warnings.Add("POZOR: ochranu disku se nepodařilo vrátit: " + undoError);
                }
                problems.Add("Nepodařilo se zapnout ochranu zdroje: " + setError);
                return Fail(steps, problems, warnings, canRetry: true);
            }

            bool verified = _protection.TryGetAttributes(
                diskNumber, out var current, out var verifyError);
            if (!verified || !current.IsReadOnly || current.IsOffline)
            {
                if (!_protection.TrySetReadOnlyState(diskNumber, false, out var undoError))
                    warnings.Add("POZOR: Nezdařilo se vrátit ochranný atribut: " + undoError);
                problems.Add("Po zápisu nelze bezpečně potvrdit READ_ONLY bez OFFLINE: " +
                             verifyError);
                return Fail(steps, problems, warnings, canRetry: false);
            }

            steps.Add("GET_DISK_ATTRIBUTES potvrdil READ_ONLY, disk zůstal ONLINE.");
            session = new Session(_volumes, _protection, diskNumber, dismounted, changedByUs: true);
            return Success(steps, problems, warnings);
        }
        finally
        {
            for (int i = locks.Count - 1; i >= 0; i--)
                locks[i].Dispose();

            // On an unsuccessful takeover some volumes might already have been
            // dismounted. Restore their original mount points before returning.
            if (session is null)
            {
                foreach (var volume in dismounted)
                {
                    if (!_volumes.TryRemount(volume))
                        warnings.Add("Nepodařilo se znovu připojit " +
                            (volume.RootPath ?? volume.GuidPath) +
                            "; zkontrolujte jej ve Správě disků.");
                }
            }
        }
    }

    private static SourceTakeoverReport Fail(
        List<string> steps, List<string> problems, List<string> warnings, bool canRetry) =>
        new()
        {
            Success = false,
            Steps = steps,
            Problems = problems,
            Warnings = warnings,
            CanRetry = canRetry,
        };

    private static SourceTakeoverReport Success(
        List<string> steps, List<string> problems, List<string> warnings) =>
        new()
        {
            Success = true,
            Steps = steps,
            Problems = problems,
            Warnings = warnings,
        };

    /// <summary>Holds the state needed to hand the disk back exactly as it was found.</summary>
    private sealed class Session : ISourceTakeoverSession
    {
        private readonly IWindowsTakeoverVolumes _volumes;
        private readonly IDiskWriteProtection _protection;
        private readonly int _diskNumber;
        private readonly IReadOnlyList<VolumeRecord> _volumesToRemount;
        private readonly bool _changedByUs;
        private bool _released;

        public Session(
            IWindowsTakeoverVolumes volumes,
            IDiskWriteProtection protection,
            int diskNumber,
            IReadOnlyList<VolumeRecord> volumesToRemount,
            bool changedByUs)
        {
            _volumes = volumes;
            _protection = protection;
            _diskNumber = diskNumber;
            _volumesToRemount = volumesToRemount;
            _changedByUs = changedByUs;
        }

        public SourceReleaseReport Release()
        {
            if (_released)
            {
                return new SourceReleaseReport { Success = true };
            }

            _released = true;

            var steps = new List<string>();
            var problems = new List<string>();

            // Only undo what we actually changed: a disk that was read-only to begin with stays so.
            if (_changedByUs)
            {
                if (_protection.TrySetReadOnlyState(_diskNumber, false, out string? error))
                {
                    if (!_protection.TryGetAttributes(_diskNumber, out var current, out var readError))
                    {
                        problems.Add("Nelze ověřit navrácení atributů zdrojového disku: " + readError);
                    }
                    else if (current.IsReadOnly || current.IsOffline)
                    {
                        problems.Add("Zdroj po uvolnění není prokazatelně ONLINE a zapisovatelný.");
                    }
                    else
                    {
                        steps.Add("GET_DISK_ATTRIBUTES potvrdil: zdroj je ONLINE a není read-only.");
                    }
                }
                else
                {
                    problems.Add(
                        "Read-only příznak zdrojového disku se nepodařilo zrušit" +
                        (string.IsNullOrWhiteSpace(error) ? "." : $" ({error})."));
                }
            }
            else
            {
                steps.Add("Disk byl read-only už před migrací – ponechán v tomto stavu.");
            }

            // Touch the original mount points so Windows mounts the volumes again right away.
            foreach (var volume in _volumesToRemount)
            {
                string? root = volume.RootPath;
                if (root is null) continue;

                if (_volumes.TryRemount(volume))
                {
                    steps.Add($"{root} je opět dostupný.");
                }
                else
                {
                    problems.Add(
                        $"{root} zatím není dostupný. Otevřete jednotku v Průzkumníkovi; " +
                        "pokud se neobjeví, restartujte počítač.");
                }
            }

            return new SourceReleaseReport
            {
                Success = problems.Count == 0,
                Steps = steps,
                Problems = problems,
            };
        }

        public void Dispose() => Release();
    }
}
