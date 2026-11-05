using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Analysis;
using Afrowave.DiskMigrator.Core.Execution;
using Afrowave.DiskMigrator.Core.Logging;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Planning;
using Afrowave.DiskMigrator.Core.Safety;
using Afrowave.DiskMigrator.Platform.Image;
using Afrowave.DiskMigrator.Platform.Windows;

namespace Afrowave.DiskMigrator.Cli;

internal static class Program
{
    public const string Version = "1.0.0";

    /// <summary>Documented exit codes, so an operator script can tell refusals from success.</summary>
    internal static class ExitCodes
    {
        public const int Success = 0;
        public const int UnexpectedError = 1;
        public const int PlatformUnsupported = 2;
        public const int NotElevated = 3;
        public const int NoDisks = 4;
        public const int SourceUnusable = 5;
        public const int PlanRefused = 6;
        public const int MigrationFailed = 7;
    }

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Neočekávaná chyba: " + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine("Nástroj se ukončil. Pokud byl cílový disk rozpracovaný, nepoužívejte jej.");
            return ExitCodes.UnexpectedError;
        }
    }

    private static int Run(string[] args)
    {
        var options = CommandLineOptions.Parse(args);

        if (options.ShowHelp)
        {
            CommandLineOptions.PrintHelp();
            return 0;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine("Afrowave DiskMigrator " + Version);
            return 0;
        }

        var ui = new ConsoleUi();
        ui.Clear();
        ui.Banner(Version);

        if (!TryCreatePlatform(ui, options, out IDiskPlatform? platform, out int platformExitCode))
        {
            return platformExitCode;
        }

        using var log = CreateAuditLog(ui, options.LogPath);
        log.Write("START", platform!.PlatformName);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cancellation.IsCancellationRequested)
            {
                return; // second Ctrl+C lets the runtime terminate the process
            }

            e.Cancel = true;
            cancellation.Cancel();
            ui.WriteLine(string.Empty);
            ui.Warning("Požadavek na přerušení přijat. Nástroj dokončí rozpracovanou operaci a poté se ukončí.");
        };

        try
        {
            return RunMigrationFlow(ui, platform, options, log, cancellation.Token);
        }
        finally
        {
            (platform as IDisposable)?.Dispose();
        }
    }

    private static int RunMigrationFlow(
        ConsoleUi ui,
        IDiskPlatform platform,
        CommandLineOptions options,
        IAuditLog log,
        CancellationToken cancellationToken)
    {
        // ---------------------------------------------------------- disk inventory

        var environment = platform.GetSafetyEnvironment();
        var disks = platform.EnumerateDisks();

        if (disks.Count == 0)
        {
            ui.Error("Nebyl nalezen žádný fyzický disk.");
            return ExitCodes.NoDisks;
        }

        var evaluations = DiskSafetyPolicy.EvaluateAll(disks, environment);

        ui.Detail("Zjišťuji, které disky jsou bez oddílů…");
        var targetStates = InspectTargetStates(platform, evaluations);

        PlanPresenter.ShowDiskList(ui, evaluations, targetStates, "Dostupné disky");
        ui.Detail("Disky označené CHRÁNĚN patří servisnímu počítači. Nelze je použít jako zdroj ani jako cíl.");

        var protectedDisks = evaluations.Where(e => e.IsProtected).ToList();
        if (protectedDisks.Count == 0)
        {
            ui.Warning("Nebyl rozpoznán žádný chráněný disk servisního počítače. " +
                       "Zkontrolujte, že nástroj běží na servisním počítači, a pokračujte opatrně.");
        }

        // ---------------------------------------------------------- source

        var sourceEvaluation = SelectDisk(ui, evaluations, targetStates, options.SourceDiskNumber,
            "Zadejte číslo ZDROJOVÉHO disku (pouze pro čtení)",
            e => DiskRoleRules.CanServeAsSource(e, targetStates.GetValueOrDefault(e.Disk.Identity.Number)),
            "Jako zdroj lze vybrat jen disk, který obsahuje oddíly.");

        if (sourceEvaluation is null) return ExitCodes.PlanRefused;

        // The source disk is switched to read-only *before* anything looks inside it.
        // Detecting the operating system means mounting its volumes; if the disk were still
        // writable the running Windows could replay an NTFS journal or touch System Volume
        // Information, changing the customer's disk before the migration even started.
        bool sourceMarkedReadOnly = false;
        if (platform.TrySetDiskReadOnly(sourceEvaluation.Disk, true))
        {
            sourceMarkedReadOnly = true;
        }
        else
        {
            ui.Warning(
                "Zdrojový disk se nepodařilo přepnout do režimu pouze pro čtení. " +
                "Obsah jeho svazků nebude prozkoumán a operační systém nemusí být rozpoznán.");
        }

        try
        {
            return MigrateSelectedDisks(
                ui, platform, options, log, cancellationToken,
                evaluations, environment, targetStates, sourceEvaluation,
                probeContents: sourceMarkedReadOnly);
        }
        finally
        {
            // Always hand the customer's disk back in its normal state.
            if (sourceMarkedReadOnly && platform.TrySetDiskReadOnly(sourceEvaluation.Disk, false))
            {
                ui.Detail("Zdrojový disk vrácen do běžného režimu (read-only zrušen).");
            }
            else if (sourceMarkedReadOnly)
            {
                ui.Warning("Read-only příznak zdrojového disku se nepodařilo zrušit – po restartu zmizí.");
            }
        }
    }


    /// <summary>
    /// Everything that happens once the operator has chosen the source disk: analysis,
    /// target selection, planning, confirmation and execution.
    /// </summary>
    private static int MigrateSelectedDisks(
        ConsoleUi ui,
        IDiskPlatform platform,
        CommandLineOptions options,
        IAuditLog log,
        CancellationToken cancellationToken,
        IReadOnlyList<DiskEvaluation> evaluations,
        SafetyEnvironment environment,
        IReadOnlyDictionary<int, TargetInspection> targetStates,
        DiskEvaluation sourceEvaluation,
        bool probeContents)
    {
        var sourceIdentity = sourceEvaluation.Disk.Identity;

        ui.Line();
        ui.Info("Analyzuji zdrojový disk…");

        var analysis = DiskAnalyzer.Analyze(sourceEvaluation.Disk, platform, probeContents);
        PlanPresenter.ShowSourceDetail(ui, analysis);

        if (!analysis.IsUsable)
        {
            ui.Section("Zdrojový disk nelze použít");
            foreach (string error in analysis.Errors) ui.Error(error);
            ui.Info("Migrace byla odmítnuta. Na disky nebylo nic zapsáno.");
            return ExitCodes.SourceUnusable;
        }

        if (analysis.ContainsContainerPartition)
        {
            ui.Error("Zdrojový disk obsahuje kontejner (dynamický disk / Storage Spaces), " +
                     "který tento nástroj neumí bezpečně přenést.");
            ui.Info("Migrace byla odmítnuta. Na disky nebylo nic zapsáno.");
            return ExitCodes.SourceUnusable;
        }

        // ---------------------------------------------------------- target

        var targetEvaluation = SelectDisk(ui, evaluations, targetStates, options.TargetDiskNumber,
            "Zadejte číslo CÍLOVÉHO disku (musí být bez oddílů)",
            e => DiskRoleRules.CanServeAsTarget(e, targetStates.GetValueOrDefault(e.Disk.Identity.Number))
                 && !DiskSafetyPolicy.AreSameDisk(e.Disk.Identity, sourceIdentity),
            "Jako cíl lze vybrat jen disk bez definovaných oddílů.");

        if (targetEvaluation is null) return ExitCodes.PlanRefused;

        if (DiskSafetyPolicy.AreSameDisk(sourceIdentity, targetEvaluation.Disk.Identity))
        {
            ui.Error("Zdrojový a cílový disk jsou totožné zařízení.");
            return ExitCodes.SourceUnusable;
        }


        // ---------------------------------------------------------- plan

        int sourceNumber = sourceIdentity.Number;
        int targetNumber = targetEvaluation.Disk.Identity.Number;

        var targetInspection = targetStates.GetValueOrDefault(targetNumber);

        // An explicitly supplied temporary location is honoured from the start; otherwise the
        // plan is built first and storage is only requested if the migration really needs it.
        TemporaryVolume? temporary = string.IsNullOrWhiteSpace(options.TemporaryPath)
            ? null
            : DescribeVolume(ui, platform, environment, options.TemporaryPath, sourceNumber, targetNumber);

        MigrationPlan plan = BuildPlan(temporary);

        for (int attempt = 0; attempt < 3 && !plan.IsFeasible && NeedsBetterTemporary(plan); attempt++)
        {
            ui.Warning("Pro tuto migraci je potřeba dočasné úložiště pro obraz WIM.");
            var alternative = ResolveTemporaryStorage(
                ui, platform, evaluations, environment, options, sourceNumber, targetNumber);

            if (alternative is null) break;

            temporary = alternative;
            plan = BuildPlan(temporary);
        }

        MigrationPlan BuildPlan(TemporaryVolume? storage) =>
            MigrationPlanner.Build(analysis, targetEvaluation.Disk, new PlannerOptions
            {
                PreserveDiskIdentifiers = !options.NewIdentifiers,
                EnableVerification = !options.SkipVerification,
                TemporaryRootPath = storage?.RootPath,
                TemporaryFreeBytes = storage?.FreeBytes ?? 0,
                TemporaryFilesystem = storage?.Filesystem ?? FilesystemKind.Unknown,
                ZeroFreeClusters = options.ZeroFreeClusters,
                IgnoreTemporarySpaceCheck = options.IgnoreTemporarySpaceCheck,
                TargetInspection = targetInspection,
                BootConfigurationAvailable = platform.Boot.IsAvailable,
                BootConfigurationUnavailableReason = platform.Boot.UnavailableReason,
            });

        PlanPresenter.ShowPlan(ui, plan);

        if (!plan.IsFeasible)
        {
            PlanPresenter.ShowErrors(ui, plan);
            ui.Line();
            ui.Info("Migrace byla odmítnuta. Na disky nebylo nic zapsáno.");
            return ExitCodes.PlanRefused;
        }

        // ---------------------------------------------------------- fingerprint + confirmation

        using var sourceReader = platform.OpenReader(sourceEvaluation.Disk);
        var sourceFingerprint = DiskFingerprintFactory.Capture(sourceEvaluation.Disk, sourceReader);

        DiskFingerprint targetFingerprint;
        using (var targetReader = platform.OpenReader(targetEvaluation.Disk))
        {
            targetFingerprint = DiskFingerprintFactory.Capture(targetEvaluation.Disk, targetReader);
        }

        log.Write("PLAN", $"Zdroj {sourceIdentity.Number} ({sourceIdentity.SerialNumber}), " +
                          $"cíl {targetEvaluation.Disk.Identity.Number} ({targetEvaluation.Disk.Identity.SerialNumber}), " +
                          $"plán: {plan.Partitions.Count} oddílů");

        if (options.DryRun)
        {
            ui.Line();
            var dryReporter = new MigratorReporter(ui);
            var dryExecutor = new MigrationExecutor(platform, dryReporter, log);
            var dryResult = dryExecutor.Execute(plan, sourceEvaluation.Disk, targetEvaluation.Disk,
                sourceFingerprint, targetFingerprint, new ExecutionOptions { DryRun = true }, cancellationToken);

            PlanPresenter.ShowResult(ui, dryResult);
            ui.Info("Kontrolní režim: na cílový disk nebylo zapsáno nic.");
            return dryResult.Success ? ExitCodes.Success : ExitCodes.MigrationFailed;
        }

        if (!options.AssumeYes)
        {
            ui.Section("Potvrzení");
            ui.Detail($"Zdroj: {sourceIdentity.ShortLabel}  (SN {Serial(sourceIdentity)})");
            ui.Detail($"Cíl:   {targetEvaluation.Disk.Identity.ShortLabel}  " +
                      $"(SN {Serial(targetEvaluation.Disk.Identity)})");
            ui.Line();
            ui.Warning("Spuštěním zápisu převezme cílový disk obsah zdrojového disku.");
            ui.Warning("Zdrojový disk zůstane nedotčen. Cílový disk je bez oddílů a bude inicializován.");

            if (!ui.ConfirmByTyping("ANO", "Potvrzení spuštění migrace:"))
            {
                ui.Line();
                ui.Info("Migrace nebyla potvrzena. Na disky nebylo nic zapsáno.");
                return 0;
            }
        }
        else
        {
            ui.Warning("Potvrzení bylo přeskočeno parametrem --yes.");
        }

        // ---------------------------------------------------------- execute

        var reporter = new MigratorReporter(ui);
        var executor = new MigrationExecutor(platform, reporter, log);

        var result = executor.Execute(
            plan,
            sourceEvaluation.Disk,
            targetEvaluation.Disk,
            sourceFingerprint,
            targetFingerprint,
            new ExecutionOptions
            {
                DryRun = false,
                Verify = plan.VerificationEnabled,
                ZeroFreeClusters = options.ZeroFreeClusters,
            },
            cancellationToken);

        PlanPresenter.ShowResult(ui, result);

        if (log is FileAuditLog fileLog)
        {
            ui.Detail("Protokol: " + fileLog.Path);
        }

        return result.Success ? ExitCodes.Success : ExitCodes.MigrationFailed;
    }

    // ---------------------------------------------------------------- helpers

    private static DiskEvaluation? SelectDisk(
        ConsoleUi ui,
        IReadOnlyList<DiskEvaluation> evaluations,
        IReadOnlyDictionary<int, TargetInspection> targetStates,
        int? preselected,
        string prompt,
        Func<DiskEvaluation, bool> allowed,
        string rejectionMessage)
    {
        // A disk named on the command line gets a precise answer instead of a generic refusal.
        if (preselected is { } number)
        {
            var chosen = evaluations.FirstOrDefault(e => e.Disk.Identity.Number == number);
            if (chosen is null)
            {
                ui.Error($"Disk {number} neexistuje.");
                return null;
            }

            if (!allowed(chosen))
            {
                ui.Error($"Disk {number} nelze pro tuto roli použít. {rejectionMessage}");
                ExplainRejection(ui, chosen, targetStates);
                return null;
            }

            ui.Info($"Vybrán disk {number} z příkazové řádky.");
            return chosen;
        }

        var candidates = evaluations.Where(allowed).ToList();

        if (candidates.Count == 0)
        {
            bool forTarget = prompt.Contains("CÍLOVÉHO", StringComparison.Ordinal);

            ui.Error(forTarget
                ? "Žádný disk není bez oddílů, takže není kam migrovat."
                : "Žádný disk neobsahuje oddíly, takže není co migrovat.");

            foreach (var evaluation in evaluations.Where(e => !e.IsProtected))
            {
                ui.Detail($"Disk {evaluation.Disk.Identity.Number}: " +
                          IneligibilityReason(evaluation, targetStates, forTarget));
            }

            return null;
        }

        while (true)
        {
            ui.Line();
            ui.WriteLine("  Dostupné: " + string.Join(", ", candidates.Select(c => c.Disk.Identity.Number)),
                ConsoleColor.DarkGray);

            int chosenNumber = ui.AskNumber(prompt, 0, 99);
            var evaluation = evaluations.FirstOrDefault(e => e.Disk.Identity.Number == chosenNumber);

            if (evaluation is null)
            {
                ui.Error($"Disk {chosenNumber} neexistuje.");
                continue;
            }

            if (!allowed(evaluation))
            {
                ui.Error($"{rejectionMessage} Disk {chosenNumber} nelze vybrat.");
                ExplainRejection(ui, evaluation, targetStates);
                continue;
            }

            return evaluation;
        }
    }

    private static void ExplainRejection(
        ConsoleUi ui,
        DiskEvaluation evaluation,
        IReadOnlyDictionary<int, TargetInspection> targetStates)
    {
        if (evaluation.IsProtected)
        {
            foreach (string reason in evaluation.ProtectionReasons)
            {
                ui.Detail("• " + reason);
            }

            return;
        }

        foreach (string blocker in evaluation.SourceBlockers.Concat(evaluation.TargetBlockers))
        {
            ui.Detail("• " + blocker);
        }

        if (targetStates.TryGetValue(evaluation.Disk.Identity.Number, out var inspection) && !inspection.IsEmpty)
        {
            foreach (string reason in inspection.Reasons)
            {
                ui.Detail("• " + reason.Trim().TrimStart('•').Trim());
            }
        }
    }

    private static string IneligibilityReason(
        DiskEvaluation evaluation,
        IReadOnlyDictionary<int, TargetInspection> targetStates,
        bool forTarget)
    {
        if (evaluation.IsProtected) return "chráněný disk servisního počítače";

        var state = targetStates.GetValueOrDefault(evaluation.Disk.Identity.Number);
        if (state is null) return "stav disku se nepodařilo zjistit";

        if (forTarget)
        {
            if (state.State == TargetDiskState.Uncertain) return "nejednoznačný stav tabulky oddílů";
            if (!evaluation.CanBeTarget) return evaluation.TargetBlockers.FirstOrDefault() ?? "nelze použít jako cíl";
            return "obsahuje " + state.DefinedPartitionCount + " oddílů – může být jen zdrojem";
        }

        if (state.State == TargetDiskState.Uncertain) return "nejednoznačný stav tabulky oddílů";
        if (state.State == TargetDiskState.Empty) return "neobsahuje žádné oddíly – může být jen cílem";
        return evaluation.SourceBlockers.FirstOrDefault() ?? "nelze použít jako zdroj";
    }

    private static string Serial(DiskIdentity identity) =>
        string.IsNullOrWhiteSpace(identity.SerialNumber) ? "neuvedeno" : identity.SerialNumber;

    /// <summary>
    /// Reads the partition table of every disk that could plausibly be a target and records
    /// whether it is free of partitions. A disk we cannot read simply gets no entry.
    /// </summary>
    private static Dictionary<int, TargetInspection> InspectTargetStates(
        IDiskPlatform platform,
        IReadOnlyList<DiskEvaluation> evaluations)
    {
        var states = new Dictionary<int, TargetInspection>();

        foreach (var evaluation in evaluations)
        {
            if (evaluation.IsProtected || !evaluation.CanBeSource) continue;

            try
            {
                using var reader = platform.OpenReader(evaluation.Disk);
                states[evaluation.Disk.Identity.Number] = TargetDiskInspector.Inspect(reader);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                states[evaluation.Disk.Identity.Number] = TargetInspection.Uncertain(
                    ["Disk nelze přečíst: " + ex.Message]);
            }
        }

        return states;
    }

    private sealed record TemporaryVolume(string RootPath, long FreeBytes, FilesystemKind Filesystem, string Label);

    /// <summary>
    /// Picks the volume that will hold the temporary WIM image. The temporary volume must never
    /// sit on the source or the target disk - a WIM image written there would be overwritten or
    /// would compete with the copy itself.
    /// </summary>
    private static TemporaryVolume? ResolveTemporaryStorage(
        ConsoleUi ui,
        IDiskPlatform platform,
        IReadOnlyList<DiskEvaluation> evaluations,
        SafetyEnvironment environment,
        CommandLineOptions options,
        int sourceDiskNumber,
        int targetDiskNumber,
        bool force = false)
    {
        if (!string.IsNullOrWhiteSpace(options.TemporaryPath))
        {
            var requested = DescribeVolume(ui, platform, environment, options.TemporaryPath,
                sourceDiskNumber, targetDiskNumber);
            if (requested is null) return null;
            return requested;
        }

        var candidates = new List<TemporaryVolume>();

        foreach (var volume in environment.MountedVolumes)
        {
            if (volume.DriveLetter is not { } letter) continue;
            if (volume.Usage != VolumeUsage.None) continue;
            if (volume.DiskNumber == sourceDiskNumber || volume.DiskNumber == targetDiskNumber) continue;

            var evaluation = evaluations.FirstOrDefault(e => e.Disk.Identity.Number == volume.DiskNumber);
            if (evaluation is null || evaluation.IsProtected) continue;

            var kind = ClassifyFileSystem(volume.FileSystemName);
            string root = letter + ":\\";
            long free = platform.Volumes.GetFreeBytes(root);
            if (free <= 0) continue;

            candidates.Add(new TemporaryVolume(root, free, kind, volume.Label ?? root));
        }

        if (candidates.Count == 0 && !force)
        {
            ui.Detail("Nebyl nalezen žádný vhodný svazek pro dočasné úložiště.");
        }

        if (candidates.Count == 0)
        {
            string manual = ui.Ask("Zadejte cestu k dočasnému úložišti (např. E:\\ nebo \\\\server\\share)");
            return DescribeVolume(ui, platform, environment, manual, sourceDiskNumber, targetDiskNumber);
        }

        var best = candidates.OrderByDescending(c => c.FreeBytes).First();

        if (!force)
        {
            ui.Section("Dočasné úložiště");
            ui.Detail("Pro zmenšení oddílu je potřeba dočasný obraz WIM.");
            for (int i = 0; i < candidates.Count; i++)
            {
                ui.Detail($"{i + 1}) {candidates[i].RootPath} – volno {MigrationPlanner.Format(candidates[i].FreeBytes)}");
            }

            ui.Detail($"{candidates.Count + 1}) zadat cestu ručně (včetně síťového disku)");

            int choice = ui.AskNumber(
                $"Zvolte svazek pro dočasné soubory (doporučeno 1 = {best.RootPath})",
                1, candidates.Count + 1, 1);

            if (choice == candidates.Count + 1)
            {
                string manual = ui.Ask("Zadejte cestu k dočasnému úložišti (např. E:\\ nebo \\\\server\\share)");
                return DescribeVolume(ui, platform, environment, manual, sourceDiskNumber, targetDiskNumber);
            }

            best = candidates[choice - 1];
        }

        if (best.Filesystem is FilesystemKind.Fat32)
        {
            ui.Warning($"Svazek {best.RootPath} je pravděpodobně FAT32 – soubor WIM větší než 4 GB se na něj nevejde.");
        }

        return best;
    }

    /// <summary>Turns a user supplied path into a candidate, validating that it is usable.</summary>
    private static TemporaryVolume? DescribeVolume(
        ConsoleUi ui,
        IDiskPlatform platform,
        SafetyEnvironment environment,
        string path,
        int sourceDiskNumber = -1,
        int targetDiskNumber = -1)
    {
        string trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            ui.Error("Nebyla zadána žádná cesta.");
            return null;
        }

        if (trimmed.Length == 2 && trimmed[1] == ':') trimmed += "\\";

        bool isNetwork = trimmed.StartsWith(@"\\", StringComparison.Ordinal);

        if (!isNetwork && !Directory.Exists(trimmed))
        {
            ui.Error($"Cesta {trimmed} neexistuje nebo není dostupná.");
            return null;
        }

        if (isNetwork && !Directory.Exists(trimmed))
        {
            ui.Error($"Síťové úložiště {trimmed} není dostupné. Připojte je a zkuste to znovu.");
            return null;
        }

        long free = platform.Volumes.GetFreeBytes(trimmed);
        if (free <= 0)
        {
            ui.Warning(
                $"Na {trimmed} se nepodařilo zjistit volné místo. Dostatek prostoru " +
                "musíte ověřit sami – nástroj zápis předčasně ukončí, pokud se obraz nevejde.");
        }

        if (!isNetwork)
        {
            // Refuse a temporary location on the disks that take part in the migration.
            foreach (var volume in environment.MountedVolumes)
            {
                if (volume.DriveLetter is not { } letter) continue;
                if (!trimmed.StartsWith(letter + ":\\", StringComparison.OrdinalIgnoreCase)) continue;

                if (volume.DiskNumber == sourceDiskNumber || volume.DiskNumber == targetDiskNumber)
                {
                    ui.Error(
                        $"Svazek {letter}: leží na disku, který se migruje. " +
                        "Dočasné úložiště musí být na jiném disku.");
                    return null;
                }
            }
        }
        else
        {
            ui.Info("Síťové úložiště: rychlost přenosu přes obraz WIM bude záviset na síti.");
        }

        var kind = isNetwork ? FilesystemKind.Ntfs : GuessFileSystem(trimmed);

        if (kind is FilesystemKind.Fat32)
        {
            ui.Warning("Cílový svazek může být FAT32 – soubor WIM větší než 4 GB se na něj nevejde.");
        }

        return new TemporaryVolume(trimmed, free, kind, trimmed);
    }

    private static FilesystemKind GuessFileSystem(string rootPath)
    {
        var drive = new DriveInfo(Path.GetPathRoot(rootPath) ?? rootPath);
        if (!drive.IsReady) return FilesystemKind.Unknown;
        return ClassifyFileSystem(drive.DriveFormat);
    }

    private static FilesystemKind ClassifyFileSystem(string name) => name.ToUpperInvariant() switch
    {
        "NTFS" => FilesystemKind.Ntfs,
        "REFS" => FilesystemKind.ReFs,
        "EXFAT" => FilesystemKind.ExFat,
        "FAT32" => FilesystemKind.Fat32,
        "FAT" => FilesystemKind.Fat16,
        _ => FilesystemKind.Unknown,
    };

    private static bool NeedsBetterTemporary(MigrationPlan plan) =>
        plan.Errors.Any(e =>
            e.Contains("dočasné úložiště", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("dostatek místa", StringComparison.OrdinalIgnoreCase));

    private static bool TryCreatePlatform(
        ConsoleUi ui,
        CommandLineOptions options,
        out IDiskPlatform? platform,
        out int exitCode)
    {
        platform = null;
        exitCode = ExitCodes.PlatformUnsupported;

        if (options.SimulateDirectory is { } directory)
        {
            if (!TryCreateSimulatedPlatform(ui, directory, out platform))
            {
                exitCode = ExitCodes.PlatformUnsupported;
                return false;
            }
            ui.Warning("SIMULAČNÍ REŽIM: pracuji se soubory, nikoli s fyzickými disky.");
            ui.Detail("Zdroj: " + directory);
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            ui.Error("Tento nástroj je určen pro Windows 10/11. Na jiném systému nepracuje s fyzickými disky.");
            ui.Info("Pro vyzkoušení bez rizika lze použít parametr --simulate <adresář>.");
            exitCode = ExitCodes.PlatformUnsupported;
            return false;
        }

        var windows = new WindowsDiskPlatform();

        if (!windows.IsElevated)
        {
            ui.Error("Nástroj musí běžet jako správce – jinak nemá přístup k fyzickým diskům.");
            ui.Info("Ukončete program, klikněte pravým tlačítkem na jeho soubor a zvolte „Spustit jako správce“.");
            exitCode = ExitCodes.NotElevated;
            return false;
        }

        platform = windows;
        return true;
    }

    private static bool TryCreateSimulatedPlatform(ConsoleUi ui, string directory, out IDiskPlatform? platform)
    {
        platform = null;

        if (!Directory.Exists(directory))
        {
            ui.Error("Adresář se simulovanými disky neexistuje: " + directory);
            return false;
        }

        var files = Directory.GetFiles(directory, "disk*.img")
            .Select(path => (Path: path, Number: ParseDiskNumber(path)))
            .Where(x => x.Number is not null)
            .OrderBy(x => x.Number)
            .ToList();

        if (files.Count == 0)
        {
            ui.Error("V adresáři nejsou žádné soubory disk*.img.");
            return false;
        }

        var protectedPath = Path.Combine(directory, "protected.txt");
        var protectedNumbers = File.Exists(protectedPath)
            ? File.ReadAllLines(protectedPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Select(l => int.TryParse(l, out int n) ? n : -1)
                .Where(n => n >= 0)
                .ToList()
            : [];

        var disks = new List<SimulatedDisk>();
        foreach (var (path, number) in files)
        {
            long size = new FileInfo(path).Length;
            disks.Add(new SimulatedDisk
            {
                ImagePath = path,
                Identity = new DiskIdentity(
                    number!.Value,
                    path,
                    "Simulovaný disk " + Path.GetFileNameWithoutExtension(path),
                    "SIM" + number.Value.ToString("D7"),
                    size,
                    512,
                    4096,
                    false,
                    "Virtual"),
            });
        }

        platform = new ImageDiskPlatform(disks, new SafetyEnvironment
        {
            ProtectedDisks = protectedNumbers.Select(n => new ProtectedDisk(n, "disk je uveden v protected.txt")).ToList(),
            MountedVolumes = [],
            ExecutablePath = Environment.ProcessPath ?? "AfrowaveDiskMigrator.exe",
            SystemDrive = null,
            SystemRoot = null,
        });

        return true;
    }

    private static int? ParseDiskNumber(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string digits = new(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int value) ? value : null;
    }

    private static IAuditLog CreateAuditLog(ConsoleUi ui, string? requestedPath)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            candidates.Add(requestedPath);
        }

        string executableDirectory = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(executableDirectory,
            $"AfrowaveDiskMigrator-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        candidates.Add(Path.Combine(Path.GetTempPath(),
            $"AfrowaveDiskMigrator-{DateTime.Now:yyyyMMdd-HHmmss}.log"));

        foreach (string candidate in candidates)
        {
            try
            {
                var log = new FileAuditLog(candidate);
                log.Write("UI", "Protokol vytvořen: " + candidate);
                return log;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        ui.Warning("Protokol o průběhu se nepodařilo založit. Pokračuji bez něj.");
        return NullAuditLog.Instance;
    }
}
