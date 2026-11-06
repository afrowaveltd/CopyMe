# Testování a výsledky

Tento dokument popisuje **skutečně provedené** testy a jejich výsledky. Nic zde není
zamýšlený stav – vše níže bylo spuštěno.

---

## 1. Prostředí

Sestavení i testy probíhaly na vývojovém počítači s Linuxem (Debian 13, x86-64) pomocí
.NET SDK 10.0.402, s cílem `win-x64` pro výsledný EXE.

Důsledek, který je třeba mít na paměti:

> **Vrstva pro skutečný hardware nebyla spuštěna na reálném počítači s Windows.**
> V tomto prostředí žádný počítač s Windows 10/11 a fyzickými disky k dispozici nebyl.
> Kód pro Windows se přeloží bez chyb a varování, ale ověření přístupu k
> `\\.\PhysicalDriveN`, připojování svazků, `DISM` a `bcdboot` musí proběhnout
> na skutečném servisním počítači – viz část 6.

Veškerá logika rozhodování, čtení tabulek oddílů, práce s NTFS, plánování, kopírování
a ověřování otestována **byla**, a to nad skutečnými soubory disků (obrazové disky).

---

## 2. Automatické testy

Spuštění:

```bash
./build.sh test
```

Aktuální výsledek:

```
Afrowave DiskMigrator – test suite (119 testů)
Výsledek: 119 prošlo, 0 selhalo (GitHub Actions, Linux; Windows EXE byl sestaven)
```

Testy nepoužívají žádnou testovací knihovnu – součástí projektu je malý runner
(`tests/.../TestFramework.cs`), aby projekt neměl ani vývojovou závislost.

### 2.1 Tabulky oddílů (12 testů)

| Test | Co ověřuje |
|---|---|
| `Crc32KnownVector` | CRC-32 proti publikovanému vektoru (`0xCBF43926` pro „123456789“) |
| `GptRoundTrip` | Zápis GPT → čtení → shodná pole (GUID disku, GUID oddílů, názvy, atributy) |
| `GptHandCraftedBytes` | GPT sestavená ručně mimo zapisovač se načte správně |
| `GptCorruptHeaderRefused` | Poškozený CRC hlavičky (primární i záložní) → odmítnuto |
| `GptBackupFallback` | Poškozená primární GPT → použita záložní kopie + varování |
| `MbrPrimaries` | Čtyři primární oddíly, aktivní příznak, signatura disku |
| `MbrLogicalPartitions` | Rozšířený oddíl se dvěma logickými, správné offsety |
| `MbrWithoutSignature` | Disk bez `0x55AA` → chyba, ne odhad |
| `OverlappingPartitionsRefused` | Překrývající se oddíly → odmítnuto |
| `PartitionBeyondDiskRefused` | Oddíl za koncem disku → odmítnuto |
| `WriterRefusesOverflow` | Zapisovač odmítne oddíl, který se nevejde |
| `FourKilobyteSectors` | Disky se sektorem 4 KiB – zápis i čtení |

### 2.2 Souborové systémy a NTFS (7 testů)

| Test | Co ověřuje |
|---|---|
| `DetectNtfs` | Rozpoznání NTFS z boot sektoru |
| `DetectFat32` | FAT32 včetně popisku svazku |
| `DetectVarious` | exFAT, BitLocker, ext4, neznámý obsah; BitLocker označen jako šifrovaný |
| `DetectUnformatted` | Prázdný oddíl = neformátováno |
| `NtfsReaderBasics` | Čtečka NTFS: velikost clusteru, popisek, obsazené clustery, alokační mapa |
| `NtfsDirtyFlag` | Rozpoznání příznaku „dirty“ v `$Volume` |
| `NtfsRejectsGarbage` | Oddíl, který se jen vydává za NTFS, je odmítnut (ne odhadnut) |

### 2.3 Převzetí zdrojového disku (15 testů) – nové v této verzi

| Test | Co ověřuje |
|---|---|
| `TakeOverSucceeds` | Převzetí připojeného disku projde a hlásí, co provedlo |
| `TakeOverFailureLeavesNoSession` | Nepoužitelný svazek → odmítnuto, žádná relace, nic se nepsalo |
| `RetryAfterFailureSucceeds` | Po uvolnění svazku druhý pokus projde |
| `NoRetryWhenHopeless` | U neřešitelného stavu se na opakování neptá |
| `RetryCanBeDeclined` | Technik může opakování odmítnout |
| `RetryHonoursLimit` | Opakování se zastaví na limitu pokusů |
| `UnavailableTakeoverRefused` | Bez podpory převzetí se migrace nespustí |
| `ReleaseIsIdempotent` | Dvojí uvolnění nespadne a uvolní jen jednou |
| `ReleaseFailureIsReported` | Selhání úklidu se hlásí, nevyhazuje výjimku |
| `TakeOverFlipsReadOnlyAndRestores` | Převzetí nastaví read-only, úklid jej zase zruší |
| `OriginallyReadOnlyStaysReadOnly` | Původně read-only disk zůstane read-only |
| `ExecutorRefusesUnprotectedSource` | Vykonavatel odmítne zápis, když zdroj není prokazatelně read-only |
| `EndToEndWithTakeover` | Celý sled převzetí → migrace → úklid; zdroj zůstane bajtově shodný |
| `RefusedTakeoverStopsEverything` | Odmítnuté převzetí zastaví migraci, cíl zůstane nedotčen |
| `CancelledMigrationStillReleases` | Přerušení migrace stále provede úklid a vrátí disk |

### 2.4 Role disků – seznamy „zdroj“ a „cíl“ (6 testů) – nové v této verzi

| Test | Co ověřuje |
|---|---|
| `PartitionedDiskIsSourceOnly` | Disk s oddíly se nabídne jako zdroj a nikdy jako cíl |
| `EmptyDiskIsTargetOnly` | Disk bez oddílů se nabídne jako cíl a nikdy jako zdroj |
| `ProtectedDiskHasNoRole` | Systémový disk se nenabídne v žádné roli |
| `UncertainDiskHasNoRole` | Nejednoznačný disk se nenabídne v žádné roli |
| `UninspectedDiskHasNoRole` | Bez kontroly stavu se disk nenabídne v žádné roli |
| `ReadOnlyDiskCanBeSource` | Read-only disk s oddíly zůstává platným zdrojem (ideální případ) |

### 2.5 Cílový disk (12 testů)

| Test | Co ověřuje |
|---|---|
| `BlankDiskIsEmpty` | Zcela prázdný disk je použitelný jako cíl |
| `GptWithPartitionsRefused` | GPT s oddílem → odmítnuto, včetně výčtu oddílů |
| `GptWithoutPartitionsIsEmpty` | Platná GPT bez oddílů → použitelná |
| `MbrWithPartitionsRefused` | MBR s oddílem → odmítnuto |
| `MbrExtendedContainerRefused` | Rozšířený MBR kontejner se počítá jako definovaný oddíl |
| `MbrWithEmptyEntriesIsEmpty` | MBR se samými prázdnými položkami → použitelný |
| `CorruptTableRefused` | Poškozená GPT → nejednoznačný stav → odmítnuto |
| `StrayGptHeaderRefused` | GPT hlavička bez ochranného MBR → odmítnuto |
| `StaleBackupGptRefused` | Zůstala jen záložní GPT → odmítnuto |
| `HybridMbrRefused` | Hybridní MBR → odmítnuto |
| `SuperFloppyIsEmptyWithNote` | Souborový systém přímo na disku → prázdný disk s poznámkou |
| `PartitionBeyondDiskReported` | Oddíl přesahující disk je stále definovaný oddíl |

### 2.6 Bezpečnostní politika (10 testů)

| Test | Co ověřuje |
|---|---|
| `RunningOsDiskProtected` | Disk s běžícím OS nelze použít jako zdroj ani cíl |
| `ExecutableHostDiskProtected` | Disk, ze kterého nástroj běží, je chráněn |
| `OfflineWindowsDiskIsSelectable` | **Offline instalace Windows zůstává volitelná jako zdroj** (hlavní účel nástroje) |
| `RunningOsWithWindowsStillProtected` | Disk běžícího systému zůstává chráněn i když obsahuje Windows |
| `ExplicitlyProtectedDisk` | Explicitně označený disk je odmítnut pro obě role |
| `BusyVolumeBlocksTarget` | Disk s otevřenými svazky nelze použít jako cíl |
| `ContainerDiskProtected` | Dynamický disk / Storage Spaces je chráněn |
| `OfflineAndReadOnly` | Offline a read-only disk nelze použít jako cíl |
| `SameDiskDetection` | Rozpoznání totožného disku podle sériového čísla a velikosti |
| `PairValidation` | Validace dvojice hlásí všechny důvody najednou |

### 2.7 Plánovač (18 testů)

| Test | Co ověřuje |
|---|---|
| `ShrinkWindowsToSmallerDisk` | **Hlavní scénář**: 500 GB → 240 GB, 120 GB dat. Zachován počet, pořadí i typy oddílů; systémový oddíl přes WIM; ostatní chytrou kopií |
| `ShrinkWithoutTemporaryStorageRefused` | Zmenšení bez dočasného úložiště → odmítnuto s vysvětlením |
| `EstimatedWriteVolumeForWim` | Odhad zápisu vychází z dat, ne z velikosti oddílu |
| `InsufficientTemporarySpaceRefused` | Nedostatek místa pro WIM → odmítnuto |
| `FullSizeTargetUsesIntelligentCopy` | Dost velký cíl → beze zmenšení, bez dočasného úložiště, původní offsety |
| `TooSmallTargetRefused` | Příliš malý cíl → odmítnuto |
| `UnshrinkableFilesystemRefused` | 300GB FAT32 nelze zmenšit → odmítnuto, ne zahozeno |
| `MbrBootPlan` | MBR disk → BIOS, `bootsect`, aktivní příznak, typ oddílu |
| `DataOnlyDisk` | Disk bez OS → jen data, žádné kroky zavádění |
| `LayoutInvariants` | Oddíly se nepřekrývají, začínají za 1 MiB, jsou zarovnané, končí uvnitř disku |
| `DifferentSectorSizes` | Zdroj 4 KiB sektory, cíl 512 B – plán v bajtech |
| `EmptySourceRefused` | Prázdná tabulka → odmítnuto |
| `ContainerSourceRefused` | Kontejner na zdroji → odmítnuto |
| `OccupiedTargetRefused` | Neprázdný cílový disk → odmítnuto s vysvětlením, že nástroj nemaže |
| `UncertainTargetRefused` | Nejednoznačný cílový disk → odmítnuto |
| `DirtyNtfsShrinkRefused` | Nečistý NTFS + zmenšení → odmítnuto s použitelným řešením |
| `WindowsWithoutEspRefused` | GPT s Windows bez ESP → odmítnuto (nešel by připravit UEFI) |
| `BootUnavailableRefused` | Chybí `bcdboot` → odmítnuto, netvrdíme bootovatelnost |

### 2.8 Průběh v terminálu (5 testů)

| Test | Co ověřuje |
|---|---|
| `FullLineFits` | Řádek průběhu se vejde do 78 znaků a obsahuje procenta i strategii |
| `OptionalPartsDropped` | Při nedostatku místa se zahazuje nejdřív odhad času, ne procenta |
| `ExtremeLabelTruncated` | I extrémně dlouhý popisek zůstane v limitu |
| `FractionClamped` | Záporná hodnota, >100 % a NaN se ošetří |
| `NarrowTerminal` | Funguje i na 60znakovém terminálu |

### 2.9 End-to-end nad obrazovými disky (10 testů)

Tyto testy proženou celý pipeline – analýzu, plán, kopírování i ověření – nad
skutečnými soubory, a porovnávají bajty.

| Test | Co ověřuje |
|---|---|
| `RawCloneEndToEnd` | Sektorová kopie dvou oddílů MBR disku; zdroj otevřen jen pro čtení; výsledná tabulka platná |
| `NtfsAllocationCloneEndToEnd` | NTFS: obsazené clustery bajtově shodné, **volné clustery na cíli zůstaly nedotčené** (0xFF), zapsáno méně než velikost oddílu |
| `DryRunWritesNothing` | Kontrolní režim neotevře žádný disk pro zápis a hash cíle se nezmění |
| `FingerprintMismatchAborts` | Změna cílového disku po vytvoření plánu → ukončení **bez jediného zápisu** |
| `ProtectedTargetRefused` | Chráněný cílový disk → odmítnuto, disk se ani neotevře pro zápis |
| `WimUnavailableFailsClearly` | Zmenšení bez DISM → jasná chyba + upozornění, že cíl je nepoužitelný |
| `MbrBootCodeIsPreserved` | Zaváděcí kód MBR a signatura disku se přenesou ze zdroje |
| `OccupiedTargetRefusedByExecutor` | Cíl s oddíly je odmítnut i tehdy, když plánovač kontrolu nedostal – cíl zůstane nedotčen |
| `CancellationReportsIncompleteTarget` | Přerušení Ctrl+C → jasné hlášení, že cíl je nepoužitelný |
| `AuditLogRedaction` | Obnovovací klíč se v protokolu neobjeví |

---

## 2.10 Ověření chování z příkazové řádky

Nad rámec testů proběhlo ověření přes skutečné CLI v simulačním režimu:

| Scénář | Výsledek |
|---|---|
| Migrace na prázdný cílový disk | návratový kód **0**, 1,12 GiB zapsáno za 9 s, SHA-256 shoda |
| Druhý běh na tentýž (nyní již naplněný) cíl | návratový kód **6**, výpis 4 nalezených oddílů, cílový disk nezměněn |
| Interaktivní volba chráněného disku jako zdroje | odmítnuto, znovu se ptá, vypíše důvod |
| Potvrzení jiným slovem než `ANO` | migrace se nespustí, návratový kód 0 |

## 2.11 Windows API a bezpečnost převzetí (18 nových testů)

Testy `WindowsSafetyProtocolTests` běží i na Linuxu: ověřují skutečný formát
`SET_DISK_ATTRIBUTES` (40 bajtů, Version=16), `GET_DISK_ATTRIBUTES`,
bitovou hodnotu READ_ONLY=0x2 vs. OFFLINE=0x1, jejich masky,
`STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR` pro 512e/4Kn a správnou úpravu
GUID cest pro `CreateFile`.

Další testy na falešných svazcích ověřují **držení všech zámků**
až do skutečného potvrzení read-only, odmítnutí neznámých/offline atributů,
obnovu po selhání druhého zámku, rollback částečně aplikovaného IOCTL
a zachování původních písmen jednotek.

Tyto testy nevolají Windows kernel: pro `FSCTL_*` a skutečný VHDX
je pořád nutný integrační test na Windows.
GitHub Actions navíc sestavuje `win-x64` a kontroluje syntaxi PowerShell
testovacího scénáře, **nikoli jeho vykonání**.

---

## 3. Test integrity zdrojového disku

Nad rámec automatických testů proběhlo ověření přes reálné CLI:

```
source hash before: d5a166175fc0e06b83b4a986a529611b565f1fe31314a61e0b48c471795cebbe
source hash after:  d5a166175fc0e06b83b4a986a529611b565f1fe31314a61e0b48c471795cebbe
RESULT: source disk byte-identical (never written)
```

Zdrojový disk byl po celé migraci (analýza, kopírování, ověřování) **bajtově shodný**.

---

## 4. Simulovaná migrace „Windows“ disku

Simulovaná sada: zdrojový disk 6 GiB (ESP 64 MiB, MSR 16 MiB, Windows 4 GiB s 1 GiB dat,
WinRE 256 MiB) a cílový disk 8 GiB.

```
   #  Oddíl                Systém   Zdroj     Cíl     Způsob přenosu
   1  EFI system partition FAT32    64 MiB    64 MiB  sektorová kopie 1:1
   2  Microsoft reserved   neform.  16 MiB    16 MiB  vytvořit prázdný oddíl
   3  Windows              NTFS     4 GiB     4 GiB   chytrá kopie NTFS (jen obsazené shluky)
   4  Windows Recovery     NTFS     256 MiB   256 MiB chytrá kopie NTFS (jen obsazené shluky)

  Celkem se zapíše: 1.12 GiB   (namísto 4.33 GiB obsahu oddílů)

  OK   Oddíl #1 přenesen (64 MiB).
  OK   Oddíl #2 připraven.
  OK   Oddíl #3 přenesen (1.01 GiB).
  OK   Oddíl #4 přenesen (48 MiB).
  OK   Oddíl #1: SHA-256 shoda pro 64 MiB
  OK   Oddíl #3: SHA-256 shoda pro 1.01 GiB přenesených dat
  OK   Oddíl #4: SHA-256 shoda pro 48 MiB přenesených dat
  OK   Tabulka oddílů na cílovém disku odpovídá plánu (4 oddílů, Gpt).
  OK   Zapsáno 1.12 GiB za 0:09.
```

Chytré kopírování zapsalo **1,12 GiB místo 4,33 GiB** a kontrola SHA-256 prošla.

### Scénář se zmenšením

Stejný zdroj, cílový disk 2,5 GiB:

```
   1  EFI system partition FAT32    64 MiB   64 MiB  sektorová kopie 1:1
   2  Microsoft reserved   neform.  16 MiB   16 MiB  vytvořit prázdný oddíl
   3  Windows              NTFS     4 GiB    2.17 GiB obraz WIM (DISM) – umožní zmenšení
   4  Windows Recovery     NTFS     256 MiB  256 MiB chytrá kopie NTFS (jen obsazené shluky)

  !  Oddíl #3 (Windows) bude zmenšen z 4 GiB na 2.17 GiB.
  !  Dočasný obraz WIM: potřeba přibližně 1.3 GiB
```

Počet, pořadí a typy oddílů zůstaly zachovány, zmenšil se jen ten, který to umožňuje.

---

## 5. Nalezené a opravené chyby

Testy a simulované běhy odhalily tyto skutečné chyby – všechny jsou opravené:

| # | Chyba | Projev | Oprava |
|---|---|---|---|
| 1 | Plánovač nezmenšoval | Po zmenšení jednoho oddílu zůstaly následující na původních offsetech, takže se rozvržení nikdy nevešlo | `SimulateLayout` posouvá „ocas“, jakmile se předchozí oddíl změnil |
| 2 | Zaváděcí kód MBR se ztrácel | Čtení boot kódu z cíle **po** vymazání tabulky → samé nuly → BIOS by nenastartoval | Boot kód se přebírá ze **zdrojového** disku |
| 3 | Odhad zápisu u WIM | `Celkem se zapíše` ukazovalo velikost oddílu (4 GiB) místo dat (1 GiB) | `EstimatedWriteBytes` počítá s obsazenými daty |
| 4 | Pomalé fragmentované kopírování | 2:49 na 1 GiB – pro každý fragment se alokoval 4 MiB buffer | Buffer se velikostí přizpůsobuje transferu + tolerance mezer |
| 5 | Velikost volných shluků v NTFS summary | `UsedBytes` vycházelo jako celý svazek | Alokační mapa se čte před sestavením summary |
| 6 | Zápisová rukojeť cíle před ověřením otisku | Cílový disk se otevíral pro zápis ještě před ověřením identity | Otisk se ověřuje přes handle **pouze pro čtení**, write-handle vzniká až potom |
| 7 | Příliš velké okrajové oblasti | U malých NTFS oddílů se „chytrá“ kopie rovnala plné kopii | Okrajové oblasti se škálují podle velikosti oddílu |
| 8 | Kontrola po WIM neodpovídala kopii | Verifikace používala jinou množinu clusterů než kopie | Jediný zdroj pravdy `NtfsCloneRanges` pro kopii i kontrolu |
| 9 | Zaváděcí kód v testu | Test porovnával pole před zápisem signatury | Test porovnává zdroj s cílem |

---

## 5b. Připravený testovací scénář pro Windows (nespuštěn)

V `tools/windows-vm-test/` je připravený PowerShell skript
`Invoke-DiskMigrationTest.ps1`, který na skutečném Windows vytvoří dva VHDX disky,
naplní zdroj daty, otestuje odmítnutí neprázdného cíle, projde `--dry-run`,
provede migraci a porovná SHA-256 všech souborů.

**Tento skript nebyl spuštěn** – v prostředí, kde nástroj vznikl, žádný Windows
nebyl. Byla ověřena pouze syntaxe (`PowerShell Parser`) a to, že používá existující
parametry příkazové řádky. První spuštění je na vás; postup je v
[tools/windows-vm-test/README.md](../tools/windows-vm-test/README.md).

## 6. Co je ještě potřeba ověřit na skutečném hardware

Toto jsou **jediné** oblasti, které v tomto prostředí ověřit nešlo, a doporučený postup:

### 6.1 Přístup k fyzickým diskům

```
1. Spustit AfrowaveDiskMigrator.exe jako správce na počítači s Windows 10/11.
2. Ověřit, že se vypíše seznam disků se správnými modely, sériovými čísly a velikostmi.
3. Ověřit, že systémový disk je označen jako CHRÁNĚN a nelze jej vybrat.
```

### 6.2 Příprava: virtuální počítač

Nejbezpečnější první test: dva **virtuální disky** (VHDX) připojené k virtuálnímu počítači.

```
1. Vytvořit zákaznickou VM s Windows na disku A (60 GB), připravit data a vypnout ji.
2. Vytvořit samostatnou servisní VM s vlastním Windows na disku C.
3. Připojit vypnutý disk A a prázdný disk B (40 GB) do servisní VM.
4. CopyMe spustit **v servisní VM**, kde je C chráněný systém, A offline zdroj
   (ve smyslu neběžící instalace Windows, nikoli atribut DISK_ATTRIBUTE_OFFLINE) a B cíl.
5. Projít dry-run, migrovat A → B a ověřit navrácení stavu zdroje.
6. Vypnout servisní VM, odpojit A; připojit do původní VM pouze B a ověřit boot.
```

Tím se ověří celý řetězec včetně `DISM`, `bcdboot` a startu systému – a přitom
zůstáváme ve virtuálním prostředí, kde selhání neznamená ztrátu dat.

### 6.3 Režim UEFI a BIOS

Otestovat oba: jednou na VM s UEFI (GPT + ESP), jednou s BIOS (MBR + aktivní oddíl).
Ověřit, že cílový systém nastartuje a že obnova systému (WinRE) je zaregistrovaná
(`reagentc /info /target C:\Windows`).

### 6.4 Co při tom sledovat

* že se zdrojový disk v Průzkumníkovi ani ve Správě disků nezmění,
* že po ukončení nezůstane zdrojový disk omylem read-only (nástroj atribut ruší,
  při pádu pomůže odpojení a připojení disku),
* že na cíli zůstala stejná písmena jednotek (díky zachování identifikátorů),
* že v protokolu nejsou žádné šifrovací klíče.

### 6.5 První nasazení

Doporučený postup při prvním použití v praxi:

1. Spustit `Invoke-DiskMigrationTest.ps1` – ověří datovou část na VHDX.
2. Projít scénář s virtuálním počítačem – ověří, že cílový disk nastartuje.
3. `--dry-run` na skutečných discích – ukáže plán bez jakéhokoli zápisu.
4. Migrace na **disk, o jehož obsah nestojíte**.
5. Až poté naostro.

---

## 7. Opakování testů

```bash
./build.sh test              # všech 95 testů
./build.sh test Planner      # jen testy, jejichž název obsahuje "Planner"
./build.sh test Ntfs         # jen NTFS testy
```

Návratový kód je počet neúspěšných testů, takže lze snadno použít v CI.
