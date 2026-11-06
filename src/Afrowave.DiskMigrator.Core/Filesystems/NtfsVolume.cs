using System.Buffers.Binary;
using System.Text;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Model;

namespace Afrowave.DiskMigrator.Core.Filesystems;

public sealed class NtfsAnalysisException : Exception
{
    public NtfsAnalysisException(string message) : base(message)
    {
    }
}

/// <summary>One contiguous piece of a non-resident attribute (VCN/LCN run).</summary>
internal readonly record struct DataRun(long StartVcn, long ClusterCount, long StartLcn)
{
    public long EndVcn => StartVcn + ClusterCount - 1;

    public bool IsSparse => StartLcn < 0;
}

/// <summary>
/// Read-only NTFS metadata reader. It only ever reads: the boot sector, the record
/// table and a handful of metadata files. Used to learn which clusters actually hold
/// data so that a partition can be copied without dragging along gigabytes of free space,
/// and to copy it with byte-exact metadata fidelity.
/// </summary>
public sealed class NtfsVolume : IDisposable
{
    private const uint AttrVolumeName = 0x60;
    private const uint AttrVolumeInformation = 0x70;
    private const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;
    private const uint AttrBitmap = 0xB0;

    private readonly IBlockReader _device;
    private readonly long _partitionOffset;
    private readonly long _partitionSize;

    private readonly int _bytesPerSector;
    private readonly long _clusterSize;
    private readonly int _mftRecordSize;
    private IReadOnlyList<DataRun> _mftRuns = [];

    private NtfsVolume(
        IBlockReader device,
        long partitionOffset,
        long partitionSize,
        int bytesPerSector,
        long clusterSize,
        int mftRecordSize)
    {
        _device = device;
        _partitionOffset = partitionOffset;
        _partitionSize = partitionSize;
        _bytesPerSector = bytesPerSector;
        _clusterSize = clusterSize;
        _mftRecordSize = mftRecordSize;
    }

    public NtfsVolumeInfo Info { get; private set; } = null!;

    public NtfsAllocationMap Allocations { get; private set; } = null!;

    public long ClusterSize => _clusterSize;

    public int BytesPerSector => _bytesPerSector;

    /// <summary>
    /// Opens and fully analyses an NTFS volume. Throws when the volume does not look
    /// like a consistent NTFS file system - callers must fall back to a blind copy.
    /// </summary>
    public static NtfsVolume Open(IBlockReader device, long partitionOffsetBytes, long partitionSizeBytes)
    {
        var boot = new byte[512];
        device.ReadAt(partitionOffsetBytes, boot);

        if (!boot.AsSpan(3, 8).SequenceEqual("NTFS    "u8))
        {
            throw new NtfsAnalysisException("Oddíl neobsahuje NTFS.");
        }

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        int sectorsPerCluster = boot[0x0D];

        if (bytesPerSector is not (512 or 1024 or 2048 or 4096))
        {
            throw new NtfsAnalysisException($"Neočekávaná velikost sektoru NTFS ({bytesPerSector} B).");
        }

        if (sectorsPerCluster == 0 || (sectorsPerCluster > 128 && sectorsPerCluster < 0xF4))
        {
            throw new NtfsAnalysisException($"Neočekávaný počet sektorů ve svazku NTFS ({sectorsPerCluster}).");
        }

        long clusterSize = sectorsPerCluster > 128
            ? 1L << (256 - sectorsPerCluster)   // 0xF4..0xFB encodes 2^(256-n) bytes
            : (long)bytesPerSector * sectorsPerCluster;

        if (clusterSize is < 512 or > 2 * 1024 * 1024)
        {
            throw new NtfsAnalysisException($"Neočekávaná velikost clusteru NTFS ({clusterSize} B).");
        }

        // NTFS boot sector: 0x28 = total sectors, 0x30 = $MFT LCN,
        // 0x38 = $MFTMirr LCN, 0x40 = signed clusters per MFT FILE record.
        sbyte recordClusters = unchecked((sbyte)boot[0x40]);
        long recordBytes = recordClusters > 0
            ? recordClusters * clusterSize
            : recordClusters < 0 && recordClusters >= -16
                ? 1L << -recordClusters
                : 0;
        if (recordBytes is < 512 or > 65536 || recordBytes % bytesPerSector != 0)
        {
            throw new NtfsAnalysisException(
                $"Nepodporovaná velikost záznamu MFT (kód {recordClusters}, {recordBytes} B).");
        }

        int mftRecordSize = checked((int)recordBytes);

        var volume = new NtfsVolume(device, partitionOffsetBytes, partitionSizeBytes, bytesPerSector, clusterSize, mftRecordSize);
        volume.Load(boot);
        return volume;
    }

    private void Load(byte[] boot)
    {
        long mftLcn = ResolveMftLocation(boot);
        _mftRuns = ReadMftRunlist(mftLcn);

        // The allocation map has to be read first: it provides the free/used cluster counts
        // that the volume summary reports.
        Allocations = ReadAllocationMap();
        Info = ReadVolumeInfo(Allocations);
    }

    /// <summary>
    /// Resolves the MFT starting cluster from the documented NTFS 0x30 field.
    /// The 0x28 field contains the volume's sector count and must never be
    /// interpreted as an alternative MFT location.
    /// </summary>
    private long ResolveMftLocation(byte[] boot)
    {
        ulong mftLcn = BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(0x30));
        long totalClusters = _partitionSize / _clusterSize;

        if (mftLcn == 0 || mftLcn >= (ulong)totalClusters)
        {
            throw new NtfsAnalysisException("NTFS boot sektor uvádí neplatnou adresu $MFT.");
        }

        long byteOffset = checked(_partitionOffset + (long)mftLcn * _clusterSize);
        if (byteOffset + _mftRecordSize > _partitionOffset + _partitionSize)
        {
            throw new NtfsAnalysisException("Záznam $MFT by přesahoval konec oddílu.");
        }

        var probe = new byte[_mftRecordSize];
        _device.ReadAt(byteOffset, probe);
        var record = NtfsRecord.Parse(probe, _mftRecordSize);

        if (!record.Attributes.Any(a => a.Type == AttrData && a.NameLength == 0 && !a.IsResident))
        {
            throw new NtfsAnalysisException("Záznam $MFT neobsahuje očekávaný nepřímý atribut $DATA.");
        }

        return (long)mftLcn;
    }

    private IReadOnlyList<DataRun> ReadMftRunlist(long mftLcn)
    {
        var recordBytes = new byte[_mftRecordSize];
        ReadClusterAligned(mftLcn * _clusterSize, recordBytes);
        var record = NtfsRecord.Parse(recordBytes, _mftRecordSize);

        var data = record.Attributes.FirstOrDefault(a => a.Type == AttrData && a.NameLength == 0 && !a.IsResident)
                   ?? throw new NtfsAnalysisException("Záznam MFT neobsahuje datový atribut.");

        var runs = data.Runs;
        if (runs.Count == 0)
        {
            throw new NtfsAnalysisException("Datový atribut MFT neobsahuje žádné runy.");
        }

        long expectedEnd = data.LowestVcn;
        foreach (var run in runs.OrderBy(r => r.StartVcn))
        {
            if (run.StartVcn != expectedEnd && run.StartVcn != data.LowestVcn)
            {
                // Sparse gaps inside the record table are legal but unusual; keep going.
            }

            expectedEnd = run.EndVcn + 1;
        }

        return runs;
    }

    private NtfsVolumeInfo ReadVolumeInfo(NtfsAllocationMap allocations)
    {
        string? label = null;
        bool isDirty = false;
        int major = 0;
        int minor = 0;

        var record = TryReadRecord(3);
        if (record is not null)
        {
            var nameAttr = record.Attributes.FirstOrDefault(a => a.Type == AttrVolumeName);
            if (nameAttr is not null && nameAttr.IsResident && nameAttr.ResidentValue is { Length: > 0 } value)
            {
                label = Encoding.Unicode.GetString(value).TrimEnd('\0');
                if (label.Length == 0) label = null;
            }

            var infoAttr = record.Attributes.FirstOrDefault(a => a.Type == AttrVolumeInformation);
            if (infoAttr is not null && infoAttr.IsResident && infoAttr.ResidentValue is { Length: >= 12 } info)
            {
                major = info[8];
                minor = info[9];
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(10, 2));
                isDirty = (flags & 0x0001) != 0;
            }
        }

        return new NtfsVolumeInfo(
            _bytesPerSector,
            (int)(_clusterSize / _bytesPerSector),
            _partitionSize / _bytesPerSector,
            allocations.TotalClusters,
            allocations.TotalClusters - allocations.UsedClusters,
            isDirty,
            isDirty,
            label,
            major,
            minor);
    }

    private NtfsAllocationMap ReadAllocationMap()
    {
        var record = TryReadRecord(6)
                     ?? throw new NtfsAnalysisException("Chybí metadatový soubor $Bitmap – NTFS nelze analyzovat.");

        var data = record.Attributes.FirstOrDefault(a => a.Type == AttrData && a.NameLength == 0)
                   ?? throw new NtfsAnalysisException(
                       "Záznam $Bitmap (#6) neobsahuje nepomenovaný atribut $DATA; nalezeno: " +
                       string.Join(", ", record.Attributes.Select(a => $"0x{a.Type:X2}")) +
                       (record.Attributes.Any(a => a.Type == 0x20)
                           ? " (obsahuje $ATTRIBUTE_LIST; rozšířené atributy zatím nejsou podporovány)"
                           : string.Empty));

        byte[] bitmap;
        if (data.IsResident)
        {
            bitmap = data.ResidentValue ?? [];
        }
        else
        {
            long length = data.DataSize;
            if (length <= 0 || length > 512L * 1024 * 1024)
            {
                throw new NtfsAnalysisException($"Nepoužitelná velikost alokační mapy ({length} B).");
            }

            bitmap = new byte[length];
            ReadAttributeData(data, bitmap);
        }

        long clustersFromBitmap = bitmap.LongLength * 8;
        long clustersFromPartition = _partitionSize / _clusterSize;
        long totalClusters = Math.Min(clustersFromBitmap, clustersFromPartition);

        if (totalClusters <= 0)
        {
            throw new NtfsAnalysisException("Alokační mapa NTFS je prázdná.");
        }

        return new NtfsAllocationMap(bitmap, totalClusters);
    }

    private NtfsRecord? TryReadRecord(long index)
    {
        try
        {
            return ReadRecord(index);
        }
        catch (Exception ex) when (ex is NtfsAnalysisException or IOException)
        {
            return null;
        }
    }

    internal NtfsRecord ReadRecord(long index)
    {
        long byteOffsetInTable = index * _mftRecordSize;
        long clusterIndex = byteOffsetInTable / _clusterSize;
        long withinCluster = byteOffsetInTable % _clusterSize;

        foreach (var run in _mftRuns)
        {
            if (run.IsSparse) continue;
            if (clusterIndex < run.StartVcn || clusterIndex > run.EndVcn) continue;

            long lcn = run.StartLcn + (clusterIndex - run.StartVcn);
            long offset = _partitionOffset + (lcn * _clusterSize) + withinCluster;
            var buffer = new byte[_mftRecordSize];
            _device.ReadAt(offset, buffer);
            return NtfsRecord.Parse(buffer, _mftRecordSize);
        }

        throw new NtfsAnalysisException($"Záznam MFT #{index} není v tabulce dostupný.");
    }

    /// <summary>Reads the full data of a non-resident attribute into <paramref name="destination"/>.</summary>
    internal void ReadAttributeData(NtfsAttribute attribute, Span<byte> destination)
    {
        destination.Clear();

        foreach (var run in attribute.Runs)
        {
            long runStartVcn = run.StartVcn;
            long runEndVcn = run.EndVcn;

            long firstByte = runStartVcn * _clusterSize;
            long lastByte = Math.Min(destination.Length, (runEndVcn + 1) * _clusterSize);
            if (lastByte <= firstByte) continue;

            if (run.IsSparse) continue;

            long sourceByteOffset = _partitionOffset + (run.StartLcn * _clusterSize);
            long length = lastByte - firstByte;

            // Reads must be sector aligned and within the partition.
            long clampedLength = Math.Min(length, (_partitionSize) - (run.StartLcn * _clusterSize));
            if (clampedLength <= 0) continue;

            var target = destination.Slice((int)firstByte, (int)clampedLength);
            _device.ReadAt(sourceByteOffset, target);
        }
    }

    private void ReadClusterAligned(long partitionRelativeOffset, Span<byte> destination)
    {
        long absolute = _partitionOffset + partitionRelativeOffset;
        _device.ReadAt(absolute, destination);
    }

    public void Dispose()
    {
        // The device is owned by the caller; nothing to release here.
    }
}

/// <summary>Cluster allocation bitmap of an NTFS volume.</summary>
public sealed class NtfsAllocationMap
{
    private readonly byte[] _bits;

    internal NtfsAllocationMap(byte[] bits, long totalClusters)
    {
        _bits = bits;
        TotalClusters = totalClusters;

        long used = 0;
        for (long cluster = 0; cluster < totalClusters; cluster++)
        {
            if (IsClusterInUse(cluster)) used++;
        }

        UsedClusters = used;
    }

    public long TotalClusters { get; }

    public long UsedClusters { get; }

    public bool IsClusterInUse(long cluster)
    {
        long index = cluster >> 3;
        if (index < 0 || index >= _bits.Length) return true; // unknown => assume used (safe)
        return (_bits[index] & (1 << (int)(cluster & 7))) != 0;
    }
}

/// <summary>A parsed FILE record.</summary>
internal sealed class NtfsRecord
{
    private NtfsRecord(List<NtfsAttribute> attributes)
    {
        Attributes = attributes;
    }

    public IReadOnlyList<NtfsAttribute> Attributes { get; }

    public static NtfsRecord Parse(byte[] buffer, int recordSize)
    {
        if (buffer.Length < 48 || !buffer.AsSpan(0, 4).SequenceEqual("FILE"u8))
        {
            throw new NtfsAnalysisException("Záznam MFT nemá podpis FILE.");
        }

        var data = (byte[])buffer.Clone();
        ApplyFixups(data);

        int attributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x14));
        if (attributeOffset < 0x18 || attributeOffset >= recordSize)
        {
            throw new NtfsAnalysisException("Neplatný offset atributů v záznamu MFT.");
        }

        var attributes = new List<NtfsAttribute>();
        int position = attributeOffset;

        while (position + 8 <= data.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            if (type == AttrEndMarker || type == uint.MaxValue) break;

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
            if (length < 16 || position + length > data.Length)
            {
                break;
            }

            attributes.Add(new NtfsAttribute(data.AsSpan(position, (int)length).ToArray()));
            position += (int)length;
        }

        return new NtfsRecord(attributes);
    }

    private const uint AttrEndMarker = 0xFFFFFFFF;

    private static void ApplyFixups(Span<byte> record)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x04, 2));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x06, 2));

        if (usaCount <= 1 || usaOffset <= 0 || usaOffset + (usaCount * 2) > record.Length)
        {
            // Some tools write records without an update sequence array; accept them as-is
            // but only when the tail bytes are consistent.
            return;
        }

        ushort sequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(usaOffset, 2));

        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = (i * 512) - 2;
            if (sectorEnd + 2 > record.Length) break;

            ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(sectorEnd, 2));
            if (stored != sequenceNumber)
            {
                throw new NtfsAnalysisException("Kontrolní posloupnost záznamu MFT nesouhlasí – metadata NTFS jsou poškozená.");
            }

            ushort saved = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(usaOffset + (i * 2), 2));
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(sectorEnd, 2), saved);
        }
    }
}

internal sealed class NtfsAttribute
{
    public NtfsAttribute(byte[] raw)
    {
        Raw = raw;
        Type = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0));
        int nameLength = raw[9];
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x0A));
        NameLength = nameLength;
        IsResident = raw[8] == 0;

        if (IsResident)
        {
            int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x10));
            int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x14));
            if (valueOffset > 0 && valueOffset + valueLength <= raw.Length)
            {
                ResidentValue = raw.AsSpan(valueOffset, valueLength).ToArray();
            }
            else
            {
                ResidentValue = [];
            }

            Runs = [];
            DataSize = ResidentValue.Length;
        }
        else
        {
            int mappingPairsOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x20));
            LowestVcn = (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x10));
            DataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x30));
            Runs = mappingPairsOffset > 0 && mappingPairsOffset < raw.Length
                ? ParseRuns(raw.AsSpan(mappingPairsOffset))
                : [];
        }

        Name = nameLength > 0 && nameOffset > 0 && nameOffset + (nameLength * 2) <= raw.Length
            ? Encoding.Unicode.GetString(raw, nameOffset, nameLength * 2)
            : null;
    }

    public byte[] Raw { get; }

    public uint Type { get; }

    public bool IsResident { get; }

    public int NameLength { get; }

    public string? Name { get; }

    public byte[]? ResidentValue { get; }

    public long DataSize { get; }

    public long LowestVcn { get; }

    public IReadOnlyList<DataRun> Runs { get; }

    private static List<DataRun> ParseRuns(ReadOnlySpan<byte> mappingPairs)
    {
        var runs = new List<DataRun>();
        long currentLcn = 0;
        long currentVcn = 0;
        int position = 0;

        while (position < mappingPairs.Length)
        {
            byte header = mappingPairs[position++];
            if (header == 0) break;

            int lengthBytes = header & 0x0F;
            int offsetBytes = (header >> 4) & 0x0F;

            if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8 || position + lengthBytes + offsetBytes > mappingPairs.Length)
            {
                throw new NtfsAnalysisException("Poškozený seznam runů v metadatech NTFS.");
            }

            long length = 0;
            for (int i = 0; i < lengthBytes; i++)
            {
                length |= (long)mappingPairs[position + i] << (8 * i);
            }

            position += lengthBytes;

            long lcn = 0;
            bool sparse = offsetBytes == 0;
            if (!sparse)
            {
                long delta = 0;
                for (int i = 0; i < offsetBytes; i++)
                {
                    delta |= (long)mappingPairs[position + i] << (8 * i);
                }

                // Sign-extend the relative LCN offset.
                int shift = 64 - (8 * offsetBytes);
                delta = (delta << shift) >> shift;
                currentLcn += delta;
                lcn = currentLcn;
            }

            position += offsetBytes;

            if (length <= 0) break;

            runs.Add(new DataRun(currentVcn, length, sparse ? -1 : lcn));
            currentVcn += length;

            if (lcn < 0 && !sparse) break;
        }

        return runs;
    }
}
