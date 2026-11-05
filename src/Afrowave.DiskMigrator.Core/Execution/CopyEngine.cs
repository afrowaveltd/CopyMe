using System.Security.Cryptography;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Core.Filesystems;
using Afrowave.DiskMigrator.Core.Planning;

namespace Afrowave.DiskMigrator.Core.Execution;

/// <summary>
/// Plain and NTFS-aware sector copying. Both write through an <see cref="IBlockDevice"/>
/// and never mount anything, which keeps them independent of the operating system.
/// </summary>
public static class CopyEngine
{
    public const int DefaultChunkBytes = 4 * 1024 * 1024;

    /// <summary>Upper bound for the always-copied head/tail regions of an NTFS partition.</summary>
    public const long NtfsEdgeBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Short free gaps between allocated clusters are copied as well. Copying a few extra
    /// kilobytes is much cheaper than a separate seek per fragment, and it keeps the written
    /// region contiguous for the verification pass.
    /// </summary>
    public const int GapToleranceClusters = 8;

    /// <summary>
    /// Size of the head and tail regions that are copied regardless of the allocation map,
    /// because the NTFS boot sector and its backup live there. Scaled down for small
    /// partitions so that the "intelligent" copy never degenerates into a full copy.
    /// </summary>
    public static long ComputeEdgeBytes(long partitionSizeBytes, long clusterSize)
    {
        long proportional = partitionSizeBytes / 16;
        long edge = Math.Min(NtfsEdgeBytes, Math.Max(clusterSize, proportional));
        return Math.Min(edge, partitionSizeBytes);
    }

    /// <summary>Copies a contiguous byte range from the source to the target.</summary>
    public static long CopyRange(
        IBlockReader source,
        long sourceOffset,
        IBlockDevice target,
        long targetOffset,
        long length,
        int chunkBytes,
        ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (length <= 0) return 0;

        if (sourceOffset < 0 || sourceOffset + length > source.Length)
        {
            throw new IOException(
                $"Čtení by přesáhlo konec zdrojového zařízení (offset {sourceOffset}, délka {length}, velikost {source.Length}).");
        }

        if (targetOffset < 0 || targetOffset + length > target.Length)
        {
            throw new IOException(
                $"Zápis by přesáhl konec cílového zařízení (offset {targetOffset}, délka {length}, velikost {target.Length}).");
        }

        int alignment = Math.Max(target.SectorSize, 512);
        int wanted = Math.Max(alignment, chunkBytes / alignment * alignment);
        // Size the buffer to the transfer itself: a fragmented copy issues many tiny range
        // requests, and allocating the maximum chunk for each of them would dominate the run.
        int chunk = (int)Math.Min(wanted, length / alignment * alignment);
        if (chunk < alignment) chunk = (int)Math.Min(alignment, Math.Max(length, alignment));

        var buffer = new byte[chunk];
        long done = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = 0;

        while (done < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int request = (int)Math.Min(chunk, length - done);
            var slice = buffer.AsSpan(0, request);
            source.ReadAt(sourceOffset + done, slice);
            target.WriteAt(targetOffset + done, slice);
            done += request;

            if (progress is not null && (done - lastReport >= 32L * 1024 * 1024 || done == length))
            {
                lastReport = done;
                double speed = stopwatch.Elapsed.TotalSeconds > 0 ? done / stopwatch.Elapsed.TotalSeconds : 0;
                progress(new CopyProgress(done, length, speed));
            }
        }

        return done;
    }

    /// <summary>Copies a whole partition byte for byte.</summary>
    public static long ClonePartition(
        PlannedPartition partition,
        IBlockReader source,
        IBlockDevice target,
        int chunkBytes,
        ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
        => CopyRange(source, partition.SourceStartByte, target, partition.TargetStartByte,
            partition.SourceSizeBytes, chunkBytes, progress, cancellationToken);

    /// <summary>
    /// NTFS-aware copy: the boot area, the trailing area and every cluster the volume marks
    /// as allocated are copied to the same byte offsets. Unallocated clusters are skipped, so
    /// a partition with 120 GB of data inside a 500 GB container copies only what matters,
    /// while the copied metadata stays byte-identical.
    /// </summary>
    public static long CloneAllocatedClusters(
        PlannedPartition partition,
        NtfsVolume ntfs,
        IBlockReader source,
        IBlockDevice target,
        int chunkBytes,
        bool zeroFreeClusters,
        ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        var map = ntfs.Allocations;
        long clusterSize = ntfs.ClusterSize;
        long sourceSize = partition.SourceSizeBytes;

        long totalClusters = Math.Min(map.TotalClusters, sourceSize / clusterSize);
        long edgeClusters = ComputeEdgeBytes(sourceSize, clusterSize) / clusterSize;

        var ranges = NtfsCloneRanges
            .Enumerate(map, totalClusters, edgeClusters, GapToleranceClusters)
            .ToList();

        long toCopy = 0;
        foreach (var range in ranges)
        {
            toCopy += Math.Min(range.ClusterCount * clusterSize, sourceSize - (range.StartCluster * clusterSize));
        }

        long progressTotal = zeroFreeClusters ? sourceSize : Math.Min(toCopy, sourceSize);
        long done = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        void Report()
        {
            if (progress is null) return;
            double speed = stopwatch.Elapsed.TotalSeconds > 0 ? done / stopwatch.Elapsed.TotalSeconds : 0;
            progress(new CopyProgress(done, progressTotal, speed));
        }

        foreach (var range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long byteStart = range.StartCluster * clusterSize;
            long size = Math.Min(range.ClusterCount * clusterSize, sourceSize - byteStart);
            if (size <= 0) continue;

            done += CopyRange(
                source,
                partition.SourceStartByte + byteStart,
                target,
                partition.TargetStartByte + byteStart,
                size,
                chunkBytes,
                null,
                cancellationToken);

            Report();
        }

        if (zeroFreeClusters)
        {
            long cursor = 0;
            foreach (var range in ranges)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long gapStart = cursor;
                long gapEnd = range.StartCluster * clusterSize;
                if (gapEnd > gapStart)
                {
                    done += ZeroRange(target, partition.TargetStartByte + gapStart, gapEnd - gapStart, chunkBytes);
                }

                cursor = Math.Min(sourceSize, (range.StartCluster + range.ClusterCount) * clusterSize);
            }

            if (cursor < sourceSize)
            {
                done += ZeroRange(target, partition.TargetStartByte + cursor, sourceSize - cursor, chunkBytes);
            }
        }

        Report();
        return done;
    }

    /// <summary>Writes zeroes over a target range, in sector aligned chunks.</summary>
    public static long ZeroRange(IBlockDevice target, long offset, long length, int chunkBytes)
    {
        int alignment = Math.Max(target.SectorSize, 512);
        int chunk = Math.Max(alignment, chunkBytes / alignment * alignment);
        var zeros = new byte[chunk];

        long done = 0;
        while (done < length)
        {
            int request = (int)Math.Min(chunk, length - done);
            target.WriteAt(offset + done, zeros.AsSpan(0, request));
            done += request;
        }

        return done;
    }
}

/// <summary>
/// The single definition of which clusters an allocation aware copy moves. Both the copy
/// engine and the verifier use it, so verification can never test a different set of
/// clusters than the one that was written.
/// </summary>
public static class NtfsCloneRanges
{
    public static bool NeedsCopy(NtfsAllocationMap map, long cluster, long totalClusters, long edgeClusters)
    {
        if (cluster < edgeClusters) return true;
        if (cluster >= totalClusters - edgeClusters) return true;
        return map.IsClusterInUse(cluster);
    }

    /// <summary>
    /// Yields the contiguous cluster ranges that will be copied, in order. Gaps of up to
    /// <paramref name="gapTolerance"/> free clusters are swallowed so that heavily fragmented
    /// data does not turn into tens of thousands of tiny transfers.
    /// </summary>
    public static IEnumerable<(long StartCluster, long ClusterCount)> Enumerate(
        NtfsAllocationMap map,
        long totalClusters,
        long edgeClusters,
        int gapTolerance = 0)
    {
        long cluster = 0;
        while (cluster < totalClusters)
        {
            if (!NeedsCopy(map, cluster, totalClusters, edgeClusters))
            {
                cluster++;
                continue;
            }

            long runEnd = cluster + 1;

            while (runEnd < totalClusters)
            {
                if (NeedsCopy(map, runEnd, totalClusters, edgeClusters))
                {
                    runEnd++;
                    continue;
                }

                if (gapTolerance <= 0) break;

                long probe = runEnd;
                int gap = 0;
                while (probe < totalClusters && gap < gapTolerance &&
                       !NeedsCopy(map, probe, totalClusters, edgeClusters))
                {
                    probe++;
                    gap++;
                }

                if (probe < totalClusters && NeedsCopy(map, probe, totalClusters, edgeClusters))
                {
                    runEnd = probe + 1;
                    continue;
                }

                break;
            }

            yield return (cluster, runEnd - cluster);
            cluster = runEnd;
        }
    }
}

/// <summary>Result of comparing a written region against the source data.</summary>
public sealed record VerificationResult(bool Match, long BytesCompared, string? Difference, string SourceHash, string TargetHash);

/// <summary>Re-reads what was written and proves it is identical to the source.</summary>
public static class VerificationEngine
{
    /// <summary>
    /// Compares a region of the target against the source with SHA-256. Both sides are
    /// hashed in the same order with the same chunking, so a mismatch is always real.
    /// </summary>
    public static VerificationResult CompareRange(
        IBlockReader source,
        long sourceOffset,
        IBlockReader target,
        long targetOffset,
        long length,
        int chunkBytes = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        int alignment = Math.Max(Math.Max(source.SectorSize, target.SectorSize), 512);
        int chunk = Math.Max(alignment, chunkBytes / alignment * alignment);

        var sourceBuffer = new byte[chunk];
        var targetBuffer = new byte[chunk];

        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var targetHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long done = 0;
        while (done < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int request = (int)Math.Min(chunk, length - done);
            var sourceSlice = sourceBuffer.AsSpan(0, request);
            var targetSlice = targetBuffer.AsSpan(0, request);

            source.ReadAt(sourceOffset + done, sourceSlice);
            target.ReadAt(targetOffset + done, targetSlice);

            if (!sourceSlice.SequenceEqual(targetSlice))
            {
                long mismatch = done + FirstDifference(sourceSlice, targetSlice);
                return new VerificationResult(
                    false,
                    done,
                    $"obsah se liší na offsetu {mismatch} B (relativně {mismatch - done} B v bloku)",
                    "–",
                    "–");
            }

            sourceHash.AppendData(sourceSlice);
            targetHash.AppendData(targetSlice);
            done += request;
        }

        return new VerificationResult(
            true,
            done,
            null,
            Convert.ToHexString(sourceHash.GetHashAndReset()),
            Convert.ToHexString(targetHash.GetHashAndReset()));
    }

    private static int FirstDifference(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int limit = Math.Min(left.Length, right.Length);
        for (int i = 0; i < limit; i++)
        {
            if (left[i] != right[i]) return i;
        }

        return limit;
    }
}
