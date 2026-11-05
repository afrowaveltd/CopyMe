using System.Runtime.InteropServices;
using Afrowave.DiskMigrator.Core.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Afrowave.DiskMigrator.Platform.Windows;

/// <summary>
/// Raw block access to a Windows physical drive. Windows requires sector aligned transfers on
/// raw devices, so this type transparently widens unaligned requests to the enclosing
/// sector window. Upper layers can therefore work with arbitrary record sizes.
/// </summary>
internal sealed class WindowsDiskDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;
    private readonly Lock _gate = new();
    private bool _disposed;

    public WindowsDiskDevice(string devicePath, SafeFileHandle handle, int sectorSize, long length, bool writable)
    {
        Name = devicePath;
        _handle = handle;
        SectorSize = sectorSize;
        Length = length;
        IsWritable = writable;
    }

    public string Name { get; }

    public long Length { get; }

    public int SectorSize { get; }

    public bool IsWritable { get; }

    public static WindowsDiskDevice Open(string devicePath, bool writable, int sectorSize, long length)
    {
        uint access = Native.GenericRead | (writable ? Native.GenericWrite : 0);

        var handle = Native.CreateFile(
            devicePath,
            access,
            Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero,
            Native.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Zařízení {devicePath} nelze otevřít {(writable ? "pro zápis" : "pro čtení")} (chyba {error}: " +
                Win32Error.Describe(error) + ").");
        }

        return new WindowsDiskDevice(devicePath, handle, sectorSize, length, writable);
    }

    public void ReadAt(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

        int alignment = Math.Max(SectorSize, 512);

        if (offset % alignment == 0 && buffer.Length % alignment == 0)
        {
            DirectRead(offset, buffer);
            return;
        }

        long start = offset / alignment * alignment;
        long end = ((offset + buffer.Length + alignment - 1) / alignment) * alignment;
        int window = checked((int)(end - start));
        var temp = new byte[window];
        DirectRead(start, temp);
        temp.AsSpan((int)(offset - start), buffer.Length).CopyTo(buffer);
    }

    public void WriteAt(long offset, ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsWritable) throw new InvalidOperationException($"Zařízení {Name} bylo otevřeno pouze pro čtení.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

        int alignment = Math.Max(SectorSize, 512);

        if (offset % alignment == 0 && buffer.Length % alignment == 0)
        {
            DirectWrite(offset, buffer);
            return;
        }

        long start = offset / alignment * alignment;
        long end = ((offset + buffer.Length + alignment - 1) / alignment) * alignment;
        int window = checked((int)(end - start));

        var temp = new byte[window];
        DirectRead(start, temp);                     // read-modify-write keeps neighbours intact
        buffer.CopyTo(temp.AsSpan((int)(offset - start)));
        DirectWrite(start, temp);
    }

    private unsafe void DirectRead(long offset, Span<byte> buffer)
    {
        lock (_gate)
        {
            if (!Native.SetFilePointerEx(_handle, offset, out _, 0))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException($"Nepodařilo se nastavit pozici čtení (chyba {error}: {Win32Error.Describe(error)}).");
            }

            fixed (byte* pointer = buffer)
            {
                int total = 0;
                while (total < buffer.Length)
                {
                    if (!Native.ReadFile(_handle, pointer + total, buffer.Length - total, out int read, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        throw new IOException(
                            $"Chyba čtení z {Name} na offsetu {offset + total} " +
                            $"(chyba {error}: {Win32Error.Describe(error)}).");
                    }

                    if (read <= 0)
                    {
                        throw new IOException($"Neočekávaný konec zařízení {Name} na offsetu {offset + total}.");
                    }

                    total += read;
                }
            }
        }
    }

    private unsafe void DirectWrite(long offset, ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            if (!Native.SetFilePointerEx(_handle, offset, out _, 0))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException($"Nepodařilo se nastavit pozici zápisu (chyba {error}: {Win32Error.Describe(error)}).");
            }

            fixed (byte* pointer = buffer)
            {
                int total = 0;
                while (total < buffer.Length)
                {
                    if (!Native.WriteFile(_handle, pointer + total, buffer.Length - total, out int written, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        throw new IOException(
                            $"Chyba zápisu na {Name} na offsetu {offset + total} " +
                            $"(chyba {error}: {Win32Error.Describe(error)}).");
                    }

                    if (written <= 0)
                    {
                        throw new IOException($"Zápis na {Name} neuložil žádná data (offset {offset + total}).");
                    }

                    total += written;
                }
            }
        }
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Native.FlushFileBuffers(_handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}

internal static class Win32Error
{
    public static string Describe(int code) => code switch
    {
        5 => "přístup odepřen – spusťte nástroj jako administrátor",
        32 => "zařízení nebo soubor je používán jiným procesem",
        19 => "médium je chráněno proti zápisu",
        23 => "chyba dat (CRC)",
        87 => "neplatný parametr",
        1117 => "chyba vstupně-výstupní operace",
        1167 => "zařízení není připraveno",
        21 => "zařízení není připraveno",
        _ => "neznámá chyba",
    };
}
