using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Planning;
using Afrowave.DiskMigrator.Core.Safety;

namespace Afrowave.DiskMigrator.Cli;

/// <summary>Renders migration progress into the terminal.</summary>
internal sealed class MigratorReporter : IMigrationReporter
{
    private readonly ConsoleUi _ui;
    private string _label = string.Empty;
    private DateTime _lastRender = DateTime.MinValue;

    public MigratorReporter(ConsoleUi ui) => _ui = ui;

    public void Phase(string message)
    {
        _ui.EndProgress();
        _ui.Title(message);
    }

    public void Detail(string message) => _ui.Detail(message);

    public void Notice(string message) => _ui.Info(message);

    public void Warning(string message) => _ui.Warning(message);

    public void PartitionStarted(PlannedPartition partition)
    {
        _ui.EndProgress();
        _label = $"#{partition.Source.Number} {Shorten(partition.Source.DisplayName, 22)}";
        _ui.WriteLine($"  → oddíl #{partition.Source.Number}: {partition.StrategyDescription}", ConsoleColor.White);
    }

    public void Progress(PlannedPartition partition, CopyProgress progress)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRender).TotalMilliseconds < 120) return;
        _lastRender = now;

        if (progress.TotalBytes > 100)
        {
            string data = $"{Bytes(progress.DoneBytes)}/{Bytes(progress.TotalBytes)}";
            string speed = Speed(progress.BytesPerSecond);
            string? eta = progress.EstimatedRemaining is { } remaining ? "zbývá " + Duration(remaining) : null;

            _ui.WriteProgress(_ui.ProgressLine(_label, progress.Fraction, data, speed, eta));
            return;
        }

        _ui.WriteProgress(_ui.ProgressLine(_label, progress.Fraction));
    }

    public void PartitionCompleted(PlannedPartition partition, long bytesWritten)
    {
        _ui.EndProgress();
        if (bytesWritten > 0)
        {
            _ui.Success($"Oddíl #{partition.Source.Number} přenesen ({Bytes(bytesWritten)}).");
        }
        else
        {
            _ui.Success($"Oddíl #{partition.Source.Number} připraven.");
        }
    }

    public static string Bytes(long bytes) => MigrationPlanner.Format(bytes);

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "–" : MigrationPlanner.Format((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>Renders disk lists, disk details and the migration plan.</summary>
internal static class PlanPresenter
{
    public static void ShowDiskList(
        ConsoleUi ui,
        IReadOnlyList<DiskEvaluation> evaluations,
        IReadOnlyDictionary<int, TargetInspection> targetStates,
        string heading)
    {
        ui.Section(heading);
        ui.WriteLine(
            $"  {"#",2}  {"Model / zařízení",30} {"Velikost",10}  {"Rozhraní",8}  Stav",
            ConsoleColor.DarkGray);

        foreach (var evaluation in evaluations.OrderBy(e => e.Disk.Identity.Number))
        {
            var identity = evaluation.Disk.Identity;
            var state = targetStates.GetValueOrDefault(identity.Number);
            var role = DiskRoleRules.Classify(evaluation, state);

            string status;
            ConsoleColor color;

            if (evaluation.IsProtected)
            {
                status = "CHRÁNĚN – nelze použít";
                color = ConsoleColor.DarkYellow;
            }
            else
            {
                switch (role)
                {
                    case DiskRole.Source:
                        status = "zdroj";
                        color = ConsoleColor.Green;
                        break;
                    case DiskRole.Target:
                        status = "cíl";
                        color = ConsoleColor.Green;
                        break;
                    default:
                        status = state is null || state.State == TargetDiskState.Uncertain
                            ? "nejednoznačný stav – nelze použít"
                            : "nedostupný";
                        color = ConsoleColor.Gray;
                        break;
                }
            }

            string model = identity.Model.Length == 0 ? "(neznámý)" : identity.Model;
            ui.WriteLine(
                $"  {identity.Number,2}  {Clip(model, 28),-28} {MigrationPlanner.Format(identity.SizeBytes),10}  " +
                $"{Clip(identity.BusType, 7),-7}  {status}",
                color);

            if (evaluation.IsProtected)
            {
                foreach (string reason in evaluation.ProtectionReasons.Take(2))
                {
                    ui.WriteLine($"      └ {reason}", ConsoleColor.DarkGray);
                }

                continue;
            }

            if (role == DiskRole.Source)
            {
                string detail = evaluation.CanBeTarget
                    ? $"obsahuje {state!.DefinedPartitionCount} oddílů – zdroj, nebo cíl po výslovném vymazání"
                    : $"obsahuje {state!.DefinedPartitionCount} oddílů – cíl nelze: " +
                      (evaluation.TargetBlockers.FirstOrDefault() ?? "nepoužitelný");
                ui.WriteLine("      └ " + detail, ConsoleColor.DarkGray);
            }
            else if (role == DiskRole.Target)
            {
                ui.WriteLine("      └ bez oddílů – může být cílem", ConsoleColor.DarkGray);
            }
            else
            {
                string? diagnostic = evaluation.SourceBlockers.FirstOrDefault()
                    ?? evaluation.TargetBlockers.FirstOrDefault()
                    ?? state?.Reasons.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(diagnostic))
                    ui.WriteLine("      └ " +
                        Clip(diagnostic.Trim().TrimStart('•').Trim(), 100), ConsoleColor.DarkGray);
            }
        }

        ui.Line();
        ui.Detail("Zdroj = disk s oddíly. Cíl = prázdný disk nebo výslovně připravený disk.");
        ui.Detail("Příprava obsazeného cíle smaže VŠECHNY oddíly jen po zvláštním potvrzení.");
        ui.Detail("Disky servisního počítače se nezobrazují jako zdroj ani cíl.");
    }

    public static void ShowSourceDetail(ConsoleUi ui, Core.Analysis.DiskAnalysis analysis)
    {
        var identity = analysis.Disk.Identity;

        ui.Section("Zdrojový disk");
        ui.Detail($"Zařízení:    {identity.DevicePath}");
        ui.Detail($"Model:       {(identity.Model.Length == 0 ? "neznámý" : identity.Model)}");
        ui.Detail($"Sériové č.:  {(identity.SerialNumber.Length == 0 ? "neuvedeno" : identity.SerialNumber)}");
        ui.Detail($"Velikost:    {MigrationPlanner.Format(identity.SizeBytes)} " +
                  $"(sektor {identity.LogicalSectorSize} B)");
        ui.Detail($"Rozhraní:    {identity.BusType}");
        ui.Detail($"Tabulka:     {(analysis.Table?.Kind switch
        {
            PartitionTableKind.Gpt => "GPT",
            PartitionTableKind.Mbr => "MBR",
            _ => "nerozpoznaná",
        })}");

        if (analysis.Windows is { } windows)
        {
            ui.Success($"Byl rozpoznán operační systém: {(windows.Version ?? "Windows")} " +
                       $"(oddíl #{windows.PartitionNumber}).");
        }
        else
        {
            ui.Info("Na disku nebyl rozpoznán operační systém. Přenáší se obsah jako data.");
        }

        ui.Section("Oddíly zdrojového disku");
        ui.WriteLine($"  {"#",2} {"Typ / název",30} {"Systém",12} {"Velikost",11} {"Obsazeno",11}", ConsoleColor.DarkGray);

        foreach (var partition in analysis.Partitions.OrderBy(p => p.StartLba))
        {
            long size = partition.SectorCount * (analysis.Table?.LogicalSectorSize ?? 512);
            string name = Clip(partition.DisplayName, 30);
            string fs = partition.FileSystem?.DisplayName ?? "–";
            string used = partition.FileSystem?.Ntfs is { } ntfs
                ? MigrationPlanner.Format(ntfs.UsedBytes)
                : "–";

            ui.WriteLine($"  {partition.Number,2} {name,-30} {Clip(fs, 12),-12} " +
                         $"{MigrationPlanner.Format(size),11} {used,11}");
        }

        foreach (string warning in analysis.Warnings)
        {
            ui.Warning(warning);
        }

        foreach (string error in analysis.Errors)
        {
            ui.Error(error);
        }
    }

    public static void ShowPlan(ConsoleUi ui, MigrationPlan plan)
    {
        ui.Section("Navržený postup migrace");

        ui.WriteLine($"  Zdroj: {plan.SourceDisk.ShortLabel}  " +
                     $"({MigrationPlanner.Format(plan.SourceDisk.SizeBytes)}, SN {Mask(plan.SourceDisk.SerialNumber)})",
            ConsoleColor.White);
        ui.WriteLine($"  Cíl:   {plan.TargetDisk.ShortLabel}  " +
                     $"({MigrationPlanner.Format(plan.TargetDisk.SizeBytes)}, SN {Mask(plan.TargetDisk.SerialNumber)})",
            ConsoleColor.White);
        string targetState = string.IsNullOrEmpty(plan.TargetSummary) ? "bez oddílů" : plan.TargetSummary;
        ui.Detail($"Cílový disk: {targetState} – bude inicializován jako {plan.SourceTable.Kind}.");

        ui.Line();
        ui.WriteLine($"  {"#",2} {"Oddíl",22} {"Systém",10} {"Zdroj",10} {"Cíl",10} Způsob přenosu",
            ConsoleColor.DarkGray);

        foreach (var partition in plan.Partitions)
        {
            string name = Clip(partition.Source.DisplayName, 22);
            string fs = Clip(partition.Source.FileSystem?.DisplayName ?? "–", 10);
            string sourceSize = MigrationPlanner.Format(partition.SourceSizeBytes);
            string targetSize = MigrationPlanner.Format(partition.TargetSizeBytes);

            var color = partition.IsShrunk ? ConsoleColor.Yellow : ConsoleColor.Gray;

            ui.WriteLine(
                $"  {partition.Source.Number,2} {name,-22} {fs,-10} {sourceSize,10} {targetSize,10} " +
                partition.StrategyDescription,
                color);
        }

        ui.Line();
        ui.Detail($"Celkem se zapíše:        {MigrationPlanner.Format(plan.TotalBytesToWrite)}");
        ui.Detail($"Použitelná kapacita cíle: {MigrationPlanner.Format(plan.TargetUsableBytes)}");
        ui.Detail($"Navržené oddíly celkem:   {MigrationPlanner.Format(plan.TotalTargetPartitionBytes)} " +
                  $"(volno zůstane {MigrationPlanner.Format(Math.Max(0, plan.FreeBytesLeftOnTarget))})");

        if (plan.RequiredTemporaryBytes > 0)
        {
            ui.Warning($"Dočasný obraz WIM: potřeba přibližně {MigrationPlanner.Format(plan.RequiredTemporaryBytes)} " +
                       $"v {plan.TemporaryRootPath}");
        }

        ui.Section("Zavádění systému");
        if (plan.Boot.Mode == BootMode.None)
        {
            ui.Detail("Zavádění systému nebude konfigurováno (na zdroji nebyl rozpoznán operační systém).");
        }
        else
        {
            ui.Detail($"Režim: {(plan.Boot.Mode == BootMode.Uefi ? "UEFI" : "BIOS (MBR)")}");
            foreach (string step in plan.Boot.Steps)
            {
                ui.Detail("• " + step);
            }
        }

        foreach (string note in plan.Boot.Notes)
        {
            ui.Info(note);
        }

        if (plan.PreserveDiskIdentifiers)
        {
            ui.Info("Cílový disk převezme identifikátory zdroje, aby fungovalo existující zavádění " +
                    "a písmena jednotek. Po migraci zdrojový disk odpojte.");
        }

        ui.Section("Co se bude dít");
        ui.Warning("Zdrojový disk bude pouze čten – nikdy se na něj nezapisuje.");
        ui.Warning("Cílový disk bude PŘEPSÁN. Veškerá data na něm budou nenávratně ztracena.");

        foreach (string step in plan.Preparation)
        {
            ui.Detail("• " + step);
        }

        if (plan.Warnings.Count > 0)
        {
            ui.Section("Upozornění");
            foreach (string warning in plan.Warnings)
            {
                ui.Warning(warning);
            }
        }

        if (!plan.VerificationEnabled)
        {
            ui.Warning("Kontrola obsahu po zápisu je vypnutá.");
        }
    }

    public static void ShowErrors(ConsoleUi ui, MigrationPlan plan)
    {
        ui.Section("Migraci nelze provést");
        foreach (string error in plan.Errors)
        {
            ui.Error(error);
        }
    }

    public static void ShowResult(ConsoleUi ui, MigrationResult result)
    {
        ui.Section(result.Success ? "Výsledek" : "Migrace nebyla dokončena");

        if (result.Errors.Count > 0)
        {
            foreach (string error in result.Errors)
            {
                ui.Error(error);
            }
        }

        if (result.Warnings.Count > 0)
        {
            foreach (string warning in result.Warnings)
            {
                ui.Warning(warning);
            }
        }

        if (result.VerificationNotes.Count > 0)
        {
            ui.Section("Kontrola po přenosu");
            foreach (string note in result.VerificationNotes)
            {
                ui.Success(note);
            }
        }

        if (result.Success)
        {
            ui.Line();
            ui.Success($"Zapsáno {MigrationPlanner.Format(result.BytesWritten)} za " +
                       MigratorReporter.Duration(result.Elapsed) + ".");
            ui.Line();
            ui.Info("Skutečné spuštění cílového systému nebylo tímto nástrojem otestováno.");
            ui.Info("Před předáním počítače ověřte start systému z cílového disku.");
        }
    }

    private static string Mask(string? serial) =>
        string.IsNullOrWhiteSpace(serial)
            ? "neuvedeno"
            : serial.Length <= 4 ? serial : "…" + serial[^4..];

    public static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";
}
