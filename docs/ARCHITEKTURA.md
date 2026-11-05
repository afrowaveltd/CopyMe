# Architektura a odůvodnění rozhodnutí

Tento dokument popisuje, jak je nástroj postavený a **proč** byly zvoleny konkrétní
postupy. Cílem bylo minimum závislostí, maximální bezpečnost dat a možnost všechno
kromě samotného přístupu k hardwaru otestovat automaticky.

> Stav: **experimentální verze 1.0**. Vrstva pro skutečný hardware čeká na ověření
> na Windows – viz [TESTOVANI.md](TESTOVANI.md).

---

## 1. Volba technologie

**C# / .NET 10, soběstačný jediný EXE pro `win-x64`.**

| Požadavek | Jak ho .NET splňuje |
|---|---|
| Jediný EXE bez instalace | `PublishSingleFile` + `SelfContained` + `PublishTrimmed` → ~11 MB, žádný runtime na cíli |
| Přístup k fyzickým diskům | P/Invoke na `kernel32` (`CreateFileW`, `DeviceIoControl`, `ReadFile`…) – přesně to, co dělají všechny nástroje pro práci s disky |
| Využití systémových nástrojů Microsoftu | `Process` + `Dism.exe`, `bcdboot.exe`, `bootsect.exe`, `reagentc.exe` |
| Testovatelnost bez fyzického hardwaru | .NET běží i na Linuxu → celý pipeline lze spustit nad obrazovými soubory |
| Bez komerčních licencí | .NET je MIT, žádné další balíčky – **nula** závislostí třetích stran |
| Snadná údržba | silně typovaný jazyk, `Nullable enable`, žádná reflexe, žádné generování kódu |

Zvažované alternativy:

* **C++ / Win32** – nejmenší binárka, ale ruční správa paměti u kódu, který přepisuje
  disky, je zbytečné riziko a testování by vyžadovalo reálný hardware nebo VM.
* **Python + pywin32** – vyžaduje interpret na cíli, což odporuje požadavku „jediný EXE“.
* **Klonování přes `dd`/`ddrescue`** – neumí zmenšovat oddíly ani obnovit zavádění.

Rozhodující argument: .NET umožňuje **oddělit logiku od hardwaru** tak, že všechna
rozhodnutí (co a jak kopírovat) jsou čisté funkce testované na Linuxu nad obrazovými
soubory, a na Windows zůstává jen tenká vrstva pro skutečné I/O.

---

## 2. Vrstvy

```
┌──────────────────────────────────────────────────────────┐
│ Afrowave.DiskMigrator.Cli                                │
│   české terminálové UI, potvrzení, průběh                │
└───────────────────────────┬──────────────────────────────┘
                            │ používá rozhraní
┌───────────────────────────▼──────────────────────────────┐
│ Afrowave.DiskMigrator.Core            (bez OS závislostí)│
│   Partitioning   GPT / MBR čtení, zápis, CRC32, validace │
│   Filesystems    detekce FS, čtečka NTFS ($MFT, $Bitmap) │
│   Analysis       analýza disku, rozpoznání OS            │
│   Safety         politika ochrany, otisky disků          │
│   Planning       plánovač migrace                        │
│   Execution      kopírovací engine, vykonavatel, verifikace│
└───────────────────────────┬──────────────────────────────┘
                            │ IDiskPlatform
        ┌───────────────────┴────────────────────┐
┌───────▼──────────────────┐      ┌──────────────▼───────────────┐
│ Platform.Windows         │      │ Platform.Image               │
│  \\.\PhysicalDriveN      │      │  obrazové soubory            │
│  svazky, DISM, bcdboot   │      │  (testy + simulační režim)   │
└──────────────────────────┘      └──────────────────────────────┘
```

Klíčové rozhraní je `IDiskPlatform`. Díky němu se produkční kód nijak neliší od
testovaného – testy používají stejný `MigrationExecutor`, stejný plánovač a stejný
kopírovací engine, jen nad jiným „hardwarem“.

---

## 3. Bezpečnostní model

### 3.1 Ochrana disků servisního počítače

Politika (`DiskSafetyPolicy`) je **čistá funkce**: dostane fakta o prostředí
(`SafetyEnvironment`) a vrátí verdikt pro každý disk. Proto je plně pokryta testy.

Disk je chráněn, pokud:

* na něm leží svazek s `%SystemDrive%` nebo `%SystemRoot%` běžícího systému,
* je na něm spuštěn tento EXE,
* obsahuje `pagefile.sys`, `hiberfil.sys` nebo `swapfile.sys`,
* obsahuje živou instalaci Windows (`\Windows\System32`),
* ho platforma označí jako kontejner (dynamický disk, Storage Spaces),
* ho uživatel nebo konfigurace výslovně označí.

Chráněný disk se **zobrazí** v seznamu, ale nelze ho vybrat – ani jako zdroj, ani jako
cíl. Uživatel vidí důvod, takže rozumí, proč tam je.

„Disk, ze kterého nástroj běží“ se určuje z `Environment.ProcessPath` → kořen svazku →
`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` → číslo fyzického disku. Nepoužívá se písmeno
jednotky jako identita.

### 3.15 Převzetí již připojeného zdrojového disku

V praxi je zákazníkův disk k servisnímu počítači připojený dřív, než technik spustí
nástroj – Windows jej rozpozná a přiřadí mu písmena (např. `E:` a `F:`). Nástroj
s tím počítá a **zdroj si sám převezme**; technik nemusí nic odpojovat ani měnit.

Proč ne prostě „nastavit disk jako read-only“: připojený svazek má **živý souborový
systém s nevyřízenými zápisy** (NTFS žurnál, USN, `System Volume Information`).
Kdyby se disk přepnul do režimu jen pro čtení jako první, tyto zápisy by selhaly
uprostřed a na zákazníkově disku by zůstal nečistý souborový systém. Proto se **nejdřív
vyprázdní a odpojí, a teprve pak chrání.**

Skutečné pořadí (pro každý svazek se souborovým systémem):

| # | Operace | Proč právě tady |
|---|---|---|
| 1 | `CreateFile(\\.\GLOBALROOT\Device\HarddiskVolume… )` resp. `\\?\Volume{GUID}\`, `GENERIC_READ \| GENERIC_WRITE`, **share = 0** | Výhradní otevření. Selže, pokud kdokoli drží otevřený soubor – přesně tu pojistku chceme. |
| 2 | `FSCTL_LOCK_VOLUME` | Uzamkne svazek. Dokumentovaně selže, dokud má někdo otevřené soubory. |
| 3 | `FSCTL_ALLOW_EXTENDED_DASD_IO` | Umožní následné surové I/O celého disku. |
| 4 | `FSCTL_DISMOUNT_VOLUME` | Vyprázdní nevyřízené zápisy do zařízení a odpojí souborový systém. |
| 5 | `FSCTL_UNLOCK_VOLUME`, zavření handle | Zámek se uvolní; odpojený svazek zůstává odpojený do dalšího přístupu. |
| 6 | `IOCTL_DISK_SET_DISK_ATTRIBUTES` s `DISK_ATTRIBUTE_READ_ONLY`, `Persist = 0` | Teprve teď, když už žádný zápis neběží. |
| 7 | Ověření: pokus o otevření s `GENERIC_WRITE` musí selhat | Bez ověření bychom jen věřili, že ochrana platí. |

Volumes bez souborového systému (MSR, zamčený BitLocker) se přeskakují – není co
vyprázdnit.

**Co nástroj záměrně nedělá:**

* **Nemaže písmena jednotek.** Po odpojení Windows svazek při dalším přístupu připojí
  znovu, a protože je disk read-only, bude to připojení jen pro čtení. Díky tomu
  není po skončení co obnovovat.
* **Nedrží zámky po celou migraci.** Držený zámek by znemožnil připojení svazku, které
  potřebujeme pro rozpoznání operačního systému i pro `DISM`. Skutečnou zárukou je
  atribut read-only na disku; uvolněný zámek tedy nic neztrácí – a navíc po pádu
  procesu nezůstane viset.
* **Nemění `NoAutoMount` v registru** servisního počítače.
* **Neukončuje cizí procesy.** Když svazek nelze uzamknout, nástroj to řekne a nabídne
  opakování.

### 3.16 Navrácení disku Windows

V `finally` – tedy po úspěchu, po chybě i po přerušení:

1. Zruší se read-only příznak, **ale jen když jej nástroj sám nastavil**. Disk, který
   byl read-only už před migrací, zůstane read-only.
2. Ověří se, že disk je skutečně zase zapisovatelný.
3. „Klepnutím“ na původní mount pointy se svazky nechají znovu připojit, aby je
   technik viděl v Průzkumníkovi.
4. Na zdrojový disk se přitom nikdy nezapisuje – mění se jen atributy a stav
   připojení.

Když úklid selže, nástroj vypíše konkrétní nápravu (otevřít jednotku ručně,
restartovat počítač).

**Pád procesu nebo výpadek napájení:** `finally` se nemusí provést. Proto nástroj
během migrace **nedrží žádné zámky** (operační systém je uvolní s ukončením procesu)
a read-only atribut nastavuje s `Persist = 0`, takže **restart počítače jej sám zruší**.
Nejhorší následek pádu je tedy dočasně read-only disk do restartu.

### 3.17 Co nástroj nemůže ovlivnit

Windows mohl na zákazníkův disk zapisovat **už před spuštěním nástroje** – při
automatickém připojení svazků (replay NTFS žurnálu, aktualizace `System Volume
Information`, časová razítka). Nástroj to nemůže vzít zpět ani zjistit v plném
rozsahu. Zaručuje pouze to, že **od okamžiku převzetí** na zdroj nezapíše.

### 3.2 Zdrojový disk pouze pro čtení

Tři nezávislé vrstvy:

1. **Handle**: `CreateFile` se žádostí `GENERIC_READ` – bez práva zápisu. Rozhraní
   `IBlockReader` ani nemá metodu pro zápis; zapisovat lze jen přes `IBlockDevice`,
   které zdroj nikdy nedostane.
0. **Ještě před analýzou.** Zdroj se převezme a přepne do read-only režimu **dřív, než
   se do něj cokoli podívá** (viz 3.15). Rozpoznání operačního systému totiž znamená svazky připojit; kdyby
   byl disk zapisovatelný, běžící Windows by při připojení mohl přehrát NTFS žurnál
   nebo aktualizovat `System Volume Information` – tedy změnit zákazníkův disk ještě
   před začátkem migrace. Pokud read-only režim nelze nastavit, nástroj obsah svazků
   vůbec nezkoumá (a upozorní, že operační systém nemusí rozpoznat).

2. **Zařízení**: `IOCTL_DISK_SET_DISK_ATTRIBUTES` s `DISK_ATTRIBUTE_READ_ONLY`
   a **`Persist = 0`**. Windows pak odmítne zápis i z jiných procesů, ale atribut
   platí jen pro tuto relaci. Kdyby nástroj spadl nebo vypadl proud, restart jej
   sám zruší – zákazníkův disk nezůstane „divně“ nastavený. Po dokončení se atribut
   zruší explicitně.
3. **Testy**: end-to-end testy hashují zdrojový obraz před a po migraci a porovnávají;
   navíc platforma zaznamenává každý pokus o otevření disku pro zápis.

### 3.3 Opětovné ověření před destruktivní operací

Plán se vytváří nad stavem v okamžiku analýzy. Mezi zobrazením plánu a prvním zápisem
může technik disky přepojit. Proto:

`DiskFingerprint` = SHA-256 z (číslo disku, cesta zařízení, sériové číslo, velikost,
velikost sektoru, otisk prvních a posledních 64 KiB disku – tedy tabulky oddílů).

Před prvním zápisem se otisk **znovu spočítá přes handle otevřený pouze pro čtení**.
Při neshodě se ukončí bez jediného zápisu a vypíše se, co se změnilo.

Write-capable handle na cílový disk vzniká až po tomto ověření – a otevírá se
aktuálně nalezený disk, nikoli ten z doby plánování. Po otevření se otisk spočítá
**ještě jednou přes zapisovací handle**; tím je pokryta i situace, kdy se po
přepojení USB změnilo pořadí `\\.\PhysicalDriveN`.

### 3.4 Odmítnutí místo riskování

Nástroj odmítne migraci, pokud:

* cílový disk není prázdný nebo je jeho stav nejednoznačný,
* se obsah nevejde ani po maximálním zmenšení (a vypíše, který oddíl to blokuje),
* je na zdrojovém disku kontejner (dynamický disk / LDM / Storage Spaces),
* GPT nebo MBR neprojde validací (CRC, překryvy, rozsahy),
* je cílový disk offline, read-only, zaneprázdněný nebo menší než 1 GiB,
* chybí nástroj DISM pro potřebné zmenšení,
* nelze zaručit zachování funkčnosti (např. zmenšení FAT32 oddílu),
* se má zmenšovat NTFS svazek, který není čistě odpojen (read-only připojení
  u „dirty“ NTFS obvykle selže, takže by migrace skončila napůl zapsaným cílem),
* má zdroj Windows na GPT, ale chybí EFI System Partition (UEFI nelze připravit),
* by bylo potřeba obnovit zavádění, ale `bcdboot`/`bootsect` nejsou k dispozici.

Každé odmítnutí obsahuje konkrétní důvod a – pokud existuje – použitelné řešení.

---

## 4. Tabulky oddílů

Vlastní implementace, žádná knihovna.

### Čtení

* **GPT**: podpis `EFI PART`, ověření CRC32 hlavičky (s vynulovaným polem CRC)
  i pole položek, kontrola `MyLBA`, `FirstUsableLBA`/`LastUsableLBA`, rozsahů a překryvů.
  Při poškození primární hlavičky se **použije záložní kopie z konce disku** a vydá se
  varování.
* **MBR**: podpis `0x55AA`, 4 primární položky, průchod řetězcem rozšířených oddílů
  (EBR) s detekcí smyčky a počítadlem logických oddílů.
* **Hybridní MBR** (protective + klasické položky) se rozpozná a ohlásí.

### Zápis

* GPT: nová hlavička + pole položek + záložní kopie na konci disku, ochranný MBR.
  Vše se počítá od začátku, nic se nepřebírá.
* MBR: **zaváděcí kód (446 bajtů) se přebírá ze zdrojového disku** – bez něj by BIOS
  nenastartoval. Dále se přebírá signatura disku a příznak „aktivní“. Logické oddíly
  se zapíší jako řetězec EBR.
* Před zápisem se vymažou oblasti, kde dříve mohla být tabulka oddílů.

### Zarovnání a velikosti

* Offsety se drží v **1 MiB** hranicích, pokud to jde; dokud se předchozí oddíly
  nezměnily, zachovává se **původní offset ze zdroje** (důležité pro BIOS a písmena jednotek).
* Pokud se oddíl zmenšuje, všechny následující se posunou – simulace rozvržení s tím
  počítá (`SimulateLayout`). Toto byl reálný bug odhalený testy: bez posouvání
  „ocasu“ se zmenšení nikdy neprojevilo.
* Velikosti kopírovaných oddílů zůstávají **přesně shodné** se zdrojem, aby velikost
  svazku NTFS/FAT odpovídala původní.
* Různá velikost sektoru (512 B vs 4 KiB) mezi zdrojem a cílem je podporovaná –
  plánuje se v bajtech, nikoli v LBA.

---

## 5. Čtečka NTFS

Klíčová komponenta pro chytré kopírování. Je **pouze pro čtení** a záměrně malá:

1. **Boot sector** – velikost sektoru, shluků, poloha MFT a velikost záznamu.
2. **Tabulka MFT** – runlist datového atributu záznamu 0 (parsování „mapping pairs“).
3. **Fixupy** – ověření update sequence u každého záznamu; poškozená metadata se
   **odmítnou** (oddíl se pak kopíruje sektorově), nikdy se neopravují.
4. **`$Volume`** – popisek svazku, verze a příznak „dirty“.
5. **`$Bitmap`** – alokační mapa, ze které se zjistí obsazené shluky a obsazené bajty.

Z `$Bitmap` se počítá obsazené místo, takže plánovač **nepotřebuje svazek připojovat**.
Díky tomu funguje analýza i u svazku, který Windows odmítne připojit.

Poloha MFT se ověřuje dvěma kandidáty (dokumentovaný offset a záložní varianta) a přijme
se jen tehdy, když na daném místě vznikne validní záznam `FILE`. Když se to nepodaří,
oddíl se kopíruje sektorově – nikdy se neodhaduje.

---

## 6. Strategie kopírování

| Strategie | Podmínka | Princip |
|---|---|---|
| `NtfsAllocationClone` | NTFS, vejde se v původní velikosti | Přenese boot sektory, okrajové oblasti a všechny obsazené shluky na **shodné offsety**. Metadata zůstanou bajtově identická (MFT, ACL, hardlinky, ADS, EFS, sparse). |
| `WimReimage` | NTFS, nutné zmenšení | `DISM /Capture-Image` → formát zmenšeného svazku → `DISM /Apply-Image`. |
| `RawClone` | ostatní (FAT32, exFAT, BitLocker, neznámé) | Bajt po bajtu. |
| `RecreateEmpty` | Microsoft Reserved | Nemá obsah, jen se znovu vytvoří. |

### Proč ne rovnou `DISM` pro všechno

`NtfsAllocationClone` je **věrnější** (bajtově shodná metadata, žádná reinterpretace
souborů) a stejně rychlý, protože také přenáší jen obsazená data. `DISM` se použije
teprve tehdy, když je potřeba změnit velikost svazku – tam je to naopak jediná
bezpečná cesta.

### Proč ne vlastní změna velikosti NTFS

Změna velikosti NTFS znamená přeskládat shluky a přepsat `$MFT`, `$Bitmap` a boot
sector. To je přesně `ntfsresize`. Implementovat to znovu by znamenalo vystavit data
riziku chyby v kódu, který nemá jak být otestovaný na reálných datech. `DISM` je
oficiální nástroj Microsoftu, který tuto práci dělá při každé instalaci Windows.

### Okrajové oblasti a tolerance mezer

* První a poslední část oddílu se kopíruje vždy, bez ohledu na alokační mapu – tam
  leží boot sector a jeho záložní kopie. Velikost se přizpůsobuje (u malých oddílů
  by 4 MiB znamenaly kopii celého oddílu).
* Mezery do 8 volných shluků (32 KiB) se „spolknou“. Ušetří to tisíce malých
  transferů a je to levnější než zápis 32 KiB navíc. Volné shluky na cíli zůstávají
  volné (kopíruje se i alokační mapa), takže se nic nezmění.

### Výkon

* Velikost bufferu se přizpůsobuje skutečné délce transferu. Původní verze alokovala
  maximální blok pro každý fragment, což u fragmentovaných dat znamenalo
  262 144 alokací po 4 MiB. Po opravě: 1,12 GiB za 9 s místo 2:49.
* Postupné načítání a zápis po 4 MiB, průběh hlášen max. 8× za sekundu.

---

## 6b. Průběh, přerušení a chyby

* Zrušení (Ctrl+C) prochází jako `CancellationToken` až do spouštěných nástrojů:
  `DISM` a `bcdboot` se při přerušení ukončí, neběží dál na pozadí.
* Po přerušení nástroj výslovně uvede, že cílový disk může být neúplný a nesmí se
  použít. Obnovování přerušených migrací záměrně neexistuje – bylo by to složitější
  než migraci prostě zopakovat.
* Chyba čtení nebo zápisu (odpojený kabel, vypadlé napájení) skončí výjimkou
  s offsetem a popisem chyby Win32; výsledek vždy říká, že cíl je nepoužitelný.
* Neúspěch se nikdy netváří jako úspěch: odmítnutí mají vlastní nenulové návratové
  kódy (viz README), takže automatizace je rozezná.

## 7. Zavádění systému

Nástroj **nepřevádí** práci, kterou už Microsoft umí:

| Režim | Postup |
|---|---|
| **UEFI** | `bcdboot <win>:\Windows /s <esp>: /f UEFI /v` – vytvoří nový BCD odkazující na nové oddíly |
| **BIOS** | `bcdboot … /f BIOS` + `bootsect /nt60 <sys>: /force /mbr` – obnoví i zaváděcí kód MBR |
| **WinRE** | `reagentc /setreimage /path <rec>:\Recovery\WindowsRE /target <win>:\Windows` |

Rozhodnutí o režimu vychází z tabulky oddílů: GPT + ESP → UEFI, jinak BIOS.
ESP se kopíruje sektorově (je malý a obsahuje `bootmgfw.efi` i BCD), takže zůstane
bajtově shodný. ESP se rozpozná podle typu oddílu, a pokud ten nesouhlasí, tak
i podle obsahu (`\EFI\Microsoft\Boot\bootmgfw.efi`).

**Nástroj neslibuje bootovatelný výsledek, který neumí připravit.** Proto plán
odmítne migraci, když:

* zdroj má Windows na GPT, ale chybí EFI System Partition (UEFI bez ESP nejde),
* je potřeba obnovit zavádění, ale `bcdboot`/`bootsect` na počítači nejsou.

Bez těchto pojistek by vznikl disk s daty, který ale nenastartuje – přesně to,
čemu se má nástroj vyhnout.

### Zachování identifikátorů

Cílový disk přebírá **GPT disk GUID a GUID oddílů** (resp. MBR signaturu), aby
existující položka v NVRAM firmwaru i písmena jednotek ve Windows zůstaly platné.
Aby přitom během migrace nevznikly kolize (zdroj i cíl připojené současně), probíhá
zápis tabulky **dvakrát**:

1. nejprve s dočasnými náhodnými identifikátory → bezpečné kopírování a připojování,
2. po odpojení všech svazků se přepíší původní identifikátory → dvě malé operace.

Firmware servisního počítače se **nemění** – nástroj do NVRAM nezapisuje.

---

## 8. Šifrování

* BitLocker se rozpozná podle typu oddílu i podle podpisu `-FVE-FS-` v boot sektoru.
* **Sektorová kopie zašifrovaného oddílu funguje** – na novém disku se odemkne stejným
  klíčem, protože jsou data i metadata shodná.
* Zmenšit zašifrovaný oddíl nástroj neumí a **odmítne to** s vysvětlením.
* Nástroj šifrování neobchází a klíče si nevyžaduje.
* Protokol prochází redakcí (`LogRedaction`), která nahradí jakýkoli řetězec
  připomínající 48místný obnovovací klíč. I kdyby se v budoucnu zadávání klíčů
  doplnilo, klíč se do protokolu nedostane.

---

## 9. Ověřování

* **Data**: během kopie se zapisuje, poté se cíl **znovu přečte** a porovná se
  zdrojem pomocí SHA-256. U chytré kopie se porovnávají přesně ty rozsahy, které se
  kopírovaly (`NtfsCloneRanges` je jediný zdroj pravdy pro kopii i kontrolu).
* **Tabulka oddílů**: po dokončení se z cíle znovu načte a porovná s plánem –
  počet, typy, pozice i identifikátory.
* **Svazek po WIM**: znovu se otevře čtečkou NTFS a zkontroluje se, že obsahuje
  očekávané množství dat.
* **Zavádění**: výstup `bcdboot`/`bootsect`/`reagentc` se kontroluje a chyby se hlásí.
* **Spuštění systému se netestuje** a nástroj to vždy výslovně uvede.

---

## 10. Co záměrně není uděláno

* **Vlastní `ntfsresize`** – příliš velké riziko, viz výše.
* **Zápis do NVRAM servisního počítače** – zakázáno zadáním; boot se řeší nástroji
  Microsoftu na cílovém disku.
* **Automatické mountování svazků** – svazky se připojují jen na dobu nezbytně nutnou,
  vždy na dočasné písmeno, které se pak uvolní.
* **Změna registru servisního počítače** – nástroj nic takového nepotřebuje.

---

## 11. Kde hledat v kódu

| Co | Soubor |
|---|---|
| Ochrana disků | `Core/Safety/DiskSafetyPolicy.cs` |
| Otisky disků | `Core/Safety/DiskFingerprint.cs` |
| GPT / MBR čtení | `Core/Partitioning/PartitionTableReader.cs` |
| GPT / MBR zápis | `Core/Partitioning/PartitionTableWriter.cs` |
| Detekce souborových systémů | `Core/Filesystems/FilesystemDetector.cs` |
| Čtečka NTFS | `Core/Filesystems/NtfsVolume.cs` |
| Rozpoznání OS | `Core/Analysis/DiskAnalyzer.cs` |
| Plánovač | `Core/Planning/MigrationPlanner.cs` |
| Kopírování | `Core/Execution/CopyEngine.cs` |
| Vykonavatel | `Core/Execution/MigrationExecutor.cs` |
| Windows vrstva | `Platform.Windows/WindowsDiskPlatform.cs` |
| DISM | `Platform.Windows/DismWimService.cs` |
| Zavádění | `Platform.Windows/WindowsBootConfigurator.cs` |
| UI | `Cli/Program.cs`, `Cli/ConsoleUi.cs`, `Cli/PlanPresenter.cs` |
