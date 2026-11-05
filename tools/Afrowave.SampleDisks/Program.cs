using System.Text;
using Afrowave.DiskMigrator.Core.Partitioning;
using Afrowave.DiskMigrator.Platform.Image;

// Creates a set of simulated disks for demonstrating and training Afrowave DiskMigrator
// without touching a real disk.
//
//   make-sample-disks <output-directory> [--shrink]
//
// Then run:  AfrowaveDiskMigrator.exe --simulate <output-directory>

string output = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "sample-disks";
bool shrink = args.Any(a => a.Equals("--shrink", StringComparison.OrdinalIgnoreCase));

if (Directory.Exists(output))
{
    Directory.Delete(output, recursive: true);
}

Directory.CreateDirectory(output);

const long MiB = 1024L * 1024;
const long GiB = 1024L * 1024 * 1024;

// ---------------------------------------------------------------- disk 0: service PC system disk
DiskImageBuilder.CreateGpt(Path.Combine(output, "disk0.img"), 8 * GiB)
    .AddGptPartition(GptPartitionTypes.EfiSystem, "SYSTEM", 1 * MiB, 64 * MiB)
    .AddGptPartition(GptPartitionTypes.MicrosoftReserved, "MSR", 65 * MiB, 16 * MiB)
    .AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Windows", 81 * MiB, 6 * GiB)
    .Build();

// ---------------------------------------------------------------- disk 1: the source disk
long espOffset = 1 * MiB;
long msrOffset = 65 * MiB;
long windowsOffset = 81 * MiB;
const long WindowsSize = 4 * GiB;
long recoveryOffset = windowsOffset + WindowsSize;
const long RecoverySize = 256 * MiB;

DiskImageBuilder.CreateGpt(Path.Combine(output, "disk1.img"), 6 * GiB)
    .AddGptPartition(GptPartitionTypes.EfiSystem, "EFI system partition", espOffset, 64 * MiB)
    .AddGptPartition(GptPartitionTypes.MicrosoftReserved, "Microsoft reserved partition", msrOffset, 16 * MiB)
    .AddGptPartition(GptPartitionTypes.MicrosoftBasicData, "Windows", windowsOffset, WindowsSize)
    .AddGptPartition(GptPartitionTypes.MicrosoftRecovery, "Windows Recovery", recoveryOffset, RecoverySize)
    .Build();

using (var device = ImageBlockDevice.OpenWritable(Path.Combine(output, "disk1.img")))
{
    WriteFat32BootSector(device, espOffset, "SYSTEM");

    // ~1 GiB of allocated clusters inside the 4 GiB Windows partition, in a handful of large
    // contiguous blocks - which is what real file system usage looks like.
    const int clusterCount = (int)(WindowsSize / SyntheticNtfs.ClusterSize);
    const int blockSize = 65536;                  // 256 MiB per block
    const int blockCount = 4;                     // ~1 GiB in total
    var dataClusters = new List<long>();
    for (int block = 0; block < blockCount; block++)
    {
        long start = 20_000 + (block * 200_000L);
        for (long i = 0; i < blockSize && start + i < clusterCount; i++)
        {
            dataClusters.Add(start + i);
        }
    }

    SyntheticNtfs.WriteTo(device, windowsOffset, clusterCount, dataClusters, dirty: false, label: "WINDOWS");

    long winReUsedClusters = 40 * MiB / SyntheticNtfs.ClusterSize;
    SyntheticNtfs.WriteTo(device, recoveryOffset, (int)(RecoverySize / SyntheticNtfs.ClusterSize),
        Enumerable.Range(2_000, (int)winReUsedClusters).Select(i => (long)i),
        dirty: false, label: "RECOVERY");
}

// ---------------------------------------------------------------- disk 2: the target disk
long targetSize = shrink ? 2560 * MiB : 8 * GiB;

using (var stream = new FileStream(Path.Combine(output, "disk2.img"), FileMode.Create, FileAccess.Write))
{
    stream.SetLength(targetSize);
}

File.WriteAllLines(Path.Combine(output, "protected.txt"), ["0"], Encoding.UTF8);

Console.WriteLine($"Simulované disky vytvořeny v: {Path.GetFullPath(output)}");
Console.WriteLine("  disk0.img - systémový disk servisního počítače 8 GiB (chráněný, nelze vybrat)");
Console.WriteLine("  disk1.img - zdrojový disk 6 GiB: ESP, MSR, Windows 4 GiB (1 GiB dat), WinRE");
Console.WriteLine($"  disk2.img - cílový disk {targetSize / MiB} MiB{(shrink ? " (vynutí zmenšení oddílu)" : " (obsah se vejde beze zmenšení)")}");
Console.WriteLine();
Console.WriteLine($"Spuštění: AfrowaveDiskMigrator.exe --simulate \"{Path.GetFullPath(output)}\"");
Console.WriteLine("Nápověda:  nástroj nikdy nezapisuje na zdrojový disk a disk 0 vůbec nenabídne.");
return 0;

static void WriteFat32BootSector(ImageBlockDevice device, long offset, string label)
{
    var sector = new byte[512];
    sector[0] = 0xEB;
    sector[1] = 0x58;
    sector[2] = 0x90;
    Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(sector, 3);
    BitConverter.TryWriteBytes(sector.AsSpan(0x0B, 2), (ushort)512);
    sector[0x0D] = 8;
    Encoding.ASCII.GetBytes("FAT32   ").CopyTo(sector, 82);
    Encoding.ASCII.GetBytes(label.PadRight(11)).CopyTo(sector, 71);
    sector[510] = 0x55;
    sector[511] = 0xAA;
    device.WriteAt(offset, sector);
    device.Flush();
}
