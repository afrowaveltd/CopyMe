using Afrowave.DiskMigrator.Core.Abstractions;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>An in-memory block device so partition table tests need no files at all.</summary>
public sealed class MemoryBlockDevice : IBlockDevice
{
    private readonly byte[] _data;

    public MemoryBlockDevice(long length, int sectorSize = 512)
    {
        if (length % sectorSize != 0)
        {
            throw new ArgumentException("Velikost musí být násobkem sektoru.", nameof(length));
        }

        _data = new byte[length];
        SectorSize = sectorSize;
    }

    public string Name => "memory";

    public long Length => _data.Length;

    public int SectorSize { get; }

    public byte[] Buffer => _data;

    public void ReadAt(long offset, Span<byte> buffer) => _data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);

    public void WriteAt(long offset, ReadOnlySpan<byte> buffer) => buffer.CopyTo(_data.AsSpan((int)offset));

    public void Flush()
    {
    }

    public void Dispose()
    {
    }
}
