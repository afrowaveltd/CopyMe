using System.Diagnostics;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Filesystems;
using Afrowave.DiskMigrator.Core.Logging;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Planning;
using Afrowave.DiskMigrator.Core.Safety;

namespace Afrowave.DiskMigrator.Core.Execution;

public sealed class ExecutionOptions
{
    /// <summary>Build the plan and report what would happen, without writing anything.</summary>
    public bool DryRun { get; init; }

    public bool Verify { get; init; } = true;

    public int ChunkBytes { get; init; } = CopyEngine.DefaultChunkBytes;

    public bool ZeroFreeClusters { get; init; }

    public bool KeepSourceReadOnlyFlag { get; init; }

    public TimeSpan VolumeWaitTimeout { get; init; } = TimeSpan.FromSeconds(45);
}

public sealed class MigrationAbortedException : Exception
{
    public MigrationAbortedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Executes a reviewed <see cref="MigrationPlan"/>. The class owns the destructive part of
/// the tool, so every step is written defensively: verify, write, verify again.
/// </summary>
public sealed class MigrationExecutor
{
    private readonly IDiskPlatform _platform;
    private readonly IMigrationReporter _reporter;
    private readonly IAuditLog _log;

    public MigrationExecutor(IDiskPlatform platform, IMigrationReporter reporter, IAuditLog? log = null)
    {
        _platform = platform;
        _reporter = reporter;
        _log = log ?? NullAuditLog.Instance;
    }

    public MigrationResult Execute(
        MigrationPlan plan,
        PhysicalDisk sourceDisk,
        PhysicalDisk targetDisk,
        DiskFingerprint sourceFingerprint,
        DiskFingerprint targetFingerprint,
        ExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>(plan.Warnings);
        var verificationNotes = new List<string>();
        long bytesWritten = 0;
        bool sourceMarkedReadOnly = false;
        bool targetModified = false;

        _log.Write("PLAN", $"Zdroj: {plan.SourceDisk.ShortLabel} ({plan.SourceDisk.SerialNumber})");
        _log.Write("PLAN", $"Cíl:   {plan.TargetDisk.ShortLabel} ({plan.TargetDisk.SerialNumber})");
        _log.Write("PLAN", $"Oddílů: {plan.Partitions.Count}, plánovaný zápis {MigrationPlanner.Format(plan.TotalBytesToWrite)}");

        IBlockReader? sourceReader = null;
        IBlockReader? targetReader = null;
        IBlockDevice? targetDevice = null;

        try
        {
            // ---------------------------------------------------------- pre-flight

            _reporter.Phase("Kontrola před spuštěním");

            var pairBlockers = DiskSafetyPolicy.ValidatePair(sourceDisk, targetDisk, _platform.GetSafetyEnvironment());
            if (pairBlockers.Count > 0)
            {
                foreach (string blocker in pairBlockers) _log.Write("REFUSE", blocker);
                return new MigrationResult(false, false, pairBlockers, warnings, [], false, stopwatch.Elapsed, 0);
            }

            if (!plan.IsFeasible)
            {
                return new MigrationResult(false, false, plan.Errors, warnings, [], false, stopwatch.Elapsed, 0);
            }

            if (options.DryRun)
            {
                return RunDryRun(plan, stopwatch, warnings, cancellationToken);
            }

            // ---------------------------------------------------------- prepare

            _reporter.Phase("Příprava cílového disku");
            _log.Write("STEP", "Odpojuji svazky cílového disku");
            _platform.Volumes.RemoveAllMountPoints(targetDisk.Identity.Number);

            foreach (var partition in plan.Partitions)
            {
                if (!_platform.Volumes.TryDismountVolume(targetDisk.Identity.Number, partition.TargetStartByte))
                {
                    warnings.Add(
                        $"Svazek v oblasti oddílu #{partition.Source.Number} na cílovém disku se nepodařilo " +
                        "korektně odpojit. Operační systém na něj může mít otevřené popisovače.");
                    _log.Write("WARN", $"Svazek oddílu {partition.Source.Number} nebylo možné odpojit");
                }
            }

            _reporter.Detail("Zdrojový disk přepínám do režimu pouze pro čtení.");
            if (_platform.TrySetDiskReadOnly(sourceDisk, true))
            {
                sourceMarkedReadOnly = true;
                _log.Write("STEP", "Zdrojový disk nastaven jako read-only na úrovni zařízení");
            }
            else
            {
                warnings.Add("Zdrojový disk se nepodařilo přepnout do režimu pouze pro čtení; bude otevřen pouze pro čtení.");
                _log.Write("WARN", "Read-only atribut zdrojového disku nebylo možné nastavit");
            }

            _reporter.Detail("Otevírám zdrojový disk a kontroluji cílový disk (oba pouze pro čtení).");
            sourceReader = _platform.OpenReader(sourceDisk);
            targetReader = _platform.OpenReader(targetDisk);

            // ---------------------------------------------------------- re-verify identity

            _reporter.Phase("Opětovné ověření identity disků");
            _platform.Rescan();

            var currentTarget = _platform.EnumerateDisks()
                .FirstOrDefault(d => d.Identity.Number == targetDisk.Identity.Number);

            if (currentTarget is null)
            {
                throw new MigrationAbortedException(
                    "Cílový disk po opětovném načtení zmizel. Zápis nebyl zahájen.");
            }

            // The identity is checked through a read-only handle: if anything changed, a
            // write capable handle to the target is never even created.
            var actualTarget = DiskFingerprintFactory.Capture(currentTarget, targetReader);
            if (!DiskFingerprintFactory.Matches(targetFingerprint, actualTarget))
            {
                string difference = DiskFingerprintFactory.DescribeDifference(targetFingerprint, actualTarget);
                throw new MigrationAbortedException(
                    "Cílový disk se od načtení plánu změnil (" + difference + "). " +
                    "Zápis nebyl zahájen. Spusťte nástroj znovu a plán znovu zkontrolujte.");
            }

            var actualSource = DiskFingerprintFactory.Capture(sourceDisk, sourceReader);
            if (!DiskFingerprintFactory.Matches(sourceFingerprint, actualSource))
            {
                string difference = DiskFingerprintFactory.DescribeDifference(sourceFingerprint, actualSource);
                throw new MigrationAbortedException(
                    "Zdrojový disk se od načtení plánu změnil (" + difference + "). Zápis nebyl zahájen.");
            }

            _reporter.Detail("Identita obou disků potvrzena.");
            _log.Write("VERIFY", $"Identita potvrzena: cíl {actualTarget.Digest[..16]}, zdroj {actualSource.Digest[..16]}");

            // The target must still define no partitions. This is the last gate before the
            // first write; the operator, not the tool, is responsible for choosing the disk.
            var targetInspection = TargetDiskInspector.Inspect(targetReader);
            if (!targetInspection.IsEmpty)
            {
                throw new MigrationAbortedException(
                    "Cílový disk není prázdný (" + targetInspection.ShortDescription + "). Zápis nebyl zahájen. " +
                    string.Join(" ", targetInspection.Reasons));
            }

            _reporter.Detail("Cílový disk neobsahuje žádné oddíly.");
            _log.Write("VERIFY", "Cílový disk je bez oddílů: " + targetInspection.ShortDescription);

            targetReader.Dispose();
            targetReader = null;

            _reporter.Detail("Otevírám cílový disk pro zápis.");

            // Open the device that carries the verified identity right now, not the one from
            // planning time: after a USB re-plug the PhysicalDrive numbers may have shifted.
            targetDevice = _platform.OpenDevice(currentTarget);

            var handleTarget = DiskFingerprintFactory.Capture(currentTarget, targetDevice);
            if (!DiskFingerprintFactory.Matches(targetFingerprint, handleTarget))
            {
                string difference = DiskFingerprintFactory.DescribeDifference(targetFingerprint, handleTarget);
                throw new MigrationAbortedException(
                    "Otevřené cílové zařízení neodpovídá ověřenému disku (" + difference + "). " +
                    "Zápis nebyl zahájen. Zkontrolujte připojení disků a spusťte nástroj znovu.");
            }

            _log.Write("VERIFY", $"Zapisovací handle ověřen: {handleTarget.Digest[..16]}");

            // ---------------------------------------------------------- destructive part begins

            _reporter.Phase("Zápis tabulky oddílů na cílový disk");
            _log.Write("STEP", "Mažu původní tabulku oddílů cílového disku");

            bool gpt = plan.SourceTable.Kind == PartitionTableKind.Gpt;
            PartitionTableWriter.ClearPartitionAreas(targetDevice, gpt);
            targetModified = true;

            _log.Write("STEP", "Zapisuji novou tabulku oddílů (dočasné identifikátory)");
            WritePartitionTable(targetDevice, sourceReader, plan, final: false);
            targetDevice.Flush();

            cancellationToken.ThrowIfCancellationRequested();

            // ---------------------------------------------------------- copies that need no OS

            var nonWimPartitions = plan.Partitions.Where(p => p.Strategy != CopyStrategy.WimReimage).ToList();

            foreach (var partition in nonWimPartitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesWritten += CopyPartition(partition, plan, sourceReader, targetDevice, options, cancellationToken);
            }

            // ---------------------------------------------------------- verify before the OS sees it

            if (options.Verify)
            {
                _reporter.Phase("Ověřování přenesených dat");
                _log.Write("STEP", "Ověřuji zapsaná data proti zdroji");

                foreach (var partition in nonWimPartitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    VerifyPartition(partition, plan, sourceReader, targetDevice, options, verificationNotes, warnings, cancellationToken);
                }
            }

            targetDevice.Flush();

            // ---------------------------------------------------------- OS assisted steps

            var wimPartitions = plan.Partitions.Where(p => p.Strategy == CopyStrategy.WimReimage).ToList();

            if (wimPartitions.Count > 0 || plan.Boot.RequiresBcdboot || plan.Boot.RequiresWinReRegistration)
            {
                _reporter.Phase("Zpřístupnění oddílů cílového disku");
                _platform.Volumes.RefreshPartitionTable(targetDisk.Identity.Number);

                if (!_platform.Volumes.WaitForVolumes(targetDisk.Identity.Number, options.VolumeWaitTimeout))
                {
                    warnings.Add("Operační systém nezveřejnil svazky cílového disku v očekávaném čase.");
                }
            }

            foreach (var partition in wimPartitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesWritten += ReimagePartition(partition, plan, options, verificationNotes, warnings, cancellationToken);
            }

            if (plan.Boot.RequiresBcdboot || plan.Boot.RequiresWinReRegistration)
            {
                ConfigureBoot(plan, warnings, verificationNotes, cancellationToken);
            }

            // ---------------------------------------------------------- cleanup and final identity write

            _reporter.Phase("Dokončení a uzamčení cílového disku");
            _platform.Volumes.RemoveAllMountPoints(targetDisk.Identity.Number);

            foreach (var partition in plan.Partitions)
            {
                _platform.Volumes.TryDismountVolume(targetDisk.Identity.Number, partition.TargetStartByte);
            }

            if (plan.PreserveDiskIdentifiers)
            {
                _log.Write("STEP", "Zapisuji původní identifikátory disku a oddílů");
                WritePartitionTable(targetDevice, sourceReader, plan, final: true);
                targetDevice.Flush();
            }

            _platform.Volumes.RefreshPartitionTable(targetDisk.Identity.Number);

            // ---------------------------------------------------------- final structural verification

            verificationNotes.AddRange(VerifyPartitionTable(targetDevice, plan, warnings));

            stopwatch.Stop();
            _reporter.Phase("Hotovo");
            _log.Write("DONE", $"Migrace dokončena za {stopwatch.Elapsed:hh\\:mm\\:ss}, zapsáno {MigrationPlanner.Format(bytesWritten)}");

            return new MigrationResult(
                true,
                true,
                [],
                warnings,
                verificationNotes,
                BootTested: false,
                stopwatch.Elapsed,
                bytesWritten);
        }
        catch (OperationCanceledException)
        {
            _log.Write("ABORT", "Operace byla přerušena uživatelem");
            warnings.Add("Operace byla přerušena. Cílový disk je pouze částečně zapsaný a nesmí se použít.");
            return new MigrationResult(
                false, false,
                [targetModified
                    ? "Operace byla přerušena uživatelem. Cílový disk obsahuje neúplná data."
                    : "Operace byla přerušena uživatelem. Cílový disk nebyl změněn."],
                warnings, verificationNotes, false, stopwatch.Elapsed, bytesWritten);
        }
        catch (MigrationAbortedException ex)
        {
            _log.Write("ABORT", ex.Message);

            var errors = new List<string> { ex.Message };
            if (targetModified)
            {
                errors.Add("Cílový disk obsahuje neúplná data a nesmí se použít. Opakujte migraci od začátku.");
            }

            return new MigrationResult(false, false, errors, warnings, verificationNotes, false, stopwatch.Elapsed, bytesWritten);
        }
        catch (Exception ex)
        {
            _log.Write("ERROR", ex.GetType().Name + ": " + ex.Message);
            return new MigrationResult(
                false, false,
                [
                    $"Migrace selhala: {ex.Message}",
                    "Cílový disk obsahuje neúplná data a nesmí se použít. Opakujte migraci od začátku.",
                ],
                warnings, verificationNotes, false, stopwatch.Elapsed, bytesWritten);
        }
        finally
        {
            targetDevice?.Dispose();
            targetReader?.Dispose();
            sourceReader?.Dispose();

            if (sourceMarkedReadOnly && !options.KeepSourceReadOnlyFlag)
            {
                if (_platform.TrySetDiskReadOnly(sourceDisk, false))
                {
                    _log.Write("STEP", "Read-only atribut zdrojového disku zrušen");
                }
                else
                {
                    _log.Write("WARN", "Read-only atribut zdrojového disku se nepodařilo zrušit – po restartu zmizí");
                }
            }
        }
    }

    // ---------------------------------------------------------------- steps

    private MigrationResult RunDryRun(
        MigrationPlan plan,
        Stopwatch stopwatch,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        _reporter.Phase("Kontrolní režim – nic se nezapisuje");

        foreach (var partition in plan.Partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reporter.PartitionStarted(partition);
            _reporter.Detail(
                $"oddíl #{partition.Source.Number}: " +
                $"{MigrationPlanner.Format(partition.TargetStartByte)} .. " +
                $"{MigrationPlanner.Format(partition.TargetStartByte + partition.TargetSizeBytes - 1)} " +
                $"({MigrationPlanner.Format(partition.TargetSizeBytes)}), {partition.StrategyDescription}");
            _reporter.PartitionCompleted(partition, 0);
        }

        foreach (var step in plan.Boot.Steps)
        {
            _reporter.Detail($"zavádění: {step}");
        }

        stopwatch.Stop();
        return new MigrationResult(
            true,
            false,
            [],
            warnings,
            ["Kontrolní režim: na disky nebylo zapsáno nic."],
            BootTested: false,
            stopwatch.Elapsed,
            0);
    }

    private long CopyPartition(
        PlannedPartition partition,
        MigrationPlan plan,
        IBlockReader source,
        IBlockDevice target,
        ExecutionOptions options,
        CancellationToken cancellationToken)
    {
        _reporter.PartitionStarted(partition);

        if (partition.Strategy == CopyStrategy.RecreateEmpty)
        {
            // The partition entry already exists; clear anything the old disk left behind.
            long clearLength = Math.Min(partition.TargetSizeBytes, CopyEngine.NtfsEdgeBytes);
            CopyEngine.ZeroRange(target, partition.TargetStartByte, clearLength, options.ChunkBytes);
            _reporter.PartitionCompleted(partition, 0);
            _log.Write("COPY", $"Oddíl #{partition.Source.Number}: rezervovaný oddíl znovu vytvořen (bez obsahu)");
            return 0;
        }

        long written;

        if (partition.Strategy == CopyStrategy.NtfsAllocationClone)
        {
            using var ntfs = NtfsVolume.Open(source, partition.SourceStartByte, partition.SourceSizeBytes);
            written = CopyEngine.CloneAllocatedClusters(
                partition,
                ntfs,
                source,
                target,
                options.ChunkBytes,
                options.ZeroFreeClusters,
                progress => _reporter.Progress(partition, progress),
                cancellationToken);
        }
        else
        {
            written = CopyEngine.ClonePartition(
                partition,
                source,
                target,
                options.ChunkBytes,
                progress => _reporter.Progress(partition, progress),
                cancellationToken);
        }

        target.Flush();
        _reporter.PartitionCompleted(partition, written);
        _log.Write(
            "COPY",
            $"Oddíl #{partition.Source.Number} ({partition.Source.DisplayName}): {partition.StrategyDescription}, " +
            $"{MigrationPlanner.Format(written)}");

        return written;
    }

    private void VerifyPartition(
        PlannedPartition partition,
        MigrationPlan plan,
        IBlockReader source,
        IBlockDevice target,
        ExecutionOptions options,
        List<string> verificationNotes,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (partition.Strategy == CopyStrategy.RecreateEmpty) return;

        _reporter.Detail($"Ověřuji oddíl #{partition.Source.Number} ({partition.Source.DisplayName})…");

        long length;
        long sourceOffset;
        long targetOffset;

        if (partition.Strategy == CopyStrategy.NtfsAllocationClone)
        {
            using var ntfs = NtfsVolume.Open(source, partition.SourceStartByte, partition.SourceSizeBytes);
            var map = ntfs.Allocations;
            long clusterSize = ntfs.ClusterSize;
            long totalClusters = Math.Min(map.TotalClusters, partition.SourceSizeBytes / clusterSize);
            long edgeClusters = CopyEngine.ComputeEdgeBytes(partition.SourceSizeBytes, clusterSize) / clusterSize;

            long expected = 0;
            foreach (var range in NtfsCloneRanges.Enumerate(map, totalClusters, edgeClusters, CopyEngine.GapToleranceClusters))
            {
                expected += Math.Min(range.ClusterCount * clusterSize,
                    partition.SourceSizeBytes - (range.StartCluster * clusterSize));
            }

            long verified = 0;
            foreach (var range in NtfsCloneRanges.Enumerate(map, totalClusters, edgeClusters, CopyEngine.GapToleranceClusters))
            {
                cancellationToken.ThrowIfCancellationRequested();

                long runStart = range.StartCluster * clusterSize;
                long runLength = Math.Min(range.ClusterCount * clusterSize, partition.SourceSizeBytes - runStart);
                if (runLength <= 0) continue;

                var runComparison = VerificationEngine.CompareRange(
                    source, partition.SourceStartByte + runStart,
                    target, partition.TargetStartByte + runStart,
                    runLength, options.ChunkBytes, cancellationToken);

                if (!runComparison.Match)
                {
                    string message =
                        $"Ověření oddílu #{partition.Source.Number} selhalo: {runComparison.Difference}. " +
                        "Cílový disk nelze považovat za správně přenesený.";
                    _log.Write("VERIFY-FAIL", message);
                    throw new MigrationAbortedException(message);
                }

                verified += runComparison.BytesCompared;
                _reporter.Progress(partition, new CopyProgress(verified, expected, 0));
            }

            verificationNotes.Add(
                $"Oddíl #{partition.Source.Number}: SHA-256 shoda pro {MigrationPlanner.Format(verified)} přenesených dat " +
                "(obsazené shluky a okrajové oblasti).");
            return;
        }

        length = partition.SourceSizeBytes;
        sourceOffset = partition.SourceStartByte;
        targetOffset = partition.TargetStartByte;

        var comparison = VerificationEngine.CompareRange(
            source, sourceOffset, target, targetOffset, length, options.ChunkBytes, cancellationToken);

        if (!comparison.Match)
        {
            string message =
                $"Ověření oddílu #{partition.Source.Number} selhalo: {comparison.Difference}. " +
                "Cílový disk nelze považovat za správně přenesený.";
            _log.Write("VERIFY-FAIL", message);
            throw new MigrationAbortedException(message);
        }

        verificationNotes.Add(
            $"Oddíl #{partition.Source.Number}: SHA-256 shoda pro {MigrationPlanner.Format(length)} " +
            $"(zdroj {comparison.SourceHash[..16]}…).");
    }

    private long ReimagePartition(
        PlannedPartition partition,
        MigrationPlan plan,
        ExecutionOptions options,
        List<string> verificationNotes,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!_platform.Wim.IsAvailable)
        {
            throw new MigrationAbortedException(
                "Zmenšení oddílu vyžaduje nástroj DISM, který není k dispozici (" +
                (_platform.Wim.UnavailableReason ?? "neznámý důvod") + "). Migraci nelze dokončit.");
        }

        if (string.IsNullOrWhiteSpace(plan.TemporaryRootPath))
        {
            throw new MigrationAbortedException("Není zvoleno dočasné úložiště pro obraz WIM.");
        }

        _reporter.PartitionStarted(partition);
        _log.Write(
            "WIM",
            $"Oddíl #{partition.Source.Number}: obraz WIM, {MigrationPlanner.Format(partition.SourceSizeBytes)} -> " +
            $"{MigrationPlanner.Format(partition.TargetSizeBytes)}");

        var request = new WimReimageRequest(
            plan.SourceDisk.Number,
            partition.SourceStartByte,
            partition.SourceSizeBytes,
            plan.TargetDisk.Number,
            partition.TargetStartByte,
            partition.TargetSizeBytes,
            partition.Index,
            plan.TargetSectorSize,
            plan.TemporaryRootPath!,
            partition.Source.FileSystem?.Label,
            $"Oddíl #{partition.Source.Number} ({partition.Source.DisplayName})");

        var result = _platform.Wim.Execute(
            request,
            progress => _reporter.Progress(partition, progress),
            line => _reporter.Detail(line),
            cancellationToken);

        if (!result.Success)
        {
            throw new MigrationAbortedException(
                $"Přenos oddílu #{partition.Source.Number} přes obraz WIM selhal: {result.Error}");
        }

        _reporter.PartitionCompleted(partition, result.BytesWritten);

        // Verify by re-reading the freshly built file system on the target.
        try
        {
            using var reader = _platform.OpenReader(
                _platform.EnumerateDisks().First(d => d.Identity.Number == plan.TargetDisk.Number));

            using var targetVolume = NtfsVolume.Open(reader, partition.TargetStartByte, partition.TargetSizeBytes);
            var info = targetVolume.Info;

            if (info.IsDirty)
            {
                warnings.Add($"Nový svazek v oddílu #{partition.Source.Number} je po přenosu označen jako nečistý.");
            }

            long expected = partition.Source.FileSystem?.Ntfs?.UsedBytes ?? 0;
            if (expected > 0 && info.UsedBytes * 1.05 < expected)
            {
                throw new MigrationAbortedException(
                    $"Nový svazek v oddílu #{partition.Source.Number} obsahuje méně dat, než se očekávalo " +
                    $"({MigrationPlanner.Format(info.UsedBytes)} vs. {MigrationPlanner.Format(expected)}).");
            }

            verificationNotes.Add(
                $"Oddíl #{partition.Source.Number}: svazek WIM znovu načten, " +
                $"{MigrationPlanner.Format(info.UsedBytes)} obsazeno, NTFS {info.MajorVersion}.{info.MinorVersion}.");
        }
        catch (NtfsAnalysisException ex)
        {
            throw new MigrationAbortedException(
                $"Nový svazek v oddílu #{partition.Source.Number} nelze přečíst jako NTFS: {ex.Message}");
        }

        return result.BytesWritten;
    }

    private void ConfigureBoot(MigrationPlan plan, List<string> warnings, List<string> verificationNotes, CancellationToken cancellationToken)
    {
        if (!_platform.Boot.IsAvailable)
        {
            warnings.Add(
                "Konfiguraci zavádění systému nelze provést (" +
                (_platform.Boot.UnavailableReason ?? "nástroje nejsou dostupné") +
                "). Disk bude obsahovat data, ale nemusí být spustitelný.");
            return;
        }

        _reporter.Phase("Nastavení zavádění systému");
        _log.Write("BOOT", $"Režim: {plan.Boot.Mode}");

        long? windows = plan.Boot.WindowsIndex is { } winIndex ? plan.Partitions[winIndex].TargetStartByte : null;
        long? recovery = plan.Boot.RecoveryIndex is { } recIndex ? plan.Partitions[recIndex].TargetStartByte : null;

        // UEFI boots from the EFI system partition; BIOS boots from the active system partition.
        int? systemIndex = plan.Boot.Mode == BootMode.Uefi
            ? plan.Boot.EfiSystemIndex
            : plan.Boot.ActiveIndex ?? plan.Boot.WindowsIndex;

        long? system = systemIndex is { } sysIndex ? plan.Partitions[sysIndex].TargetStartByte : null;

        var request = new BootConfigurationRequest(
            plan.Boot.Mode,
            plan.TargetDisk.Number,
            system,
            windows,
            recovery,
            plan.Boot.RequiresBootsect,
            plan.Boot.RequiresWinReRegistration);

        var result = _platform.Boot.Configure(request, cancellationToken);

        foreach (string action in result.Actions)
        {
            _reporter.Detail(action);
            _log.Write("BOOT", action);
        }

        if (!result.Success)
        {
            foreach (string error in result.Errors)
            {
                warnings.Add("Zavádění systému: " + error);
                _log.Write("BOOT-ERROR", error);
            }

            verificationNotes.Add("Konfigurace zavádění systému nebyla dokončena – viz varování.");
            return;
        }

        verificationNotes.Add("Zavádění systému bylo znovu vytvořeno nástroji Microsoft (bcdboot).");
    }

    private List<string> VerifyPartitionTable(IBlockDevice target, MigrationPlan plan, List<string> warnings)
    {
        var notes = new List<string>();

        var read = PartitionTableReader.Read(target);
        if (read.Table is null)
        {
            warnings.Add("Výslednou tabulku oddílů na cílovém disku nelze znovu přečíst: " +
                         string.Join("; ", read.Errors));
            return notes;
        }

        if (read.Table.Kind != plan.SourceTable.Kind)
        {
            warnings.Add("Typ tabulky oddílů na cílovém disku neodpovídá zdroji.");
        }

        if (read.Table.Partitions.Count != plan.Partitions.Count)
        {
            warnings.Add(
                $"Počet oddílů na cílovém disku ({read.Table.Partitions.Count}) neodpovídá plánu " +
                $"({plan.Partitions.Count}).");
        }

        int mismatches = 0;
        foreach (var planned in plan.Partitions)
        {
            long startLba = planned.TargetStartByte / plan.TargetSectorSize;
            var actual = read.Table.Partitions.FirstOrDefault(p => p.StartLba == startLba);

            if (actual is null)
            {
                warnings.Add($"Oddíl #{planned.Source.Number} nebyl na cílovém disku nalezen.");
                mismatches++;
                continue;
            }

            if (planned.TargetTypeGuid != Guid.Empty && actual.TypeGuid != planned.TargetTypeGuid)
            {
                warnings.Add($"Oddíl #{planned.Source.Number} má na cíli jiný typ, než určuje plán.");
                mismatches++;
            }

            if (plan.PreserveDiskIdentifiers &&
                planned.TargetUniqueGuid != Guid.Empty &&
                actual.UniqueGuid != planned.TargetUniqueGuid)
            {
                warnings.Add($"Oddíl #{planned.Source.Number} nemá na cíli původní identifikátor.");
                mismatches++;
            }
        }

        if (mismatches == 0)
        {
            notes.Add($"Tabulka oddílů na cílovém disku odpovídá plánu ({read.Table.Partitions.Count} oddílů, {read.Table.Kind}).");
        }

        return notes;
    }

    private void WritePartitionTable(IBlockDevice target, IBlockReader source, MigrationPlan plan, bool final)
    {
        // While copying, fresh identifiers are used so that the source and the target can
        // be mounted at the same time without the operating system confusing their volumes.
        bool useFinalIdentifiers = final && plan.PreserveDiskIdentifiers;

        if (plan.SourceTable.Kind == PartitionTableKind.Gpt)
        {
            var entries = plan.Partitions.Select(p => new PartitionWriteEntry
            {
                Number = p.Index,
                StartLba = p.TargetStartByte / plan.TargetSectorSize,
                SectorCount = p.TargetSizeBytes / plan.TargetSectorSize,
                TypeGuid = p.TargetTypeGuid == Guid.Empty ? GptPartitionTypes.MicrosoftBasicData : p.TargetTypeGuid,
                UniqueGuid = useFinalIdentifiers ? p.TargetUniqueGuid : Guid.NewGuid(),
                Name = p.TargetName,
                Attributes = p.TargetAttributes,
            }).ToList();

            ValidateEntrySizes(plan, entries);

            PartitionTableWriter.WriteGpt(target, new GptWriteSpec
            {
                DiskGuid = useFinalIdentifiers ? plan.TargetDiskGuid : Guid.NewGuid(),
                Entries = entries,
            });

            return;
        }

        // The MBR boot code decides whether a BIOS machine can start at all, so it is taken from
        // the source disk - the target's own sector 0 has already been cleared by this point.
        byte[] bootCode = new byte[446];
        if (plan.SourceTable.Kind == PartitionTableKind.Mbr)
        {
            var sourceSector = new byte[source.SectorSize];
            source.ReadAt(0, sourceSector);
            Array.Copy(sourceSector, bootCode, 446);
        }

        var mbrEntries = plan.Partitions.Select(p => new PartitionWriteEntry
        {
            Number = p.Index,
            StartLba = p.TargetStartByte / plan.TargetSectorSize,
            SectorCount = p.TargetSizeBytes / plan.TargetSectorSize,
            MbrType = p.TargetMbrType == 0
                ? (p.Role == PartitionRole.EfiSystem ? MbrPartitionTypes.EfiSystem : MbrPartitionTypes.NtfsExFat)
                : p.TargetMbrType,
            IsActive = p.TargetIsActive,
            IsLogical = p.TargetIsLogical,
        }).ToList();

        ValidateEntrySizes(plan, mbrEntries);

        PartitionTableWriter.WriteMbr(target, new MbrWriteSpec
        {
            DiskSignature = useFinalIdentifiers ? plan.TargetMbrSignature : (uint)Random.Shared.NextInt64(1, uint.MaxValue),
            BootCode = bootCode,
            Entries = mbrEntries,
        });
    }

    private static void ValidateEntrySizes(MigrationPlan plan, List<PartitionWriteEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.StartLba * plan.TargetSectorSize != plan.Partitions.First(p => p.Index == entry.Number).TargetStartByte)
            {
                throw new InvalidOperationException(
                    $"Oddíl #{entry.Number} není zarovnán na velikost sektoru cílového disku.");
            }
        }
    }
}
