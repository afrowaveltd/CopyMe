using System.Globalization;
using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>
/// Uses the Windows Storage management provider as a second, independent
/// source of disk state when a USB bridge does not support the native disk IOCTL.
/// Neither a failed IOCTL nor USB bus type is ever interpreted as permission to write.
/// </summary>
internal static class WindowsDiskStorageCmdlets
{
    public static bool TryParseStatus(
        string text, int expectedDiskNumber,
        out WindowsDiskAttributes attributes)
    {
        attributes = default;
        string[] parts = text.Trim().TrimStart('\uFEFF').Split('|');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
            number != expectedDiskNumber ||
            !bool.TryParse(parts[1], out bool offline) ||
            !bool.TryParse(parts[2], out bool readOnly))
        {
            return false;
        }

        attributes = new WindowsDiskAttributes(
            (offline ? WindowsDiskProtocol.Offline : 0UL) |
            (readOnly ? WindowsDiskProtocol.ReadOnly : 0UL));
        return true;
    }

    public static bool TryRead(
        int diskNumber, IToolRunner runner,
        out WindowsDiskAttributes attributes, out string? error)
    {
        attributes = default;
        var script = "$ErrorActionPreference='Stop'; " +
            $"$d=Get-Disk -Number {diskNumber} -ErrorAction Stop; " +
            "Write-Output ([string]::Format('{0}|{1}|{2}', $d.Number, $d.IsOffline, $d.IsReadOnly))";

        if (!TryRun(runner, script, out string output, out error))
            return false;

        if (!TryParseStatus(output, diskNumber, out attributes))
        {
            error = "Get-Disk vrátil neplatnou nebo nejednoznačnou identitu či atributy disku.";
            return false;
        }
        return true;
    }

    public static bool TrySet(
        int diskNumber, bool readOnly, IToolRunner runner, out string? error)
    {
        string value = readOnly ? "$true" : "$false";
        string script = "$ErrorActionPreference='Stop'; " +
                        $"Set-Disk -Number {diskNumber} -IsReadOnly {value} -ErrorAction Stop";
        return TryRun(runner, script, out _, out error);
    }

    private static bool TryRun(IToolRunner runner, string script, out string output, out string? error)
    {
        output = "";
        error = null;
        string exe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

        if (!File.Exists(exe))
        {
            error = "Windows PowerShell není dostupný pro záložní dotaz Get-Disk.";
            return false;
        }

        try
        {
            var result = runner.Run(exe,
                ["-NoProfile", "-NonInteractive", "-Command", script],
                TimeSpan.FromSeconds(20));
            output = result.StandardOutput;
            if (result.Success) return true;

            error = "Windows Storage: " + result.CombinedOutput.Trim();
            return false;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            error = "Windows Storage dotaz selhal: " + ex.Message;
            return false;
        }
    }
}
