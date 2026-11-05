using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Model;
using Afrowave.DiskMigrator.Core.Partitioning;

namespace Afrowave.DiskMigrator.Platform.Image;

/// <summary>One simulated disk: an image file plus the identity the tool will report for it.</summary>
public sealed class SimulatedDisk
{
    public required string ImagePath { get; init; }

    public required DiskIdentity Identity { get; init; }

    /// <summary>Models the device level read-only attribute.</summary>
    public bool ReadOnly { get; set; }

    public bool Offline { get; set; }

    public bool ContainerMember { get; set; }

    public string? ContainerReason { get; set; }

    public PhysicalDisk ToPhysicalDisk() => new()
    {
        Identity = Identity,
        IsOffline = Offline,
        IsReadOnly = ReadOnly,
        IsContainerMember = ContainerMember,
        ContainerReason = ContainerReason,
    };
}

/// <summary>
/// An <see cref="IDiskPlatform"/> that works on image files. Used by the automated tests and
/// by the training/dry-run mode of the CLI; it can never reach a real disk.
/// </summary>
public sealed class ImageDiskPlatform : IDiskPlatform
{
    private readonly List<SimulatedDisk> _disks;
    private readonly SafetyEnvironment _environment;

    public ImageDiskPlatform(IEnumerable<SimulatedDisk> disks, SafetyEnvironment environment)
    {
        _disks = disks.ToList();
        _environment = environment;
        ContentProbe = new DelegateContentProbe((_, _) => new PartitionContentProbe(
            false, "simulované disky neobsahují připojitelné svazky", false, null, false, false, false, null, []));
        Volumes = new NullVolumeService();
        Tools = new NullToolRunner();
        Wim = new UnavailableWimService("simulované prostředí");
        Boot = new UnavailableBootConfigurator("simulované prostředí");
        SourceTakeover = new SimulatedSourceTakeover(_disks);
    }

    public string PlatformName => "Simulované disky (obrazové soubory)";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public bool IsElevated => false;

    public IReadOnlyList<SimulatedDisk> SimulatedDisks => _disks;

    public IPartitionContentProbe ContentProbe { get; set; }

    public IVolumeService Volumes { get; set; }

    public IToolRunner Tools { get; set; }

    public IWimReimageService Wim { get; set; }

    public IBootConfigurator Boot { get; set; }

    public ISourceTakeoverService? SourceTakeover { get; set; }

    /// <summary>Set to true when the tool tried to open a disk for writing that it must not.</summary>
    public List<int> WriteAttempts { get; } = [];

    public IReadOnlyList<PhysicalDisk> EnumerateDisks() =>
        _disks.Select(d => d.ToPhysicalDisk()).ToList();

    public IBlockReader OpenReader(PhysicalDisk disk)
    {
        var simulated = Find(disk);
        return ImageBlockDevice.OpenReader(simulated.ImagePath, disk.Identity.LogicalSectorSize);
    }

    public IBlockDevice OpenDevice(PhysicalDisk disk)
    {
        var simulated = Find(disk);
        WriteAttempts.Add(disk.Identity.Number);

        if (simulated.ReadOnly)
        {
            throw new UnauthorizedAccessException(
                $"Disk {disk.Identity.Number} je nastaven jako pouze pro čtení a nelze jej otevřít pro zápis.");
        }

        return ImageBlockDevice.OpenWritable(simulated.ImagePath, disk.Identity.LogicalSectorSize);
    }

    public SafetyEnvironment GetSafetyEnvironment() => _environment;

    public bool TrySetDiskReadOnly(PhysicalDisk disk, bool readOnly)
    {
        var simulated = _disks.FirstOrDefault(d => d.Identity.Number == disk.Identity.Number);
        if (simulated is null) return false;
        simulated.ReadOnly = readOnly;
        return true;
    }

    public void Rescan()
    {
        // Nothing to refresh: image files cannot appear or disappear behind our back.
    }

    private SimulatedDisk Find(PhysicalDisk disk) =>
        _disks.FirstOrDefault(d => d.Identity.Number == disk.Identity.Number)
        ?? throw new InvalidOperationException($"Simulovaný disk {disk.Identity.Number} neexistuje.");
}

public sealed class DelegateContentProbe : IPartitionContentProbe
{
    private readonly Func<int, long, PartitionContentProbe> _probe;

    public DelegateContentProbe(Func<int, long, PartitionContentProbe> probe) => _probe = probe;

    public PartitionContentProbe Probe(int diskNumber, long partitionStartBytes) => _probe(diskNumber, partitionStartBytes);
}

public sealed class NullVolumeService : IVolumeService
{
    public char MountVolume(int diskNumber, long partitionStartBytes, bool readOnly) =>
        throw new NotSupportedException("Simulované disky nelze připojit.");

    public void DismountVolume(char driveLetter)
    {
    }

    public void RefreshPartitionTable(int diskNumber)
    {
    }

    public bool WaitForVolumes(int diskNumber, TimeSpan timeout) => true;

    public long GetFreeBytes(string rootPath) => 0;

    public bool TryDismountVolume(int diskNumber, long partitionStartBytes) => true;

    public void RemoveAllMountPoints(int diskNumber)
    {
    }
}

public sealed class NullToolRunner : IToolRunner
{
    public string? ResolveTool(string fileName) => null;

    public ToolResult Run(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default) =>
        new(-1, string.Empty, "Simulované prostředí: nástroje systému nejsou dostupné.");
}

public sealed class UnavailableWimService : IWimReimageService
{
    public UnavailableWimService(string reason) => UnavailableReason = reason;

    public bool IsAvailable => false;

    public string? UnavailableReason { get; }

    public WimReimageResult Execute(
        WimReimageRequest request,
        ProgressCallback? onProgress,
        Action<string>? onOutput,
        CancellationToken cancellationToken = default) =>
        new(false, 0, "Služba obrazů WIM není v tomto prostředí dostupná.");
}

public sealed class UnavailableBootConfigurator : IBootConfigurator
{
    public UnavailableBootConfigurator(string reason) => UnavailableReason = reason;

    public bool IsAvailable => false;

    public string? UnavailableReason { get; }

    public BootConfigurationResult Configure(BootConfigurationRequest request, CancellationToken cancellationToken = default) =>
        new(false, [], ["Konfigurace zavádění není v tomto prostředí dostupná."]);
}

/// <summary>Builds realistic GPT/MBR disk images for tests and for the training mode.</summary>
public sealed class DiskImageBuilder
{
    private readonly List<PartitionWriteEntry> _entries = [];
    private readonly List<(long Offset, byte[] Data)> _content = [];
    private readonly string _path;
    private readonly long _totalBytes;
    private readonly PartitionTableKind _kind;
    private readonly int _sectorSize;
    private Guid _diskGuid = Guid.NewGuid();
    private uint _signature = 0x1234_ABCD;
    private byte[]? _bootCode;

    private DiskImageBuilder(string path, long totalBytes, PartitionTableKind kind, int sectorSize)
    {
        _path = path;
        _totalBytes = totalBytes;
        _kind = kind;
        _sectorSize = sectorSize;
    }

    public static DiskImageBuilder CreateGpt(string path, long totalBytes, int sectorSize = 512) =>
        new(path, totalBytes, PartitionTableKind.Gpt, sectorSize);

    public static DiskImageBuilder CreateMbr(string path, long totalBytes, int sectorSize = 512) =>
        new(path, totalBytes, PartitionTableKind.Mbr, sectorSize);

    public DiskImageBuilder WithDiskGuid(Guid guid)
    {
        _diskGuid = guid;
        return this;
    }

    public DiskImageBuilder WithMbrSignature(uint signature)
    {
        _signature = signature;
        return this;
    }

    public DiskImageBuilder WithBootCode(byte[] bootCode)
    {
        _bootCode = bootCode;
        return this;
    }

    public DiskImageBuilder AddGptPartition(
        Guid typeGuid,
        string? name,
        long startBytes,
        long sizeBytes,
        Guid? uniqueGuid = null,
        ulong attributes = 0)
    {
        _entries.Add(new PartitionWriteEntry
        {
            Number = _entries.Count + 1,
            StartLba = startBytes / _sectorSize,
            SectorCount = sizeBytes / _sectorSize,
            TypeGuid = typeGuid,
            UniqueGuid = uniqueGuid ?? Guid.NewGuid(),
            Name = name,
            Attributes = attributes,
        });

        return this;
    }

    public DiskImageBuilder AddMbrPartition(
        byte mbrType,
        long startBytes,
        long sizeBytes,
        bool active = false,
        bool logical = false)
    {
        _entries.Add(new PartitionWriteEntry
        {
            Number = _entries.Count + 1,
            StartLba = startBytes / _sectorSize,
            SectorCount = sizeBytes / _sectorSize,
            MbrType = mbrType,
            IsActive = active,
            IsLogical = logical,
        });

        return this;
    }

    /// <summary>Writes arbitrary bytes at an absolute offset when <see cref="Build"/> runs.</summary>
    public DiskImageBuilder WithContent(long offsetBytes, byte[] data)
    {
        _content.Add((offsetBytes, data));
        return this;
    }

    /// <summary>Fills a range with a repeating byte pattern - handy to make copies detectable.</summary>
    public DiskImageBuilder WithPattern(long offsetBytes, long lengthBytes, byte seed)
    {
        var data = new byte[Math.Min(lengthBytes, 1L * 1024 * 1024)];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(seed + i);
        }

        for (long written = 0; written < lengthBytes; written += data.Length)
        {
            int chunk = (int)Math.Min(data.Length, lengthBytes - written);
            var slice = new byte[chunk];
            Array.Copy(data, slice, chunk);
            _content.Add((offsetBytes + written, slice));
        }

        return this;
    }

    public void Build()
    {
        using (var stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            stream.SetLength(_totalBytes);
        }

        using var device = ImageBlockDevice.OpenWritable(_path, _sectorSize);

        if (_kind == PartitionTableKind.Gpt)
        {
            PartitionTableWriter.ClearPartitionAreas(device, gpt: true);
            PartitionTableWriter.WriteGpt(device, new GptWriteSpec
            {
                DiskGuid = _diskGuid,
                Entries = _entries,
            });
        }
        else
        {
            var bootCode = _bootCode ?? new byte[446];
            PartitionTableWriter.ClearPartitionAreas(device, gpt: false);
            PartitionTableWriter.WriteMbr(device, new MbrWriteSpec
            {
                DiskSignature = _signature,
                BootCode = bootCode,
                Entries = _entries,
            });
        }

        foreach (var (offset, data) in _content)
        {
            device.WriteAt(offset, data);
        }

        device.Flush();
    }
}
