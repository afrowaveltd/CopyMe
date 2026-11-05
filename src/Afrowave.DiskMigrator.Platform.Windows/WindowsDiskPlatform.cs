using System.Runtime.InteropServices;
using System.Security.Principal;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Model;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>
/// The real Windows platform: physical drives, volumes, DISM and the boot tools.
/// Physical drives are addressed exclusively through \\.\PhysicalDriveN - never through drive
/// letters - so identity survives letter changes.
/// </summary>
public sealed class WindowsDiskPlatform : IDiskPlatform
{
    private const int MaxDriveProbe = 32;
    private const int MissesBeforeStop = 8;

    private readonly WindowsVolumeService _volumes;
    private readonly ProcessToolRunner _tools;
    private readonly bool _elevated;

    public WindowsDiskPlatform()
    {
        _volumes = new WindowsVolumeService();
        _tools = new ProcessToolRunner();
        _elevated = DetectElevation();

        ContentProbe = new WindowsContentProbe(_volumes);
        Wim = new DismWimService(_tools, _volumes);
        Boot = new WindowsBootConfigurator(_tools, _volumes);
    }

    public string PlatformName => "Windows (fyzické disky)";

    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? UnavailableReason => IsAvailable ? null : "Tento nástroj je určen pro Windows 10/11.";

    public bool IsElevated => _elevated;

    public IVolumeService Volumes => _volumes;

    public IPartitionContentProbe ContentProbe { get; }

    public IToolRunner Tools => _tools;

    public IWimReimageService Wim { get; }

    public IBootConfigurator Boot { get; }

    public IReadOnlyList<PhysicalDisk> EnumerateDisks()
    {
        var disks = new List<PhysicalDisk>();
        int misses = 0;

        for (int index = 0; index < MaxDriveProbe && misses < MissesBeforeStop; index++)
        {
            var disk = TryReadDisk(index);
            if (disk is null)
            {
                misses++;
                continue;
            }

            misses = 0;
            disks.Add(disk);
        }

        return disks;
    }

    private PhysicalDisk? TryReadDisk(int index)
    {
        string path = @"\\.\PhysicalDrive" + index;

        var handle = Native.CreateFile(
            path, Native.GenericRead, Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        using (handle)
        {
            try
            {
                var geometryBuffer = new byte[256];
                if (!Native.DeviceIoControl(handle, Native.IoctlDiskGetDriveGeometryEx, null, 0,
                        geometryBuffer, geometryBuffer.Length, out int geometryReturned, IntPtr.Zero))
                {
                    return null;
                }

                var (sizeBytes, logicalSectorSize, physicalSectorSize) =
                    NativeParsers.ParseGeometry(geometryBuffer, geometryReturned);

                var numberBuffer = new byte[16];
                int deviceNumber = index;
                if (Native.DeviceIoControl(handle, Native.IoctlStorageGetDeviceNumber, null, 0,
                        numberBuffer, numberBuffer.Length, out _, IntPtr.Zero))
                {
                    deviceNumber = BitConverter.ToInt32(numberBuffer, 4);
                }

                var descriptorBuffer = new byte[4096];
                var query = new byte[12];
                BitConverter.TryWriteBytes(query.AsSpan(0, 4), Native.StorageDeviceProperty);
                BitConverter.TryWriteBytes(query.AsSpan(4, 4), Native.PropertyStandardQuery);

                string model = string.Empty;
                string serial = string.Empty;
                bool removable = false;
                uint busType = 0;

                if (Native.DeviceIoControl(handle, Native.IoctlStorageQueryProperty, query, query.Length,
                        descriptorBuffer, descriptorBuffer.Length, out int descriptorReturned, IntPtr.Zero))
                {
                    (model, serial, removable, busType) =
                        NativeParsers.ParseStorageDescriptor(descriptorBuffer, descriptorReturned);
                }

                bool isContainer = busType == 16; // Storage Spaces
                string? containerReason = isContainer ? "disk je součástí Storage Spaces" : null;

                bool isReadOnly = IsDeviceReadOnly(deviceNumber);

                return new PhysicalDisk
                {
                    Identity = new DiskIdentity(
                        deviceNumber,
                        path,
                        model,
                        serial,
                        sizeBytes,
                        logicalSectorSize,
                        physicalSectorSize,
                        removable,
                        NativeParsers.DescribeBusType(busType)),
                    IsReadOnly = isReadOnly,
                    IsContainerMember = isContainer,
                    ContainerReason = containerReason,
                };
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private static bool IsDeviceReadOnly(int diskNumber)
    {
        string path = @"\\.\PhysicalDrive" + diskNumber;

        var handle = Native.CreateFile(
            path, Native.GenericRead | Native.GenericWrite, Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            // A disk that refuses a write capable handle is either read-only or write-protected.
            handle.Dispose();
            return true;
        }

        handle.Dispose();
        return false;
    }

    public IBlockReader OpenReader(PhysicalDisk disk) =>
        WindowsDiskDevice.Open(
            disk.Identity.DevicePath,
            writable: false,
            disk.Identity.LogicalSectorSize,
            disk.Identity.SizeBytes);

    public IBlockDevice OpenDevice(PhysicalDisk disk) =>
        WindowsDiskDevice.Open(
            disk.Identity.DevicePath,
            writable: true,
            disk.Identity.LogicalSectorSize,
            disk.Identity.SizeBytes);

    public SafetyEnvironment GetSafetyEnvironment()
    {
        var volumes = new List<MountedVolume>();
        string? systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        string executablePath = Environment.ProcessPath ?? "AfrowaveDiskMigrator.exe";
        string? executableRoot = Path.GetPathRoot(executablePath);

        foreach (var volume in WindowsVolumeService.Enumerate())
        {
            if (volume.Extents.Count == 0) continue;

            var usage = VolumeUsage.None;
            string root = volume.DriveLetter is { } letter ? letter + ":\\" : volume.GuidPath;

            if (volume.DriveLetter is { } drive)
            {
                string driveRoot = drive + ":\\";

                if (systemDrive is not null &&
                    string.Equals(driveRoot, systemDrive + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    usage |= VolumeUsage.RunningOs;
                }

                if (systemRoot is not null &&
                    systemRoot.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase))
                {
                    usage |= VolumeUsage.RunningOs;
                }

                if (executableRoot is not null &&
                    string.Equals(driveRoot, executableRoot, StringComparison.OrdinalIgnoreCase))
                {
                    usage |= VolumeUsage.ExecutableHost;
                }

                try
                {
                    if (File.Exists(Path.Combine(driveRoot, "pagefile.sys"))
                        || File.Exists(Path.Combine(driveRoot, "hiberfil.sys"))
                        || File.Exists(Path.Combine(driveRoot, "swapfile.sys")))
                    {
                        usage |= VolumeUsage.PagingCarrier;
                    }

                    if (Directory.Exists(Path.Combine(driveRoot, @"Windows\System32")))
                    {
                        usage |= VolumeUsage.ContainsWindows;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A volume we cannot inspect is still described; protection comes from other flags.
                }
            }

            volumes.Add(new MountedVolume(
                root,
                volume.DriveLetter,
                volume.DiskNumber,
                volume.StartingOffset,
                volume.FileSystemName,
                volume.TotalBytes,
                volume.FreeBytes,
                usage,
                volume.Label));
        }

        return new SafetyEnvironment
        {
            ProtectedDisks = [],
            MountedVolumes = volumes,
            ExecutablePath = executablePath,
            SystemDrive = systemDrive is null ? null : systemDrive + "\\",
            SystemRoot = systemRoot,
            ContainerDisks = [],
        };
    }

    public bool TrySetDiskReadOnly(PhysicalDisk disk, bool readOnly)
    {
        string path = disk.Identity.DevicePath;

        var handle = Native.CreateFile(
            path, Native.GenericRead | Native.GenericWrite, Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();

            // Clearing a read-only flag needs a write capable handle; setting it may not.
            if (readOnly) return false;

            handle = Native.CreateFile(
                path, Native.GenericRead, Native.FileShareRead | Native.FileShareWrite,
                IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                return false;
            }
        }

        using (handle)
        {
            var buffer = new byte[24];
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), Native.DiskAttributeVersion);

            // Persist = 0 on purpose: the attribute then lives only for this session. If the tool
            // is killed or the machine loses power, a restart clears it. A persisted read-only
            // flag would quietly change how the customer's disk behaves afterwards, which is
            // exactly what must not happen.
            BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), 0u);

            BitConverter.TryWriteBytes(buffer.AsSpan(8, 8), readOnly ? Native.DiskAttributeReadOnly : 0UL);
            BitConverter.TryWriteBytes(buffer.AsSpan(16, 8), Native.DiskAttributeReadOnly);

            return Native.DeviceIoControl(handle, Native.IoctlDiskSetDiskAttributes,
                buffer, buffer.Length, null, 0, out _, IntPtr.Zero);
        }
    }

    public void Rescan()
    {
        // EnumerateDisks always queries the device afresh, so there is nothing cached to drop.
    }

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
