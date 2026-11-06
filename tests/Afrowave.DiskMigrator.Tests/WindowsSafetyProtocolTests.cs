using System.Buffers.Binary;
using Afrowave.DiskMigrator.Core.Abstractions;
using Afrowave.DiskMigrator.Platform.Windows;

namespace Afrowave.DiskMigrator.Tests;

/// <summary>Cross-platform tests for real Windows IOCTL layouts and the safety state machine.</summary>
public static class WindowsSafetyProtocolTests
{
    [Test("READ_ONLY must be 0x2 and OFFLINE must be 0x1")]
    public static void DiskAttributeConstants()
    {
        Assert.Equal(1UL, WindowsDiskProtocol.Offline, "OFFLINE bit");
        Assert.Equal(2UL, WindowsDiskProtocol.ReadOnly, "READ_ONLY bit");
        Assert.Equal(16, WindowsDiskProtocol.GetLength, "GET struct length");
        Assert.Equal(40, WindowsDiskProtocol.SetLength, "SET struct length includes 16 reserved bytes");
    }

    [Test("Setting READ_ONLY never sets the OFFLINE bit, never persists and uses a full 40-byte struct")]
    public static void EncodeSetReadOnly()
    {
        byte[] b = WindowsDiskProtocol.EncodeSetReadOnly(true);
        Assert.Equal(40, b.Length, "SET wire length");
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0, 4)), "Version = sizeof(GET)");
        Assert.Equal((byte)0, b[4], "Persist must default to false");
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8, 8)), "only read-only bit set");
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16, 8)), "only read-only bit masked");
        Assert.True(b.AsSpan(24, 16).ToArray().All(x => x == 0), "reserved bytes must be zero");
    }

    [Test("Clearing READ_ONLY must not alter the OFFLINE bit or any other disk state")]
    public static void EncodeClearReadOnly()
    {
        byte[] b = WindowsDiskProtocol.EncodeSetReadOnly(false);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8, 8)), "attribute value");
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16, 8)), "only read-only mask");
        Assert.Equal((byte)0, b[4], "must remain non-persistent");
    }

    [Test("GET_DISK_ATTRIBUTES explicitly distinguishes offline and read-only flags")]
    public static void DecodeGetDiskAttributes()
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), 1);
        Assert.True(WindowsDiskProtocol.TryDecodeGet(b, out var offline), "GET decode");
        Assert.True(offline.IsOffline, "disk offline");
        Assert.False(offline.IsReadOnly, "offline must not imply read-only");
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), 2);
        Assert.True(WindowsDiskProtocol.TryDecodeGet(b, out var readOnly), "GET decode");
        Assert.False(readOnly.IsOffline, "read-only must not imply offline");
        Assert.True(readOnly.IsReadOnly, "read-only");
    }

    [Test("Corrupt or truncated GET_DISK_ATTRIBUTES must be rejected")]
    public static void RejectInvalidGetDiskAttributes()
    {
        var b = new byte[16];
        Assert.False(WindowsDiskProtocol.TryDecodeGet(b, out _), "missing version");
        BinaryPrimitives.WriteUInt32LittleEndian(b, 16);
        Assert.False(WindowsDiskProtocol.TryDecodeGet(b.AsSpan(0, 15), out _), "truncated GET");
    }

    [Test("Storage alignment query uses PropertyId 6 (not the device descriptor)")]
    public static void AlignmentQueryProperty()
    {
        var q = WindowsStorageAlignmentProtocol.CreateQuery();
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(q), "PropertyId 6");
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(4)), "StandardQuery");
    }

    [Test("STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR distinguishes 512e and 4Kn")]
    public static void AlignmentDescriptors()
    {
        byte[] d = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(20), 4096);
        Assert.True(WindowsStorageAlignmentProtocol.TryDecode(d, out int logical, out int physical), "512e descriptor");
        Assert.Equal(512, logical, "logical 512");
        Assert.Equal(4096, physical, "physical 4096");

        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), 4096);
        Assert.True(WindowsStorageAlignmentProtocol.TryDecode(d, out logical, out physical), "4Kn descriptor");
        Assert.Equal(4096, logical, "logical 4096");
        Assert.Equal(4096, physical, "physical 4096");
    }

    [Test("Malformed alignment descriptors are rejected instead of inventing a sector size")]
    public static void RejectInvalidAlignmentDescriptor()
    {
        var d = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(20), 3072);
        Assert.False(WindowsStorageAlignmentProtocol.TryDecode(d, out _, out _), "non-power-of-two physical sector");
        Assert.False(WindowsStorageAlignmentProtocol.TryDecode(d.AsSpan(0, 27), out _, out _), "truncated descriptor");
    }

    [Test("CreateFile must open volume GUID device, not root path with trailing slash")]
    public static void VolumeGuidDevicePath()
    {
        Assert.Equal(@"\\?\Volume{1234}", WindowsVolumeService.ToDevicePath(@"\\?\Volume{1234}\"),
            "strip trailing slash only for device CreateFile");
        Assert.Equal(@"\\.\E:", WindowsVolumeService.ToDevicePath(@"\\.\E:"),
            "direct volume device path stays intact");
    }

    [Test("An unowned drive letter is never removed by temporary-volume cleanup")]
    public static void OriginalDriveLetterIsNotRemoved()
    {
        // Would call DeleteVolumeMountPoint on Windows with the old code.
        new WindowsVolumeService().DismountVolume('E');
    }

    [Test("All volumes must remain locked while the READ_ONLY attribute is set and verified")]
    public static void LocksHeldAcrossAttributeChange()
    {
        var events = new List<string>();
        var volumes = new FakeVolumes(events, 2);
        var protection = new FakeProtection(events, volumes);
        var takeover = new WindowsSourceTakeover(volumes, protection);
        var report = takeover.TakeOver(Disk(), out var session);
        Assert.True(report.Success, string.Join(" | ", report.Problems));
        Assert.NotEqual<ISourceTakeoverSession?>(null, session, "takeover session");
        Assert.True(protection.AllLockedWhenSet, "all volumes locked when setting protection");
        Assert.True(protection.AllLockedWhenVerified, "all volumes locked during readback verification");
        Assert.Equal(0, volumes.LiveLocks, "locks released after verifying protection");
        Assert.True(protection.Current.IsReadOnly, "source is protected");

        var release = session!.Release();
        Assert.True(release.Success, string.Join(" | ", release.Problems));
        Assert.False(protection.Current.IsReadOnly, "restored original writeable state");
        Assert.Equal(2, volumes.Remounts, "original volume paths restored");
        Assert.Equal(1, protection.ClearCalls, "restore only once");
    }

    [Test("A failed second volume lock releases the first lock without setting disk attributes")]
    public static void PartialLockFailureFailsClosed()
    {
        var events = new List<string>();
        var volumes = new FakeVolumes(events, 2) { FailAtIndex = 1 };
        var protection = new FakeProtection(events, volumes);
        var report = new WindowsSourceTakeover(volumes, protection).TakeOver(Disk(), out var session);
        Assert.False(report.Success, "takeover rejected");
        Assert.Equal<ISourceTakeoverSession?>(null, session, "no session on failure");
        Assert.Equal(0, volumes.LiveLocks, "partial locks released");
        Assert.Equal(1, volumes.Remounts, "first volume remounted on partial failure");
        Assert.Equal(0, protection.SetCalls, "no attributes modified");
    }

    [Test("Unknown disk attributes fail closed before any volume is locked")]
    public static void UnknownAttributesCannotPass()
    {
        var events = new List<string>();
        var volumes = new FakeVolumes(events, 1);
        var protection = new FakeProtection(events, volumes) { FailReads = true };
        var report = new WindowsSourceTakeover(volumes, protection).TakeOver(Disk(), out var session);
        Assert.False(report.Success, "unknown attributes refused");
        Assert.Equal<ISourceTakeoverSession?>(null, session, "no session");
        Assert.Equal(0, volumes.LockAttempts, "no volume touches");
        Assert.Equal(0, protection.SetCalls, "no write attribute IOCTL");
    }

    [Test("Unreadable volume extents refuse takeover instead of silently skipping a mounted volume")]
    public static void UnknownVolumeOwnershipCannotPass()
    {
        var events = new List<string>();
        var volumes = new FakeVolumes(events, 1) { UnknownVolumeExtents = true };
        var protection = new FakeProtection(events, volumes);
        var report = new WindowsSourceTakeover(volumes, protection).TakeOver(Disk(), out var session);
        Assert.False(report.Success, "unknown volume ownership must refuse");
        Assert.Equal<ISourceTakeoverSession?>(null, session, "no takeover session");
        Assert.Equal(0, volumes.LockAttempts, "no lock or dismount");
        Assert.Equal(0, protection.SetCalls, "no attribute changes");
    }

    [Test("OFFLINE must never be mistaken for READ_ONLY and must be refused")]
    public static void OfflineIsNotReadOnly()
    {
        var events = new List<string>();
        var volumes = new FakeVolumes(events, 1);
        var protection = new FakeProtection(events, volumes) { Current = new WindowsDiskAttributes(1) };
        var report = new WindowsSourceTakeover(volumes, protection).TakeOver(Disk(), out _);
        Assert.False(report.Success, "offline disk rejected");
        Assert.Equal(0, protection.SetCalls, "offline remains untouched");
    }

    [Test("An originally read-only disk remains read-only after release")]
    public static void OriginalReadOnlyIsPreserved()
    {
        var events = new List<string>();
        var volumes = new FakeVolumes(events, 1);
        var protection = new FakeProtection(events, volumes) { Current = new WindowsDiskAttributes(2) };
        var report = new WindowsSourceTakeover(volumes, protection).TakeOver(Disk(), out var session);
        Assert.True(report.Success, "pre-protected source allowed");
        Assert.Equal(0, volumes.LockAttempts, "no unnecessary dismount");
        Assert.True(session!.Release().Success, "release");
        Assert.True(protection.Current.IsReadOnly, "original state preserved");
        Assert.Equal(0, protection.SetCalls, "no disk attributes modified");
    }

    [Test("A failed read-back of READ_ONLY triggers rollback before unlocking")]
    public static void VerificationFailureRollsBack()
    {
        var events = new List<string>();
        var volumes = new FakeVolumes(events, 1);
        var protection = new FakeProtection(events, volumes) { FailVerifyRead = true };
        var report = new WindowsSourceTakeover(volumes, protection).TakeOver(Disk(), out var session);
        Assert.False(report.Success, "unverified state refused");
        Assert.Equal<ISourceTakeoverSession?>(null, session, "no session");
        Assert.Equal(2, protection.SetCalls, "set and rollback");
        Assert.False(protection.Current.IsReadOnly, "old writable state restored");
        Assert.Equal(0, volumes.LiveLocks, "locks released");
        Assert.True(events.IndexOf("set:rw") < events.IndexOf("unlock:0"),
            "rollback must happen before unlock");
    }

    [Test("If a failed SET ioctl applied READ_ONLY anyway, revert it safely")]
    public static void FailedSetIoctlCanBeRolledBack()
    {
        var events = new List<string>();
        var volumes = new FakeVolumes(events, 1);
        var protection = new FakeProtection(events, volumes) { FailSetAfterChange = true };
        var report = new WindowsSourceTakeover(volumes, protection).TakeOver(Disk(), out var session);
        Assert.False(report.Success, "failed attribute IOCTL must refuse");
        Assert.Equal<ISourceTakeoverSession?>(null, session, "no session");
        Assert.False(protection.Current.IsReadOnly, "rollback restored source");
        Assert.Equal(2, protection.SetCalls, "set and rollback");
        Assert.Equal(0, volumes.LiveLocks, "unlocked");
        Assert.Equal(1, volumes.Remounts, "restore mount after failed takeover");
    }

    private static PhysicalDisk Disk() => new()
    {
        Identity = Fixtures.Identity(7, @"\\.\PhysicalDrive7", 8 * Fixtures.GiB),
        IsReadOnly = false
    };

    private sealed class FakeVolumes(List<string> events, int count) : IWindowsTakeoverVolumes
    {
        public int FailAtIndex { get; set; } = -1;
        public bool UnknownVolumeExtents { get; set; }
        public int LockAttempts { get; private set; }
        public int LiveLocks { get; private set; }
        public int Remounts { get; private set; }

        private readonly IReadOnlyList<VolumeRecord> _volumes = Enumerable.Range(0, count)
            .Select(i => new VolumeRecord
            {
                GuidPath = "volume-" + i,
                DriveLetter = (char)('E' + i),
                Extents = [new VolumeDiskExtent(7, i * Fixtures.GiB, Fixtures.GiB)],
                FileSystemName = "NTFS"
            }).ToArray();

        public IReadOnlyList<VolumeRecord> EnumerateForTakeover() => UnknownVolumeExtents
            ? [.. _volumes, new VolumeRecord
                { GuidPath = "unidentified-volume", Extents = [], ExtentsUnavailable = true }]
            : _volumes;

        public WindowsVolumeService.VolumeFlushResult FlushAndLock(
            VolumeRecord volume, out ISourceVolumeLock? volumeLock)
        {
            int index = LockAttempts++;
            events.Add("lock:" + index);
            if (index == FailAtIndex)
            {
                volumeLock = null;
                return new(false, "locked by another process");
            }
            LiveLocks++;
            volumeLock = new FakeLock(() => { LiveLocks--; events.Add("unlock:" + index); });
            return new(true, "locked");
        }

        public bool TryRemount(VolumeRecord volume)
        {
            Remounts++;
            return true;
        }

        private sealed class FakeLock(Action release) : ISourceVolumeLock
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                release();
            }
        }
    }

    private sealed class FakeProtection(List<string> events, FakeVolumes volumes) : IDiskWriteProtection
    {
        public WindowsDiskAttributes Current { get; set; }
        public bool FailReads { get; set; }
        public bool FailVerifyRead { get; set; }
        public bool FailSetAfterChange { get; set; }
        public bool AllLockedWhenSet { get; private set; }
        public bool AllLockedWhenVerified { get; private set; }
        public int SetCalls { get; private set; }
        public int ClearCalls { get; private set; }
        private int _readCalls;

        public bool TryGetAttributes(int diskNumber, out WindowsDiskAttributes attributes, out string? error)
        {
            _readCalls++;
            if (_readCalls == 2)
                AllLockedWhenVerified = volumes.LiveLocks == volumes.EnumerateForTakeover().Count;
            events.Add("get:" + _readCalls);
            if (FailReads || (FailVerifyRead && _readCalls == 2))
            {
                attributes = default;
                error = "simulated IOCTL read failure";
                return false;
            }
            attributes = Current;
            error = null;
            return true;
        }

        public bool TrySetReadOnlyState(int diskNumber, bool readOnly, out string? error)
        {
            SetCalls++;
            if (!readOnly) ClearCalls++;
            if (readOnly) AllLockedWhenSet = volumes.LiveLocks == volumes.EnumerateForTakeover().Count;
            events.Add(readOnly ? "set:ro" : "set:rw");
            Current = new WindowsDiskAttributes(readOnly ? 2UL : 0UL);
            if (readOnly && FailSetAfterChange)
            {
                error = "simulated IOCTL failure after state change";
                return false;
            }
            error = null;
            return true;
        }
    }
}
