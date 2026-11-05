using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>Read-only flag control on a physical disk, used by the source takeover.</summary>
internal interface IDiskWriteProtection
{
    bool TrySetReadOnlyState(int diskNumber, bool readOnly, out string? error);

    bool IsReadOnly(int diskNumber);
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
    private readonly WindowsVolumeService _volumes;
    private readonly IDiskWriteProtection _protection;

    public WindowsSourceTakeover(WindowsVolumeService volumes, IDiskWriteProtection protection)
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
        var allVolumes = WindowsVolumeService.Enumerate();

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

        // ---------------------------------------------------------- already read-only

        if (_protection.IsReadOnly(diskNumber))
        {
            steps.Add("Disk je už nastaven jako pouze pro čtení – není co měnit.");
            warnings.Add(
                "Disk byl read-only ještě před spuštěním nástroje. Nástroj jej takovým ponechá i po skončení.");
            session = new Session(_volumes, _protection, diskNumber, volumes, changedByUs: false);
            return Success(steps, problems, warnings);
        }

        // ---------------------------------------------------------- flush and detach

        var flushed = new List<VolumeRecord>();

        foreach (var volume in volumes)
        {
            string name = volume.RootPath ?? volume.GuidPath;

            if (!volume.HasFileSystem)
            {
                steps.Add($"{name}: svazek bez souborového systému – není co odpojovat.");
                continue;
            }

            var result = _volumes.FlushAndDismount(volume);

            if (result.Success)
            {
                flushed.Add(volume);
                steps.Add($"{name}: {result.Message}.");
            }
            else
            {
                problems.Add($"{name}: {result.Message}.");
            }
        }

        if (problems.Count > 0)
        {
            return Fail(steps, problems, warnings, canRetry: true);
        }

        // ---------------------------------------------------------- write protection

        if (!_protection.TrySetReadOnlyState(diskNumber, true, out string? error))
        {
            problems.Add(
                "Zdrojový disk se nepodařilo přepnout do režimu pouze pro čtení" +
                (string.IsNullOrWhiteSpace(error) ? "." : $" ({error})."));
            problems.Add("Migraci odmítáme – pokračovat bez ochrany zdroje by bylo riskantní.");
            return Fail(steps, problems, warnings, canRetry: true);
        }

        if (!_protection.IsReadOnly(diskNumber))
        {
            _protection.TrySetReadOnlyState(diskNumber, false, out _);
            problems.Add("Režim pouze pro čtení se nepodařilo ověřit, proto jej ruším a migraci odmítám.");
            return Fail(steps, problems, warnings, canRetry: true);
        }

        steps.Add("Zdrojový disk je nastaven jako pouze pro čtení a ověřeno.");

        session = new Session(_volumes, _protection, diskNumber, flushed, changedByUs: true);
        return Success(steps, problems, warnings);
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
        private readonly WindowsVolumeService _volumes;
        private readonly IDiskWriteProtection _protection;
        private readonly int _diskNumber;
        private readonly IReadOnlyList<VolumeRecord> _volumesToRemount;
        private readonly bool _changedByUs;
        private bool _released;

        public Session(
            WindowsVolumeService volumes,
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
                    if (_protection.IsReadOnly(_diskNumber))
                    {
                        problems.Add("Read-only příznak zdrojového disku stále platí, i když se jeho zrušení hlásilo jako úspěšné.");
                    }
                    else
                    {
                        steps.Add("Read-only příznak zdrojového disku zrušen – disk je opět zapisovatelný.");
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
