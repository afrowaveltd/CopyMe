namespace Afrowave.DiskMigrator.Core.Partitioning;

/// <summary>
/// Standard CRC-32 as used by the GPT headers and the partition entry array
/// (ISO-HDLC / IEEE 802.3, reflected polynomial 0xEDB88320).
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    public static uint Compute(uint seed, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            seed = Table[(seed ^ b) & 0xFF] ^ (seed >> 8);
        }

        return seed;
    }

    public static uint Begin() => 0xFFFFFFFFu;

    public static uint Finish(uint seed) => seed ^ 0xFFFFFFFFu;
}
