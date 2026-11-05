using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>Win32 declarations needed for raw disk access. Deliberately minimal.</summary>
internal static class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;

    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    public const uint IoctlDiskUpdateProperties = 0x00070140;
    public const uint IoctlDiskSetDiskAttributes = 0x0007C0F4;
    public const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    public const uint FsctlLockVolume = 0x00090018;
    public const uint FsctlDismountVolume = 0x00090020;
    public const uint FsctlAllowExtendedDasdIo = 0x00090083;

    public const uint StorageDeviceProperty = 0;
    public const uint PropertyStandardQuery = 0;

    public const ulong DiskAttributeReadOnly = 0x0000000000000001;
    public const uint DiskAttributeVersion = 1;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[]? inBuffer,
        int inBufferSize,
        byte[]? outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool ReadFile(
        SafeFileHandle file,
        byte* buffer,
        int bytesToRead,
        out int bytesRead,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool WriteFile(
        SafeFileHandle file,
        byte* buffer,
        int bytesToWrite,
        out int bytesWritten,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFilePointerEx(
        SafeFileHandle file,
        long distanceToMove,
        out long newFilePointer,
        uint moveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushFileBuffers(SafeFileHandle file);

    // ---------------------------------------------------------------- volume enumeration

    [DllImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFindHandle FindFirstVolume(StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "FindNextVolumeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextVolume(SafeFindHandle findVolume, StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindVolumeClose(IntPtr findVolume);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathNamesForVolumeName(
        string volumeName,
        char[] volumePathNames,
        int bufferLength,
        out int returnLength);

    [DllImport("kernel32.dll", EntryPoint = "SetVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetVolumeMountPoint(string volumeMountPoint, string volumeName);

    [DllImport("kernel32.dll", EntryPoint = "DeleteVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteVolumeMountPoint(string volumeMountPoint);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPoint(
        string mountPoint,
        StringBuilder volumeName,
        int bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetLogicalDrives")]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalBytes,
        out ulong totalFreeBytes);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);
}

internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindHandle() : base(true)
    {
    }

    protected override bool ReleaseHandle() => Native.FindVolumeClose(handle);
}

/// <summary>VOLUME_DISK_EXTENT decode.</summary>
internal readonly record struct VolumeDiskExtent(int DiskNumber, long StartingOffset, long Length);

internal static class NativeParsers
{
    /// <summary>Parses DISK_GEOMETRY_EX into size and sector sizes.</summary>
    public static (long SizeBytes, int BytesPerSector, int PhysicalBytesPerSector) ParseGeometry(byte[] buffer, int returned)
    {
        if (returned < 32) throw new IOException("Zařízení nevrátilo platnou geometrii.");

        int bytesPerSector = BitConverter.ToInt32(buffer, 16);
        long size = BitConverter.ToInt64(buffer, 24);

        // DISK_GEOMETRY_EX.Data may hold a DISK_GEOMETRY_DETECTION_INFO with the physical sector size.
        int physical = bytesPerSector;
        int dataLength = returned - 32;
        if (dataLength >= 8)
        {
            int dataOffset = 32;
            // The first 8 bytes of the data blob are the detection info: legacy geometry (24 bytes)
            // followed by LogicalBytesPerSector / PhysicalBytesPerSector.
            if (dataLength >= 24 + 8)
            {
                int logical = BitConverter.ToInt32(buffer, dataOffset + 24);
                int phys = BitConverter.ToInt32(buffer, dataOffset + 28);
                if (logical > 0) bytesPerSector = logical;
                if (phys >= bytesPerSector) physical = phys;
            }
        }

        if (size <= 0) throw new IOException("Zařízení hlásí nulovou velikost.");
        if (bytesPerSector <= 0) bytesPerSector = 512;

        return (size, bytesPerSector, physical);
    }

    /// <summary>Parses STORAGE_DEVICE_DESCRIPTOR.</summary>
    public static (string Model, string Serial, bool Removable, uint BusType) ParseStorageDescriptor(byte[] buffer, int returned)
    {
        if (returned < 36) return (string.Empty, string.Empty, false, 0);

        bool removable = buffer[10] != 0;
        int productOffset = BitConverter.ToInt32(buffer, 16);
        int vendorOffset = BitConverter.ToInt32(buffer, 12);
        int serialOffset = BitConverter.ToInt32(buffer, 24);
        uint busType = BitConverter.ToUInt32(buffer, 28);

        string vendor = ReadAnsi(buffer, returned, vendorOffset);
        string product = ReadAnsi(buffer, returned, productOffset);
        string serial = ReadAnsi(buffer, returned, serialOffset);

        string model = (vendor + " " + product).Trim();
        return (model, serial, removable, busType);
    }

    public static string ReadAnsi(byte[] buffer, int returned, int offset)
    {
        if (offset <= 0 || offset >= returned) return string.Empty;

        int end = offset;
        while (end < returned && buffer[end] != 0) end++;
        if (end == offset) return string.Empty;

        return Encoding.UTF8.GetString(buffer, offset, end - offset).Trim();
    }

    /// <summary>Parses VOLUME_DISK_EXTENTS.</summary>
    public static List<VolumeDiskExtent> ParseVolumeExtents(byte[] buffer, int returned)
    {
        var extents = new List<VolumeDiskExtent>();
        if (returned < 8) return extents;

        int count = BitConverter.ToInt32(buffer, 0);
        const int extentSize = 24;
        const int firstExtent = 8;

        for (int i = 0; i < count; i++)
        {
            int offset = firstExtent + (i * extentSize);
            if (offset + extentSize > returned) break;

            int diskNumber = BitConverter.ToInt32(buffer, offset);
            long startingOffset = BitConverter.ToInt64(buffer, offset + 8);
            long length = BitConverter.ToInt64(buffer, offset + 16);
            extents.Add(new VolumeDiskExtent(diskNumber, startingOffset, length));
        }

        return extents;
    }

    public static string DescribeBusType(uint busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        6 => "Fibre",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "Secure Digital",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "neznámé",
    };
}
