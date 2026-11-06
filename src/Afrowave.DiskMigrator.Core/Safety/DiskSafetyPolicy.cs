using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Model;

namespace Afrowave.DiskMigrator.Core.Safety;

/// <summary>The verdict for one disk: may it be a source, may it be a target, and why not.</summary>
public sealed class DiskEvaluation
{
    public required PhysicalDisk Disk { get; init; }

    public bool IsProtected { get; init; }

    public required IReadOnlyList<string> ProtectionReasons { get; init; }

    public required IReadOnlyList<string> SourceBlockers { get; init; }

    public required IReadOnlyList<string> TargetBlockers { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    public bool CanBeSource => SourceBlockers.Count == 0;

    public bool CanBeTarget => TargetBlockers.Count == 0 && !IsProtected;

    public string PrimaryProtectionReason => ProtectionReasons.Count > 0 ? ProtectionReasons[0] : string.Empty;
}

/// <summary>
/// Decides which disks are untouchable and which disk may serve as source or target.
/// This type is deliberately pure: everything it needs comes from <see cref="SafetyEnvironment"/>,
/// so the decision logic is covered by unit tests rather than by experiments on real hardware.
/// </summary>
public static class DiskSafetyPolicy
{
    /// <summary>Smallest disk we are willing to write to by default; anything smaller is a mistake.</summary>
    public const long DefaultMinimumTargetBytes = 1024L * 1024 * 1024;

    public static DiskEvaluation Evaluate(PhysicalDisk disk, SafetyEnvironment environment)
    {
        var protection = new List<string>();
        var sourceBlockers = new List<string>();
        var targetBlockers = new List<string>();
        var notes = new List<string>();

        int number = disk.Identity.Number;

        foreach (var protectedDisk in environment.ProtectedDisks)
        {
            if (protectedDisk.DiskNumber == number)
            {
                protection.Add(protectedDisk.Reason);
            }
        }

        foreach (var container in environment.ContainerDisks)
        {
            if (container.DiskNumber == number)
            {
                protection.Add(container.Reason);
            }
        }

        if (disk.IsContainerMember)
        {
            protection.Add(disk.ContainerReason ?? "disk je součástí kontejneru (dynamický disk / Storage Spaces)");
        }

        var volumes = environment.MountedVolumes.Where(v => v.DiskNumber == number).ToList();

        if (volumes.Any(v => v.SpansMultipleDisks))
        {
            protection.Add("je součástí svazku rozloženého přes více fyzických disků – takový disk nelze bezpečně převzít");
        }

        bool carriesRunningOs = volumes.Any(v => v.Usage.HasFlag(VolumeUsage.RunningOs));
        bool carriesExecutable = volumes.Any(v => v.Usage.HasFlag(VolumeUsage.ExecutableHost));
        bool carriesPaging = volumes.Any(v => v.Usage.HasFlag(VolumeUsage.PagingCarrier));
        bool isBusy = volumes.Any(v => v.Usage.HasFlag(VolumeUsage.Busy));

        if (carriesRunningOs)
        {
            protection.Add("obsahuje systémový svazek běžícího servisního počítače");
        }

        if (carriesExecutable)
        {
            protection.Add("je na něm spuštěn tento nástroj");
        }

        if (carriesPaging)
        {
            protection.Add("obsahuje aktivní stránkovací nebo hibernační soubor servisního počítače");
        }

        protection = protection.Distinct().ToList();

        if (protection.Count > 0)
        {
            sourceBlockers.AddRange(protection);
            targetBlockers.AddRange(protection);
            return new DiskEvaluation
            {
                Disk = disk,
                IsProtected = true,
                ProtectionReasons = protection,
                SourceBlockers = sourceBlockers.Distinct().ToList(),
                TargetBlockers = targetBlockers.Distinct().ToList(),
                Notes = notes,
            };
        }

        // ------------------------------------------------------------ source rules

        if (!string.IsNullOrWhiteSpace(disk.AvailabilityProblem))
        {
            sourceBlockers.Add(disk.AvailabilityProblem);
            targetBlockers.Add(disk.AvailabilityProblem);
        }

        if (disk.IsOffline)
        {
            sourceBlockers.Add("disk je offline");
        }

        if (disk.Identity.SizeBytes <= 0)
        {
            sourceBlockers.Add("nepodařilo se zjistit velikost disku");
        }

        if (isBusy)
        {
            notes.Add("na disku jsou otevřené svazky, čtení může být nekonzistentní");
        }

        if (volumes.Any(v => v.Usage.HasFlag(VolumeUsage.ContainsWindows)))
        {
            notes.Add("disk obsahuje instalaci Windows – může být zdrojem migrace");
        }

        // ------------------------------------------------------------ target rules

        if (disk.IsOffline)
        {
            targetBlockers.Add("disk je offline");
        }

        if (disk.IsReadOnly)
        {
            targetBlockers.Add("disk je nastaven jako read-only");
        }

        long minimumTarget = environment.MinimumTargetBytes;
        if (disk.Identity.SizeBytes < minimumTarget)
        {
            targetBlockers.Add($"disk je menší než {minimumTarget / (1024 * 1024)} MiB");
        }

        if (isBusy)
        {
            targetBlockers.Add("na disku jsou otevřené svazky, nelze bezpečně zapisovat");
        }

        foreach (var volume in volumes)
        {
            if (volume.DriveLetter is { } letter)
            {
                notes.Add($"svazek {letter}: bude před zápisem odpojen");
            }
        }

        return new DiskEvaluation
        {
            Disk = disk,
            IsProtected = false,
            ProtectionReasons = [],
            SourceBlockers = sourceBlockers.Distinct().ToList(),
            TargetBlockers = targetBlockers.Distinct().ToList(),
            Notes = notes.Distinct().ToList(),
        };
    }

    public static IReadOnlyList<DiskEvaluation> EvaluateAll(
        IEnumerable<PhysicalDisk> disks,
        SafetyEnvironment environment) =>
        disks.Select(d => Evaluate(d, environment)).ToList();

    /// <summary>
    /// Final gate before a destructive operation. Returns every reason the pair must be refused.
    /// </summary>
    public static IReadOnlyList<string> ValidatePair(
        PhysicalDisk source,
        PhysicalDisk target,
        SafetyEnvironment environment)
    {
        var blockers = new List<string>();

        var sourceEvaluation = Evaluate(source, environment);
        var targetEvaluation = Evaluate(target, environment);

        foreach (string reason in sourceEvaluation.SourceBlockers)
        {
            blockers.Add($"Zdrojový disk nelze použít: {reason}.");
        }

        foreach (string reason in targetEvaluation.TargetBlockers)
        {
            blockers.Add($"Cílový disk nelze použít: {reason}.");
        }

        if (AreSameDisk(source.Identity, target.Identity))
        {
            blockers.Add("Zdrojový a cílový disk jsou totožné zařízení.");
        }

        return blockers;
    }

    /// <summary>
    /// Compares two disks by every attribute that survives a reboot: the device number,
    /// the serial number and the exact size. Drive letters are deliberately not used.
    /// </summary>
    public static bool AreSameDisk(DiskIdentity a, DiskIdentity b)
    {
        if (a.Number == b.Number && string.Equals(a.DevicePath, b.DevicePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool sameSerial = !string.IsNullOrWhiteSpace(a.SerialNumber)
                          && !string.IsNullOrWhiteSpace(b.SerialNumber)
                          && string.Equals(a.SerialNumber.Trim(), b.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase);

        return sameSerial && a.SizeBytes == b.SizeBytes;
    }
}
