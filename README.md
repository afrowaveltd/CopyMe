# Afrowave DiskMigrator

> ## ⚠️ EXPERIMENTÁLNÍ VERZE 1.0
>
> Nástroj je funkční a má za sebou 113 automatických testů, ale **nebyl spuštěn na
> skutečném počítači s Windows a fyzickými disky**. Vrstva pro hardware, `DISM`,
> `bcdboot` a start cílového systému vyžadují ověření na reálném stroji nebo ve VM.
>
> Než jej poprvé použijete na zákaznická data, projděte
> [testovací scénář](tools/windows-vm-test/README.md).

Malý servisní nástroj pro Windows 10/11, který bezpečně přenese obsah jednoho fyzického
disku na druhý – včetně operačního systému a včetně případů, kdy je cílový disk **menší**
než zdrojový.

Technik spustí jediný EXE z USB flash disku, vybere zdrojový a cílový disk, zkontroluje
navržený postup a potvrdí operaci. Vše ostatní řeší nástroj.

```
  Afrowave DiskMigrator 1.0.0
  Bezpečný přenos obsahu disku na jiný disk
------------------------------------------------------------------------------
  #   Model / zařízení      Velikost  Rozhraní  Stav
  0   Samsung SSD 970      500 GiB   NVMe      CHRÁNĚN
  1   WDC WD5000AZLX        465 GiB   SATA      lze použít
  2   CT240BX500SSD1        223 GiB   SATA      lze použít
```

---

## Co nástroj umí

| Schopnost | Stav |
|---|---|
| Přenos celého disku na stejně velký nebo větší disk | ✅ |
| Přenos na **menší** disk díky chytrému kopírování | ✅ |
| Zachování struktury, pořadí a typů oddílů | ✅ |
| Rozpoznání operačního systému a obnova zavádění Windows | ⚠️ implementováno, neověřeno na HW |
| Sektorová kopie neznámých souborových systémů | ✅ |
| Kontrola přenesených dat (SHA-256) | ✅ |
| Režim kontroly bez zápisu (`--dry-run`) | ✅ |
| Ochrana disků servisního počítače | ✅ |
| Odmítnutí nebezpečné migrace s vysvětlením | ✅ |
| České terminálové UI | ✅ |

Legenda: ✅ ověřeno automatickými testy · ⚠️ kód hotový, čeká na ověření na Windows

## Rychlý start

1. Zkopírujte `AfrowaveDiskMigrator.exe` na USB flash disk.
2. Na servisním počítači klikněte na EXE pravým tlačítkem → **Spustit jako správce**.
3. Zadejte číslo zdrojového disku, potom cílového.
   **Cílový disk musí být bez oddílů** – nástroj oddíly nikdy nemaže.
4. Zkontrolujte navržený plán a napište `ANO`.
5. Po dokončení **odpojte zdrojový disk** a ověřte start systému z cílového disku.

Podrobný postup: [docs/NAVOD.md](docs/NAVOD.md)

## Filozofie

**Uživatel rozhoduje, program kontroluje, a když si není jistý, bezpečně odmítne.**

Program nikdy nevybírá disky sám, nikdy nemaže oddíly, aby si uvolnil cestu, a nikdy
netvrdí, že výsledek bude bootovatelný, pokud to nedokáže připravit.

## Bezpečnostní model

Čtyři pravidla, na kterých nástroj stojí:

0. **Cílový disk vybírá uživatel a musí být bez oddílů.** Nástroj sám žádný disk
   nevybere. Pokud cílový disk obsahuje oddíly, migraci odmítne – a nikdy je nesmaže,
   aby si disk uvolnil. Stejně tak odmítne disk, jehož tabulka oddílů je poškozená
   nebo nejednoznačná (hybridní MBR, zbytky GPT, nečitelná tabulka).

1. **Disky servisního počítače jsou nedotknutelné.** Disk s běžícím systémem, disk
   s hibernačním nebo stránkovacím souborem a disk, ze kterého nástroj běží, se vůbec
   nenabídnou jako zdroj ani jako cíl. Uživatel je vidí, ale označené jako `CHRÁNĚN`
   a s vysvětlením, proč.
   Naopak **offline instalace Windows zákazníka zůstává běžně volitelná jako zdroj** –
   to je ostatně hlavní účel nástroje.

2. **Zdrojový disk je pouze pro čtení.** Nástroj si jej **sám převezme** – technik
   nemusí nic odpojovat, i když Windows už disk připojil s písmeny jednotek. Svazky se
   nejdřív vyprázdní a odpojí (aby na disku nezůstal nečistý souborový systém), teprve
   pak se disk přepne do režimu read-only a změna se ověří. Na zdroj se přitom nikdy
   nezapisuje – ani při zmenšování, ani při kontrole. Po skončení se vše vrátí:
   read-only příznak zmizí (pokud jej nástroj nastavil) a svazky jsou zase dostupné.

3. **Cílový disk se mění až po jednoznačném potvrzení.** Před prvním zápisem se znovu
   ověří identita obou disků (sériové číslo, velikost, velikost sektoru, otisk tabulky
   oddílů) – a to výhradně přes handle otevřený **pouze pro čtení**. Zapisovací handle
   vzniká až po tomto ověření a jeho identita se ověří ještě jednou. Tím se řeší
   i situace, kdy se po přepojení USB změní pořadí `PhysicalDrive`.

Když si nástroj není jistý, operaci odmítne a vysvětlí proč. Nikdy nepředstírá, že
pouhé překopírování dat zajistí funkční systém.

### Návratové kódy

| Kód | Význam |
|---|---|
| 0 | úspěch (nebo uživatel potvrzení odmítl – na disky se nepsalo) |
| 1 | neočekávaná chyba |
| 2 | nepodporovaná platforma |
| 3 | nástroj neběží jako správce |
| 4 | nebyl nalezen žádný disk |
| 5 | zdrojový disk nelze použít |
| 6 | migrace odmítnuta – plán není bezpečně proveditelný |
| 7 | migrace selhala nebo byla přerušena |

## Jak funguje chytré kopírování

Nástroj nekopíruje disk bit po bitu za každou cenu. Pro každý oddíl zvolí nejvhodnější
strategii:

| Strategie | Kdy se použije | Co dělá |
|---|---|---|
| **Chytrá kopie NTFS** | NTFS oddíl, který se vejde ve své velikosti | Přečte alokační mapu NTFS (`$Bitmap`) a přenese jen obsazené shluky plus okrajové oblasti. Metadata zůstanou bajtově shodná, volné místo se nepřenáší. |
| **Obraz WIM (DISM)** | NTFS oddíl, který je nutné zmenšit | Vytvoří WIM obraz pomocí `DISM`, zformátuje zmenšený oddíl a obraz na něj rozbalí. Zachová ACL, hardlinky, reparse pointy i alternativní datové toky. |
| **Sektorová kopie 1:1** | FAT32, exFAT, BitLocker, neznámé systémy | Překopíruje oddíl bajt po bajtu. Jediná možnost, jak zaručit zachování obsahu, kterému nástroj nerozumí. |
| **Vytvořit prázdný oddíl** | Microsoft Reserved (MSR) | Rezervovaný oddíl nemá obsah, znovu se vytvoří ve stejné velikosti. |

Díky tomu se disk s 120 GB dat v 400GB oddílu přenese na 240GB disk, a to i když se
původní oddíly musí zmenšit. Počet, pořadí a typy oddílů zůstávají zachovány.

## Požadavky

* Windows 10 nebo 11, 64 bitů
* Oprávnění administrátora (vyžádá si je manifest EXE)
* .NET **není** potřeba – EXE je soběstačný
* Pro zmenšení oddílu: `Dism.exe` (součást Windows)
* Pro obnovu zavádění: `bcdboot.exe`, `bootsect.exe`, `reagentc.exe` (součást Windows)

## Sestavení ze zdrojových kódů

```bash
./build.sh test     # automatické testy (běží i na Linuxu)
./build.sh build    # vytvoří dist/AfrowaveDiskMigrator.exe
./build.sh all      # testy + build
```

Výsledkem je jediný soubor `dist/AfrowaveDiskMigrator.exe` (~11 MB, soběstačný,
bez instalace, bez závislostí). Sestavení vyžaduje .NET SDK 10; vše ostatní (NuGet
balíčky i SDK cache) zůstává uvnitř repozitáře.

## Struktura projektu

```
src/
  Afrowave.DiskMigrator.Core/            platformně nezávislé jádro
    Partitioning/                        GPT a MBR – čtení, zápis, validace, CRC32
    Filesystems/                         detekce souborových systémů, čtečka NTFS
    Analysis/                            analýza disku a rozpoznání operačního systému
    Safety/                              bezpečnostní politika, otisky disků
    Planning/                            plánovač migrace
    Execution/                           kopírovací engine, vykonavatel, verifikace
  Afrowave.DiskMigrator.Platform.Image/  disky nad obrazovými soubory (testy, simulace)
  Afrowave.DiskMigrator.Platform.Windows/ reálné disky, svazky, DISM, bcdboot
  Afrowave.DiskMigrator.Cli/             české terminálové uživatelské rozhraní
tests/                                   automatické testy (113 testů)
tools/Afrowave.SampleDisks/              generátor simulovaných disků pro školení
tools/windows-vm-test/                   testovací scénář pro Windows (VHDX + VM)
docs/                                    dokumentace
```

Architektura a odůvodnění rozhodnutí: [docs/ARCHITEKTURA.md](docs/ARCHITEKTURA.md)

## Simulační režim

Nástroj lze vyzkoušet bez jakéhokoli rizika, na souborech místo fyzických disků:

```bash
# vytvoří simulovanou sadu disků
dotnet run --project tools/Afrowave.SampleDisks -c Release -- disks

# spustí nástroj nad simulovanými disky
AfrowaveDiskMigrator.exe --simulate disks
```

Simulační režim je vhodný pro školení techniků: projde celý plán, kopírování
a kontrolu, ale nikdy nemůže sáhnout na skutečný disk.

## Dokumentace

* [Testovací scénář pro Windows](tools/windows-vm-test/README.md) – **začněte zde**
* [Návod k použití](docs/NAVOD.md)
* [Architektura a rozhodnutí](docs/ARCHITEKTURA.md)
* [Testování a výsledky](docs/TESTOVANI.md)
* [Omezení první verze](docs/OMEZENI.md)

## Licence

Zdrojové kódy: volně použitelné ve firmě Afrowave i mimo ni. Nástroj nepoužívá žádnou
komerční ani copyleft knihovnu – runtime tvoří .NET (MIT) a systémové nástroje Microsoft,
které jsou součástí Windows.
