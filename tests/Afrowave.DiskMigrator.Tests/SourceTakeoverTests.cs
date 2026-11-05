using System.Security.Cryptography;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Analysis;
using Afrowave.DiskMigrator.Core.Execution;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Planning;
using Afrowave.DiskMigrator.Platform.Image;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>
/// Taking an already mounted customer disk over for read-only use, and handing it back.
///
/// The Windows specific sequence (FSCTL_LOCK_VOLUME, FSCTL_DISMOUNT_VOLUME,
/// IOCTL_DISK_SET_DISK_ATTRIBUTES) cannot run here. What is covered: the lifecycle contract
/// (attempt, refusal, retry, release), the executor refusing to run on an unprotected source,
/// and the end-to-end composition the command line actually performs.
/// </summary>
public static class SourceTakeoverTests
{
    private const long MiB = Fixtures.MiB;

    [Test("A mounted source disk can be taken over and reports what was done")]
    public static void TakeOverSucceeds()
    {
        var (platform, source, _) = BuildPlatform();
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;

        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk());

        Assert.True(outcome.Success, "převzetí musí uspět: " + string.Join(" | ", outcome.Problems));
        Assert.NotEqual<ISourceTakeoverSession?>(null, outcome.Session, "musí vzniknout relace");
        Assert.NotEmpty(outcome.Steps, "musí být vidět, co se provedlo");
        Assert.Contains(takeover.TakeOverCalls, source.Identity.Number, "disk musel být převzat");

        SourceProtectionGuard.Release(outcome.Session!);
    }

    [Test("A busy volume makes the takeover fail without leaving anything behind")]
    public static void TakeOverFailureLeavesNoSession()
    {
        var (platform, source, _) = BuildPlatform();
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;
        takeover.FailTakeOver = true;
        takeover.Problems = ["E: svazek nelze uzamknout – svazek je používán."];

        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk(), _ => false);

        Assert.False(outcome.Success, "převzetí musí selhat");
        Assert.Equal<ISourceTakeoverSession?>(null, outcome.Session, "nesmí vzniknout relace");
        Assert.Contains("uzamknout", string.Join(" | ", outcome.Problems), "důvod musí být uveden");
        Assert.Contains("nic zapsáno", string.Join(" | ", outcome.Problems), "musí být řečeno, že se nepsalo");
        Assert.Equal(0, takeover.ReleaseCount, "nic se nesmí uvolňovat, když se nic nepřevzalo");
    }

    [Test("After the technician frees the volume the retry succeeds")]
    public static void RetryAfterFailureSucceeds()
    {
        var (platform, source, _) = BuildPlatform();
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;
        takeover.FailTakeOverTimes = 1;

        int asked = 0;
        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk(), _ =>
        {
            asked++;
            return true;
        });

        Assert.True(outcome.Success, "druhý pokus musí uspět");
        Assert.Equal(1, asked, "operátor musí být dotázán právě jednou");
        Assert.Equal(2, takeover.TakeOverCalls.Count, "musely proběhnout dva pokusy");

        SourceProtectionGuard.Release(outcome.Session!);
    }

    [Test("The retry is not offered when it cannot help")]
    public static void NoRetryWhenHopeless()
    {
        var (platform, source, _) = BuildPlatform();
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;
        takeover.FailTakeOver = true;
        takeover.FailTakeOverWithRetryPossible = false;

        int asked = 0;
        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk(), _ =>
        {
            asked++;
            return true;
        });

        Assert.False(outcome.Success, "převzetí musí selhat");
        Assert.Equal(0, asked, "u neřešitelného stavu se nemá na opakování ptát");
        Assert.Equal(1, takeover.TakeOverCalls.Count, "musí proběhnout jediný pokus");
    }

    [Test("The technician can decline the retry")]
    public static void RetryCanBeDeclined()
    {
        var (platform, source, _) = BuildPlatform();
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;
        takeover.FailTakeOver = true;

        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk(), _ => false);

        Assert.False(outcome.Success, "převzetí musí selhat");
        Assert.Equal(1, takeover.TakeOverCalls.Count, "po odmítnutí se už nezkouší");
    }

    [Test("Repeated failures stop at the attempt limit")]
    public static void RetryHonoursLimit()
    {
        var (platform, source, _) = BuildPlatform();
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;
        takeover.FailTakeOver = true;

        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk(), _ => true, maxAttempts: 3);

        Assert.False(outcome.Success, "převzetí musí selhat");
        Assert.Equal(3, takeover.TakeOverCalls.Count, "musí proběhnout právě tři pokusy");
    }

    [Test("A platform without takeover support refuses to migrate")]
    public static void UnavailableTakeoverRefused()
    {
        var (platform, source, _) = BuildPlatform();
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;
        takeover.IsAvailable = false;
        takeover.UnavailableReason = "test: převzetí není dostupné";

        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk());

        Assert.False(outcome.Success, "bez převzetí se nesmí pokračovat");
        Assert.Contains("převzetí není dostupné", string.Join(" | ", outcome.Problems), "důvod musí být uveden");
        Assert.Contains("nezapisuje", string.Join(" | ", outcome.Problems), "musí zaznít důvod odmítnutí");
    }

    [Test("Releasing twice is harmless")]
    public static void ReleaseIsIdempotent()
    {
        var (platform, source, _) = BuildPlatform();
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;

        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk());
        Assert.True(outcome.Success, "převzetí musí uspět");

        var first = SourceProtectionGuard.Release(outcome.Session!);
        var second = SourceProtectionGuard.Release(outcome.Session!);

        Assert.True(first.Success, "první uvolnění musí uspět");
        Assert.True(second.Success, "druhé uvolnění nesmí hlásit chybu");
        Assert.Equal(1, takeover.ReleaseCount, "skutečné uvolnění proběhne jen jednou");
    }

    [Test("A failing release is reported, not thrown")]
    public static void ReleaseFailureIsReported()
    {
        var (platform, source, _) = BuildPlatform();
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;
        takeover.FailRelease = true;

        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk());
        Assert.True(outcome.Success, "převzetí musí uspět");

        var release = SourceProtectionGuard.Release(outcome.Session!);

        Assert.False(release.Success, "selhání úklidu musí být vidět");
        Assert.NotEmpty(release.Problems, "musí být popsáno, co se nepovedlo");
    }

    [Test("Taking the disk over makes it read-only and releasing restores it")]
    public static void TakeOverFlipsReadOnlyAndRestores()
    {
        var (platform, source, target) = BuildPlatform();
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;

        Assert.False(source.ReadOnly, "disk je na začátku zapisovatelný");

        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk());
        Assert.True(outcome.Success, "převzetí musí uspět");
        Assert.True(source.ReadOnly, "zdroj musí být po převzetí read-only");

        SourceProtectionGuard.Release(outcome.Session!);

        Assert.False(source.ReadOnly, "po úklidu musí být disk opět zapisovatelný");
        Assert.Equal(1, takeover.ReleaseCount, "uvolnění musí proběhnout");
        _ = target;
    }

    [Test("A source that was read-only to begin with stays read-only")]
    public static void OriginallyReadOnlyStaysReadOnly()
    {
        var (platform, source, _) = BuildPlatform();
        source.ReadOnly = true;

        var outcome = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk());
        Assert.True(outcome.Success, "read-only disk se přebírá bez potíží");

        SourceProtectionGuard.Release(outcome.Session!);

        Assert.True(source.ReadOnly, "původní read-only stav se nesmí zrušit");
    }

    [Test("The executor refuses to write when the source is not demonstrably read-only")]
    public static void ExecutorRefusesUnprotectedSource()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        DiskImageBuilder.CreateGpt(sourcePath, 32 * MiB)
            .AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data", 1 * MiB, 8 * MiB)
            .Build();

        using (var blank = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        {
            blank.SetLength(32 * MiB);
        }

        var source = Fixtures.Simulated(1, sourcePath, 32 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 32 * MiB);
        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        var reporter = new Fixtures.RecordingReporter();

        var analysis = DiskAnalyzer.Analyze(source.ToPhysicalDisk(), platform, probeContents: false);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());
        Assert.True(plan.IsFeasible, "plán musí být sestavitelný");

        string hashBefore = Hash(targetPath);

        // The caller claims to have protected the source, but it did not actually do it.
        var executor = new MigrationExecutor(platform, reporter);
        var result = executor.Execute(
            plan,
            source.ToPhysicalDisk(),
            target.ToPhysicalDisk(),
            Capture(sourcePath, source),
            Capture(targetPath, target),
            new ExecutionOptions { SourceAlreadyProtected = true });

        Assert.False(result.Success, "migrace se nesmí spustit bez ochrany zdroje");
        Assert.Contains("pouze pro čtení", string.Join(" | ", result.Errors), "důvod musí být uveden");
        Assert.Equal(hashBefore, Hash(targetPath), "na cílový disk se nesmělo zapsat");
    }

    [Test("End to end: takeover, migration and hand-back in the order the tool performs them")]
    public static void EndToEndWithTakeover()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        DiskImageBuilder.CreateGpt(sourcePath, 32 * MiB)
            .AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data", 1 * MiB, 12 * MiB)
            .AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data2", 14 * MiB, 8 * MiB)
            .Build();

        using (var blank = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        {
            blank.SetLength(32 * MiB);
        }

        var source = Fixtures.Simulated(1, sourcePath, 32 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 32 * MiB);
        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;
        var reporter = new Fixtures.RecordingReporter();

        string sourceHashBefore = Hash(sourcePath);

        var protection = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk());
        Assert.True(protection.Success, "převzetí musí uspět");

        MigrationResult result;
        try
        {
            var analysis = DiskAnalyzer.Analyze(source.ToPhysicalDisk(), platform, probeContents: false);
            var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());
            Assert.True(plan.IsFeasible, "plán musí být sestavitelný");

            var executor = new MigrationExecutor(platform, reporter);
            result = executor.Execute(
                plan,
                source.ToPhysicalDisk(),
                target.ToPhysicalDisk(),
                Capture(sourcePath, source),
                Capture(targetPath, target),
                new ExecutionOptions { SourceAlreadyProtected = true });
        }
        finally
        {
            SourceProtectionGuard.Release(protection.Session!);
        }

        Assert.True(result.Success, "migrace musí uspět: " + string.Join(" | ", result.Errors));
        Assert.Equal(1, takeover.ReleaseCount, "po migraci musí proběhnout úklid");
        Assert.False(source.ReadOnly, "zdroj musí být vrácen jako zapisovatelný");
        Assert.Equal(sourceHashBefore, Hash(sourcePath), "zdrojový disk musí zůstat bajtově shodný");
    }

    [Test("A refused takeover never lets the migration start")]
    public static void RefusedTakeoverStopsEverything()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        DiskImageBuilder.CreateGpt(sourcePath, 32 * MiB)
            .AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data", 1 * MiB, 8 * MiB)
            .Build();

        using (var blank = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        {
            blank.SetLength(32 * MiB);
        }

        var source = Fixtures.Simulated(1, sourcePath, 32 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 32 * MiB);
        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        ((SimulatedSourceTakeover)platform.SourceTakeover!).FailTakeOver = true;

        string hashBefore = Hash(targetPath);

        var protection = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk(), _ => false);

        Assert.False(protection.Success, "převzetí musí selhat");
        Assert.Equal(hashBefore, Hash(targetPath), "na cílový disk se nesmělo zapsat");
        Assert.Equal(0, ((SimulatedSourceTakeover)platform.SourceTakeover!).ReleaseCount,
            "u neúspěšného převzetí není co uvolňovat");
    }

    [Test("A cancelled migration still hands the source disk back")]
    public static void CancelledMigrationStillReleases()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        DiskImageBuilder.CreateGpt(sourcePath, 32 * MiB)
            .AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data", 1 * MiB, 12 * MiB)
            .Build();

        using (var blank = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        {
            blank.SetLength(32 * MiB);
        }

        var source = Fixtures.Simulated(1, sourcePath, 32 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 32 * MiB);
        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        var takeover = (SimulatedSourceTakeover)platform.SourceTakeover!;

        using var cancellation = new CancellationTokenSource();
        var reporter = new CancellingReporter(cancellation);

        var protection = SourceProtectionGuard.Acquire(platform, source.ToPhysicalDisk());
        Assert.True(protection.Success, "převzetí musí uspět");
        Assert.True(source.ReadOnly, "zdroj je během migrace read-only");

        MigrationResult result;
        try
        {
            var analysis = DiskAnalyzer.Analyze(source.ToPhysicalDisk(), platform, probeContents: false);
            var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());
            Assert.True(plan.IsFeasible, "plán musí být sestavitelný");

            var executor = new MigrationExecutor(platform, reporter);
            result = executor.Execute(
                plan,
                source.ToPhysicalDisk(),
                target.ToPhysicalDisk(),
                Capture(sourcePath, source),
                Capture(targetPath, target),
                new ExecutionOptions { SourceAlreadyProtected = true },
                cancellation.Token);
        }
        finally
        {
            SourceProtectionGuard.Release(protection.Session!);
        }

        Assert.False(result.Success, "přerušená migrace nesmí hlásit úspěch");
        Assert.Equal(1, takeover.ReleaseCount, "i po přerušení musí proběhnout úklid");
        Assert.False(source.ReadOnly, "zdroj musí být vrácen jako zapisovatelný");
    }

    /// <summary>Cancels the run as soon as the first partition copy starts.</summary>
    private sealed class CancellingReporter : IMigrationReporter
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingReporter(CancellationTokenSource cancellation) => _cancellation = cancellation;

        public void Phase(string message) { }

        public void Detail(string message) { }

        public void Notice(string message) { }

        public void Warning(string message) { }

        public void PartitionStarted(PlannedPartition partition) => _cancellation.Cancel();

        public void Progress(PlannedPartition partition, CopyProgress progress) { }

        public void PartitionCompleted(PlannedPartition partition, long bytesWritten) { }
    }

    // ---------------------------------------------------------------- helpers

    private static (ImageDiskPlatform Platform, SimulatedDisk Source, SimulatedDisk Target) BuildPlatform()
    {
        var source = Fixtures.Simulated(1, "source.img", 500 * Fixtures.GiB);
        var target = Fixtures.Simulated(2, "target.img", 500 * Fixtures.GiB);
        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        return (platform, source, target);
    }

    private static Core.Safety.DiskFingerprint Capture(string path, SimulatedDisk disk)
    {
        using var reader = ImageBlockDevice.OpenReader(path, disk.Identity.LogicalSectorSize);
        return Core.Safety.DiskFingerprintFactory.Capture(disk.ToPhysicalDisk(), reader);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
