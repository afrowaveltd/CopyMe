using Afrowave.DiskMigrator.Core.Execution;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Safety;
using Afrowave.DiskMigrator.Platform.Image;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>Destructive target preparation is tested exclusively against image files.</summary>
public static class TargetPreparationTests
{
    private const long MiB = Fixtures.MiB;
    private const long DiskBytes = 32 * MiB;

    [Test("Confirmed occupied GPT target can be prepared without touching the source")]
    public static void PrepareOccupiedTarget()
    {
        using var temp = new TempDirectory();
        var (platform, source, target) = BuildDisks(temp);
        string beforeSource = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source.ImagePath)));

        var result = TargetPreparationService.Prepare(
            platform, source.ToPhysicalDisk(), target.ToPhysicalDisk(),
            Fingerprint(platform, source), Fingerprint(platform, target));

        Assert.True(result.Success, result.Message);
        Assert.True(result.TargetModified, "partition table was erased");
        Assert.True(result.NewFingerprint is not null, "a new post-clean fingerprint is required");
        Assert.Contains(platform.WriteAttempts, 2, "only the target was opened writable");
        Assert.False(platform.WriteAttempts.Contains(1), "the source must never be opened writable");

        using var reader = platform.OpenReader(target.ToPhysicalDisk());
        Assert.True(TargetDiskInspector.Inspect(reader).IsEmpty, "target must be empty after preparation");

        string afterSource = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source.ImagePath)));
        Assert.Equal(beforeSource, afterSource, "source must be byte-for-byte intact");
    }

    [Test("Target protection is never bypassed by preparation")]
    public static void ProtectedTargetIsRefused()
    {
        using var temp = new TempDirectory();
        var (platform, source, target) = BuildDisks(temp, protectTarget: true);
        var result = TargetPreparationService.Prepare(
            platform, source.ToPhysicalDisk(), target.ToPhysicalDisk(),
            Fingerprint(platform, source), Fingerprint(platform, target));

        Assert.False(result.Success, "protected disk must be refused");
        Assert.False(result.TargetModified, "nothing was written");
        Assert.Empty(platform.WriteAttempts, "never open a protected target writable");
    }

    [Test("Changed target fingerprint prevents any write")]
    public static void ChangedTargetIsRefused()
    {
        using var temp = new TempDirectory();
        var (platform, source, target) = BuildDisks(temp);
        var sourceFingerprint = Fingerprint(platform, source);
        var expectedTarget = Fingerprint(platform, target);

        using (var fs = new FileStream(target.ImagePath, FileMode.Open, FileAccess.Write))
        {
            fs.Position = DiskBytes - 1;
            fs.WriteByte(0x3A);
        }

        var result = TargetPreparationService.Prepare(
            platform, source.ToPhysicalDisk(), target.ToPhysicalDisk(),
            sourceFingerprint, expectedTarget);

        Assert.False(result.Success, "changed target must be refused");
        Assert.False(result.TargetModified, "no destructive access");
        Assert.Empty(platform.WriteAttempts, "fingerprint mismatch must be caught before opening writable");
    }

    [Test("Writable source must block target preparation")]
    public static void UnprotectedSourceIsRefused()
    {
        using var temp = new TempDirectory();
        var (platform, source, target) = BuildDisks(temp);
        source.ReadOnly = false;

        var result = TargetPreparationService.Prepare(
            platform, source.ToPhysicalDisk(), target.ToPhysicalDisk(),
            Fingerprint(platform, source), Fingerprint(platform, target));

        Assert.False(result.Success, "source must be read-only");
        Assert.Empty(platform.WriteAttempts, "no target writes when source is unprotected");
    }

    private static (ImageDiskPlatform Platform, SimulatedDisk Source, SimulatedDisk Target)
        BuildDisks(TempDirectory temp, bool protectTarget = false)
    {
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        DiskImageBuilder.CreateGpt(sourcePath, DiskBytes)
            .AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "SOURCE", 1 * MiB, 8 * MiB)
            .Build();
        DiskImageBuilder.CreateGpt(targetPath, DiskBytes)
            .AddGptPartition(GptPartitionTypes.MicrosoftReserved, "MSR", 1 * MiB, 16 * MiB)
            .Build();

        var source = Fixtures.Simulated(1, sourcePath, DiskBytes);
        source.ReadOnly = true;
        var target = Fixtures.Simulated(2, targetPath, DiskBytes);
        var platform = new ImageDiskPlatform(
            [source, target], protectTarget ? Fixtures.Safety(2) : Fixtures.Safety());

        return (platform, source, target);
    }

    private static DiskFingerprint Fingerprint(ImageDiskPlatform platform, SimulatedDisk disk)
    {
        using var reader = platform.OpenReader(disk.ToPhysicalDisk());
        return DiskFingerprintFactory.Capture(disk.ToPhysicalDisk(), reader);
    }
}
