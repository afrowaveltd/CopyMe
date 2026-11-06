using System.Runtime.InteropServices;
using System.Text;
using Afrowave.DiskMigrator.Core.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Afrowave.DiskMigrator.Platform.Windows;

internal sealed class VolumeRecord
{
    public required string GuidPath { get; init; }

    public required IReadOnlyList<VolumeDiskExtent> Extents { get; init; }

    /// <summary>True if Windows could not determine the physical disk ownership.</summary>
    public bool ExtentsUnavailable { get; init; }

    public char? DriveLetter { get; set; }

    public string? Label { get; init; }

    public string FileSystemName { get; init; } = string.Empty;

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public int DiskNumber => Extents.Count > 0 ? Extents[0].DiskNumber : -1;

    public long StartingOffset => Extents.Count > 0 ? Extents[0].StartingOffset : -1;

    /// <summary>True when the volume lives on more than one physical disk.</summary>
    public bool SpansMultipleDisks => Extents.Count > 1;

    public bool HasFileSystem => !string.IsNullOrEmpty(FileSystemName);

    /// <summary>Root path such as "E:\", or null when the volume has no drive letter.</summary>
    public string? RootPath => DriveLetter is { } letter ? letter + ":\\" : null;
}

/// <summary>
/// Mounting, dismounting and enumerating volumes of physical disks through the Windows
/// volume manager. Used both for inspection and for handing partitions to DISM and bcdboot.
/// </summary>
internal interface ISourceVolumeLock : IDisposable;

/// <summary>Injectable volume takeover API so locking/rollback can be tested without Windows.</summary>
internal interface IWindowsTakeoverVolumes
{
    IReadOnlyList<VolumeRecord> EnumerateForTakeover();
    WindowsVolumeService.VolumeFlushResult FlushAndLock(VolumeRecord volume, out ISourceVolumeLock? volumeLock);
    bool TryRemount(VolumeRecord volume);
}

internal sealed class WindowsVolumeService : IVolumeService, IWindowsTakeoverVolumes
{
    public IReadOnlyList<VolumeRecord> EnumerateForTakeover() => Enumerate();

    /// <summary>
    /// Win32 volume enumeration/mount-point APIs need the trailing backslash,
    /// but CreateFile requires the volume DEVICE path without it.
    /// </summary>
    internal static string ToDevicePath(string volumeGuidPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeGuidPath);
        return volumeGuidPath.EndsWith('\\') ? volumeGuidPath[..^1] : volumeGuidPath;
    }

    private static readonly char[] CandidateLetters =
        ['Z', 'Y', 'X', 'W', 'V', 'U', 'T', 'S', 'R', 'Q', 'P', 'O', 'N', 'M', 'L', 'K'];

    private readonly Dictionary<char, string> _mounted = [];

    public static List<VolumeRecord> Enumerate()
    {
        var records = new List<VolumeRecord>();
        var nameBuffer = new StringBuilder(1024);

        using var find = Native.FindFirstVolume(nameBuffer, nameBuffer.Capacity);
        if (find.IsInvalid)
            throw new IOException("FindFirstVolume selhal: nelze bezpečně zjistit připojené svazky.");

        do
        {
            string guidPath = nameBuffer.ToString();
            if (guidPath.Length == 0) continue;

            var extents = ReadExtents(guidPath, out bool extentsUnavailable);
            if (extents.Count == 0)
            {
                // Never silently assume that an unreadable volume belongs to another disk.
                records.Add(new VolumeRecord
                {
                    GuidPath = guidPath, Extents = extents, ExtentsUnavailable = extentsUnavailable
                });
                continue;
            }

            char? letter = ReadDriveLetter(guidPath);

            var label = new StringBuilder(261);
            var fsName = new StringBuilder(261);
            Native.GetVolumeInformation(guidPath, label, label.Capacity, out _, out _, out _, fsName, fsName.Capacity);

            long total = 0;
            long free = 0;
            if (letter is { } drive)
            {
                Native.GetDiskFreeSpaceEx(drive + ":\\", out _, out ulong totalBytes, out ulong freeBytes);
                total = (long)totalBytes;
                free = (long)freeBytes;
            }

            records.Add(new VolumeRecord
            {
                GuidPath = guidPath,
                Extents = extents,
                DriveLetter = letter,
                Label = label.Length > 0 ? label.ToString() : null,
                FileSystemName = fsName.ToString(),
                TotalBytes = total,
                FreeBytes = free,
            });
        }
        while (Native.FindNextVolume(find, nameBuffer, nameBuffer.Capacity));

        return records;
    }

    private static IReadOnlyList<VolumeDiskExtent> ReadExtents(
        string guidPath, out bool unavailable)
    {
        unavailable = false;
        using var handle = Native.CreateFile(
            ToDevicePath(guidPath), Native.GenericRead, Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            unavailable = true;
            return [];
        }

        var buffer = new byte[512];
        if (!Native.DeviceIoControl(handle, Native.IoctlVolumeGetVolumeDiskExtents, null, 0,
                buffer, buffer.Length, out int returned, IntPtr.Zero))
        {
            unavailable = true;
            return [];
        }

        try
        {
            return NativeParsers.ParseVolumeExtents(buffer, returned);
        }
        catch (IOException)
        {
            unavailable = true;
            return [];
        }
    }

    private static char? ReadDriveLetter(string guidPath)
    {
        var paths = new char[1024];
        if (!Native.GetVolumePathNamesForVolumeName(guidPath, paths, paths.Length, out int length) || length < 2)
        {
            return null;
        }

        // The returned block is a sequence of null-terminated strings. Accept only "X:\" style
        // drive letters, not directory mount points.
        int index = 0;
        while (index < length && paths[index] != '\0')
        {
            int end = index;
            while (end < length && paths[end] != '\0') end++;

            string candidate = new(paths, index, end - index);
            if (candidate.Length == 3 && candidate[1] == ':' && candidate[2] == '\\')
            {
                if (DriveInfo.GetDrives().Any(d =>
                        string.Equals(d.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return char.ToUpperInvariant(candidate[0]);
                }
            }

            index = end + 1;
        }

        return null;
    }

    public static VolumeRecord? FindVolume(int diskNumber, long partitionStartBytes)
    {
        return Enumerate().FirstOrDefault(v =>
            v.Extents.Count > 0 &&
            v.Extents[0].DiskNumber == diskNumber &&
            Math.Abs(v.Extents[0].StartingOffset - partitionStartBytes) <= 512);
    }

    public char MountVolume(int diskNumber, long partitionStartBytes, bool readOnly)
    {
        var volume = FindVolume(diskNumber, partitionStartBytes)
                     ?? throw new IOException(
                         $"Svazek na disku {diskNumber} (offset {partitionStartBytes}) nebyl nalezen. " +
                         "Po zápisu tabulky oddílů může chvíli trvat, než jej systém zveřejní.");

        if (volume.DriveLetter is { } existing)
        {
            return existing;
        }

        char letter = PickFreeLetter();
        string mountPoint = letter + ":\\";

        if (!Native.SetVolumeMountPoint(mountPoint, volume.GuidPath))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Nepodařilo se přiřadit písmeno {letter}: svazku (chyba {error}: {Win32Error.Describe(error)}). " +
                "Zkuste uvolnit některé písmeno jednotky.");
        }

        _mounted[letter] = volume.GuidPath;
        return letter;
    }

    public void DismountVolume(char driveLetter)
    {
        // NEVER delete an existing customer drive letter. Only release temporary
        // letters allocated through MountVolume by this service instance.
        if (!_mounted.ContainsKey(driveLetter)) return;

        string mountPoint = driveLetter + ":\\";
        if (!Native.DeleteVolumeMountPoint(mountPoint))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException($"Dočasné písmeno {driveLetter}: nelze uvolnit (chyba {error}).");
        }
        _mounted.Remove(driveLetter);
    }

    public void RefreshPartitionTable(int diskNumber)
    {
        string path = @"\\.\PhysicalDrive" + diskNumber;

        using var handle = Native.CreateFile(
            path, Native.GenericRead | Native.GenericWrite, Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid) return;

        Native.DeviceIoControl(handle, Native.IoctlDiskUpdateProperties, null, 0, null, 0, out _, IntPtr.Zero);
    }

    public bool WaitForVolumes(int diskNumber, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int stableCount = 0;
        int lastCount = -1;

        while (DateTime.UtcNow < deadline)
        {
            int count = Enumerate().Count(v => v.Extents.Any(e => e.DiskNumber == diskNumber));

            if (count == lastCount && count > 0)
            {
                stableCount++;
                if (stableCount >= 2) return true;
            }
            else
            {
                stableCount = 0;
                lastCount = count;
            }

            Thread.Sleep(250);
        }

        return lastCount > 0;
    }

    public long GetFreeBytes(string rootPath)
    {
        if (!Native.GetDiskFreeSpaceEx(rootPath, out ulong free, out _, out _)) return 0;
        return (long)free;
    }

    /// <summary>Outcome of locking/flush and dismount.</summary>
    public sealed record VolumeFlushResult(bool Success, string Message);

    private sealed class LockedSourceVolume(SafeFileHandle handle) : ISourceVolumeLock
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!handle.IsInvalid)
                    Native.DeviceIoControl(handle, Native.FsctlUnlockVolume,
                        null, 0, null, 0, out _, IntPtr.Zero);
            }
            finally
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Hold FSCTL_LOCK_VOLUME all the way through the read-only attribute transition.
    /// FSCTL_LOCK_VOLUME flushes pending writes. Unlike dismount+unlock, the live
    /// handle prevents another process from remounting a writable filesystem in between.
    /// Caller MUST dispose volumeLock on every path.
    /// </summary>
    public VolumeFlushResult FlushAndLock(VolumeRecord volume, out ISourceVolumeLock? volumeLock)
    {
        volumeLock = null;
        // Microsoft documents FILE_SHARE_READ | FILE_SHARE_WRITE for volume FSCTL handles.
        var handle = Native.CreateFile(
            ToDevicePath(volume.GuidPath),
            Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return new VolumeFlushResult(false,
                $"svazek nelze otevřít (chyba {error}: {Win32Error.Describe(error)})");
        }

        if (!Native.DeviceIoControl(handle, Native.FsctlLockVolume,
                null, 0, null, 0, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return new VolumeFlushResult(false,
                $"svazek nelze uzamknout (chyba {error}: {Win32Error.Describe(error)}) – " +
                "něco na něm má otevřené soubory");
        }

        var locked = new LockedSourceVolume(handle);
        if (!Native.DeviceIoControl(handle, Native.FsctlDismountVolume,
                null, 0, null, 0, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            locked.Dispose();
            return new VolumeFlushResult(false,
                $"svazek nelze odpojit (chyba {error}: {Win32Error.Describe(error)})");
        }

        // The volume stays locked until the caller has set and verified disk read-only.
        volumeLock = locked;
        return new VolumeFlushResult(true, "zápisy vyprázdněny, svazek odpojen a uzamčen");
    }

    /// <summary>Re-opens a volume so that Windows mounts it again at its usual place.</summary>
    public bool TryRemount(VolumeRecord volume)
    {
        string? root = volume.RootPath;
        if (root is null) return true;

        try
        {
            return Directory.Exists(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public bool TryDismountVolume(int diskNumber, long partitionStartBytes)
    {
        var volume = FindVolume(diskNumber, partitionStartBytes);
        if (volume is null) return true;

        using var handle = OpenVolume(volume.GuidPath);
        if (handle is null || handle.IsInvalid) return false;

        if (!Native.DeviceIoControl(handle, Native.FsctlLockVolume, null, 0, null, 0, out _, IntPtr.Zero))
        {
            return false;
        }

        Native.DeviceIoControl(handle, Native.FsctlAllowExtendedDasdIo, null, 0, null, 0, out _, IntPtr.Zero);

        // Dismounting flushes the file system and detaches it, so raw writes below it cannot race
        // with a live file system. A failure here is not fatal: the caller reports a warning.
        return Native.DeviceIoControl(handle, Native.FsctlDismountVolume, null, 0, null, 0, out _, IntPtr.Zero);
    }

    public void RemoveAllMountPoints(int diskNumber)
    {
        foreach (var volume in Enumerate())
        {
            if (!volume.Extents.Any(e => e.DiskNumber == diskNumber)) continue;
            if (volume.DriveLetter is { } letter)
            {
                Native.DeleteVolumeMountPoint(letter + ":\\");
                _mounted.Remove(letter);
            }
        }
    }

    public void ReleaseAll()
    {
        foreach (char letter in _mounted.Keys.ToList())
        {
            Native.DeleteVolumeMountPoint(letter + ":\\");
        }

        _mounted.Clear();
    }

    private static SafeFileHandle? OpenVolume(string guidPath)
    {
        var handle = Native.CreateFile(
            ToDevicePath(guidPath),
            Native.GenericRead | Native.GenericWrite,
            0,
            IntPtr.Zero,
            Native.OpenExisting,
            0,
            IntPtr.Zero);

        return handle.IsInvalid ? null : handle;
    }

    private static char PickFreeLetter()
    {
        uint mask = Native.GetLogicalDrives();

        foreach (char letter in CandidateLetters)
        {
            int index = letter - 'A';
            if (index < 0 || index > 25) continue;
            if ((mask & (1u << index)) == 0) return letter;
        }

        throw new IOException("Nelze nalézt volné písmeno jednotky pro dočasné připojení svazku.");
    }
}
