using System.Runtime.InteropServices;
using System.Text;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>
/// Finds page files actually registered in the currently running Windows kernel.
/// A pagefile.sys or hiberfil.sys on an offline customer installation is NOT
/// evidence that the servicing computer uses that volume.
/// </summary>
internal static class WindowsActivePaging
{
    public static IReadOnlySet<string> GetActiveVolumeGuids(IReadOnlyList<VolumeRecord> volumes)
    {
        var activePageFiles = new List<string>();
        Native.PageFileEnumerationCallback callback = (_, _, path) =>
        {
            activePageFiles.Add(path);
            return true;
        };

        if (!Native.EnumPageFiles(callback, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Windows nedovolil ověřit aktivní stránkovací soubory (EnumPageFilesW, chyba {error}).");
        }

        var mappings = volumes.Select(volume =>
            (Volume: volume, DevicePath: TryGetNtDevicePath(volume.GuidPath))).ToArray();

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pageFile in activePageFiles)
        {
            var match = mappings.FirstOrDefault(m =>
                IsPageFileOnVolume(pageFile, m.DevicePath, m.Volume.RootPath));

            if (match.Volume is null || match.Volume.Extents.Count == 0)
            {
                // An unknown active paging host could be a proposed target.
                throw new IOException("Aktivní stránkovací soubor nemá jednoznačně určený fyzický disk: " +
                                      pageFile + ". Operaci odmítáme.");
            }
            active.Add(match.Volume.GuidPath);
        }
        return active;
    }

    internal static bool IsPageFileOnVolume(
        string pageFilePath, string? ntDevicePath, string? volumeRoot)
    {
        if (string.IsNullOrWhiteSpace(pageFilePath)) return false;
        string candidate = pageFilePath.Replace('/', '\\');
        if (candidate.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[4..];

        return HasPathPrefix(candidate, ntDevicePath) ||
               HasPathPrefix(candidate, volumeRoot);
    }

    private static bool HasPathPrefix(string path, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        string root = prefix.TrimEnd('\\') + "\\";
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetNtDevicePath(string guidPath)
    {
        // QueryDosDevice expects Volume{GUID}, not \\?\Volume{GUID}\.
        if (!guidPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return null;

        string deviceName = guidPath[4..].TrimEnd('\\');
        var buffer = new StringBuilder(2048);
        uint count = Native.QueryDosDevice(deviceName, buffer, buffer.Capacity);
        if (count == 0) return null;

        return buffer.ToString().Split('\0')[0];
    }
}
