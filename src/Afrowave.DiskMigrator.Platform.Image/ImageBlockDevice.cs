using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Platform.Image;

/// <summary>
/// A block device backed by a plain image file. On Linux and on the test runner this is
/// how "physical disks" are simulated; the production tool only ever sees it through the
/// same <see cref="IBlockReader"/>/<see cref="IBlockDevice"/> interfaces as real hardware.
/// </summary>
public sealed class ImageBlockDevice : IBlockDevice
{
    private readonly FileStream _stream;
    private bool _disposed;

    private ImageBlockDevice(string path, FileStream stream, int sectorSize, bool writable)
    {
        Name = path;
        _stream = stream;
        SectorSize = sectorSize;
        Length = stream.Length;
        IsWritable = writable;
    }

    public string Name { get; }

    public long Length { get; }

    public int SectorSize { get; }

    public bool IsWritable { get; }

    public static ImageBlockDevice OpenReader(string path, int sectorSize = 512)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
        return new ImageBlockDevice(path, stream, sectorSize, false);
    }

    public static ImageBlockDevice OpenWritable(string path, int sectorSize = 512)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.None);
        return new ImageBlockDevice(path, stream, sectorSize, true);
    }

    public void ReadAt(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(_stream.SafeFileHandle, buffer[total..], offset + total);
            if (read <= 0)
            {
                throw new IOException($"Neočekávaný konec souboru při čtení {Name} na offsetu {offset + total}.");
            }

            total += read;
        }
    }

    public void WriteAt(long offset, ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsWritable)
        {
            throw new InvalidOperationException($"Zařízení {Name} bylo otevřeno pouze pro čtení.");
        }

        RandomAccess.Write(_stream.SafeFileHandle, buffer, offset);
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
