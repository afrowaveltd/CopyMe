using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Safety;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Platform.Windows;

namespace Afrowave.DiskMigrator.Tests;

public static class WindowsDiskInventoryTests
{
    [Test("A stale offline-Windows pagefile path does not match a different active NT volume")]
    public static void OfflineWindowsPagefileNotActive()
    {
        Assert.True(
            WindowsActivePaging.IsPageFileOnVolume(
                @"\Device\HarddiskVolume2\pagefile.sys", @"\Device\HarddiskVolume2", @"C:\"),
            "active pagefile on the servicing system is matched");

        Assert.False(
            WindowsActivePaging.IsPageFileOnVolume(
                @"\Device\HarddiskVolume2\pagefile.sys", @"\Device\HarddiskVolume9", @"F:\"),
            "customer Windows pagefile.sys must not count as active paging");

        Assert.False(
            WindowsActivePaging.IsPageFileOnVolume(
                @"\Device\HarddiskVolume2\hiberfil.sys", @"\Device\HarddiskVolume9", @"F:\"),
            "hibernation files on other volumes do not protect them");
    }

    [Test("Active paging detection supports DOS paths and enforces full volume prefix")]
    public static void PagefilePathMatching()
    {
        Assert.True(
            WindowsActivePaging.IsPageFileOnVolume(@"F:\pagefile.sys", null, @"F:\"),
            "DOS-letter active pagefile");
        Assert.True(
            WindowsActivePaging.IsPageFileOnVolume(@"\??\C:\pagefile.sys", null, @"C:\"),
            "NT-prefixed DOS-letter active pagefile");
        Assert.False(
            WindowsActivePaging.IsPageFileOnVolume(
                @"\Device\HarddiskVolume21\pagefile.sys", @"\Device\HarddiskVolume2", null),
            "volume 21 must not match volume 2");
    }

    [Test("Get-Disk fallback validates exact requested disk number and booleans")]
    public static void GetDiskFallbackParser()
    {
        Assert.True(
            WindowsDiskStorageCmdlets.TryParseStatus("3|False|False\r\n", 3, out var normal),
            "valid writable disk");
        Assert.False(normal.IsReadOnly, "writable");
        Assert.False(normal.IsOffline, "online");

        Assert.True(
            WindowsDiskStorageCmdlets.TryParseStatus("3|False|True\n", 3, out var readOnly),
            "valid read-only disk");
        Assert.True(readOnly.IsReadOnly, "flag is READ_ONLY, not OFFLINE");
        Assert.False(readOnly.IsOffline, "online");

        Assert.True(
            WindowsDiskStorageCmdlets.TryParseStatus("3|True|False", 3, out var offline),
            "valid offline disk");
        Assert.True(offline.IsOffline, "offline state preserved");
    }

    [Test("Get-Disk fallback rejects unknown, ambiguous and mismatched output")]
    public static void GetDiskFallbackRejectsUntrustedOutput()
    {
        Assert.False(WindowsDiskStorageCmdlets.TryParseStatus("7|False|False", 3, out _),
            "wrong disk number must be rejected");
        Assert.False(WindowsDiskStorageCmdlets.TryParseStatus("3|False|Nope", 3, out _),
            "invalid boolean must be rejected");
        Assert.False(WindowsDiskStorageCmdlets.TryParseStatus("3|False|False\n4|False|False", 3, out _),
            "multiple disk records must be rejected");
        Assert.False(WindowsDiskStorageCmdlets.TryParseStatus("", 3, out _),
            "empty output must be rejected");
    }

    [Test("An unverified disk is unavailable, not a protected Storage Spaces member")]
    public static void UnverifiedAttributesRemainBlockedWithPreciseReason()
    {
        var disk = new PhysicalDisk
        {
            Identity = Fixtures.Identity(7, "physical7", 128 * Fixtures.GiB),
            AvailabilityProblem = "IOCTL ani Get-Disk nevrátil atributy"
        };
        var evaluation = DiskSafetyPolicy.Evaluate(disk, Fixtures.Safety());
        Assert.False(evaluation.IsProtected, "not a falsely claimed OS/container disk");
        Assert.False(evaluation.CanBeSource, "cannot read source without verified state");
        Assert.False(evaluation.CanBeTarget, "cannot write target without verified state");
        Assert.Contains("Get-Disk", string.Join("; ", evaluation.TargetBlockers), "diagnostic displayed");
    }

    [Test("USB bus does not bypass running OS or executable hosting protections")]
    public static void UsbNeverOverridesRealProtection()
    {
        var usb = new PhysicalDisk
        {
            Identity = new DiskIdentity(8, "usb8", "USB disk", "S8", 128 * Fixtures.GiB,
                512, 4096, true, "USB")
        };
        var environment = new SafetyEnvironment
        {
            ProtectedDisks = [],
            MountedVolumes = [
                new MountedVolume(@"E:\", 'E', 8, Fixtures.MiB, "NTFS",
                    128 * Fixtures.GiB, 100 * Fixtures.GiB,
                    VolumeUsage.ExecutableHost, "TOOLS")
            ],
            ExecutablePath = @"E:\CopyMe.exe"
        };
        var evaluation = DiskSafetyPolicy.Evaluate(usb, environment);
        Assert.True(evaluation.IsProtected, "USB tools disk must stay protected");
    }
}
