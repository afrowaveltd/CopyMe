using Afrowave.DiskMigrator.Core.Text;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>
/// The progress indicator has to stay readable on an 80 column service monitor: percentages
/// matter more than the estimated time left, so parts are dropped from the right.
/// </summary>
public static class ProgressLineTests
{
    private const int Width = 78;

    [Test("A full progress line stays within the terminal width")]
    public static void FullLineFits()
    {
        string line = ProgressLineFormatter.Format(
            "#3 chytrá NTFS", 0.542, Width, "89.3 GiB/164.8 GiB", "112.4 MiB/s", "zbývá 11:18");

        Assert.True(line.Length <= Width, $"řádek průběhu musí mít nejvýše {Width} znaků (má {line.Length})");
        Assert.Contains("54.2 %", line, "procenta musí být vždy vidět");
        Assert.Contains("chytrá NTFS", line, "název oddílu a strategie musí být vidět");
    }

    [Test("Optional parts are dropped instead of truncating important information")]
    public static void OptionalPartsDropped()
    {
        string line = ProgressLineFormatter.Format(
            "#12 Microsoft reserved partition a ještě něco navíc", 0.99, Width,
            "1.01 GiB/1.01 GiB", "120.5 MiB/s", "zbývá 0:01");

        Assert.True(line.Length <= Width, "řádek musí zůstat v limitu");
        Assert.Contains("99.0 %", line, "procenta se nesmí ztratit");
        Assert.False(line.Contains("zbývá"), "nejdřív se zahazuje odhad času");
    }

    [Test("A very long label is still cut to the width limit")]
    public static void ExtremeLabelTruncated()
    {
        string line = ProgressLineFormatter.Format(
            "oddíl s opravdu velmi dlouhým názvem, který se nevejde", 0.5, Width);

        Assert.True(line.Length <= Width, "i extrémní popisek musí zůstat v limitu");
    }

    [Test("Fraction is clamped and NaN safe")]
    public static void FractionClamped()
    {
        string negative = ProgressLineFormatter.Format("x", -5, Width);
        Assert.Contains("0.0 %", negative, "záporná hodnota se ořízne na 0 %");

        string over = ProgressLineFormatter.Format("x", 42, Width);
        Assert.Contains("100.0 %", over, "hodnota nad 1 se ořízne na 100 %");

        string notANumber = ProgressLineFormatter.Format("x", double.NaN, Width);
        Assert.Contains("0.0 %", notANumber, "NaN se považuje za 0 %");
    }

    [Test("A 60 column terminal still gets a usable bar")]
    public static void NarrowTerminal()
    {
        string line = ProgressLineFormatter.Format(
            "#3 chytrá NTFS", 0.25, 60, "10 GiB/40 GiB", "100 MiB/s", "zbývá 5:00");

        Assert.True(line.Length <= 60, "řádek musí respektovat zadanou šířku");
        Assert.Contains("25.0 %", line, "procenta musí zůstat vidět");
    }
}
