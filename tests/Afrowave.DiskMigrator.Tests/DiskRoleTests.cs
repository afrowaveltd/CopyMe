using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Planning;
using Afrowave.DiskMigrator.Core.Safety;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>
/// The two menus the technician works with. A disk that holds partitions is a source, a disk
/// without them is a destination, and nothing is ever offered for both roles at once.
/// </summary>
public static class DiskRoleTests
{
    private static DiskEvaluation Evaluate(SimulatedDiskInfo disk, params int[] protectedDisks) =>
        DiskSafetyPolicy.Evaluate(disk.Disk.ToPhysicalDisk(), Fixtures.Safety(protectedDisks));

    [Test("A disk with partitions is offered as a source and never as a target")]
    public static void PartitionedDiskIsSourceOnly()
    {
        var disk = Make(1);
        var evaluation = Evaluate(disk);
        var state = Occupied();

        Assert.Equal(DiskRole.Source, DiskRoleRules.Classify(evaluation, state), "role disku s oddíly");
        Assert.True(DiskRoleRules.CanServeAsSource(evaluation, state), "může být zdrojem");
        Assert.False(DiskRoleRules.CanServeAsTarget(evaluation, state), "nesmí být cílem");
    }

    [Test("A disk without partitions is offered as a target and never as a source")]
    public static void EmptyDiskIsTargetOnly()
    {
        var disk = Make(2);
        var evaluation = Evaluate(disk);
        var state = TargetInspection.Empty("disk neobsahuje žádnou tabulku oddílů");

        Assert.Equal(DiskRole.Target, DiskRoleRules.Classify(evaluation, state), "role prázdného disku");
        Assert.True(DiskRoleRules.CanServeAsTarget(evaluation, state), "může být cílem");
        Assert.False(DiskRoleRules.CanServeAsSource(evaluation, state), "nesmí být zdrojem");
    }

    [Test("The servicing system disk is offered for neither role")]
    public static void ProtectedDiskHasNoRole()
    {
        var disk = Make(0);
        var evaluation = Evaluate(disk, 0);

        Assert.Equal(DiskRole.None, DiskRoleRules.Classify(evaluation, Occupied()), "chráněný disk jako zdroj");
        Assert.Equal(DiskRole.None, DiskRoleRules.Classify(evaluation, TargetInspection.Empty("x")),
            "chráněný disk jako cíl");
    }

    [Test("A disk in an ambiguous state is offered for neither role")]
    public static void UncertainDiskHasNoRole()
    {
        var disk = Make(3);
        var evaluation = Evaluate(disk);
        var state = TargetInspection.Uncertain(["poškozená tabulka oddílů"]);

        Assert.Equal(DiskRole.None, DiskRoleRules.Classify(evaluation, state), "nejednoznačný disk");
        Assert.False(DiskRoleRules.CanServeAsSource(evaluation, state), "nesmí být zdrojem");
        Assert.False(DiskRoleRules.CanServeAsTarget(evaluation, state), "nesmí být cílem");
    }

    [Test("Without an inspection a disk is offered for neither role")]
    public static void UninspectedDiskHasNoRole()
    {
        var evaluation = Evaluate(Make(4));

        Assert.Equal(DiskRole.None, DiskRoleRules.Classify(evaluation, null), "nezkontrolovaný disk");
    }

    [Test("A read-only disk with partitions can still be a source")]
    public static void ReadOnlyDiskCanBeSource()
    {
        var disk = Make(5);
        disk.Disk.ReadOnly = true;

        var evaluation = Evaluate(disk);
        var state = Occupied();

        Assert.True(DiskRoleRules.CanServeAsSource(evaluation, state),
            "read-only disk je ideální zdroj – nesmí být vyřazen");
        Assert.False(DiskRoleRules.CanServeAsTarget(evaluation, state), "read-only disk nesmí být cílem");
    }

    private sealed record SimulatedDiskInfo(Platform.Image.SimulatedDisk Disk);

    private static SimulatedDiskInfo Make(int number) =>
        new(Fixtures.Simulated(number, $"disk{number}.img", 100 * Fixtures.GiB));

    private static TargetInspection Occupied() =>
        TargetInspection.Occupied(PartitionTableKind.Gpt, 3, ["Cílový disk už obsahuje 3 definovaných oddílů (GPT)."]);
}
