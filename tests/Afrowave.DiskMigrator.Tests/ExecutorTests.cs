using System.Security.Cryptography;
using System.Text;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Analysis;
using Afrowave.DiskMigrator.Core.Execution;
using Afrowave.DiskMigrator.Core.Logging;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Planning;
using Afrowave.DiskMigrator.Core.Safety;
using Afrowave.DiskMigrator.Platform.Image;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>
/// End-to-end runs of the whole pipeline against disk images: analysis, planning, copying,
/// verification. Nothing here can reach a physical disk.
/// </summary>
public static class ExecutorTests
{
    private const long MiB = Fixtures.MiB;

    [Test("A full raw clone copies every byte and never touches the source")]
    public static void RawCloneEndToEnd()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        // Source: 32 MiB MBR disk with a FAT32 ESP-like partition and an unknown file system.
        var builder = DiskImageBuilder.CreateMbr(sourcePath, 32 * MiB);
        builder.AddMbrPartition(0x0C, 1 * MiB, 4 * MiB);
        builder.AddMbrPartition(0x07, 6 * MiB, 8 * MiB);
        builder.Build();

        WriteFat32BootSector(sourcePath, 1 * MiB, "START");
        WritePattern(sourcePath, 6 * MiB, 8 * MiB, 0x31);

        CreateFilledImage(targetPath, 32 * MiB, 0x00);

        var source = Fixtures.Simulated(1, sourcePath, 32 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 32 * MiB);

        var result = RunMigration(temp, source, target, out var platform, out var reporter);

        Assert.True(result.Success, "migrace musí uspět: " + string.Join(" | ", result.Errors));
        Assert.True(result.CompletedAllSteps, "všechny kroky musí proběhnout");
        Assert.False(result.BootTested, "spuštění cílového systému se netestuje, musí to být uvedeno");
        Assert.NotEmpty(result.VerificationNotes, "musí existovat zpráva o ověření");

        // The source file must be byte-identical: the tool only ever opened it for reading.
        Assert.Contains(platform.WriteAttempts, 2, "zápis smí být otevřen jen na cílovém disku");
        Assert.False(platform.WriteAttempts.Contains(1), "zdrojový disk nesmí být nikdy otevřen pro zápis");

        byte[] sourceBytes = File.ReadAllBytes(sourcePath);
        byte[] targetBytes = File.ReadAllBytes(targetPath);

        // Partition 1: FAT32 at 1 MiB, 4 MiB long.
        Assert.Equal<string?>(null,
            Fixtures.CompareRanges(sourceBytes, 1 * MiB, targetBytes, 1 * MiB, 4 * MiB),
            "první oddíl musí být zkopírován 1:1");

        // Partition 2: unknown file system at 6 MiB, 8 MiB long.
        Assert.Equal<string?>(null,
            Fixtures.CompareRanges(sourceBytes, 6 * MiB, targetBytes, 6 * MiB, 8 * MiB),
            "druhý oddíl musí být zkopírován 1:1");

        // The resulting partition table must describe the same layout.
        using var device = ImageBlockDevice.OpenReader(targetPath);
        var read = PartitionTableReader.Read(device);
        Assert.Empty(read.Errors, "výsledná tabulka oddílů musí být platná");
        Assert.Equal(2, read.Table!.Partitions.Count, "počet oddílů na cíli");
        Assert.True(sourceBytes.AsSpan(446, 64).SequenceEqual(targetBytes.AsSpan(446, 64)),
            "položky MBR musí odpovídat zdroji");
    }

    [Test("NTFS is copied intelligently: only allocated clusters, byte-identical metadata")]
    public static void NtfsAllocationCloneEndToEnd()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        long[] dataClusters = [100, 101, 102, 250, 900, 901, 902, 1500];

        // Source: 32 MiB GPT disk, 8 MiB NTFS partition at 1 MiB, 4 MiB raw partition at 10 MiB.
        var builder = DiskImageBuilder.CreateGpt(sourcePath, 32 * MiB);
        builder.AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "NTFS", 1 * MiB, 8 * MiB);
        builder.AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data", 10 * MiB, 4 * MiB);
        builder.Build();

        var ntfsVolume = SyntheticNtfs.Build(totalClusters: 2048, dataClusters: dataClusters);
        using (var device = ImageBlockDevice.OpenWritable(sourcePath))
        {
            device.WriteAt(1 * MiB, ntfsVolume);
        }

        WritePattern(sourcePath, 10 * MiB, 4 * MiB, 0x77);

        // The target starts out filled with 0xFF so untouched regions are obvious.
        CreateFilledImage(targetPath, 32 * MiB, 0xFF);

        var source = Fixtures.Simulated(1, sourcePath, 32 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 32 * MiB);

        var result = RunMigration(temp, source, target, out _, out _);

        Assert.True(result.Success, "migrace musí uspět: " + string.Join(" | ", result.Errors));
        Assert.True(result.BytesWritten < 8 * MiB + 4 * MiB,
            $"chytrá kopie nesmí zapsat celý oddíl (zapsáno {result.BytesWritten} B)");

        byte[] sourceBytes = File.ReadAllBytes(sourcePath);
        byte[] targetBytes = File.ReadAllBytes(targetPath);

        // Allocated clusters must be identical.
        long clusterSize = SyntheticNtfs.ClusterSize;
        foreach (long cluster in SyntheticNtfs.MetadataClusters(2048).Concat(dataClusters).Distinct())
        {
            long offset = (1 * MiB) + (cluster * clusterSize);
            Assert.Equal<string?>(null,
                Fixtures.CompareRanges(sourceBytes, offset, targetBytes, offset, clusterSize),
                $"obsazený cluster {cluster} musí být zkopírován 1:1");
        }

        // Free clusters must not have been touched - they still hold the target's 0xFF.
        long freeClusterOffset = (1 * MiB) + (1700L * clusterSize);
        Assert.Equal(0xFF, targetBytes[freeClusterOffset], "volný cluster zůstává nedotčený");
        Assert.Equal(SyntheticNtfs.FreeClusterPattern, sourceBytes[freeClusterOffset], "ve zdroji je volný cluster vyplněný");

        // The second partition is copied exactly.
        Assert.Equal<string?>(null,
            Fixtures.CompareRanges(sourceBytes, 10 * MiB, targetBytes, 10 * MiB, 4 * MiB),
            "druhý oddíl musí být zkopírován 1:1");
    }

    [Test("Dry run must not write a single byte")]
    public static void DryRunWritesNothing()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        var builder = DiskImageBuilder.CreateGpt(sourcePath, 16 * MiB);
        builder.AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data", 1 * MiB, 8 * MiB);
        builder.Build();
        WritePattern(sourcePath, 1 * MiB, 8 * MiB, 0x42);

        CreateFilledImage(targetPath, 16 * MiB, 0xCD);
        string beforeHash = HashFile(targetPath);

        var source = Fixtures.Simulated(1, sourcePath, 16 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 16 * MiB);

        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        var reporter = new Fixtures.RecordingReporter();
        var analysis = DiskAnalyzer.Analyze(source.ToPhysicalDisk(), platform, probeContents: false);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());
        Assert.True(plan.IsFeasible, "plán musí být proveditelný");

        var executor = new MigrationExecutor(platform, reporter);
        var result = executor.Execute(
            plan,
            source.ToPhysicalDisk(),
            target.ToPhysicalDisk(),
            Capture(sourcePath, source, platform),
            Capture(targetPath, target, platform),
            new ExecutionOptions { DryRun = true });

        Assert.True(result.Success, "kontrolní režim musí skončit úspěšně");
        Assert.False(result.CompletedAllSteps, "kontrolní režim nesmí hlásit dokončení migrace");
        Assert.Empty(platform.WriteAttempts, "kontrolní režim nesmí otevřít žádný disk pro zápis");
        Assert.Equal(beforeHash, HashFile(targetPath), "cílový disk se nesmí změnit");
    }

    [Test("A disk that changed after planning must abort before the first write")]
    public static void FingerprintMismatchAborts()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        var builder = DiskImageBuilder.CreateGpt(sourcePath, 16 * MiB);
        builder.AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data", 1 * MiB, 8 * MiB);
        builder.Build();

        CreateFilledImage(targetPath, 16 * MiB, 0x11);

        var source = Fixtures.Simulated(1, sourcePath, 16 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 16 * MiB);

        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        var reporter = new Fixtures.RecordingReporter();
        var analysis = DiskAnalyzer.Analyze(source.ToPhysicalDisk(), platform, probeContents: false);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());

        var sourceFingerprint = Capture(sourcePath, source, platform);
        var staleTargetFingerprint = Capture(targetPath, target, platform);

        // Someone swaps or repartitions the target disk between planning and execution.
        using (var device = ImageBlockDevice.OpenWritable(targetPath))
        {
            device.WriteAt(0, Encoding.ASCII.GetBytes("ZASAH"));
        }

        string hashAfterTampering = HashFile(targetPath);

        var executor = new MigrationExecutor(platform, reporter);
        var result = executor.Execute(
            plan,
            source.ToPhysicalDisk(),
            target.ToPhysicalDisk(),
            sourceFingerprint,
            staleTargetFingerprint,
            new ExecutionOptions());

        Assert.False(result.Success, "migrace musí být odmítnuta");
        Assert.Contains("změnil", string.Join(" | ", result.Errors), "chyba musí vysvětlit změnu disku");
        Assert.Empty(platform.WriteAttempts, "pro zápis nesmí být otevřen žádný disk");
        Assert.Equal(hashAfterTampering, HashFile(targetPath), "cílový disk se nesmí dále měnit");
    }

    [Test("A protected target disk is refused before anything is opened")]
    public static void ProtectedTargetRefused()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        var builder = DiskImageBuilder.CreateGpt(sourcePath, 16 * MiB);
        builder.AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data", 1 * MiB, 8 * MiB);
        builder.Build();
        CreateFilledImage(targetPath, 16 * MiB, 0x22);

        var source = Fixtures.Simulated(1, sourcePath, 16 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 16 * MiB);

        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety(2));
        var reporter = new Fixtures.RecordingReporter();
        var analysis = DiskAnalyzer.Analyze(source.ToPhysicalDisk(), platform, probeContents: false);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());

        var executor = new MigrationExecutor(platform, reporter);
        var result = executor.Execute(
            plan,
            source.ToPhysicalDisk(),
            target.ToPhysicalDisk(),
            Capture(sourcePath, source, platform),
            Capture(targetPath, target, platform),
            new ExecutionOptions());

        Assert.False(result.Success, "chráněný disk nesmí být použit jako cíl");
        Assert.Contains("chráněn", string.Join(" | ", result.Errors), "důvod odmítnutí musí být uveden");
        Assert.Empty(platform.WriteAttempts, "chráněný disk se nesmí ani otevřít pro zápis");
    }

    [Test("A shrink that needs DISM fails with a clear explanation when DISM is missing")]
    public static void WimUnavailableFailsClearly()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        var builder = DiskImageBuilder.CreateGpt(sourcePath, 16 * MiB);
        builder.AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "NTFS", 1 * MiB, 12 * MiB);
        builder.Build();

        var volume = SyntheticNtfs.Build(totalClusters: 3072, dataClusters: Enumerable.Range(200, 40).Select(i => (long)i));
        using (var device = ImageBlockDevice.OpenWritable(sourcePath))
        {
            device.WriteAt(1 * MiB, volume);
        }

        CreateFilledImage(targetPath, 8 * MiB, 0x00);

        var source = Fixtures.Simulated(1, sourcePath, 16 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 8 * MiB);

        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        var reporter = new Fixtures.RecordingReporter();
        var analysis = DiskAnalyzer.Analyze(source.ToPhysicalDisk(), platform, probeContents: false);

        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TemporaryRootPath = temp.Path,
            TemporaryFreeBytes = 2 * 1024 * MiB,
            NtfsMinimumSlackBytes = 1 * MiB,
            NtfsSlackFraction = 0.05,
        });

        Assert.True(plan.IsFeasible, "plán musí být proveditelný: " + string.Join(" | ", plan.Errors));
        Assert.Equal(CopyStrategy.WimReimage, plan.Partitions[0].Strategy, "musí být zvolen obraz WIM");

        var executor = new MigrationExecutor(platform, reporter);
        var result = executor.Execute(
            plan,
            source.ToPhysicalDisk(),
            target.ToPhysicalDisk(),
            Capture(sourcePath, source, platform),
            Capture(targetPath, target, platform),
            new ExecutionOptions());

        Assert.False(result.Success, "bez DISM nesmí migrace pokračovat");
        string message = string.Join(" | ", result.Errors);
        Assert.Contains("DISM", message, "chyba musí jmenovat chybějící nástroj");
        Assert.Contains("neúplná data", message, "musí být jasně řečeno, že cílový disk je nepoužitelný");
    }

    [Test("The audit log never contains a recovery key")]
    public static void AuditLogRedaction()
    {
        using var temp = new TempDirectory();
        string logPath = temp.File("audit.log");

        using (var log = new FileAuditLog(logPath))
        {
            log.Write("TEST", "klíč obnovení: 123456-123456-123456-123456-123456-123456-123456-123456");
            log.Write("TEST", "Běžná zpráva o průběhu migrace");
        }

        string content = File.ReadAllText(logPath);
        Assert.False(content.Contains("123456-123456"), "obnovovací klíč se nesmí zapsat do protokolu");
        Assert.Contains("<skryto", content, "klíč musí být nahrazen");
        Assert.Contains("Běžná zpráva", content, "běžné zprávy se zapisují");
    }

    [Test("BIOS boot code of the source MBR must be carried over to the target")]
    public static void MbrBootCodeIsPreserved()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        // A recognisable stand-in for the 446 byte MBR bootstrap area.
        var bootCode = new byte[446];
        for (int i = 0; i < bootCode.Length; i++) bootCode[i] = (byte)(0x40 + (i % 16));

        DiskImageBuilder.CreateMbr(sourcePath, 32 * MiB)
            .WithMbrSignature(0x5A5A1234)
            .WithBootCode(bootCode)
            .AddMbrPartition(0x07, 1 * MiB, 8 * MiB)
            .Build();

        CreateFilledImage(targetPath, 32 * MiB, 0x00);

        var source = Fixtures.Simulated(1, sourcePath, 32 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 32 * MiB);

        var result = RunMigration(temp, source, target, out _, out _);
        Assert.True(result.Success, "migrace musí uspět: " + string.Join(" | ", result.Errors));

        byte[] sourceBytes = File.ReadAllBytes(sourcePath);
        byte[] targetBytes = File.ReadAllBytes(targetPath);

        // Bytes 0..439 are the bootstrap code, 440..445 carry the disk signature.
        Assert.True(sourceBytes.AsSpan(0, 440).SequenceEqual(targetBytes.AsSpan(0, 440)),
            "zaváděcí kód MBR musí být převzat ze zdrojového disku");
        Assert.True(bootCode.AsSpan(0, 440).SequenceEqual(targetBytes.AsSpan(0, 440)),
            "zaváděcí kód musí odpovídat obsahu zdrojového disku");
        Assert.Equal(0x5A5A1234u, BitConverter.ToUInt32(targetBytes, 440),
            "signatura MBR musí zůstat shodná se zdrojem");
        Assert.True(sourceBytes.AsSpan(446, 64).SequenceEqual(targetBytes.AsSpan(446, 64)),
            "položky MBR musí odpovídat zdroji");
    }

    [Test("A target that still defines partitions aborts before the first write")]
    public static void OccupiedTargetRefusedByExecutor()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        DiskImageBuilder.CreateGpt(sourcePath, 16 * MiB)
            .AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data", 1 * MiB, 8 * MiB)
            .Build();

        // The customer plugged in a disk that still carries an old partition table.
        DiskImageBuilder.CreateMbr(targetPath, 16 * MiB)
            .AddMbrPartition(0x07, 1 * MiB, 4 * MiB)
            .Build();

        string beforeHash = HashFile(targetPath);

        var source = Fixtures.Simulated(1, sourcePath, 16 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 16 * MiB);

        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        var reporter = new Fixtures.RecordingReporter();
        var analysis = DiskAnalyzer.Analyze(source.ToPhysicalDisk(), platform, probeContents: false);

        // Deliberately build the plan without the inspection: the executor has to catch it alone.
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());
        Assert.True(plan.IsFeasible, "plán bez kontroly cíle musí být sestavitelný");

        var executor = new MigrationExecutor(platform, reporter);
        var result = executor.Execute(
            plan,
            source.ToPhysicalDisk(),
            target.ToPhysicalDisk(),
            Capture(sourcePath, source, platform),
            Capture(targetPath, target, platform),
            new ExecutionOptions());

        Assert.False(result.Success, "migrace musí být odmítnuta");
        Assert.Contains("není prázdný", string.Join(" | ", result.Errors), "chyba musí popsat neprázdný cíl");
        Assert.Equal(beforeHash, HashFile(targetPath), "cílový disk se nesmí změnit");
    }

    [Test("Cancelling the migration reports that the target is unusable")]
    public static void CancellationReportsIncompleteTarget()
    {
        using var temp = new TempDirectory();
        string sourcePath = temp.File("source.img");
        string targetPath = temp.File("target.img");

        DiskImageBuilder.CreateGpt(sourcePath, 32 * MiB)
            .AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Data", 1 * MiB, 16 * MiB)
            .Build();
        WritePattern(sourcePath, 1 * MiB, 16 * MiB, 0x51);

        CreateFilledImage(targetPath, 32 * MiB, 0x00);

        var source = Fixtures.Simulated(1, sourcePath, 32 * MiB);
        var target = Fixtures.Simulated(2, targetPath, 32 * MiB);

        var platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        using var cancellation = new CancellationTokenSource();
        var reporter = new CancellingReporter(cancellation);

        var analysis = DiskAnalyzer.Analyze(source.ToPhysicalDisk(), platform, probeContents: false);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());
        Assert.True(plan.IsFeasible, "plán musí být sestavitelný");

        var executor = new MigrationExecutor(platform, reporter);
        var result = executor.Execute(
            plan,
            source.ToPhysicalDisk(),
            target.ToPhysicalDisk(),
            Capture(sourcePath, source, platform),
            Capture(targetPath, target, platform),
            new ExecutionOptions(),
            cancellation.Token);

        Assert.False(result.Success, "přerušená migrace nesmí hlásit úspěch");
        string errors = string.Join(" | ", result.Errors);
        Assert.Contains("přerušena", errors, "musí být jasné, že operaci přerušil uživatel");
        Assert.Contains("neúplná data", errors, "musí být jasné, že cílový disk je nepoužitelný");
    }

    /// <summary>Cancels the migration as soon as the first partition copy starts.</summary>
    private sealed class CancellingReporter : IMigrationReporter
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingReporter(CancellationTokenSource cancellation) => _cancellation = cancellation;

        public void Phase(string message)
        {
        }

        public void Detail(string message)
        {
        }

        public void Notice(string message)
        {
        }

        public void Warning(string message)
        {
        }

        public void PartitionStarted(PlannedPartition partition) => _cancellation.Cancel();

        public void Progress(PlannedPartition partition, CopyProgress progress)
        {
        }

        public void PartitionCompleted(PlannedPartition partition, long bytesWritten)
        {
        }
    }

    // ---------------------------------------------------------------- helpers

    private static MigrationResult RunMigration(
        TempDirectory temp,
        SimulatedDisk source,
        SimulatedDisk target,
        out ImageDiskPlatform platform,
        out Fixtures.RecordingReporter reporter)
    {
        platform = new ImageDiskPlatform([source, target], Fixtures.Safety());
        reporter = new Fixtures.RecordingReporter();

        var analysis = DiskAnalyzer.Analyze(source.ToPhysicalDisk(), platform, probeContents: false);
        Assert.Empty(analysis.Errors, "analýza zdroje musí projít: " + string.Join(" | ", analysis.Errors));

        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TemporaryRootPath = temp.Path,
            TemporaryFreeBytes = 100 * MiB,
        });

        Assert.True(plan.IsFeasible, "plán musí být proveditelný: " + string.Join(" | ", plan.Errors));

        var executor = new MigrationExecutor(platform, reporter);
        return executor.Execute(
            plan,
            source.ToPhysicalDisk(),
            target.ToPhysicalDisk(),
            Capture(source.ImagePath, source, platform),
            Capture(target.ImagePath, target, platform),
            new ExecutionOptions());
    }

    private static DiskFingerprint Capture(string path, SimulatedDisk disk, ImageDiskPlatform platform)
    {
        using var reader = ImageBlockDevice.OpenReader(path, disk.Identity.LogicalSectorSize);
        return DiskFingerprintFactory.Capture(disk.ToPhysicalDisk(), reader);
    }

    private static void CreateFilledImage(string path, long size, byte fill)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(size);
        var buffer = new byte[Math.Min(size, 1 * MiB)];
        Array.Fill(buffer, fill);
        long written = 0;
        while (written < size)
        {
            int chunk = (int)Math.Min(buffer.Length, size - written);
            stream.Write(buffer, 0, chunk);
            written += chunk;
        }
    }

    private static void WritePattern(string path, long offset, long length, byte seed)
    {
        using var device = ImageBlockDevice.OpenWritable(path);
        var buffer = new byte[Math.Min(length, 1 * MiB)];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(seed + i);

        long written = 0;
        while (written < length)
        {
            int chunk = (int)Math.Min(buffer.Length, length - written);
            device.WriteAt(offset + written, buffer.AsSpan(0, chunk));
            written += chunk;
        }

        device.Flush();
    }

    private static void WriteFat32BootSector(string path, long offset, string label)
    {
        using var device = ImageBlockDevice.OpenWritable(path);
        var sector = new byte[512];
        sector[0] = 0xEB;
        sector[1] = 0x58;
        sector[2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(sector, 3);
        BitConverter.TryWriteBytes(sector.AsSpan(0x0B, 2), (ushort)512);
        sector[0x0D] = 8;
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(sector, 82);
        Encoding.ASCII.GetBytes(label.PadRight(11)).CopyTo(sector, 71);
        sector[510] = 0x55;
        sector[511] = 0xAA;
        device.WriteAt(offset, sector);
        device.Flush();
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
