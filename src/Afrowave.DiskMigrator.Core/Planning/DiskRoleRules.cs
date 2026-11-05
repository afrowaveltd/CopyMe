using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Safety;

namespace Afrowave.DiskMigrator.Core.Planning;

/// <summary>What a disk may be used for in one migration.</summary>
public enum DiskRole
{
    /// <summary>Not usable - protected, unavailable or in an ambiguous state.</summary>
    None = 0,

    /// <summary>Contains partitions, so it can be the source of a migration.</summary>
    Source = 1,

    /// <summary>Contains no partitions, so it can receive a migration.</summary>
    Target = 2,
}

/// <summary>
/// Decides which of the two lists a disk belongs to. The rule is deliberately simple and
/// matches how a technician thinks about the job: a disk that holds something is the source,
/// a disk that holds nothing is the destination. No disk is ever chosen automatically.
/// </summary>
public static class DiskRoleRules
{
    public static DiskRole Classify(DiskEvaluation evaluation, TargetInspection? targetState)
    {
        if (evaluation.IsProtected) return DiskRole.None;
        if (!evaluation.CanBeSource) return DiskRole.None;

        // The inspection is what tells "holds something" from "holds nothing". Without it we
        // cannot safely classify the disk, so it is offered for neither role.
        if (targetState is null) return DiskRole.None;

        // Partitions present: this is a source candidate and can never be a target.
        if (targetState.State == TargetDiskState.Occupied) return DiskRole.Source;

        if (targetState.State == TargetDiskState.Empty)
        {
            return evaluation.CanBeTarget ? DiskRole.Target : DiskRole.None;
        }

        return DiskRole.None;              // Uncertain: refuse entirely
    }

    public static bool CanServeAsSource(DiskEvaluation evaluation, TargetInspection? targetState) =>
        Classify(evaluation, targetState) == DiskRole.Source;

    public static bool CanServeAsTarget(DiskEvaluation evaluation, TargetInspection? targetState) =>
        Classify(evaluation, targetState) == DiskRole.Target;

    /// <summary>Short label for the disk list.</summary>
    public static string Describe(DiskRole role) => role switch
    {
        DiskRole.Source => "zdroj",
        DiskRole.Target => "cíl",
        _ => "nelze použít",
    };
}
