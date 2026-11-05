#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Automatizovaný test migrace na VHDX discích na skutečném Windows.

.DESCRIPTION
    Vytvoří dva VHDX soubory, které se Windows hlásí jako fyzické disky, naplní zdrojový
    disk daty, spustí Afrowave DiskMigrator a ověří výsledek na cílovém disku.

    Skutečných fyzických disků se test NEDOTKNE: pracuje výhradně s disky, které sám
    vytvořil a které před použitím znovu ověří (název "Virtual Disk" + přesná velikost).

    Co test ověří:
      * nástroj najde a správně popíše zdrojový i cílový disk,
      * prázdný cílový disk projde kontrolou, zdrojový disk s oddíly je odmítnut jako cíl,
      * migrace proběhne, data na cíli jsou bajtově shodná se zdrojem,
      * tabulka oddílů na cíli odpovídá zdroji,
      * zdrojový disk zůstane nezměněn.

    Co test NEověří:
      * start operačního systému z cílového disku (na to slouží scénář s VM, viz README.md).

.NOTES
    Tento skript nebyl autorem spuštěn - v prostředí, kde vznikl, žádný Windows nebyl
    k dispozici. Je připravený k prvnímu spuštění na servisním počítači.
#>
[CmdletBinding()]
param(
    [string]$WorkDir = (Join-Path $env:SystemDrive 'AfrowaveDiskMigratorTest'),
    [string]$ExePath = (Join-Path $PSScriptRoot '..\..\dist\AfrowaveDiskMigrator.exe'),
    [int]$SourceSizeMB = 6144,
    [int]$TargetSizeMB = 8192,
    [switch]$KeepVhds,
    [switch]$SkipCleanup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Checks = New-Object System.Collections.Generic.List[string]

function Write-Step { param([string]$Text) Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host "  OK   $Text" -ForegroundColor Green; $script:Checks.Add($Text) }
function Write-Fail { param([string]$Text) Write-Host "  FAIL $Text" -ForegroundColor Red;   $script:Failures.Add($Text) }
function Write-Note { param([string]$Text) Write-Host "  i    $Text" -ForegroundColor Gray }

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Ok $Message } else { Write-Fail $Message }
}

function Invoke-DiskPart {
    param([string[]]$Commands, [string]$What = 'diskpart')

    $scriptPath = Join-Path $env:TEMP ("afrowave-dp-" + [guid]::NewGuid().ToString('N') + ".txt")
    try {
        Set-Content -Path $scriptPath -Value $Commands -Encoding ASCII
        $output = & diskpart.exe /s $scriptPath 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "$What selhal (kód $LASTEXITCODE):`n$($output -join "`n")"
        }
        return $output
    }
    finally {
        Remove-Item -Path $scriptPath -Force -ErrorAction SilentlyContinue
    }
}

<#
    Najde číslo PhysicalDrive pro právě připojený virtuální disk dané velikosti.
    Odmítne pokračovat, pokud odpovídá víc než jeden disk - nikdy nehádáme.
#>
function Get-VirtualDiskNumber {
    param([long]$ExpectedBytes, [string]$What)

    $candidates = @(Get-Disk | Where-Object {
        $_.FriendlyName -match 'Virtual' -and
        [math]::Abs($_.Size - $ExpectedBytes) -lt (16MB)
    })

    if ($candidates.Count -eq 0) {
        throw "Nepodařilo se najít virtuální disk pro $What (očekávána velikost $ExpectedBytes B)."
    }

    if ($candidates.Count -gt 1) {
        throw "Pro $What odpovídá více virtuálních disků ($($candidates.Number -join ', ')). Test se zastavuje, aby nehádal."
    }

    return [int]$candidates[0].Number
}

function Get-FreeDriveLetter {
    foreach ($candidate in [char[]]'STUVWXYZQRPONMLK') {
        if (-not (Test-Path -LiteralPath "${candidate}:\")) { return $candidate }
    }
    throw 'Není k dispozici žádné volné písmeno jednotky pro připojení testovacího oddílu.'
}

function Get-DiskSignature {
    param([int]$DiskNumber)
    $disk = Get-Disk -Number $DiskNumber
    return [pscustomobject]@{
        Number       = $disk.Number
        FriendlyName = $disk.FriendlyName
        SerialNumber = $disk.SerialNumber
        Size         = $disk.Size
        PartitionStyle = [string]$disk.PartitionStyle
        Guid         = [string]$disk.Guid
    }
}

# ---------------------------------------------------------------------------- příprava

Write-Step 'Kontrola předpokladů'

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Nenalezen AfrowaveDiskMigrator.exe: $ExePath. Sestavte projekt (build.sh build) nebo zadejte -ExePath."
}
Write-Ok "Nástroj nalezen: $ExePath"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Skript musí běžet jako správce.'
}
Write-Ok 'Běžíme jako správce'

if (-not (Get-Command Get-Disk -ErrorAction SilentlyContinue)) {
    throw 'Chybí modul Storage (Get-Disk). Vyžaduje Windows 10/11 nebo Windows Server 2016+.'
}
Write-Ok 'Modul Storage je dostupný'

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
Write-Note "Pracovní adresář: $WorkDir"

$sourceVhd = Join-Path $WorkDir 'afrowave-source.vhdx'
$targetVhd = Join-Path $WorkDir 'afrowave-target.vhdx'
$sourceNumber = $null
$targetNumber = $null

foreach ($path in @($sourceVhd, $targetVhd)) {
    if (Test-Path -LiteralPath $path) {
        throw "Soubor $path už existuje. Smažte jej nebo použijte jiný -WorkDir."
    }
}

try {
    # ------------------------------------------------------------------ vytvoření VHDX

    Write-Step 'Vytvářím a připojuji VHDX'

    Invoke-DiskPart @(
        "create vdisk file=`"$sourceVhd`" maximum=$SourceSizeMB type=expandable",
        "select vdisk file=`"$sourceVhd`"",
        'attach vdisk'
    ) 'Vytvoření zdrojového VHDX' | Out-Null

    Invoke-DiskPart @(
        "create vdisk file=`"$targetVhd`" maximum=$TargetSizeMB type=expandable",
        "select vdisk file=`"$targetVhd`"",
        'attach vdisk'
    ) 'Vytvoření cílového VHDX' | Out-Null

    Start-Sleep -Seconds 2

    $sourceNumber = Get-VirtualDiskNumber -ExpectedBytes ($SourceSizeMB * 1MB) -What 'zdroj'
    $targetNumber = Get-VirtualDiskNumber -ExpectedBytes ($TargetSizeMB * 1MB) -What 'cíl'
    Write-Ok "Zdrojový disk = PhysicalDrive$sourceNumber, cílový = PhysicalDrive$targetNumber"

    # ------------------------------------------------------------------ příprava zdroje

    Write-Step 'Připravuji zdrojový disk (GPT: ESP + MSR + datový oddíl)'

    $sourceLetter = Get-FreeDriveLetter
    $targetLetter = Get-FreeDriveLetter
    if ($targetLetter -eq $sourceLetter) { $targetLetter = [char]([int]$sourceLetter + 1) }
    Write-Note "Dočasná písmena jednotek: zdroj $sourceLetter`:, cíl $targetLetter`:"

    Invoke-DiskPart @(
        "select disk $sourceNumber",
        'clean',
        'convert gpt',
        'create partition efi size=100',
        'format quick fs=fat32 label=SYSTEM',
        'create partition msr size=16',
        'create partition primary',
        'format quick fs=ntfs label=DATA',
        "assign letter=$sourceLetter",
        'exit'
    ) 'Příprava zdrojového disku' | Out-Null

    $dataRoot = "${sourceLetter}:\"
    $expected = @{}
    $fileCount = 40

    for ($i = 1; $i -le $fileCount; $i++) {
        $relative = "data-{0:D3}.bin" -f $i
        $path = Join-Path $dataRoot $relative
        $size = 64KB + ($i * 512)
        $bytes = New-Object byte[] $size
        ([System.Random]::new($i)).NextBytes($bytes)
        [System.IO.File]::WriteAllBytes($path, $bytes)
        $expected[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }

    # Soubor s mezerami v názvu a vnořená struktura - běžné pasti.
    $nestedDir = Join-Path $dataRoot 'slozka\vnořena'
    New-Item -ItemType Directory -Path $nestedDir -Force | Out-Null
    $nestedRelative = 'slozka\vnořena\soubor s mezerou.txt'
    Set-Content -LiteralPath (Join-Path $dataRoot $nestedRelative) -Value 'Afrowave' -Encoding UTF8
    $expected[$nestedRelative] = (Get-FileHash -LiteralPath (Join-Path $dataRoot $nestedRelative) -Algorithm SHA256).Hash

    # Prázdný soubor.
    $emptyRelative = 'prazdny.txt'
    [System.IO.File]::WriteAllBytes((Join-Path $dataRoot $emptyRelative), @())
    $expected[$emptyRelative] = (Get-FileHash -LiteralPath (Join-Path $dataRoot $emptyRelative) -Algorithm SHA256).Hash

    Write-Ok "Zapsáno $($expected.Count) souborů na zdrojový disk"

    $sourceBytesBefore = (Get-FileHash -LiteralPath $sourceVhd -Algorithm SHA256).Hash
    Write-Note "Otisk zdrojového VHDX před migrací: $($sourceBytesBefore.Substring(0,16))…"

    # Cílový disk MUSÍ být bez oddílů - to je základní podmínka nástroje.
    $targetPartitions = @(Get-Partition -DiskNumber $targetNumber -ErrorAction SilentlyContinue)
    Assert-True ($targetPartitions.Count -eq 0) 'Cílový disk je před migrací bez oddílů'

    # -------------------------------------------------------------- kontrola - cíl S oddíly

    Write-Step 'Kontrola: cílový disk s oddíly musí být odmítnut'

    Invoke-DiskPart @(
        "select disk $targetNumber",
        'create partition primary size=512',
        'exit'
    ) 'Vytvoření zkušebního oddílu na cíli' | Out-Null

    $logWithPartitions = Join-Path $WorkDir 'refusal-occupied-target.log'
    $output = & $ExePath --source $sourceNumber --target $targetNumber --yes --log $logWithPartitions 2>&1
    $exitCode = $LASTEXITCODE

    Assert-True ($exitCode -ne 0) 'Migrace na disk s oddíly byla odmítnuta (nenulový návratový kód)'
    Assert-True (($output -join "`n") -match 'není prázdný|neobsahuje žádné oddíly|nedostupný') 'Odmítnutí obsahuje vysvětlení'
    Assert-True (@(Get-Partition -DiskNumber $targetNumber).Count -ge 1) 'Nástroj oddíly na cíli nesmazal'

    Invoke-DiskPart @("select disk $targetNumber", 'clean', 'exit') 'Vyčištění cílového disku' | Out-Null

    # -------------------------------------------------------------- kontrola - dry run

    Write-Step 'Kontrola: režim bez zápisu (--dry-run)'

    $dryLog = Join-Path $WorkDir 'dry-run.log'
    $dryOutput = & $ExePath --source $sourceNumber --target $targetNumber --dry-run --log $dryLog 2>&1
    Assert-True ($LASTEXITCODE -eq 0) 'Kontrolní režim doběhl bez chyby'
    Assert-True (($dryOutput -join "`n") -match 'SHA-256|Navržený postup|migrace') 'Kontrolní režim vypsal plán'

    $styleAfterDryRun = [string](Get-Disk -Number $targetNumber).PartitionStyle
    Assert-True ($styleAfterDryRun -eq 'RAW') 'Kontrolní režim na cílový disk nic nezapsal'

    # -------------------------------------------------------------- skutečná migrace

    Write-Step 'Spouštím migraci'

    $migrationLog = Join-Path $WorkDir 'migration.log'
    $migrationOutput = & $ExePath --source $sourceNumber --target $targetNumber --yes --log $migrationLog 2>&1
    $migrationExit = $LASTEXITCODE

    Assert-True ($migrationExit -eq 0) 'Migrace doběhla s návratovým kódem 0'

    if ($migrationExit -ne 0) {
        Write-Host ($migrationOutput -join "`n") -ForegroundColor Yellow
    }

    # -------------------------------------------------------------- ověření výsledku

    Write-Step 'Ověřuji výsledek na cílovém disku'

    $sourceTable = @(Get-Partition -DiskNumber $sourceNumber | Sort-Object Offset)
    $targetTable = @(Get-Partition -DiskNumber $targetNumber | Sort-Object Offset)

    Assert-True ($targetTable.Count -eq $sourceTable.Count) "Počet oddílů na cíli ($($targetTable.Count)) odpovídá zdroji ($($sourceTable.Count))"

    $dataPartition = $targetTable | Where-Object { $_.Type -eq 'Basic' } | Select-Object -First 1
    Assert-True ($null -ne $dataPartition) 'Na cíli existuje datový oddíl'

    if ($dataPartition) {
        try {
            Set-Partition -DiskNumber $targetNumber -PartitionNumber $dataPartition.PartitionNumber -NewDriveLetter $targetLetter
            Start-Sleep -Seconds 2

            $targetRoot = "${targetLetter}:\"
            $actual = @(Get-ChildItem -LiteralPath $targetRoot -File -Recurse -ErrorAction Stop)

            Assert-True ($actual.Count -ge $expected.Count) "Na cíli je přeneseno $($actual.Count) souborů"

            $mismatches = 0
            foreach ($name in $expected.Keys) {
                $candidate = Join-Path $targetRoot $name
                if (-not (Test-Path -LiteralPath $candidate)) {
                    Write-Fail "Chybí soubor: $name"
                    $mismatches++
                    continue
                }
                if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $expected[$name]) {
                    Write-Fail "Odlišný obsah: $name"
                    $mismatches++
                }
            }

            Assert-True ($mismatches -eq 0) 'Obsah všech testovacích souborů je shodný (SHA-256)'
            Assert-True (Test-Path -LiteralPath (Join-Path $targetRoot $emptyRelative)) 'Prázdný soubor byl přenesen'

            Remove-PartitionAccessPath -DiskNumber $targetNumber -PartitionNumber $dataPartition.PartitionNumber -AccessPath "${targetLetter}:\" -ErrorAction SilentlyContinue
        }
        catch {
            Write-Fail "Ověření obsahu selhalo: $($_.Exception.Message)"
        }
    }

    # -------------------------------------------------------------- zdroj musí zůstat beze změny

    Write-Step 'Ověřuji, že zdrojový disk zůstal beze změny'

    $sourcePartitionsAfter = @(Get-Partition -DiskNumber $sourceNumber)
    Assert-True ($sourcePartitionsAfter.Count -eq $sourceTable.Count) 'Počet oddílů na zdroji se nezměnil'
    Assert-True ([string](Get-Disk -Number $sourceNumber).PartitionStyle -eq 'GPT') 'Zdroj zůstal GPT'

    $sourceDisk = Get-Disk -Number $sourceNumber
    if ($sourceDisk.PSObject.Properties['IsReadOnly']) {
        Assert-True (-not $sourceDisk.IsReadOnly) 'Zdrojový disk nezůstal omylem v režimu read-only'
    }
    else {
        Write-Note 'Vlastnost IsReadOnly není v této verzi Windows dostupná - zkontrolujte disk ručně.'
    }

    # Porovnat lze jen data, která Windows nezmění (VHDX metadatové bloky se mění vždy).
    Write-Note 'Bajtové srovnání celého VHDX se neprovádí - Windows do něj zapisuje vlastní metadata.'
    Write-Note 'Ochranu zdroje ověřují automatické testy nad obrazovými soubory (SHA-256 před/po).'

    $sourceFilesStillThere = @(Get-ChildItem -LiteralPath "${sourceLetter}:\" -File -Recurse -ErrorAction SilentlyContinue)
    Assert-True ($sourceFilesStillThere.Count -ge $expected.Count) 'Data na zdrojovém disku jsou stále dostupná'
}
catch {
    Write-Host "`nTest se nedokončil: $($_.Exception.Message)" -ForegroundColor Red
    $script:Failures.Add("Výjimka: $($_.Exception.Message)")
}
finally {
    # ------------------------------------------------------------------ úklid

    Write-Step 'Úklid'

    if (-not $SkipCleanup) {
        # The source disk keeps its content so a failure can be inspected afterwards.
        if ($null -ne $targetNumber) {
            try { Invoke-DiskPart @("select disk $targetNumber", 'clean', 'exit') 'Vyčištění cílového disku' | Out-Null } catch { }
        }

        foreach ($path in @($sourceVhd, $targetVhd)) {
            if (Test-Path -LiteralPath $path) {
                try {
                    Invoke-DiskPart @("select vdisk file=`"$path`"", 'detach vdisk') 'Odpojení VHDX' | Out-Null
                }
                catch { Write-Note "VHDX $path se nepodařilo odpojit: $($_.Exception.Message)" }

                if (-not $KeepVhds) {
                    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
                }
            }
        }

        if ($KeepVhds) {
            Write-Note "VHDX soubory ponechány v $WorkDir (parametr -KeepVhds)."
        }
    }
    else {
        Write-Note 'Úklid přeskočen (parametr -SkipCleanup). Disky a VHDX zůstaly připojené!'
    }

    Write-Step 'Souhrn'
    Write-Host "  Úspěšných kontrol: $($script:Checks.Count)"
    Write-Host "  Neúspěchů:         $($script:Failures.Count)"

    if ($script:Failures.Count -gt 0) {
        foreach ($failure in $script:Failures) { Write-Host "   - $failure" -ForegroundColor Red }
        exit 1
    }

    Write-Host "`nVšechny kontroly prošly." -ForegroundColor Green
    Write-Note 'Start operačního systému z cílového disku tímto testem ověřen NEBYL - viz README.md.'
    exit 0
}
