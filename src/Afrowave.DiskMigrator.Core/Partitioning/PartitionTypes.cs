using System.Text;

namespace Afrowave.DiskMigrator.Core.Partitioning;

/// <summary>
/// GUID helpers. On disk, GPT stores GUIDs in little-endian mixed byte order for the
/// first three fields, which is exactly what <see cref="Guid.ToByteArray"/> produces,
/// so <see cref="Guid"/> round-trips without manual byte swapping.
/// </summary>
public static class GuidUtil
{
    public static Guid FromBytes(ReadOnlySpan<byte> bytes) => new(bytes);

    public static void WriteBytes(Guid value, Span<byte> destination) => value.TryWriteBytes(destination);

    public static string ToText(ReadOnlySpan<byte> bytes) => new Guid(bytes).ToString("D").ToUpperInvariant();
}

/// <summary>Well-known GPT partition type GUIDs.</summary>
public static class GptPartitionTypes
{
    public static readonly Guid Unused = Guid.Empty;
    public static readonly Guid MbrScheme = new("024DEE41-33E7-11D3-9D69-0008C781F39F");
    public static readonly Guid EfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid BiosBoot = new("21686148-6449-6E6F-744E-656564454649");
    public static readonly Guid MicrosoftReserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    public static readonly Guid MicrosoftBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid MicrosoftLdmMetadata = new("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3");
    public static readonly Guid MicrosoftLdmData = new("AF9B60A0-1431-4F62-BC68-3311714A69AD");
    public static readonly Guid MicrosoftRecovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
    public static readonly Guid MicrosoftIronsides = new("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D");
    public static readonly Guid MicrosoftStorageSpaces = new("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D");
    public static readonly Guid MicrosoftCephFs = new("37AFFC90-EF7D-4E96-91C3-2D7AE055B174");
    public static readonly Guid LinuxSwap = new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");
    public static readonly Guid LinuxFilesystem = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    public static readonly Guid LinuxLvm = new("E6D6D379-F507-44C2-A23C-238F2A3DF928");
    public static readonly Guid AppleHfsPlus = new("48465300-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid AppleApfs = new("7C3457EF-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid ChromeOsKernel = new("FE3A2A5D-4F32-41A7-B725-ACCC3285A309");

    /// <summary>
    /// Type GUIDs that describe a container whose contents this tool must never
    /// attempt to reinterpret - only a raw sector copy can preserve them.
    /// </summary>
    public static bool IsContainerType(Guid type) =>
        type == MicrosoftLdmMetadata || type == MicrosoftLdmData || type == MicrosoftIronsides ||
        type == LinuxLvm || type == ChromeOsKernel;

    public static string Describe(Guid type)
    {
        if (type == Unused) return "prázdný";
        if (type == EfiSystem) return "EFI System (ESP)";
        if (type == BiosBoot) return "BIOS Boot";
        if (type == MicrosoftReserved) return "Microsoft Reserved (MSR)";
        if (type == MicrosoftBasicData) return "Microsoft Basic Data";
        if (type == MicrosoftLdmMetadata) return "LDM Metadata";
        if (type == MicrosoftLdmData) return "LDM Data";
        if (type == MicrosoftRecovery) return "Microsoft Recovery (WinRE)";
        if (type == MicrosoftIronsides) return "Microsoft Storage Spaces";
        if (type == LinuxSwap) return "Linux Swap";
        if (type == LinuxFilesystem) return "Linux Filesystem";
        if (type == LinuxLvm) return "Linux LVM";
        if (type == AppleHfsPlus) return "Apple HFS+";
        if (type == AppleApfs) return "Apple APFS";
        if (type == MbrScheme) return "MBR scheme";
        return type.ToString("D").ToUpperInvariant();
    }
}

/// <summary>Well-known MBR partition type bytes.</summary>
public static class MbrPartitionTypes
{
    public const byte Empty = 0x00;
    public const byte Fat12 = 0x01;
    public const byte Fat16Small = 0x04;
    public const byte ExtendedChs = 0x05;
    public const byte Fat16 = 0x06;
    public const byte NtfsExFat = 0x07;
    public const byte Fat32Chs = 0x0B;
    public const byte Fat32Lba = 0x0C;
    public const byte Fat16Lba = 0x0E;
    public const byte ExtendedLba = 0x0F;
    public const byte LinuxSwap = 0x82;
    public const byte Linux = 0x83;
    public const byte WindowsRe = 0x27;
    public const byte HfsPlus = 0xAF;
    public const byte GptProtective = 0xEE;
    public const byte EfiSystem = 0xEF;
    public const byte Ldm = 0x42;

    public static bool IsExtended(byte type) => type is ExtendedChs or ExtendedLba or 0x85;

    public static string Describe(byte type) => type switch
    {
        Empty => "prázdný",
        Fat12 => "FAT12",
        Fat16Small => "FAT16 <32M",
        ExtendedChs => "Rozšířený oddíl (CHS)",
        Fat16 => "FAT16",
        NtfsExFat => "NTFS / exFAT",
        Fat32Chs => "FAT32 (CHS)",
        Fat32Lba => "FAT32 (LBA)",
        Fat16Lba => "FAT16 (LBA)",
        ExtendedLba => "Rozšířený oddíl (LBA)",
        LinuxSwap => "Linux Swap",
        Linux => "Linux",
        WindowsRe => "Windows RE (skrytý NTFS)",
        HfsPlus => "HFS+",
        GptProtective => "GPT (protective)",
        EfiSystem => "EFI System (ESP)",
        Ldm => "Windows Dynamic Disk",
        _ => $"typ 0x{type:X2}",
    };
}

internal static class Ascii
{
    public static string Slice(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || offset >= data.Length) return string.Empty;
        int end = Math.Min(data.Length, offset + length);
        return Encoding.ASCII.GetString(data[offset..end]).TrimEnd('\0', ' ');
    }
}
