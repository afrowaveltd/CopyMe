using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>
/// Transfers one partition through a WIM image using Microsoft DISM: capture from the read-only
/// source mount, format the resized target volume, apply. DISM preserves ACLs, hard links,
/// reparse points, sparse files and alternate data streams, which is exactly why a shrink is
/// delegated to it instead of being reimplemented.
/// </summary>
internal sealed partial class DismWimService : IWimReimageService
{
    private readonly IToolRunner _runner;
    private readonly WindowsVolumeService _volumes;
    private readonly string? _dism;
    private readonly string? _format;
    private readonly string? _diskpart;

    public DismWimService(IToolRunner runner, WindowsVolumeService volumes)
    {
        _runner = runner;
        _volumes = volumes;
        _dism = runner.ResolveTool("Dism.exe");
        _format = runner.ResolveTool("format.com");
        _diskpart = runner.ResolveTool("diskpart.exe");
    }

    public bool IsAvailable => _dism is not null && _format is not null;

    public string? UnavailableReason => _dism is null
        ? "Dism.exe nebyl nalezen"
        : _format is null
            ? "format.com nebyl nalezen"
            : null;

    public WimReimageResult Execute(
        WimReimageRequest request,
        ProgressCallback? onProgress,
        Action<string>? onOutput,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return new WimReimageResult(false, 0, UnavailableReason);

        string workDirectory = Path.Combine(request.TemporaryRootPath, "AfrowaveDiskMigrator");
        string imagePath = Path.Combine(workDirectory, "partition.wim");
        string scratchDirectory = Path.Combine(workDirectory, "scratch");

        char? sourceLetter = null;
        char? targetLetter = null;

        try
        {
            Directory.CreateDirectory(workDirectory);
            Directory.CreateDirectory(scratchDirectory);

            DeleteIfExists(imagePath);

            onOutput?.Invoke($"Připojuji zdrojový oddíl ({request.Description})…");
            sourceLetter = _volumes.MountVolume(request.SourceDiskNumber, request.SourceStartBytes, readOnly: true);

            onProgress?.Invoke(new CopyProgress(0, 100, 0));

            // ---------------------------------------------------------- capture

            onOutput?.Invoke("Vytvářím obraz WIM ze zdrojového oddílu (DISM)…");

            var capture = _runner.Run(_dism!, [
                "/Capture-Image",
                $"/ImageFile:{imagePath}",
                $"/CaptureDir:{sourceLetter}:\\",
                "/Name:Afrowave DiskMigrator",
                "/Description:Partition captured by Afrowave DiskMigrator",
                "/Compress:fast",
                "/CheckIntegrity",
                $"/LogPath:{Path.Combine(workDirectory, "dism-capture.log")}",
                "/LogLevel:1",
                $"/ScratchDir:{scratchDirectory}",
            ], TimeSpan.FromHours(48), line =>
            {
                onOutput?.Invoke(line);
                if (TryParsePercent(line, out double percent))
                {
                    onProgress?.Invoke(new CopyProgress((long)(percent * 0.6), 100, 0));
                }
            }, cancellationToken);

            if (!capture.Success)
            {
                return new WimReimageResult(false, 0,
                    $"DISM nedokázal vytvořit obraz: {ExtractError(capture)}");
            }

            long imageSize = File.Exists(imagePath) ? new FileInfo(imagePath).Length : 0;
            onProgress?.Invoke(new CopyProgress(60, 100, 0));

            // ---------------------------------------------------------- format + apply

            targetLetter = EnsureTargetVolume(request, workDirectory, onOutput, cancellationToken);

            onOutput?.Invoke($"Formátuji cílový oddíl {targetLetter}: na NTFS…");

            string label = SanitizeLabel(request.VolumeLabel) ?? "MIGRATED";
            var format = _runner.Run(_format!, [
                $"{targetLetter}:",
                "/FS:NTFS",
                "/Q",
                "/Y",
                $"/V:{label}",
            ], TimeSpan.FromMinutes(60), onOutput, cancellationToken);

            if (!format.Success)
            {
                return new WimReimageResult(false, 0,
                    $"Cílový svazek {targetLetter}: se nepodařilo naformátovat: {ExtractError(format)}");
            }

            onProgress?.Invoke(new CopyProgress(65, 100, 0));
            onOutput?.Invoke("Rozbaluji obraz na cílový oddíl (DISM)…");

            var apply = _runner.Run(_dism!, [
                "/Apply-Image",
                $"/ImageFile:{imagePath}",
                "/Index:1",
                $"/ApplyDir:{targetLetter}:\\",
                "/CheckIntegrity",
                $"/LogPath:{Path.Combine(workDirectory, "dism-apply.log")}",
                "/LogLevel:1",
                $"/ScratchDir:{scratchDirectory}",
            ], TimeSpan.FromHours(48), line =>
            {
                onOutput?.Invoke(line);
                if (TryParsePercent(line, out double percent))
                {
                    onProgress?.Invoke(new CopyProgress(65 + (long)(percent * 0.35), 100, 0));
                }
            }, cancellationToken);

            if (!apply.Success)
            {
                return new WimReimageResult(false, imageSize,
                    $"DISM nedokázal rozbalit obraz: {ExtractError(apply)}");
            }

            // /CheckIntegrity during apply already validates the image; the caller then re-reads
            // the freshly built volume and compares the amount of data with the source.
            onProgress?.Invoke(new CopyProgress(100, 100, 0));

            _volumes.RefreshPartitionTable(request.TargetDiskNumber);
            onOutput?.Invoke("Přenos oddílu přes obraz WIM dokončen.");

            return new WimReimageResult(true, imageSize, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new WimReimageResult(false, 0, ex.Message);
        }
        finally
        {
            if (sourceLetter is { } s) _volumes.DismountVolume(s);
            if (targetLetter is { } t) _volumes.DismountVolume(t);

            TryDelete(imagePath);
            TryDelete(Path.Combine(workDirectory, "dism-capture.log"));
            TryDelete(Path.Combine(workDirectory, "dism-apply.log"));
            TryDeleteDirectory(scratchDirectory);
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    /// Obtains a mount point for the target partition. A freshly created partition that carries
    /// no recognisable file system may not get a volume automatically, so as a fallback the
    /// partition is formatted through diskpart - addressed by the already verified disk number
    /// and the partition index the migration plan wrote.
    /// </summary>
    private char EnsureTargetVolume(
        WimReimageRequest request,
        string workDirectory,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        onOutput?.Invoke($"Připojuji cílový oddíl na disku {request.TargetDiskNumber}…");

        try
        {
            return _volumes.MountVolume(request.TargetDiskNumber, request.TargetStartBytes, readOnly: false);
        }
        catch (IOException ex)
        {
            if (_diskpart is null)
            {
                throw new IOException(
                    ex.Message + " Náhradní formátování přes diskpart není dostupné.", ex);
            }

            onOutput?.Invoke("Svazek cílového oddílu není zveřejněn, formátuji jej přes diskpart…");

            string scriptPath = Path.Combine(workDirectory, "format-target.txt");
            File.WriteAllLines(scriptPath,
            [
                $"select disk {request.TargetDiskNumber}",
                $"select partition {request.TargetPartitionNumber}",
                "format fs=ntfs quick noerr",
                "exit",
            ], Encoding.ASCII);

            var result = _runner.Run(_diskpart, ["/s", scriptPath], TimeSpan.FromMinutes(30), onOutput, cancellationToken);
            TryDelete(scriptPath);

            if (!result.Success)
            {
                throw new IOException(
                    $"Cílový oddíl se nepodařilo zformátovat ani přes diskpart: {ExtractError(result)}");
            }

            _volumes.RefreshPartitionTable(request.TargetDiskNumber);
            _volumes.WaitForVolumes(request.TargetDiskNumber, TimeSpan.FromSeconds(45));

            return _volumes.MountVolume(request.TargetDiskNumber, request.TargetStartBytes, readOnly: false);
        }
    }

    private static string ExtractError(ToolResult result)
    {
        string text = result.CombinedOutput.Trim();
        if (text.Length == 0) return $"návratový kód {result.ExitCode}";

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var interesting = lines
            .Where(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("Chyba", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("error code", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToList();

        if (interesting.Count > 0) return string.Join(" / ", interesting);
        return lines.Length > 0 ? lines[^1] : text[..Math.Min(text.Length, 200)];
    }

    [GeneratedRegex(@"(\d{1,3})[.,](\d)\s*%")]
    private static partial Regex PercentPattern();

    private static bool TryParsePercent(string line, out double percent)
    {
        percent = 0;
        var match = PercentPattern().Match(line);
        if (!match.Success) return false;

        string text = match.Groups[1].Value + "." + match.Groups[2].Value;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out percent)) return false;

        percent = Math.Clamp(percent, 0, 100);
        return true;
    }

    private static string? SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;

        var builder = new StringBuilder(11);
        foreach (char c in label.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') builder.Append(c);
            if (builder.Length >= 11) break;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Leftover temporary files must never fail a migration.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Same as above.
        }
    }
}
