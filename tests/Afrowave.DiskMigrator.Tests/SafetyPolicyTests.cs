using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Safety;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>
/// The rules that protect the servicing machine. These are the most important tests in the
/// suite: a mistake here means someone's working computer loses a disk.
/// </summary>
public static class SafetyPolicyTests
{
    [Test("The disk that carries the running OS must never be offered, for either role")]
    public static void RunningOsDiskProtected()
    {
        var os = Fixtures.Simulated(0, "os.img", 500 * Fixtures.GiB);
        var spare = Fixtures.Simulated(1, "spare.img", 240 * Fixtures.GiB);

        var environment = new SafetyEnvironment
        {
            ProtectedDisks = [],
            MountedVolumes =
            [
                new MountedVolume(@"C:\", 'C', 0, 1 * Fixtures.MiB, "NTFS", 500 * Fixtures.GiB, 100 * Fixtures.GiB,
                    VolumeUsage.RunningOs | VolumeUsage.PagingCarrier, "SYSTEM"),
            ],
            ExecutablePath = @"C:\Tools\AfrowaveDiskMigrator.exe",
            SystemDrive = @"C:\",
            SystemRoot = @"C:\Windows",
        };

        var evaluation = DiskSafetyPolicy.Evaluate(os.ToPhysicalDisk(), environment);
        Assert.True(evaluation.IsProtected, "systémový disk musí být chráněn");
        Assert.False(evaluation.CanBeSource, "systémový disk nesmí být zdrojem");
        Assert.False(evaluation.CanBeTarget, "systémový disk nesmí být cílem");
        Assert.NotEmpty(evaluation.ProtectionReasons, "musí být uveden důvod ochrany");

        var spareEvaluation = DiskSafetyPolicy.Evaluate(spare.ToPhysicalDisk(), environment);
        Assert.True(spareEvaluation.CanBeTarget, "volný disk musí jít použít jako cíl");
        Assert.True(spareEvaluation.CanBeSource, "volný disk musí jít použít jako zdroj");
    }

    [Test("The disk the tool runs from must be protected even without the OS on it")]
    public static void ExecutableHostDiskProtected()
    {
        var usb = Fixtures.Simulated(2, "usb.img", 32 * Fixtures.GiB);

        var environment = new SafetyEnvironment
        {
            ProtectedDisks = [],
            MountedVolumes =
            [
                new MountedVolume(@"E:\", 'E', 2, 1 * Fixtures.MiB, "NTFS", 32 * Fixtures.GiB, 30 * Fixtures.GiB,
                    VolumeUsage.ExecutableHost, "TOOLS"),
            ],
            ExecutablePath = @"E:\AfrowaveDiskMigrator.exe",
        };

        var evaluation = DiskSafetyPolicy.Evaluate(usb.ToPhysicalDisk(), environment);
        Assert.True(evaluation.IsProtected, "disk s nástrojem musí být chráněn");
        Assert.Contains("spuštěn", string.Join("; ", evaluation.ProtectionReasons), "důvod musí zmínit spuštění nástroje");
    }

    [Test("An offline Windows installation must stay selectable as a source")]
    public static void OfflineWindowsDiskIsSelectable()
    {
        // The whole point of the tool is to migrate the customer's Windows disk. A volume that
        // merely contains a \Windows directory belongs to that disk - it is not needed by the
        // running servicing system and must never be protected.
        var customer = Fixtures.Simulated(3, "customer.img", 500 * Fixtures.GiB);

        var environment = new SafetyEnvironment
        {
            ProtectedDisks = [],
            MountedVolumes =
            [
                new MountedVolume(@"F:\", 'F', 3, 1 * Fixtures.MiB, "NTFS", 500 * Fixtures.GiB, 10 * Fixtures.GiB,
                    VolumeUsage.ContainsWindows, "WIN"),
            ],
            ExecutablePath = @"E:\tool.exe",
        };

        var evaluation = DiskSafetyPolicy.Evaluate(customer.ToPhysicalDisk(), environment);
        Assert.False(evaluation.IsProtected, "offline instalace Windows nesmí být chráněná");
        Assert.True(evaluation.CanBeSource, "offline instalace Windows musí jít vybrat jako zdroj");
        Assert.Contains("Windows", string.Join(" | ", evaluation.Notes), "disk musí být v poznámce označen");
    }

    [Test("The disk of the running OS stays protected even though it also contains Windows")]
    public static void RunningOsWithWindowsStillProtected()
    {
        var system = Fixtures.Simulated(0, "system.img", 500 * Fixtures.GiB);

        var environment = new SafetyEnvironment
        {
            ProtectedDisks = [],
            MountedVolumes =
            [
                new MountedVolume(@"C:\", 'C', 0, 1 * Fixtures.MiB, "NTFS", 500 * Fixtures.GiB, 10 * Fixtures.GiB,
                    VolumeUsage.RunningOs | VolumeUsage.ContainsWindows, "SYSTEM"),
            ],
            ExecutablePath = @"E:\tool.exe",
            SystemDrive = @"C:\",
        };

        var evaluation = DiskSafetyPolicy.Evaluate(system.ToPhysicalDisk(), environment);
        Assert.True(evaluation.IsProtected, "systémový disk musí zůstat chráněn");
        Assert.False(evaluation.CanBeSource, "systémový disk nesmí být zdrojem");
    }

    [Test("A disk listed as protected by the platform is refused for both roles")]
    public static void ExplicitlyProtectedDisk()
    {
        var disk = Fixtures.Simulated(7, "seven.img", 100 * Fixtures.GiB);
        var evaluation = DiskSafetyPolicy.Evaluate(disk.ToPhysicalDisk(), Fixtures.Safety(7));

        Assert.True(evaluation.IsProtected, "explicitně chráněný disk");
        Assert.Contains("chráněn testem", string.Join("; ", evaluation.ProtectionReasons), "důvod z platformy");
    }

    [Test("A volume that is busy blocks the disk as a target")]
    public static void BusyVolumeBlocksTarget()
    {
        var disk = Fixtures.Simulated(4, "four.img", 100 * Fixtures.GiB);

        var environment = new SafetyEnvironment
        {
            ProtectedDisks = [],
            MountedVolumes =
            [
                new MountedVolume(@"G:\", 'G', 4, 1 * Fixtures.MiB, "NTFS", 100 * Fixtures.GiB, 10 * Fixtures.GiB,
                    VolumeUsage.Busy, "BUSY"),
            ],
            ExecutablePath = @"E:\tool.exe",
        };

        var evaluation = DiskSafetyPolicy.Evaluate(disk.ToPhysicalDisk(), environment);
        Assert.False(evaluation.CanBeTarget, "zaneprázdněný disk nesmí být cílem");
        Assert.Contains("otevřené svazky", string.Join("; ", evaluation.TargetBlockers), "důvod musí být vysvětlen");
    }

    [Test("A container disk (dynamic disk / Storage Spaces) is protected")]
    public static void ContainerDiskProtected()
    {
        var disk = Fixtures.Simulated(5, "five.img", 100 * Fixtures.GiB);
        disk.ContainerMember = true;
        disk.ContainerReason = "člen Storage Spaces";

        var evaluation = DiskSafetyPolicy.Evaluate(disk.ToPhysicalDisk(), Fixtures.Safety());
        Assert.True(evaluation.IsProtected, "kontejnerový disk musí být chráněn");
    }

    [Test("Offline and read-only targets are refused")]
    public static void OfflineAndReadOnly()
    {
        var offline = Fixtures.Simulated(6, "six.img", 100 * Fixtures.GiB);
        offline.Offline = true;
        var readOnly = Fixtures.Simulated(8, "eight.img", 100 * Fixtures.GiB);
        readOnly.ReadOnly = true;

        var environment = Fixtures.Safety();

        Assert.False(DiskSafetyPolicy.Evaluate(offline.ToPhysicalDisk(), environment).CanBeTarget, "offline disk nesmí být cílem");
        Assert.False(DiskSafetyPolicy.Evaluate(readOnly.ToPhysicalDisk(), environment).CanBeTarget, "read-only disk nesmí být cílem");
    }

    [Test("The same physical disk can never be both source and target")]
    public static void SameDiskDetection()
    {
        var a = Fixtures.Identity(1, @"\\.\PhysicalDrive1", 240 * Fixtures.GiB, serial: "WD-SERIAL-1");
        var b = Fixtures.Identity(5, @"\\.\PhysicalDrive5", 240 * Fixtures.GiB, serial: "wd-serial-1");
        var c = Fixtures.Identity(2, @"\\.\PhysicalDrive2", 240 * Fixtures.GiB, serial: "WD-SERIAL-2");

        Assert.True(DiskSafetyPolicy.AreSameDisk(a, b), "stejné sériové číslo a velikost = stejný disk");
        Assert.False(DiskSafetyPolicy.AreSameDisk(a, c), "různá sériová čísla = jiné disky");
        Assert.True(DiskSafetyPolicy.AreSameDisk(a, a), "disk je sám se sebou totožný");
    }

    [Test("Validating a pair reports every blocker at once")]
    public static void PairValidation()
    {
        var source = Fixtures.Simulated(0, "os.img", 500 * Fixtures.GiB);
        var target = Fixtures.Simulated(0, "os.img", 500 * Fixtures.GiB);

        var environment = new SafetyEnvironment
        {
            ProtectedDisks = [new ProtectedDisk(0, "systémový disk servisního počítače")],
            MountedVolumes = [],
            ExecutablePath = @"E:\tool.exe",
        };

        var blockers = DiskSafetyPolicy.ValidatePair(source.ToPhysicalDisk(), target.ToPhysicalDisk(), environment);
        Assert.NotEmpty(blockers, "dvojice musí být odmítnuta");
        Assert.True(blockers.Count >= 2, "musí být hlášen zdroj i cíl");
    }
}
