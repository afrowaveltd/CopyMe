using System.Text;
using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Platform.Image;

/// <summary>
/// Writes a structurally valid, minimal NTFS volume into an image: boot sector, record table,
/// $Volume, $Bitmap and an allocation map with known contents. Used by the automated tests and
/// by the sample disk generator, so that the whole pipeline can be exercised without a real disk.
/// </summary>
public static class SyntheticNtfs
{
    public const int ClusterSize = 4096;
    public const int SectorSize = 512;
    public const int RecordSize = 1024;
    public const int SectorsPerCluster = ClusterSize / SectorSize;

    public const long MftLcn = 8;
    public const int MftRecordCount = 16;

    public const byte UsedClusterPattern = 0x5A;
    public const byte FreeClusterPattern = 0xA5;

    /// <summary>Clusters occupied by the record table.</summary>
    public static int MftClusters => (MftRecordCount * RecordSize + ClusterSize - 1) / ClusterSize;

    /// <summary>First cluster used for the allocation bitmap.</summary>
    public static long BitmapLcn => MftLcn + MftClusters;

    public static int BitmapClustersFor(long totalClusters) =>
        (int)(((totalClusters + 7) / 8 + ClusterSize - 1) / ClusterSize);

    /// <summary>Clusters occupied by metadata: boot sector, MFT and the allocation map.</summary>
    public static IEnumerable<long> MetadataClusters(long totalClusters)
    {
        yield return 0;
        for (long cluster = MftLcn; cluster < MftLcn + MftClusters; cluster++) yield return cluster;
        for (long cluster = BitmapLcn; cluster < BitmapLcn + BitmapClustersFor(totalClusters); cluster++) yield return cluster;
    }

    /// <summary>
    /// Writes the volume into an image. Only metadata and a marker inside each used cluster are
    /// written, so a multi gigabyte demo volume stays a small sparse file.
    /// </summary>
    /// <summary>Upper bound on marker writes; the rest of the data clusters stay sparse.</summary>
    public const int MarkerLimit = 4096;

    public static void WriteTo(
        IBlockDevice device,
        long partitionOffset,
        long totalClusters,
        IEnumerable<long>? dataClusters = null,
        bool dirty = false,
        string label = "TESTVOL")
    {
        var used = new HashSet<long>(MetadataClusters(totalClusters));
        foreach (long cluster in dataClusters ?? [])
        {
            if (cluster >= 0 && cluster < totalClusters) used.Add(cluster);
        }

        long bitmapBytes = (totalClusters + 7) / 8;
        int bitmapClusters = BitmapClustersFor(totalClusters);

        // ---------------------------------------------------------- boot sector
        var boot = new byte[SectorSize];
        boot[0] = 0xEB;
        boot[1] = 0x52;
        boot[2] = 0x90;
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(boot, 3);
        BitConverter.TryWriteBytes(boot.AsSpan(0x0B, 2), (ushort)SectorSize);
        boot[0x0D] = (byte)SectorsPerCluster;
        boot[0x15] = 0xF8;
        BitConverter.TryWriteBytes(boot.AsSpan(0x18, 2), (ushort)63);
        BitConverter.TryWriteBytes(boot.AsSpan(0x1A, 2), (ushort)255);
        BitConverter.TryWriteBytes(boot.AsSpan(0x28, 8), (ulong)MftLcn);
        BitConverter.TryWriteBytes(boot.AsSpan(0x30, 8), (ulong)(MftLcn + MftClusters));
        boot[0x38] = unchecked((byte)(sbyte)-10); // 1024 byte records
        boot[0x39] = 0x01;
        BitConverter.TryWriteBytes(boot.AsSpan(0x40, 8), 0x1234_5678_9ABC_DEF0UL);
        boot[510] = 0x55;
        boot[511] = 0xAA;
        device.WriteAt(partitionOffset, boot);

        // ---------------------------------------------------------- allocation bitmap
        var bitmap = new byte[bitmapBytes];
        foreach (long cluster in used)
        {
            int index = (int)(cluster >> 3);
            if (index < bitmap.Length)
            {
                bitmap[index] |= (byte)(1 << (int)(cluster & 7));
            }
        }

        device.WriteAt(partitionOffset + (BitmapLcn * ClusterSize), bitmap);

        // ---------------------------------------------------------- record table
        long mftOffset = partitionOffset + (MftLcn * ClusterSize);

        var mftRecord = BuildRecord(0,
        [
            StandardInformation(),
            NonResidentAttribute(AttrData, 0, MftClusters - 1,
                MftClusters * (long)ClusterSize, MftClusters * (long)ClusterSize,
                EncodeRuns([(MftClusters, MftLcn)]), 2),
        ]);

        var volumeRecord = BuildRecord(3,
        [
            StandardInformation(),
            ResidentAttribute(AttrVolumeName, Encoding.Unicode.GetBytes(label + "\0"), 2),
            ResidentAttribute(AttrVolumeInformation, VolumeInformation(dirty), 3),
        ]);

        var bitmapRecord = BuildRecord(6,
        [
            StandardInformation(),
            NonResidentAttribute(AttrData, 0, bitmapClusters - 1, bitmapClusters * (long)ClusterSize,
                bitmapBytes, EncodeRuns([((long)bitmapClusters, BitmapLcn)]), 2),
        ]);

        device.WriteAt(mftOffset + (0L * RecordSize), mftRecord);
        device.WriteAt(mftOffset + (3L * RecordSize), volumeRecord);
        device.WriteAt(mftOffset + (6L * RecordSize), bitmapRecord);

        var emptyRecord = BuildRecord(1, []);
        for (int index = 1; index < MftRecordCount; index++)
        {
            if (index is 3 or 6) continue;
            device.WriteAt(mftOffset + ((long)index * RecordSize), emptyRecord);
        }

        // ---------------------------------------------------------- payload markers
        // Metadata clusters are complete structures already; writing a marker into them would
        // destroy the boot sector, the record table and the allocation map.
        var metadata = new HashSet<long>(MetadataClusters(totalClusters));
        var marker = new byte[64];
        int markersWritten = 0;
        foreach (long cluster in used)
        {
            if (metadata.Contains(cluster)) continue;
            if (markersWritten >= MarkerLimit) break;
            markersWritten++;

            for (int i = 0; i < marker.Length; i++)
            {
                marker[i] = (byte)(cluster + i);
            }

            device.WriteAt(partitionOffset + (cluster * ClusterSize), marker);
        }

        device.Flush();
    }

    /// <summary>
    /// Builds the whole volume in memory with distinguishable used/free clusters. Suitable for
    /// small test volumes only.
    /// </summary>
    public static byte[] Build(int totalClusters, IEnumerable<long>? dataClusters = null, bool dirty = false, string label = "TESTVOL")
    {
        long volumeSize = (long)totalClusters * ClusterSize;
        var image = new byte[volumeSize];
        var data = (dataClusters ?? []).ToList();

        var used = new HashSet<long>(MetadataClusters(totalClusters));
        foreach (long cluster in data) used.Add(cluster);

        for (long cluster = 0; cluster < totalClusters; cluster++)
        {
            long offset = cluster * ClusterSize;
            image.AsSpan((int)offset, ClusterSize).Fill(used.Contains(cluster) ? UsedClusterPattern : FreeClusterPattern);
        }

        // Distinguish each data cluster so that a byte-for-byte comparison is meaningful.
        foreach (long cluster in data)
        {
            long offset = cluster * ClusterSize;
            for (int i = 0; i < 64; i++)
            {
                image[(int)offset + i] = (byte)(cluster & 0xFF);
            }
        }

        using (var device = new ArrayBlockDevice(image))
        {
            WriteTo(device, 0, totalClusters, data, dirty, label);
        }

        return image;
    }

    // ---------------------------------------------------------------- NTFS structures

    public const uint AttrStandardInformation = 0x10;
    public const uint AttrVolumeName = 0x60;
    public const uint AttrVolumeInformation = 0xA0;
    public const uint AttrData = 0x80;

    private static byte[] VolumeInformation(bool dirty)
    {
        var value = new byte[12];
        value[8] = 3;
        value[9] = 1;
        BitConverter.TryWriteBytes(value.AsSpan(10, 2), (ushort)(dirty ? 0x0001 : 0x0000));
        return value;
    }

    private static byte[] StandardInformation()
    {
        var value = new byte[48];
        BitConverter.TryWriteBytes(value.AsSpan(0, 8), 0x01D0000000000000L);
        return ResidentAttribute(AttrStandardInformation, value, 0);
    }

    public static byte[] ResidentAttribute(uint type, byte[] value, byte attributeId)
    {
        const int headerLength = 24;
        int totalLength = Align8(headerLength + value.Length);
        var buffer = new byte[totalLength];

        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), type);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), (uint)totalLength);
        buffer[8] = 0;
        BitConverter.TryWriteBytes(buffer.AsSpan(0x0E, 2), (ushort)attributeId);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x10, 4), (uint)value.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x14, 2), (ushort)headerLength);

        value.CopyTo(buffer, headerLength);
        return buffer;
    }

    public static byte[] NonResidentAttribute(
        uint type,
        long lowestVcn,
        long highestVcn,
        long allocatedSize,
        long dataSize,
        byte[] runlist,
        byte attributeId)
    {
        const int headerLength = 64;
        int totalLength = Align8(headerLength + runlist.Length);
        var buffer = new byte[totalLength];

        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), type);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), (uint)totalLength);
        buffer[8] = 1;
        BitConverter.TryWriteBytes(buffer.AsSpan(0x0E, 2), (ushort)attributeId);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x10, 8), (ulong)lowestVcn);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x18, 8), (ulong)highestVcn);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x20, 2), (ushort)headerLength);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x28, 8), (ulong)allocatedSize);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x30, 8), (ulong)dataSize);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x38, 8), (ulong)dataSize);

        runlist.CopyTo(buffer, headerLength);
        return buffer;
    }

    public static byte[] BuildRecord(long recordNumber, IEnumerable<byte[]> attributes)
    {
        var buffer = new byte[RecordSize];
        Encoding.ASCII.GetBytes("FILE").CopyTo(buffer, 0);

        const int usaOffset = 0x30;
        int usaCount = RecordSize / SectorSize + 1;

        BitConverter.TryWriteBytes(buffer.AsSpan(0x04, 2), (ushort)usaOffset);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x06, 2), (ushort)usaCount);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x10, 2), (ushort)1);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x12, 2), (ushort)1);

        const int attributeOffset = 0x38;
        BitConverter.TryWriteBytes(buffer.AsSpan(0x14, 2), (ushort)attributeOffset);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x16, 2), (ushort)1);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x18, 4), (uint)RecordSize);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x1C, 4), (uint)RecordSize);
        BitConverter.TryWriteBytes(buffer.AsSpan(0x2C, 4), (uint)recordNumber);

        int position = attributeOffset;
        foreach (var attribute in attributes)
        {
            if (position + attribute.Length + 8 > RecordSize) break;
            attribute.CopyTo(buffer, position);
            position += attribute.Length;
        }

        BitConverter.TryWriteBytes(buffer.AsSpan(position, 4), uint.MaxValue);
        BitConverter.TryWriteBytes(buffer.AsSpan(position + 4, 4), 0u);

        ApplyFixups(buffer, usaOffset, usaCount);
        return buffer;
    }

    private static void ApplyFixups(byte[] record, int usaOffset, int usaCount)
    {
        const ushort sequence = 0x0001;
        BitConverter.TryWriteBytes(record.AsSpan(usaOffset, 2), sequence);

        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = (i * SectorSize) - 2;
            if (sectorEnd + 2 > record.Length) break;

            ushort saved = BitConverter.ToUInt16(record, sectorEnd);
            BitConverter.TryWriteBytes(record.AsSpan(usaOffset + (i * 2), 2), saved);
            BitConverter.TryWriteBytes(record.AsSpan(sectorEnd, 2), sequence);
        }
    }

    public static byte[] EncodeRuns(IEnumerable<(long Length, long Lcn)> runs)
    {
        var output = new List<byte>();
        long previousLcn = 0;

        foreach (var (length, lcn) in runs)
        {
            long delta = lcn - previousLcn;
            previousLcn = lcn;

            int lengthBytes = BytesNeeded((ulong)length);
            int offsetBytes = SignedBytesNeeded(delta);

            output.Add((byte)((offsetBytes << 4) | lengthBytes));

            for (int i = 0; i < lengthBytes; i++)
            {
                output.Add((byte)((ulong)length >> (8 * i)));
            }

            for (int i = 0; i < offsetBytes; i++)
            {
                output.Add((byte)((ulong)delta >> (8 * i)));
            }
        }

        output.Add(0);
        return [.. output];
    }

    private static int BytesNeeded(ulong value)
    {
        int bytes = 1;
        while (bytes < 8 && (value >> (8 * bytes)) != 0) bytes++;
        return bytes;
    }

    private static int SignedBytesNeeded(long value)
    {
        for (int bytes = 1; bytes < 8; bytes++)
        {
            long min = -(1L << ((8 * bytes) - 1));
            long max = (1L << ((8 * bytes) - 1)) - 1;
            if (value >= min && value <= max) return bytes;
        }

        return 8;
    }

    private static int Align8(int value) => (value + 7) / 8 * 8;

    /// <summary>Trivial in-memory block device used to build small test volumes.</summary>
    private sealed class ArrayBlockDevice : IBlockDevice
    {
        private readonly byte[] _data;

        public ArrayBlockDevice(byte[] data) => _data = data;

        public string Name => "array";

        public long Length => _data.Length;

        public int SectorSize => SyntheticNtfs.SectorSize;

        public void ReadAt(long offset, Span<byte> buffer) => _data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);

        public void WriteAt(long offset, ReadOnlySpan<byte> buffer) => buffer.CopyTo(_data.AsSpan((int)offset));

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
