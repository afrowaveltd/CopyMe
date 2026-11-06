using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Core.Safety;

namespace Afrowave.DiskMigrator.Core.Execution;

/// <summary>Outcome of an explicitly authorised destructive target preparation.</summary>
public sealed record TargetPreparationResult(
    bool Success,
    bool TargetModified,
    DiskFingerprint? NewFingerprint,
    string Message);

/// <summary>
/// Erases partition metadata of an explicitly selected, already-occupied migration target.
/// This is NOT an automatic step: the CLI requires separate typed confirmation first.
/// No operation is attempted when disk identity, content or safety checks are uncertain.
/// </summary>
public static class TargetPreparationService
{
    public static TargetPreparationResult Prepare(
        IDiskPlatform platform,
        PhysicalDisk selectedSource,
        PhysicalDisk selectedTarget,
        DiskFingerprint expectedSource,
        DiskFingerprint expectedTarget,
        CancellationToken cancellationToken = default)
    {
        bool modified = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-enumerate the actual devices rather than trusting the old menu snapshot.
            platform.Rescan();
            var currentDisks = platform.EnumerateDisks();
            var source = currentDisks.SingleOrDefault(
                d => d.Identity.Number == selectedSource.Identity.Number);
            var target = currentDisks.SingleOrDefault(
                d => d.Identity.Number == selectedTarget.Identity.Number);

            if (source is null || target is null ||
                source.Identity != selectedSource.Identity ||
                target.Identity != selectedTarget.Identity)
            {
                return Refuse("Identita zdrojového nebo cílového disku se změnila.");
            }

            if (DiskSafetyPolicy.AreSameDisk(source.Identity, target.Identity))
                return Refuse("Zdroj a cíl jsou stejné fyzické zařízení.");

            if (!source.IsReadOnly)
                return Refuse("Zdrojový disk již není potvrzen jako pouze pro čtení.");

            var environment = platform.GetSafetyEnvironment();
            var blockers = DiskSafetyPolicy.ValidatePair(source, target, environment);
            if (blockers.Count != 0)
                return Refuse(string.Join(" | ", blockers));

            using (var sourceReader = platform.OpenReader(source))
            {
                var actualSource = DiskFingerprintFactory.Capture(source, sourceReader);
                if (!DiskFingerprintFactory.Matches(expectedSource, actualSource))
                    return Refuse("Obsah nebo identita zdrojového disku se od vytvoření plánu změnily.");
            }

            using (var targetReader = platform.OpenReader(target))
            {
                var actualTarget = DiskFingerprintFactory.Capture(target, targetReader);
                if (!DiskFingerprintFactory.Matches(expectedTarget, actualTarget))
                    return Refuse("Obsah nebo identita cílového disku se od výběru změnily.");

                var inspection = TargetDiskInspector.Inspect(targetReader);
                if (inspection.State != TargetDiskState.Occupied)
                    return Refuse("Cíl již nemá ověřenou tabulku s oddíly; příprava byla odmítnuta.");
            }

            // A mounted volume must be released successfully; never overwrite a live filesystem.
            // The safety policy above already rejects running OS, executable, paging and spanned disks.
            foreach (var volume in environment.MountedVolumes.Where(
                         v => v.DiskNumber == target.Identity.Number))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!platform.Volumes.TryDismountVolume(target.Identity.Number, volume.PartitionStartBytes))
                {
                    return Refuse("Svazek " + volume.RootPath +
                        " se nepodařilo bezpečně odpojit. Zavřete otevřené soubory a zkuste to znovu.");
                }
            }

            platform.Volumes.RemoveAllMountPoints(target.Identity.Number);
            cancellationToken.ThrowIfCancellationRequested();

            // Recheck the protection policy after the dismount and before obtaining write access.
            platform.Rescan();
            var again = platform.EnumerateDisks();
            var stillSource = again.SingleOrDefault(d => d.Identity.Number == source.Identity.Number);
            var stillTarget = again.SingleOrDefault(d => d.Identity.Number == target.Identity.Number);
            if (stillSource is null || stillTarget is null ||
                stillSource.Identity != source.Identity || stillTarget.Identity != target.Identity ||
                !stillSource.IsReadOnly)
            {
                return Refuse("Disky se při přípravě změnily nebo zdroj ztratil ochranu proti zápisu.");
            }

            blockers = DiskSafetyPolicy.ValidatePair(stillSource, stillTarget, platform.GetSafetyEnvironment());
            if (blockers.Count != 0)
                return Refuse(string.Join(" | ", blockers));

            // Validate a writable handle against the fingerprint, before any byte is written.
            using (var writableTarget = platform.OpenDevice(stillTarget))
            {
                var handleFingerprint = DiskFingerprintFactory.Capture(stillTarget, writableTarget);
                if (!DiskFingerprintFactory.Matches(expectedTarget, handleFingerprint))
                    return Refuse("Zapisovací zařízení neodpovídá odsouhlasenému cílovému disku.");

                if (TargetDiskInspector.Inspect(writableTarget).State != TargetDiskState.Occupied)
                    return Refuse("Tabulka oddílů se změnila. Příprava se neprovede.");

                cancellationToken.ThrowIfCancellationRequested();

                // Erase both GPT locations as well as the first partition-table region.
                // This is quick metadata clearing, never a full-disk zero fill.
                modified = true; // even a partially failed write must be reported as destructive
                PartitionTableWriter.ClearPartitionAreas(writableTarget, gpt: true);
            }

            platform.Volumes.RefreshPartitionTable(target.Identity.Number);

            var refreshedTarget = platform.EnumerateDisks().SingleOrDefault(
                d => d.Identity.Number == target.Identity.Number);
            if (refreshedTarget is null || refreshedTarget.Identity != target.Identity)
                return new(false, true, null,
                    "Po vymazání se změnila identita cíle. Nepokračujte bez nové kontroly.");

            using var verifiedReader = platform.OpenReader(refreshedTarget);
            var cleared = TargetDiskInspector.Inspect(verifiedReader);
            if (!cleared.IsEmpty)
                return new(false, true, null,
                    "Oddíly byly mazány, ale následná kontrola nepotvrdila prázdný cíl: " +
                    string.Join(" ", cleared.Reasons));

            var newFingerprint = DiskFingerprintFactory.Capture(refreshedTarget, verifiedReader);
            return new(true, true, newFingerprint,
                "Oddíly cílového disku byly odstraněny a prázdný stav znovu ověřen.");
        }
        catch (OperationCanceledException)
        {
            return new(false, modified, null, "Příprava byla přerušena.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(false, modified, null,
                "Příprava selhala: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static TargetPreparationResult Refuse(string message) =>
        new(false, false, null, message);
}
