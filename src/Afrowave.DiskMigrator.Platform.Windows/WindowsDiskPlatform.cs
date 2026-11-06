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
public sealed class WindowsDiskPlatform : IDiskPlatform, IDiskWriteProtection
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
        SourceTakeover = new WindowsSourceTakeover(_volumes, this);
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

    public ISourceTakeoverService? SourceTakeover { get; }

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

                // Query actual 512e/4Kn geometry instead of guessing from DISK_GEOMETRY_EX.
                byte[] alignmentQuery = WindowsStorageAlignmentProtocol.CreateQuery();
                byte[] alignmentBuffer = new byte[WindowsStorageAlignmentProtocol.DescriptorLength];
                if (Native.DeviceIoControl(handle, Native.IoctlStorageQueryProperty,
                        alignmentQuery, alignmentQuery.Length,
                        alignmentBuffer, alignmentBuffer.Length, out int alignmentReturned, IntPtr.Zero) &&
                    WindowsStorageAlignmentProtocol.TryDecode(
                        alignmentBuffer.AsSpan(0, Math.Min(alignmentReturned, alignmentBuffer.Length)),
                        out int logical, out int physical))
                {
                    logicalSectorSize = logical;
                    physicalSectorSize = physical;
                }

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

                bool attributesKnown = TryGetDiskAttributes(
                    deviceNumber, out var attributes, out var attributeError);
                // Unknown attributes and OFFLINE status must not be treated as a usable target.
                bool isContainer = busType == 16 || !attributesKnown || attributes.IsOffline;
                string? containerReason = busType == 16 ? "disk je součástí Storage Spaces"
                    : !attributesKnown ? "atributy disku nelze spolehlivě přečíst: " + attributeError
                    : attributes.IsOffline ? "disk je offline" : null;

                bool isReadOnly = attributesKnown && attributes.IsReadOnly;

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

    private bool TryGetDiskAttributes(
        int diskNumber, out WindowsDiskAttributes attributes, out string? error)
    {
        attributes = default;
        error = null;
        string path = @"\\.\PhysicalDrive" + diskNumber;
        using var handle = Native.CreateFile(
            path, Native.GenericRead, Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int openError = Marshal.GetLastWin32Error();
            if (WindowsDiskStorageCmdlets.TryRead(diskNumber, _tools, out attributes, out error))
                return true;
            error = $"IOCTL nelze otevřít (chyba {openError}), Get-Disk: {error}";
            return false;
        }

        var buffer = new byte[WindowsDiskProtocol.GetLength];
        if (!Native.DeviceIoControl(handle, Native.IoctlDiskGetDiskAttributes,
                null, 0, buffer, buffer.Length, out int returned, IntPtr.Zero))
        {
            int ioctlError = Marshal.GetLastWin32Error();
            if (WindowsDiskStorageCmdlets.TryRead(diskNumber, _tools, out attributes, out error))
                return true;
            error = $"IOCTL_DISK_GET_DISK_ATTRIBUTES selhal ({ioctlError}), Get-Disk: {error}";
            return false;
        }

        if (!WindowsDiskProtocol.TryDecodeGet(
                buffer.AsSpan(0, Math.Clamp(returned, 0, buffer.Length)), out attributes))
        {
            if (WindowsDiskStorageCmdlets.TryRead(diskNumber, _tools, out attributes, out error))
                return true;
            error = "IOCTL vrátil neplatnou strukturu, Get-Disk: " + error;
            return false;
        }

        return true;
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

        var mountedRecords = WindowsVolumeService.Enumerate();
        var activePagingVolumes = WindowsActivePaging.GetActiveVolumeGuids(mountedRecords);
        foreach (var volume in mountedRecords)
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
                    // The existence of hiberfil.sys/pagefile.sys on an old Windows
                    // installation does not make it an active paging volume.
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

            if (activePagingVolumes.Contains(volume.GuidPath))
                usage |= VolumeUsage.PagingCarrier;

            volumes.Add(new MountedVolume(
                root,
                volume.DriveLetter,
                volume.DiskNumber,
                volume.StartingOffset,
                volume.FileSystemName,
                volume.TotalBytes,
                volume.FreeBytes,
                usage,
                volume.Label)
            {
                SpansMultipleDisks = volume.SpansMultipleDisks,
            });
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

    public bool TrySetDiskReadOnly(PhysicalDisk disk, bool readOnly) =>
        TrySetDiskReadOnlyCore(disk.Identity.Number, readOnly, out _);

    bool IDiskWriteProtection.TrySetReadOnlyState(int diskNumber, bool readOnly, out string? error) =>
        TrySetDiskReadOnlyCore(diskNumber, readOnly, out error);

    bool IDiskWriteProtection.TryGetAttributes(
        int diskNumber, out WindowsDiskAttributes attributes, out string? error) =>
        TryGetDiskAttributes(diskNumber, out attributes, out error);

    private bool TrySetDiskReadOnlyCore(int diskNumber, bool readOnly, out string? error)
    {
        error = null;
        string path = @"\\.\PhysicalDrive" + diskNumber;

        using var handle = Native.CreateFile(
            path, Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int openError = Marshal.GetLastWin32Error();
            if (WindowsDiskStorageCmdlets.TrySet(diskNumber, readOnly, _tools, out error))
                return true;
            error = $"Nelze otevřít disk pro IOCTL ({openError}), Set-Disk: {error}";
            return false;
        }

        using (handle)
        {
            // Full 40-byte SET_DISK_ATTRIBUTES, Version = sizeof(GET_DISK_ATTRIBUTES).
            // The mask contains READ_ONLY only; OFFLINE is never changed.
            var buffer = WindowsDiskProtocol.EncodeSetReadOnly(readOnly);

            if (Native.DeviceIoControl(handle, Native.IoctlDiskSetDiskAttributes,
                    buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
            {
                return true;
            }

            int ioctlError = Marshal.GetLastWin32Error();
            if (WindowsDiskStorageCmdlets.TrySet(diskNumber, readOnly, _tools, out error))
                return true;
            error = $"IOCTL_DISK_SET_DISK_ATTRIBUTES selhal ({ioctlError}), Set-Disk: {error}";
            return false;
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
