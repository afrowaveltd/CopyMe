using System.Security.Cryptography;
using System.Text;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Model;

namespace Afrowave.DiskMigrator.Core.Safety;

/// <summary>
/// A value that identifies a disk and its partition table at one point in time.
/// It is captured when the plan is built and re-checked immediately before the first
/// destructive write, so that a disk swapped in the meantime can never be overwritten.
/// </summary>
public sealed record DiskFingerprint(
    int DiskNumber,
    string DevicePath,
    string SerialNumber,
    long SizeBytes,
    int LogicalSectorSize,
    string TableDigest,
    string Digest);

public static class DiskFingerprintFactory
{
    private const int SampleBytes = 64 * 1024;

    public static DiskFingerprint Capture(PhysicalDisk disk, IBlockReader reader)
    {
        int headLength = (int)Math.Min(SampleBytes, reader.Length);
        var head = new byte[headLength];
        reader.ReadAt(0, head);

        int tailLength = (int)Math.Min(SampleBytes, reader.Length);
        var tail = new byte[tailLength];
        reader.ReadAt(reader.Length - tailLength, tail);

        using var tableHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        tableHash.AppendData(head);
        tableHash.AppendData(tail);
        string tableDigest = Convert.ToHexString(tableHash.GetHashAndReset());

        string payload = string.Join(
            '|',
            disk.Identity.Number.ToString(),
            disk.Identity.DevicePath.ToUpperInvariant(),
            disk.Identity.SerialNumber.Trim().ToUpperInvariant(),
            disk.Identity.SizeBytes.ToString(),
            disk.Identity.LogicalSectorSize.ToString(),
            tableDigest);

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        return new DiskFingerprint(
            disk.Identity.Number,
            disk.Identity.DevicePath,
            disk.Identity.SerialNumber,
            disk.Identity.SizeBytes,
            disk.Identity.LogicalSectorSize,
            tableDigest,
            digest);
    }

    /// <summary>True when the disk behind the fingerprint is unchanged.</summary>
    public static bool Matches(DiskFingerprint expected, DiskFingerprint actual) =>
        string.Equals(expected.Digest, actual.Digest, StringComparison.Ordinal);

    /// <summary>Human readable description of what changed, for the refusal message.</summary>
    public static string DescribeDifference(DiskFingerprint expected, DiskFingerprint actual)
    {
        var differences = new List<string>();

        if (expected.DiskNumber != actual.DiskNumber)
        {
            differences.Add($"číslo disku {expected.DiskNumber} -> {actual.DiskNumber}");
        }

        if (!string.Equals(expected.DevicePath, actual.DevicePath, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add("změněno zařízení");
        }

        if (!string.Equals(expected.SerialNumber.Trim(), actual.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            differences.Add("změněno sériové číslo");
        }

        if (expected.SizeBytes != actual.SizeBytes)
        {
            differences.Add($"velikost {expected.SizeBytes} -> {actual.SizeBytes} B");
        }

        if (expected.LogicalSectorSize != actual.LogicalSectorSize)
        {
            differences.Add("změněna velikost sektoru");
        }

        if (!string.Equals(expected.TableDigest, actual.TableDigest, StringComparison.Ordinal))
        {
            differences.Add("změněna tabulka oddílů");
        }

        return differences.Count == 0 ? "beze změny" : string.Join(", ", differences);
    }
}
