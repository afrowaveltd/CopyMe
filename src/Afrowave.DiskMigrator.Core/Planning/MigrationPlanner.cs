using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Analysis;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;

namespace Afrowave.DiskMigrator.Core.Planning;

public sealed class PlannerOptions
{
    public const long Mebibyte = 1024L * 1024;
    public const long Gibibyte = 1024L * 1024 * 1024;

    /// <summary>Keep the original GPT disk GUID / MBR signature so that boot entries keep working.</summary>
    public bool PreserveDiskIdentifiers { get; init; } = true;

    public bool EnableVerification { get; init; } = true;

    /// <summary>Root of the volume used for temporary WIM images, e.g. "E:\".</summary>
    public string? TemporaryRootPath { get; init; }

    public long TemporaryFreeBytes { get; init; }

    public FilesystemKind TemporaryFilesystem { get; init; } = FilesystemKind.Unknown;

    /// <summary>Proceed even when the temporary volume does not look big enough.</summary>
    public bool IgnoreTemporarySpaceCheck { get; init; }

    /// <summary>Write zeros over the unallocated clusters of NTFS partitions on the target.</summary>
    public bool ZeroFreeClusters { get; init; }

    public double NtfsSlackFraction { get; init; } = 0.10;

    public long NtfsMinimumSlackBytes { get; init; } = 512 * Mebibyte;

    /// <summary>Expected compressed size of a WIM relative to the used bytes of the volume.</summary>
    public double WimCompressionFactor { get; init; } = 0.80;

    /// <summary>
    /// Result of inspecting the target disk. The target must define no partitions at all;
    /// a null value means "not inspected yet" and only produces a warning here - the executor
    /// always re-checks before writing.
    /// </summary>
    public Partitioning.TargetInspection? TargetInspection { get; init; }

    /// <summary>
    /// False when the current machine cannot run bcdboot/bootsect. A plan that would need boot
    /// configuration is then refused instead of silently producing a disk that cannot start.
    /// </summary>
    public bool BootConfigurationAvailable { get; init; } = true;

    public string? BootConfigurationUnavailableReason { get; init; }
}

/// <summary>
/// Turns "source disk + target disk" into a fully specified, reviewable migration plan.
/// It never touches a device - it only decides what should happen.
/// </summary>
public static class MigrationPlanner
{
    public static MigrationPlan Build(DiskAnalysis source, PhysicalDisk targetDisk, PlannerOptions options)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var preparation = new List<string>();

        var targetIdentity = targetDisk.Identity;

        if (!CheckTargetIsEmpty(options, errors, warnings))
        {
            return Failed(source, targetDisk, options, errors, warnings, preparation);
        }

        if (source.Table is null || !source.IsUsable)
        {
            errors.AddRange(source.Errors);
            if (source.Errors.Count == 0)
            {
                errors.Add("Zdrojový disk nelze analyzovat.");
            }

            return Failed(source, targetDisk, options, errors, warnings, preparation);
        }

        var table = source.Table;
        int sourceSectorSize = table.LogicalSectorSize;
        int targetSectorSize = targetIdentity.LogicalSectorSize;

        var partitions = source.Partitions.OrderBy(p => p.StartLba).ToList();

        if (partitions.Count == 0)
        {
            errors.Add("Zdrojový disk neobsahuje žádný oddíl, který by bylo možné přenést.");
            return Failed(source, targetDisk, options, errors, warnings, preparation);
        }

        if (source.ContainsContainerPartition)
        {
            errors.Add(
                "Zdrojový disk obsahuje kontejner, který tento nástroj neumí bezpečně přenést " +
                "(dynamický disk, LDM nebo Storage Spaces). Migraci odmítáme.");
        }

        if (sourceSectorSize != targetSectorSize)
        {
            warnings.Add(
                $"Zdrojový disk používá sektory {sourceSectorSize} B, cílový {targetSectorSize} B. " +
                "Rozložení bude přepočítáno na hranice 1 MiB.");
        }

        if (table.IsHybridMbr)
        {
            warnings.Add("Zdrojový disk má hybridní MBR. Na cíli zůstane pouze GPT.");
        }

        // ---------------------------------------------------------- geometry of the target

        long targetBytes = targetIdentity.SizeBytes;
        long targetSectors = targetBytes / targetSectorSize;

        if (targetSectors <= 0)
        {
            errors.Add("Cílový disk hlásí neplatnou velikost.");
            return Failed(source, targetDisk, options, errors, warnings, preparation);
        }

        long layoutStart;
        long layoutEndExclusive;

        if (table.Kind == PartitionTableKind.Gpt)
        {
            layoutStart = GptLayout.FirstUsableLba(targetSectorSize) * targetSectorSize;
            layoutEndExclusive = (GptLayout.LastUsableLba(targetSectors, targetSectorSize) + 1) * targetSectorSize;
        }
        else
        {
            layoutStart = targetSectorSize;
            layoutEndExclusive = targetSectors * targetSectorSize;
        }

        layoutStart = AlignUp(Math.Max(layoutStart, PlannerOptions.Mebibyte), PlannerOptions.Mebibyte);

        long usableBytes = layoutEndExclusive - layoutStart;
        if (usableBytes <= 0)
        {
            errors.Add("Cílový disk je příliš malý na vytvoření tabulky oddílů.");
            return Failed(source, targetDisk, options, errors, warnings, preparation);
        }

        // ---------------------------------------------------------- sizing

        int count = partitions.Count;
        var desired = new long[count];
        var minimum = new long[count];
        var sourceStarts = new long[count];
        var shrinkable = new bool[count];

        for (int i = 0; i < count; i++)
        {
            var partition = partitions[i];
            long sourceSize = partition.SectorCount * sourceSectorSize;

            sourceStarts[i] = partition.StartLba * sourceSectorSize;
            desired[i] = sourceSize;
            minimum[i] = ComputeMinimumSize(partition, sourceSize, options);
            shrinkable[i] = minimum[i] < desired[i];
        }

        long alignmentReserve = (long)count * PlannerOptions.Mebibyte;
        long capacity = usableBytes - alignmentReserve;

        long minimumTotal = minimum.Sum();
        if (minimumTotal > capacity)
        {
            errors.Add(
                $"Obsah se na cílový disk nevejde ani po zmenšení. " +
                $"Potřebujeme nejméně {Format(minimumTotal)}, k dispozici je {Format(Math.Max(0, capacity))}.");

            foreach (int index in Enumerable.Range(0, count))
            {
                var partition = partitions[index];
                if (minimum[index] >= desired[index] && partition.Role != PartitionRole.MicrosoftReserved)
                {
                    string because = partition.FileSystem?.IsEncrypted == true
                        ? "zašifrovaný oddíl nelze zmenšit"
                        : "nelze zmenšit";

                    errors.Add(
                        $"  • oddíl #{partition.Number} ({partition.DisplayName}, {partition.FileSystem?.DisplayName ?? "neznámý"}): " +
                        $"lze přenést nejmenším {Format(minimum[index])} – {because}.");
                }
            }

            return Failed(source, targetDisk, options, errors, warnings, preparation);
        }

        var sizes = (long[])desired.Clone();
        var layout = SimulateLayout(sizes, sourceStarts, desired, layoutStart, targetSectorSize);

        if (layout.EndExclusive > layoutEndExclusive)
        {
            bool shrunk = TryShrink(
                sizes, minimum, shrinkable, sourceStarts, desired, layoutStart, layoutEndExclusive, targetSectorSize,
                out var finalLayout, out var shrunkIndexes);

            if (!shrunk)
            {
                errors.Add(
                    "Obsah se na cílový disk nevejde. Ani po zmenšení všech podporovaných oddílů " +
                    "není na cíli dostatek místa.");
                return Failed(source, targetDisk, options, errors, warnings, preparation);
            }

            layout = finalLayout;

            foreach (int index in shrunkIndexes)
            {
                var partition = partitions[index];
                warnings.Add(
                    $"Oddíl #{partition.Number} ({partition.DisplayName}) bude zmenšen z " +
                    $"{Format(desired[index])} na {Format(sizes[index])}.");
            }
        }

        // ---------------------------------------------------------- strategies

        var plannedPartitions = new List<PlannedPartition>(count);

        for (int i = 0; i < count; i++)
        {
            var partition = partitions[i];
            long targetSize = layout.Sizes[i];
            bool isShrunk = targetSize < desired[i];

            var strategy = DetermineStrategy(partition, isShrunk);

            if (strategy == CopyStrategy.WimReimage && partition.FileSystem?.Ntfs is null)
            {
                errors.Add($"Oddíl #{partition.Number} nelze zmenšit – chybí metadata NTFS.");
                continue;
            }

            if (strategy == CopyStrategy.WimReimage && partition.FileSystem?.Ntfs?.IsDirty == true)
            {
                errors.Add(
                    $"Oddíl #{partition.Number} ({partition.DisplayName}) není čistě odpojen a musí se zmenšit. " +
                    "Zmenšení vyžaduje připojení svazku pouze pro čtení, což systém u nečistého NTFS obvykle odmítne. " +
                    "Migraci odmítáme, abychom neskončili s částečně zapsaným cílovým diskem.");
                errors.Add(
                    "  ↳ Řešení: použijte cílový disk, na který se obsah vejde bez zmenšení – pak se použije " +
                    "kopie po shlucích, která žádné připojení nepotřebuje. Nebo zdrojový svazek nejprve " +
                    "nechte zkontrolovat (chkdsk) na původním počítači.");
                continue;
            }

            plannedPartitions.Add(new PlannedPartition
            {
                Index = i + 1,
                Source = partition,
                Role = partition.Role,
                Strategy = strategy,
                SourceStartByte = sourceStarts[i],
                SourceSizeBytes = desired[i],
                TargetStartByte = layout.Starts[i],
                TargetSizeBytes = targetSize,
                TargetTypeGuid = partition.TypeGuid,
                TargetUniqueGuid = options.PreserveDiskIdentifiers
                    ? (partition.UniqueGuid == Guid.Empty ? Guid.NewGuid() : partition.UniqueGuid)
                    : Guid.NewGuid(),
                TargetMbrType = partition.MbrType,
                TargetName = partition.Name,
                TargetAttributes = partition.Attributes,
                TargetIsActive = partition.IsActive,
                TargetIsLogical = partition.IsLogical,
            });
        }

        if (errors.Count > 0)
        {
            return Failed(source, targetDisk, options, errors, warnings, preparation);
        }

        // ---------------------------------------------------------- boot plan

        var boot = BuildBootPlan(table, plannedPartitions, source, warnings, errors);

        if (boot.RequiresBcdboot && !options.BootConfigurationAvailable)
        {
            errors.Add(
                "Zavádění systému nelze na tomto počítači obnovit (" +
                (options.BootConfigurationUnavailableReason ?? "nástroje nejsou dostupné") + "). " +
                "Výsledný disk by pravděpodobně nebyl spustitelný, proto migraci odmítáme.");
        }

        // ---------------------------------------------------------- partitions / notes

        if (plannedPartitions.Any(p => p.IsResized && !p.IsShrunk))
        {
            warnings.Add("Některé oddíly budou na cíli o několik sektorů větší kvůli zarovnání.");
        }

        bool offsetsChanged = !layout.KeptSourceOffsets;
        if (offsetsChanged)
        {
            warnings.Add(
                "Pozice oddílů na cílovém disku se změní. Písmena jednotek ani odkazy na absolutní pozice " +
                "nemusí po migraci odpovídat původním.");
        }

        foreach (var partition in plannedPartitions)
        {
            if (partition.Strategy == CopyStrategy.RawClone && partition.TargetSizeBytes < partition.SourceSizeBytes)
            {
                errors.Add(
                    $"Oddíl #{partition.Source.Number} ({partition.Source.DisplayName}) vyžaduje sektorovou kopii, " +
                    "ale na cílovém disku pro něj není místo v původní velikosti.");
            }
        }

        if (errors.Count > 0)
        {
            return Failed(source, targetDisk, options, errors, warnings, preparation);
        }

        // ---------------------------------------------------------- temporary storage

        long requiredTemp = 0;
        foreach (var partition in plannedPartitions.Where(p => p.Strategy == CopyStrategy.WimReimage))
        {
            long used = partition.Source.FileSystem?.Ntfs?.UsedBytes ?? partition.SourceSizeBytes;
            long estimate = AlignUp((long)(used * options.WimCompressionFactor), PlannerOptions.Mebibyte)
                            + (512 * PlannerOptions.Mebibyte);
            requiredTemp = Math.Max(requiredTemp, estimate);
        }

        if (requiredTemp > 0)
        {
            if (string.IsNullOrWhiteSpace(options.TemporaryRootPath))
            {
                errors.Add(
                    "Pro tuto migraci je nutné zvolit dočasné úložiště – část obsahu musí být " +
                    $"přenesena přes obraz WIM (odhadem {Format(requiredTemp)}).");
            }
            else if (!options.IgnoreTemporarySpaceCheck && requiredTemp > options.TemporaryFreeBytes)
            {
                errors.Add(
                    $"Dočasné úložiště {options.TemporaryRootPath} nemá dostatek místa. " +
                    $"Je potřeba přibližně {Format(requiredTemp)}, volné je {Format(options.TemporaryFreeBytes)}. " +
                    "Připojte větší úložiště nebo použijte cílový disk, na který se obsah vejde bez zmenšení.");
            }
            else
            {
                preparation.Add($"Dočasný obraz WIM bude ukládán do {options.TemporaryRootPath}.");
            }
        }

        if (errors.Count > 0)
        {
            return Failed(source, targetDisk, options, errors, warnings, preparation);
        }

        // ---------------------------------------------------------- preparation notes

        preparation.Insert(0, "Zdrojový disk bude přepnut do režimu pouze pro čtení.");
        preparation.Add("Z cílového disku budou odpojeny všechny svazky.");
        preparation.Add("Tabulka oddílů cílového disku bude přepsána navrženým rozložením.");

        if (options.EnableVerification)
        {
            preparation.Add("Po dokončení bude zapsaný obsah znovu přečten a porovnán.");
        }

        if (options.ZeroFreeClusters)
        {
            preparation.Add("Volné shluky na cílovém disku budou vynulovány.");
        }

        var plan = new MigrationPlan
        {
            SourceDisk = source.Disk.Identity,
            TargetDisk = targetIdentity,
            SourceTable = table,
            Partitions = plannedPartitions,
            Warnings = warnings,
            Errors = errors,
            Boot = boot,
            TargetDiskGuid = options.PreserveDiskIdentifiers
                ? (table.DiskGuid == Guid.Empty ? Guid.NewGuid() : table.DiskGuid)
                : Guid.NewGuid(),
            TargetMbrSignature = table.MbrDiskSignature,
            TargetSizeBytes = targetBytes,
            TargetSectorSize = targetSectorSize,
            TargetUsableBytes = usableBytes,
            SourceSizeBytes = table.DiskBytes,
            RequiredTemporaryBytes = requiredTemp,
            TemporaryRootPath = options.TemporaryRootPath,
            TemporaryFilesystem = options.TemporaryFilesystem,
            PreserveDiskIdentifiers = options.PreserveDiskIdentifiers,
            VerificationEnabled = options.EnableVerification,
            Preparation = preparation,
            TargetSummary = options.TargetInspection?.Summary ?? string.Empty,
        };

        return plan;
    }

    // ---------------------------------------------------------------- helpers

    private static CopyStrategy DetermineStrategy(PartitionInfo partition, bool isShrunk)
    {
        if (partition.IsMicrosoftReserved) return CopyStrategy.RecreateEmpty;

        bool ntfsReady = partition.FileSystem is { Kind: FilesystemKind.Ntfs, Ntfs: not null };

        if (isShrunk)
        {
            return ntfsReady ? CopyStrategy.WimReimage : CopyStrategy.RawClone;
        }

        if (ntfsReady) return CopyStrategy.NtfsAllocationClone;

        return CopyStrategy.RawClone;
    }

    private static long ComputeMinimumSize(PartitionInfo partition, long sourceSize, PlannerOptions options)
    {
        if (partition.Role == PartitionRole.MicrosoftReserved) return sourceSize;

        if (partition.FileSystem is { Kind: FilesystemKind.Ntfs, Ntfs: not null } && partition.FileSystem.Ntfs is { } ntfs)
        {
            long slack = Math.Max(options.NtfsMinimumSlackBytes, (long)(ntfs.UsedBytes * options.NtfsSlackFraction));
            long minimum = AlignUp(ntfs.UsedBytes + slack, PlannerOptions.Mebibyte);
            return Math.Min(minimum, sourceSize);
        }

        return sourceSize;
    }

    private sealed record LayoutResult(long[] Starts, long[] Sizes, long EndExclusive, bool KeptSourceOffsets);

    /// <summary>
    /// Lays partitions out on the target. A partition keeps its original offset only while the
    /// partitions before it also kept their original offset and size - otherwise the whole tail
    /// would stay glued to positions that no longer exist, which is how a shrink attempt can
    /// silently produce a layout that never fits.
    /// </summary>
    private static LayoutResult SimulateLayout(long[] sizes, long[] sourceStarts, long[] sourceSizes, long layoutStart, int targetSectorSize)
    {
        int count = sizes.Length;
        var starts = new long[count];
        var alignedSizes = new long[count];
        long cursor = AlignUp(layoutStart, PlannerOptions.Mebibyte);
        bool shifted = false;
        bool keptAll = true;

        long previousSourceEnd = long.MinValue;
        long previousTargetEnd = long.MinValue;

        for (int i = 0; i < count; i++)
        {
            long size = AlignUp(sizes[i], targetSectorSize);
            long candidate = sourceStarts[i];

            bool tailAlignedWithSource = i == 0 || previousTargetEnd == previousSourceEnd;

            bool canKeep = !shifted
                           && tailAlignedWithSource
                           && candidate >= cursor
                           && candidate >= layoutStart
                           && candidate % targetSectorSize == 0;

            long start = canKeep ? candidate : AlignUp(cursor, PlannerOptions.Mebibyte);
            if (!canKeep)
            {
                shifted = true;
                keptAll = false;
            }

            starts[i] = start;
            alignedSizes[i] = size;
            cursor = start + size;

            previousSourceEnd = sourceStarts[i] + sourceSizes[i];
            previousTargetEnd = cursor;
        }

        return new LayoutResult(starts, alignedSizes, cursor, keptAll);
    }

    private static bool TryShrink(
        long[] sizes,
        long[] minimum,
        bool[] shrinkable,
        long[] sourceStarts,
        long[] sourceSizes,
        long layoutStart,
        long layoutEndExclusive,
        int targetSectorSize,
        out LayoutResult layout,
        out List<int> shrunkIndexes)
    {
        shrunkIndexes = [];
        layout = SimulateLayout(sizes, sourceStarts, sourceSizes, layoutStart, targetSectorSize);

        for (int pass = 0; pass < 64; pass++)
        {
            if (layout.EndExclusive <= layoutEndExclusive) return true;

            bool progress = false;

            for (int i = sizes.Length - 1; i >= 0; i--)
            {
                if (layout.EndExclusive <= layoutEndExclusive) break;
                if (!shrinkable[i]) continue;

                long room = sizes[i] - minimum[i];
                if (room <= 0) continue;

                long over = layout.EndExclusive - layoutEndExclusive;
                long take = Math.Min(room, over);
                take = AlignUp(take, PlannerOptions.Mebibyte);

                long candidate = sizes[i] - take;
                if (candidate < minimum[i])
                {
                    candidate = AlignUp(minimum[i], PlannerOptions.Mebibyte);
                }

                if (candidate >= sizes[i]) continue;

                sizes[i] = candidate;
                if (!shrunkIndexes.Contains(i)) shrunkIndexes.Add(i);

                layout = SimulateLayout(sizes, sourceStarts, sourceSizes, layoutStart, targetSectorSize);
                progress = true;
            }

            if (!progress) break;
        }

        return layout.EndExclusive <= layoutEndExclusive;
    }

    private static BootPlan BuildBootPlan(
        PartitionTable table,
        List<PlannedPartition> partitions,
        DiskAnalysis source,
        List<string> warnings,
        List<string> errors)
    {
        int? windowsIndex = IndexOfRole(partitions, PartitionRole.OperatingSystem);
        int? espIndex = IndexOfRole(partitions, PartitionRole.EfiSystem);
        int? recoveryIndex = IndexOfRole(partitions, PartitionRole.WindowsRecovery);
        int? activeIndex = IndexOfPredicate(partitions, p => p.TargetIsActive);

        var steps = new List<string>();
        var notes = new List<string>();

        if (windowsIndex is null)
        {
            notes.Add("Na zdrojovém disku nebyl rozpoznán operační systém. Zavádění systému nebude konfigurováno.");

            bool hasForeignOs = partitions.Any(p => p.FileSystem?.Kind is
                FilesystemKind.Ext2To4 or FilesystemKind.Btrfs or FilesystemKind.Xfs or FilesystemKind.Apfs);

            if (hasForeignOs)
            {
                notes.Add(
                    "Byl rozpoznán souborový systém jiného operačního systému (Linux/Apple). Obsah bude přenesen " +
                    "sektorově, ale funkčnost jeho zavádění na novém disku tímto nástrojem ověřena nebyla.");
            }

            return new BootPlan
            {
                Mode = BootMode.None,
                EfiSystemIndex = espIndex,
                WindowsIndex = null,
                RecoveryIndex = recoveryIndex,
                ActiveIndex = activeIndex,
                Steps = steps,
                Notes = notes,
            };
        }

        var bootMode = table.Kind == PartitionTableKind.Gpt && espIndex is not null
            ? BootMode.Uefi
            : BootMode.Bios;

        if (table.Kind == PartitionTableKind.Gpt && espIndex is null)
        {
            // A GPT disk with a Windows installation but without an EFI system partition cannot be
            // made bootable by this tool: UEFI needs the ESP, and BIOS boot from GPT is a different
            // layout. Refuse instead of promising something we cannot deliver.
            errors.Add(
                "Disk je rozdělen jako GPT a obsahuje Windows, ale nebyl nalezen žádný EFI System Partition. " +
                "Zavádění takového disku tento nástroj nedokáže obnovit. Migraci odmítáme, abychom " +
                "nevytvořili disk, který nepůjde nastartovat.");
        }

        if (bootMode == BootMode.Uefi)
        {
            steps.Add("Na cílovém EFI System Partition bude znovu vytvořeno zavádění systému (bcdboot /f UEFI).");
            steps.Add("BCD bude odkazovat přímo na nový systémový oddíl na cílovém disku.");
        }
        else
        {
            int systemIndex = activeIndex ?? windowsIndex.Value;
            steps.Add($"Zaváděcí soubory budou zapsány na oddíl #{systemIndex + 1} (bcdboot /f BIOS).");
            steps.Add("Zaváděcí kód MBR bude obnoven (bootsect /nt60) a oddíl označen jako aktivní.");
            steps.Add("Disk si zachová původní signaturu MBR, aby zůstala platná písmena jednotek.");
        }

        bool registerWinRe = recoveryIndex is not null;
        if (registerWinRe)
        {
            steps.Add($"Prostředí pro obnovení bude znovu zaregistrováno na oddíle #{recoveryIndex!.Value + 1} (reagentc).");
        }
        else
        {
            notes.Add("Zdrojový disk neobsahuje samostatný oddíl pro obnovení Windows.");
        }

        if (source.Windows?.HasHibernationFile == true)
        {
            warnings.Add(
                "Zdrojové Windows byly ukončeny hibernací nebo rychlým startem. " +
                "Doporučujeme zdrojový disk před migrací řádně vypnout.");
        }

        return new BootPlan
        {
            Mode = bootMode,
            EfiSystemIndex = espIndex,
            WindowsIndex = windowsIndex,
            RecoveryIndex = recoveryIndex,
            ActiveIndex = activeIndex,
            RequiresBcdboot = true,
            RequiresBootsect = bootMode == BootMode.Bios,
            RequiresWinReRegistration = registerWinRe,
            Steps = steps,
            Notes = notes,
        };
    }

    private static int? IndexOfRole(List<PlannedPartition> partitions, PartitionRole role) =>
        IndexOfPredicate(partitions, p => p.Role == role);

    private static int? IndexOfPredicate(List<PlannedPartition> partitions, Func<PlannedPartition, bool> predicate)
    {
        int index = partitions.FindIndex(p => predicate(p));
        return index >= 0 ? index : null;
    }

    private static MigrationPlan Failed(
        DiskAnalysis source,
        PhysicalDisk targetDisk,
        PlannerOptions options,
        List<string> errors,
        List<string> warnings,
        List<string> preparation) =>
        new()
        {
            SourceDisk = source.Disk.Identity,
            TargetDisk = targetDisk.Identity,
            SourceTable = source.Table ?? new PartitionTable
            {
                Kind = PartitionTableKind.Unknown,
                LogicalSectorSize = source.Disk.Identity.LogicalSectorSize,
                DiskSectors = source.Disk.Identity.SizeBytes / Math.Max(1, source.Disk.Identity.LogicalSectorSize),
                Partitions = [],
            },
            Partitions = [],
            Warnings = warnings,
            Errors = errors,
            Boot = new BootPlan { Mode = BootMode.None },
            TargetDiskGuid = Guid.Empty,
            TargetMbrSignature = 0,
            TargetSizeBytes = targetDisk.Identity.SizeBytes,
            TargetSectorSize = targetDisk.Identity.LogicalSectorSize,
            TargetUsableBytes = 0,
            SourceSizeBytes = source.Disk.Identity.SizeBytes,
            RequiredTemporaryBytes = 0,
            TemporaryRootPath = options.TemporaryRootPath,
            TemporaryFilesystem = options.TemporaryFilesystem,
            PreserveDiskIdentifiers = options.PreserveDiskIdentifiers,
            VerificationEnabled = options.EnableVerification,
            Preparation = preparation,
            TargetSummary = options.TargetInspection?.Summary ?? string.Empty,
        };

    /// <summary>
    /// The target disk must define no partitions. The tool never removes partitions to make a
    /// disk eligible, and anything ambiguous is refused rather than guessed.
    /// </summary>
    private static bool CheckTargetIsEmpty(PlannerOptions options, List<string> errors, List<string> warnings)
    {
        var inspection = options.TargetInspection;

        if (inspection is null)
        {
            warnings.Add(
                "Stav cílového disku nebyl před sestavením plánu ověřen. Před zápisem bude zkontrolován znovu.");
            return true;
        }

        if (inspection.IsEmpty)
        {
            // Only genuine caveats are repeated; the neutral summary is shown by the plan view.
            foreach (string note in inspection.Notes)
            {
                warnings.Add("Cílový disk: " + note);
            }

            return true;
        }

        errors.Add(
            "Cílový disk není prázdný (" + inspection.ShortDescription + "). " +
            "Migrace je možná jen na disk bez definovaných oddílů.");
        errors.AddRange(inspection.Reasons);
        errors.Add(
            "Nástroj nikdy nemaže oddíly, aby cílový disk uvolnil. Vyberte jiný disk – " +
            "nebo jej nejprve vyprázdněte mimo tento nástroj.");
        return false;
    }

    public static long AlignUp(long value, long alignment)
    {
        if (alignment <= 0) return value;
        long remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    public static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value:0.##} {units[unit]}";
    }
}
