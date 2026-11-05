namespace Afrowave.DiskMigrator.Core.Text;

/// <summary>
/// Builds the one-line progress indicator. Optional parts (transferred volume, speed, ETA)
/// are dropped from the right until the line fits, so the percentages stay visible even on a
/// narrow service monitor.
/// </summary>
public static class ProgressLineFormatter
{
    public const int DefaultWidth = 78;

    public static string Format(
        string label,
        double fraction,
        int width = DefaultWidth,
        params string?[] optionalParts)
    {
        var parts = new List<string>();
        foreach (string? part in optionalParts)
        {
            if (!string.IsNullOrWhiteSpace(part)) parts.Add(part!.Trim());
        }

        double clamped = double.IsNaN(fraction) ? 0d : Math.Clamp(fraction, 0d, 1d);

        for (int barWidth = 20; barWidth >= 8; barWidth -= 4)
        {
            int filled = (int)Math.Round(clamped * barWidth);
            string bar = new string('#', filled) + new string('.', barWidth - filled);
            string head = $"{label} [{bar}] {clamped * 100,5:0.0} %";

            for (int keep = parts.Count; keep >= 0; keep--)
            {
                string candidate = keep == 0
                    ? head
                    : head + "  " + string.Join("  ", parts.Take(keep));

                if (candidate.Length <= width) return candidate;
            }

            if (head.Length <= width) return head;
        }

        string fallback = $"{label} {clamped * 100,5:0.0} %";
        return fallback.Length <= width ? fallback : fallback[..width];
    }
}
