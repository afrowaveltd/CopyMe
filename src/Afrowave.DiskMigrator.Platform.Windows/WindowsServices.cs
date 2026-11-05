using System.Diagnostics;
using System.Text;
using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>Runs the Windows system tools this migration relies on.</summary>
internal sealed class ProcessToolRunner : IToolRunner
{
    private readonly string _system32;

    public ProcessToolRunner()
    {
        _system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
    }

    public string? ResolveTool(string fileName)
    {
        string candidate = Path.Combine(_system32, fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    public ToolResult Run(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var outputLock = new Lock();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outputLock) standardOutput.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (outputLock) standardError.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };

        if (!process.Start())
        {
            return new ToolResult(-1, string.Empty, $"Nástroj {Path.GetFileName(executable)} se nepodařilo spustit.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Kill the child if the deadline passes or the operator presses Ctrl+C. A DISM capture can
        // run for hours, so cancellation has to reach it.
        using var cancellable = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        if (!process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            lock (outputLock)
            {
                return new ToolResult(-1, standardOutput.ToString(),
                    $"Nástroj {Path.GetFileName(executable)} překročil časový limit {timeout}.");
            }
        }

        process.WaitForExit();

        if (cancellationToken.IsCancellationRequested)
        {
            lock (outputLock)
            {
                return new ToolResult(-1, standardOutput.ToString(),
                    $"Nástroj {Path.GetFileName(executable)} byl přerušen uživatelem.");
            }
        }

        lock (outputLock)
        {
            return new ToolResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
        }
    }
}

/// <summary>Looks inside partitions by mounting them read-only for a moment.</summary>
internal sealed class WindowsContentProbe : IPartitionContentProbe
{
    private readonly WindowsVolumeService _volumes;

    public WindowsContentProbe(WindowsVolumeService volumes) => _volumes = volumes;

    public PartitionContentProbe Probe(int diskNumber, long partitionStartBytes)
    {
        VolumeRecord? volume = WindowsVolumeService.FindVolume(diskNumber, partitionStartBytes);
        if (volume is null)
        {
            return new PartitionContentProbe(false, "svazek není rozpoznán operačním systémem",
                false, null, false, false, false, null, []);
        }

        char? letter = null;
        bool assigned = false;

        try
        {
            if (volume.DriveLetter is { } existing)
            {
                letter = existing;
            }
            else
            {
                letter = _volumes.MountVolume(diskNumber, partitionStartBytes, readOnly: true);
                assigned = true;
            }

            string root = letter + ":\\";
            if (!Directory.Exists(root))
            {
                return new PartitionContentProbe(false, "svazek nelze připojit (může být zašifrovaný nebo poškozený)",
                    false, null, false, false, false, volume.Label, []);
            }

            bool hasWindows = File.Exists(Path.Combine(root, @"Windows\System32\ntoskrnl.exe"))
                              && File.Exists(Path.Combine(root, @"Windows\System32\config\SYSTEM"));

            string? version = null;
            if (hasWindows)
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(Path.Combine(root, @"Windows\System32\ntoskrnl.exe"));
                    if (info.FileMajorPart > 0)
                    {
                        version = $"Windows {info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
                    }
                }
                catch (IOException)
                {
                    // Version is cosmetic; keep going.
                }
            }

            bool hasHibernation = File.Exists(Path.Combine(root, "hiberfil.sys"));
            bool hasPageFile = File.Exists(Path.Combine(root, "pagefile.sys"));
            bool hasWinRe = File.Exists(Path.Combine(root, @"Recovery\WindowsRE\WinRE.wim"));

            // A partition carrying EFI boot files is an EFI system partition even when its type
            // GUID says something else.
            bool hasEfiBootFiles =
                File.Exists(Path.Combine(root, @"EFI\Microsoft\Boot\bootmgfw.efi")) ||
                File.Exists(Path.Combine(root, @"EFI\BOOT\BOOTX64.EFI")) ||
                File.Exists(Path.Combine(root, @"EFI\BOOT\BOOTIA32.EFI"));

            var entries = new List<string>();
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(root).Take(32))
                {
                    entries.Add(Path.GetFileName(entry));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Listing is only used for diagnostics.
            }

            return new PartitionContentProbe(
                true, null, hasWindows, version, hasHibernation, hasPageFile, hasWinRe, volume.Label, entries)
            {
                HasEfiBootFiles = hasEfiBootFiles,
            };
        }
        catch (IOException ex)
        {
            return new PartitionContentProbe(false, ex.Message, false, null, false, false, false, null, []);
        }
        finally
        {
            if (assigned && letter is { } temporary)
            {
                _volumes.DismountVolume(temporary);
            }
        }
    }
}
