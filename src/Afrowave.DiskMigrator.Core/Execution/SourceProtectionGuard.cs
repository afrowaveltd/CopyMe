using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Core.Execution;

/// <summary>Result of acquiring exclusive read-only access to the source disk.</summary>
public sealed class SourceProtectionOutcome
{
    public required bool Success { get; init; }

    /// <summary>Set only when <see cref="Success"/> is true. The caller must release it.</summary>
    public ISourceTakeoverSession? Session { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = [];

    public IReadOnlyList<string> Problems { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Drives the source takeover including the retry conversation. Keeping this in the core - rather
/// than in the command line layer - means the whole lifecycle (attempt, refusal, retry, release)
/// is covered by automated tests.
/// </summary>
public static class SourceProtectionGuard
{
    public const int DefaultMaxAttempts = 3;

    /// <summary>
    /// Tries to take the source disk over. <paramref name="shouldRetry"/> is asked after a failure
    /// that has a realistic chance of succeeding later - for example a file somebody still has open.
    /// </summary>
    public static SourceProtectionOutcome Acquire(
        IDiskPlatform platform,
        PhysicalDisk disk,
        Func<SourceTakeoverReport, bool>? shouldRetry = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        var problems = new List<string>();

        var service = platform.SourceTakeover;

        if (service is null || !service.IsAvailable)
        {
            return new SourceProtectionOutcome
            {
                Success = false,
                Problems =
                [
                    "Zdrojový disk nelze bezpečně převzít: " +
                    (service?.UnavailableReason ?? "tato platforma převzetí nepodporuje") + ".",
                    "Migrace by běžela bez záruky, že se na zdrojový disk nezapisuje. Odmítáme pokračovat.",
                ],
            };
        }

        int attempts = Math.Max(1, maxAttempts);

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            SourceTakeoverReport report;
            ISourceTakeoverSession? session = null;

            try
            {
                report = service.TakeOver(disk, out session);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                report = new SourceTakeoverReport
                {
                    Success = false,
                    Problems = [$"Převzetí zdrojového disku selhalo: {ex.Message}"],
                    CanRetry = true,
                };
            }

            steps.AddRange(report.Steps);
            warnings.AddRange(report.Warnings);

            if (report.Success && session is not null)
            {
                return new SourceProtectionOutcome
                {
                    Success = true,
                    Session = session,
                    Steps = steps,
                    Warnings = warnings,
                };
            }

            // Never leak a partially acquired session.
            if (session is not null)
            {
                Release(session);
            }

            problems = [.. report.Problems];

            if (attempt == attempts) break;
            if (!report.CanRetry) break;
            if (shouldRetry is null || !shouldRetry(report)) break;
        }

        problems.Add(
            "Zavřete programy, které mohou mít na zdrojovém disku otevřené soubory " +
            "(Průzkumník, kancelářské aplikace, antivirová kontrola), a spusťte migraci znovu.");
        problems.Add("Na zdrojový disk ani na cílový disk nebylo nic zapsáno.");

        return new SourceProtectionOutcome
        {
            Success = false,
            Problems = problems,
            Steps = steps,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Releases the session. Never throws: the release path runs in a finally block and a failure
    /// there must be reported, not turned into a crash that hides the original outcome.
    /// </summary>
    public static SourceReleaseReport Release(ISourceTakeoverSession session)
    {
        try
        {
            return session.Release();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new SourceReleaseReport
            {
                Success = false,
                Problems =
                [
                    $"Vrácení zdrojového disku do původního stavu selhalo: {ex.Message}",
                    "Restartujte počítač – tím se uvolní všechny zámky a read-only příznak zmizí.",
                ],
            };
        }
        finally
        {
            session.Dispose();
        }
    }
}
