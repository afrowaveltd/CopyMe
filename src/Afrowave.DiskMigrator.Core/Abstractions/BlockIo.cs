namespace Afrowave.DiskMigrator.Core.Abstractions;

/// <summary>
/// Byte-addressable, read-only view of a raw block device or of a disk image file.
/// All offsets and lengths are in bytes. Implementations must fill the whole buffer
/// or throw - a short read is always an error for raw disk work.
/// </summary>
public interface IBlockReader : IDisposable
{
    /// <summary>Human readable source name (device path or image file path).</summary>
    string Name { get; }

    /// <summary>Total size in bytes.</summary>
    long Length { get; }

    /// <summary>Logical sector size in bytes (512 or 4096).</summary>
    int SectorSize { get; }

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>.</summary>
    void ReadAt(long offset, Span<byte> buffer);
}

/// <summary>A block device that may also be written. Only ever opened for the migration target.</summary>
public interface IBlockDevice : IBlockReader
{
    void WriteAt(long offset, ReadOnlySpan<byte> buffer);

    /// <summary>Pushes every buffered write to the medium.</summary>
    void Flush();
}

public static class BlockReaderExtensions
{
    /// <summary>Reads a single logical sector.</summary>
    public static byte[] ReadSectors(this IBlockReader reader, long lba, int sectorCount)
    {
        int size = checked((int)((long)sectorCount * reader.SectorSize));
        var buffer = new byte[size];
        reader.ReadAt(lba * reader.SectorSize, buffer);
        return buffer;
    }

    /// <summary>Reads into a caller-provided buffer at a byte offset.</summary>
    public static void ReadExactly(this IBlockReader reader, long offset, Span<byte> buffer)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset + buffer.Length > reader.Length)
        {
            throw new IOException(
                $"Čtení mimo rozsah zařízení: offset {offset}, délka {buffer.Length}, velikost {reader.Length}.");
        }

        reader.ReadAt(offset, buffer);
    }
}

/// <summary>Progress of a single partition copy.</summary>
public readonly record struct CopyProgress(long DoneBytes, long TotalBytes, double BytesPerSecond)
{
    public double Fraction => TotalBytes <= 0 ? 0d : Math.Clamp((double)DoneBytes / TotalBytes, 0d, 1d);

    public TimeSpan? EstimatedRemaining =>
        BytesPerSecond > 1d && TotalBytes > DoneBytes
            ? TimeSpan.FromSeconds((TotalBytes - DoneBytes) / BytesPerSecond)
            : null;
}

public delegate void ProgressCallback(CopyProgress progress);

/// <summary>Receives human-oriented progress events during a migration.</summary>
public interface IMigrationReporter
{
    void Phase(string message);

    void Detail(string message);

    void Notice(string message);

    void Warning(string message);

    void PartitionStarted(Planning.PlannedPartition partition);

    void Progress(Planning.PlannedPartition partition, CopyProgress progress);

    void PartitionCompleted(Planning.PlannedPartition partition, long bytesWritten);
}
