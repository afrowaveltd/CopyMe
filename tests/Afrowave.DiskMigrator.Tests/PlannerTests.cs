using Afrowave.DiskMigrator.Core.Analysis;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Planning;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>
/// The planner decides what is possible and what has to be refused. These tests cover the
/// headline scenario from the specification: a 500 GB disk with 120 GB of data migrated onto
/// a 240 GB disk, plus the cases that must be refused.
/// </summary>
public static class PlannerTests
{
    private const long MiB = Fixtures.MiB;
    private const long GiB = Fixtures.GiB;

    [Test("500 GB disk with 120 GB of data fits onto a 240 GB target by shrinking")]
    public static void ShrinkWindowsToSmallerDisk()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"),
            diskBytes: 500 * GiB,
            windowsSizeBytes: 400 * GiB,
            windowsUsedBytes: 120 * GiB);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 240 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TemporaryRootPath = @"X:\temp\",
            TemporaryFreeBytes = 200 * GiB,
        });

        Assert.True(plan.IsFeasible, "plán musí být proveditelný: " + string.Join(" | ", plan.Errors));
        Assert.Empty(plan.Errors, "žádné chyby");
        Assert.Equal(4, plan.Partitions.Count, "počet oddílů musí zůstat zachován");

        // Order and type of every partition are preserved.
        Assert.Equal(GptPartitionTypes.EfiSystem, plan.Partitions[0].TargetTypeGuid, "oddíl 1 zůstává ESP");
        Assert.Equal(GptPartitionTypes.MicrosoftReserved, plan.Partitions[1].TargetTypeGuid, "oddíl 2 zůstává MSR");
        Assert.Equal(GptPartitionTypes.MicrosoftBasicData, plan.Partitions[2].TargetTypeGuid, "oddíl 3 zůstává datový");
        Assert.Equal(GptPartitionTypes.MicrosoftRecovery, plan.Partitions[3].TargetTypeGuid, "oddíl 4 zůstává recovery");

        Assert.Equal(CopyStrategy.RawClone, plan.Partitions[0].Strategy, "ESP se kopíruje sektorově");
        Assert.Equal(CopyStrategy.RecreateEmpty, plan.Partitions[1].Strategy, "MSR se jen znovu vytvoří");
        Assert.Equal(CopyStrategy.WimReimage, plan.Partitions[2].Strategy, "systémový oddíl musí projít obrazem WIM");
        Assert.False(plan.Partitions[3].IsShrunk, "oddíl recovery zůstává v původní velikosti");

        Assert.True(plan.Partitions[2].IsShrunk, "systémový oddíl musí být zmenšen");
        Assert.True(plan.Partitions[2].TargetSizeBytes >= 120 * GiB,
            "zmenšený oddíl musí pojmout 120 GB dat");

        Assert.True(plan.RequiredTemporaryBytes > 0, "pro zmenšení je potřeba dočasné úložiště");
        Assert.True(plan.TotalTargetPartitionBytes <= plan.TargetUsableBytes,
            "navržené oddíly se musí vejít do použitelné kapacity cíle");
        Assert.Equal(BootMode.Uefi, plan.Boot.Mode, "zavádění musí být UEFI");
        Assert.True(plan.Boot.RequiresBcdboot, "zavádění musí být znovu vytvořeno");
    }

    [Test("Shrinking without temporary storage must be refused with an explanation")]
    public static void ShrinkWithoutTemporaryStorageRefused()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"), 500 * GiB, 400 * GiB, 120 * GiB);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 240 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());

        Assert.False(plan.IsFeasible, "bez dočasného úložiště nelze zmenšovat");
        Assert.Contains("dočasné úložiště", string.Join(" | ", plan.Errors), "chyba musí vysvětlit potřebu dočasného úložiště");
    }

    [Test("The estimated write volume of a shrunken partition reflects the data, not the container")]
    public static void EstimatedWriteVolumeForWim()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"), 500 * GiB, 400 * GiB, 120 * GiB);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 240 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TemporaryRootPath = @"X:\temp\",
            TemporaryFreeBytes = 200 * GiB,
        });

        Assert.True(plan.IsFeasible, "plán musí být proveditelný");

        var windows = plan.Partitions[2];
        Assert.Equal(CopyStrategy.WimReimage, windows.Strategy, "systémový oddíl přes obraz WIM");
        Assert.True(windows.EstimatedWriteBytes <= 130 * GiB,
            $"odhad zápisu musí vycházet z dat ({windows.EstimatedWriteBytes} B), ne z velikosti oddílu");
        Assert.True(plan.TotalBytesToWrite < 200 * GiB,
            $"celkový odhad zápisu musí odpovídat datům ({plan.TotalBytesToWrite} B)");
    }

    [Test("A target that is too small for the temporary image must be refused")]
    public static void InsufficientTemporarySpaceRefused()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"), 500 * GiB, 400 * GiB, 120 * GiB);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 240 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TemporaryRootPath = @"X:\temp\",
            TemporaryFreeBytes = 4 * GiB,
        });

        Assert.False(plan.IsFeasible, "nedostatek místa pro obraz WIM musí být odmítnut");
        Assert.Contains("nemá dostatek místa", string.Join(" | ", plan.Errors), "chyba musí popsat nedostatek místa");
    }

    [Test("When the target is large enough nothing is shrunk and no temporary storage is needed")]
    public static void FullSizeTargetUsesIntelligentCopy()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"), 500 * GiB, 400 * GiB, 120 * GiB);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 500 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());

        Assert.True(plan.IsFeasible, "plán musí být proveditelný: " + string.Join(" | ", plan.Errors));
        Assert.Equal(0L, plan.RequiredTemporaryBytes, "bez zmenšení není potřeba dočasné úložiště");
        Assert.Equal(CopyStrategy.NtfsAllocationClone, plan.Partitions[2].Strategy, "systémový oddíl se kopíruje chytře");
        Assert.False(plan.Partitions[2].IsShrunk, "nic se nezmensuje");

        // Offsets stay identical to the source, which keeps boot references and drive letters stable.
        Assert.Equal(1 * MiB, plan.Partitions[0].TargetStartByte, "první oddíl si drží pozici 1 MiB");
        Assert.Equal(analysis.Partitions[2].StartLba * 512, plan.Partitions[2].TargetStartByte,
            "systémový oddíl si drží původní pozici");
        Assert.True(plan.Partitions[2].EstimatedWriteBytes < 200 * GiB,
            "chytrá kopie musí zapsat jen obsazená data");
    }

    [Test("A target smaller than the minimum data set must be refused")]
    public static void TooSmallTargetRefused()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"), 500 * GiB, 400 * GiB, 120 * GiB);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 100 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TemporaryRootPath = @"X:\temp\",
            TemporaryFreeBytes = 500 * GiB,
        });

        Assert.False(plan.IsFeasible, "100GB cíl nemůže pojmout 120GB dat");
        Assert.Contains("nevejde", string.Join(" | ", plan.Errors), "chyba musí říci, že se obsah nevejde");
    }

    [Test("A file system that cannot be shrunk blocks the migration instead of being dropped")]
    public static void UnshrinkableFilesystemRefused()
    {
        var source = Fixtures.Simulated(1, "src.img", 500 * GiB);
        var partitions = new List<PartitionInfo>
        {
            Fixtures.Partition(1, PartitionTableKind.Gpt, 2048, 100 * MiB / 512,
                GptPartitionTypes.EfiSystem, Guid.NewGuid(), "ESP", PartitionRole.EfiSystem, FilesystemKind.Fat32),
            Fixtures.Partition(2, PartitionTableKind.Gpt, (2 * MiB + 100 * MiB) / 512, 300 * GiB / 512,
                GptPartitionTypes.MicrosoftBasicData, Guid.NewGuid(), "Data", PartitionRole.Basic, FilesystemKind.Fat32),
        };

        var analysis = Fixtures.Analysis(source, PartitionTableKind.Gpt, partitions, Guid.NewGuid());
        var target = Fixtures.Simulated(2, "dst.img", 240 * GiB);

        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TemporaryRootPath = @"X:\temp\",
            TemporaryFreeBytes = 500 * GiB,
        });

        Assert.False(plan.IsFeasible, "300GB FAT32 nelze zmenšit");
        string errors = string.Join(" | ", plan.Errors);
        Assert.Contains("nevejde", errors, "chyba musí popsat, že se obsah nevejde");
        Assert.Contains("nelze zmenšit", errors, "chyba musí vysvětlit, který oddíl nelze zmenšit");
    }

    [Test("MBR disks get a BIOS boot plan with bootsect")]
    public static void MbrBootPlan()
    {
        var source = Fixtures.Simulated(1, "src.img", 500 * GiB);
        var partitions = new List<PartitionInfo>
        {
            Fixtures.Partition(1, PartitionTableKind.Mbr, 2048, 400 * GiB / 512,
                Guid.Empty, Guid.Empty, null, PartitionRole.OperatingSystem, FilesystemKind.Ntfs,
                Fixtures.Ntfs(120 * GiB, 400 * GiB), active: true, mbrType: MbrPartitionTypes.NtfsExFat),
        };

        var analysis = Fixtures.Analysis(
            source, PartitionTableKind.Mbr, partitions, Guid.Empty,
            windows: new WindowsInstallation(1, @"\Windows", "10.0.19045", false, true));

        var target = Fixtures.Simulated(2, "dst.img", 240 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TemporaryRootPath = @"X:\temp\",
            TemporaryFreeBytes = 500 * GiB,
        });

        Assert.True(plan.IsFeasible, "plán musí být proveditelný: " + string.Join(" | ", plan.Errors));
        Assert.Equal(BootMode.Bios, plan.Boot.Mode, "MBR znamená BIOS");
        Assert.True(plan.Boot.RequiresBootsect, "BIOS potřebuje bootsect");
        Assert.True(plan.Boot.RequiresBcdboot, "BIOS potřebuje bcdboot");
        Assert.Equal(0, plan.Boot.WindowsIndex, "index systémového oddílu");
        Assert.True(plan.Partitions[0].TargetIsActive, "aktivní příznak se musí zachovat");
        Assert.Equal(MbrPartitionTypes.NtfsExFat, plan.Partitions[0].TargetMbrType, "typ oddílu se musí zachovat");
    }

    [Test("A disk without an operating system is migrated as data only")]
    public static void DataOnlyDisk()
    {
        var source = Fixtures.Simulated(1, "src.img", 500 * GiB);
        var partitions = new List<PartitionInfo>
        {
            Fixtures.Partition(1, PartitionTableKind.Gpt, 2048, 200 * GiB / 512,
                GptPartitionTypes.MicrosoftBasicData, Guid.NewGuid(), "Archiv", PartitionRole.Basic,
                FilesystemKind.Ntfs, Fixtures.Ntfs(50 * GiB, 200 * GiB)),
            Fixtures.Partition(2, PartitionTableKind.Gpt, (200 * GiB + 1 * MiB) / 512, 100 * GiB / 512,
                GptPartitionTypes.MicrosoftBasicData, Guid.NewGuid(), "Foto", PartitionRole.Basic,
                FilesystemKind.ExFat),
        };

        var analysis = Fixtures.Analysis(source, PartitionTableKind.Gpt, partitions, Guid.NewGuid());
        var target = Fixtures.Simulated(2, "dst.img", 320 * GiB);

        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());
        Assert.True(plan.IsFeasible, "datový disk se musí vejít: " + string.Join(" | ", plan.Errors));
        Assert.Equal(BootMode.None, plan.Boot.Mode, "bez OS není co zavádět");
        Assert.Equal(CopyStrategy.NtfsAllocationClone, plan.Partitions[0].Strategy, "NTFS chytře");
        Assert.Equal(CopyStrategy.RawClone, plan.Partitions[1].Strategy, "exFAT sektorově");
        Assert.Equal(0, plan.Boot.Steps.Count, "žádné kroky zavádění");
    }

    [Test("Planned partitions never overlap and always fit inside the target")]
    public static void LayoutInvariants()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"), 500 * GiB, 400 * GiB, 120 * GiB);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 240 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TemporaryRootPath = @"X:\temp\",
            TemporaryFreeBytes = 200 * GiB,
        });

        Assert.True(plan.IsFeasible, "plán musí být proveditelný");

        long previousEnd = -1;
        foreach (var partition in plan.Partitions)
        {
            Assert.True(partition.TargetStartByte >= 1 * MiB, $"oddíl #{partition.Index} musí začínat za 1 MiB");
            Assert.True(partition.TargetStartByte % MiB == 0,
                $"oddíl #{partition.Index} musí být zarovnán na 1 MiB (je {partition.TargetStartByte})");
            Assert.True(partition.TargetSizeBytes > 0, $"oddíl #{partition.Index} musí mít nenulovou velikost");
            Assert.True(partition.TargetEndByte < target.Identity.SizeBytes,
                $"oddíl #{partition.Index} musí končit uvnitř cílového disku");
            Assert.True(partition.TargetStartByte > previousEnd,
                $"oddíl #{partition.Index} se nesmí překrývat s předchozím");

            previousEnd = partition.TargetEndByte;
        }
    }

    [Test("A source disk with 4 KiB sectors and a 512 B target is planned in bytes")]
    public static void DifferentSectorSizes()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"), 500 * GiB, 400 * GiB, 120 * GiB, sectorSize: 4096);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 500 * GiB, sectorSize: 512);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());

        Assert.True(plan.IsFeasible, "plán musí být proveditelný: " + string.Join(" | ", plan.Errors));
        Assert.NotEmpty(plan.Warnings, "změna velikosti sektoru musí být oznámena");

        foreach (var partition in plan.Partitions)
        {
            Assert.True(partition.TargetStartByte % 512 == 0, "start musí být zarovnán na 512 B");
            Assert.True(partition.TargetSizeBytes % 512 == 0, "velikost musí být zarovnána na 512 B");
        }
    }

    [Test("A target disk that still defines partitions is refused")]
    public static void OccupiedTargetRefused()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"), 500 * GiB, 400 * GiB, 120 * GiB);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 500 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TargetInspection = TargetInspection.Occupied(
                PartitionTableKind.Mbr, 2,
                ["Cílový disk už obsahuje 2 definovaných oddílů (MBR)."]),
        });

        Assert.False(plan.IsFeasible, "neprázdný cílový disk musí být odmítnut");
        string errors = string.Join(" | ", plan.Errors);
        Assert.Contains("není prázdný", errors, "chyba musí říci, že disk není prázdný");
        Assert.Contains("nemaže", errors, "chyba musí vysvětlit, že nástroj oddíly nemaže");
    }

    [Test("An ambiguous target disk is refused")]
    public static void UncertainTargetRefused()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"), 500 * GiB, 400 * GiB, 120 * GiB);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 500 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TargetInspection = TargetInspection.Uncertain(
                ["Primární a záložní GPT si odporují."]),
        });

        Assert.False(plan.IsFeasible, "nejednoznačný cílový disk musí být odmítnut");
        Assert.Contains("není prázdný", string.Join(" | ", plan.Errors), "důvod musí být uveden");
    }

    [Test("Shrinking a dirty NTFS volume is refused, with a usable remedy")]
    public static void DirtyNtfsShrinkRefused()
    {
        var source = Fixtures.Simulated(1, "src.img", 500 * GiB);
        var partitions = new List<PartitionInfo>
        {
            Fixtures.Partition(1, PartitionTableKind.Gpt, 2048, 100 * MiB / 512,
                GptPartitionTypes.EfiSystem, Guid.NewGuid(), "ESP", PartitionRole.EfiSystem, FilesystemKind.Fat32),
            Fixtures.Partition(2, PartitionTableKind.Gpt, (101 * MiB) / 512, 400 * GiB / 512,
                GptPartitionTypes.MicrosoftBasicData, Guid.NewGuid(), "Windows", PartitionRole.OperatingSystem,
                FilesystemKind.Ntfs, Fixtures.Ntfs(120 * GiB, 400 * GiB, dirty: true)),
        };

        var analysis = Fixtures.Analysis(
            source, PartitionTableKind.Gpt, partitions, Guid.NewGuid(),
            windows: new WindowsInstallation(2, @"\Windows", "10.0.22631", false, true));

        var target = Fixtures.Simulated(2, "dst.img", 240 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            TemporaryRootPath = @"X:\temp\",
            TemporaryFreeBytes = 500 * GiB,
        });

        Assert.False(plan.IsFeasible, "špinavý NTFS nelze zmenšovat přes obraz WIM");
        string errors = string.Join(" | ", plan.Errors);
        Assert.Contains("čistě odpojen", errors, "chyba musí popsat nečisté odpojení");
        Assert.Contains("bez zmenšení", errors, "chyba musí nabídnout použitelné řešení");
    }

    [Test("A GPT disk with Windows but without an EFI partition is refused")]
    public static void WindowsWithoutEspRefused()
    {
        var source = Fixtures.Simulated(1, "src.img", 500 * GiB);
        var partitions = new List<PartitionInfo>
        {
            Fixtures.Partition(1, PartitionTableKind.Gpt, 2048, 400 * GiB / 512,
                GptPartitionTypes.MicrosoftBasicData, Guid.NewGuid(), "Windows", PartitionRole.OperatingSystem,
                FilesystemKind.Ntfs, Fixtures.Ntfs(120 * GiB, 400 * GiB)),
        };

        var analysis = Fixtures.Analysis(
            source, PartitionTableKind.Gpt, partitions, Guid.NewGuid(),
            windows: new WindowsInstallation(1, @"\Windows", "10.0.22631", false, false));

        var target = Fixtures.Simulated(2, "dst.img", 500 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());

        Assert.False(plan.IsFeasible, "GPT bez ESP nelze uvést do bootovatelného stavu");
        Assert.Contains("EFI System Partition", string.Join(" | ", plan.Errors), "důvod musí být uveden");
    }

    [Test("A plan needing boot configuration is refused when it is unavailable")]
    public static void BootUnavailableRefused()
    {
        using var temp = new TempDirectory();
        var (analysis, _) = Fixtures.WindowsGptLayout(
            temp.File("source.img"), 500 * GiB, 400 * GiB, 120 * GiB);

        var target = Fixtures.Simulated(2, temp.File("target.img"), 500 * GiB);
        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions
        {
            BootConfigurationAvailable = false,
            BootConfigurationUnavailableReason = "bcdboot.exe nebyl nalezen",
        });

        Assert.False(plan.IsFeasible, "bez nástrojů pro zavádění nesmíme tvrdit, že disk nastartuje");
        Assert.Contains("bcdboot", string.Join(" | ", plan.Errors), "chyba musí jmenovat chybějící nástroj");
    }

    [Test("An empty source table is refused")]
    public static void EmptySourceRefused()
    {
        var source = Fixtures.Simulated(1, "src.img", 100 * GiB);
        var analysis = Fixtures.Analysis(source, PartitionTableKind.Gpt, [], Guid.NewGuid());
        var target = Fixtures.Simulated(2, "dst.img", 240 * GiB);

        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());
        Assert.False(plan.IsFeasible, "disk bez oddílů nelze migrovat");
    }

    [Test("Container partitions on the source must be refused")]
    public static void ContainerSourceRefused()
    {
        var source = Fixtures.Simulated(1, "src.img", 500 * GiB);
        var partitions = new List<PartitionInfo>
        {
            Fixtures.Partition(1, PartitionTableKind.Gpt, 2048, 100 * GiB / 512,
                GptPartitionTypes.MicrosoftLdmMetadata, Guid.NewGuid(), "LDM", PartitionRole.Container, FilesystemKind.Unknown),
        };

        var analysis = Fixtures.Analysis(source, PartitionTableKind.Gpt, partitions, Guid.NewGuid());
        var target = Fixtures.Simulated(2, "dst.img", 240 * GiB);

        var plan = MigrationPlanner.Build(analysis, target.ToPhysicalDisk(), new PlannerOptions());
        Assert.False(plan.IsFeasible, "dynamický disk musí být odmítnut");
        Assert.Contains("kontejner", string.Join(" | ", plan.Errors), "chyba musí mluvit o kontejneru");
    }
}
