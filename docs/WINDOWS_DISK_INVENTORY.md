# Windows disk inventory safety (November 2026)

## Former false positives

An offline customer Windows installation usually contains pagefile.sys, swapfile.sys
and hiberfil.sys. Their presence does NOT indicate that the servicing Windows
installation is using that disk. File existence is therefore no longer treated
as an active paging signal.

CopyMe now enumerates kernel-registered active pagefiles via EnumPageFilesW
and maps their NT paths to volume GUIDs via QueryDosDeviceW. If the active
pagefile's disk cannot be identified, the inventory refuses to proceed.

## USB adapter support

Some USB-to-SATA adapters cannot service IOCTL_DISK_GET_DISK_ATTRIBUTES.
CopyMe uses the actual disk attributes from IOCTL when available, falling
back to Windows Storage Get-Disk (and Set-Disk for read-only transitions).
The fallback validates the exact disk number, ONLINE/OFFLINE and READ_ONLY state.
Failure of both methods blocks use as source and target, with a precise reason.

USB does NOT automatically exempt a device from protection. Disks hosting the
running OS, the executable, or an active pagefile remain protected on any bus.

An SSD target must be empty of partitions; CopyMe does not silently erase
partitions. This is independent of disk protection status.

## Coverage and limitations

Automated tests exercise path matching, PowerShell output validation, policy
blockers, source protection and USB-hosted executable safeguards. They do
not exercise Windows kernel APIs or physical USB bridge behavior.
Use disposable drives for the first Windows integration test.

Run: dotnet run --project tests/Afrowave.DiskMigrator.Tests -c Release
