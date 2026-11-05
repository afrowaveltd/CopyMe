# Testování na skutečném Windows

> **Tato složka obsahuje testovací nástroje, které autor nespustil.**
> V prostředí, kde Afrowave DiskMigrator vznikl, nebyl k dispozici žádný počítač
> s Windows ani virtuální stroj. Vše níže je připravené k prvnímu spuštění na
> servisním počítači – prosím neberte to jako ověřený výsledek.

Dva na sebe navazující kroky:

| Krok | Co ověří | Automatizované? |
|---|---|---|
| 1. `Invoke-DiskMigrationTest.ps1` | Migraci dat a tabulky oddílů na VHDX discích | Ano, skript |
| 2. Scénář s virtuálním počítačem | Že výsledný disk skutečně **nastartuje** | Ne, ruční postup |

Krok 1 nezbytně předchází kroku 2 – nejdřív musí projít migrace dat, teprve pak
má smysl řešit zavádění systému.

---

## Krok 1: automatizovaný test na VHDX

### Co skript potřebuje

* Windows 10/11 nebo Windows Server 2016+ (modul `Storage` – `Get-Disk`).
* Oprávnění správce (`#Requires -RunAsAdministrator`).
* Sestavený `dist/AfrowaveDiskMigrator.exe`.
* ~15 GB volného místa v `$env:SystemDrive\AfrowaveDiskMigratorTest`.

### Spuštění

```powershell
# z kořene repozitáře, v PowerShellu spuštěném jako správce
.\tools\windows-vm-test\Invoke-DiskMigrationTest.ps1
```

Volitelné parametry:

```powershell
.\Invoke-DiskMigrationTest.ps1 `
    -WorkDir 'D:\afrowave-test' `
    -ExePath 'E:\AfrowaveDiskMigrator.exe' `
    -SourceSizeMB 6144 `
    -TargetSizeMB 8192 `
    -KeepVhds `
    -SkipCleanup
```

### Co skript dělá

1. Vytvoří dva VHDX soubory (`diskpart create vdisk`) a připojí je.
   Windows je vidí jako fyzické disky.
2. Ověří, že nalezl právě jeden virtuální disk pro každou roli – kdyby odpovídalo
   víc kandidátů, skript se zastaví, aby nehádal.
3. Připraví zdrojový disk: GPT, ESP 100 MB (FAT32), MSR 16 MB, datový oddíl NTFS.
4. Zapíše testovací data: 40 souborů, soubor s mezerou v názvu, vnořenou složku
   a prázdný soubor; poznamená si SHA-256 každého z nich.
5. **Zkusí migraci na cíl, který má oddíl** – očekává odmítnutí (nenulový návratový
   kód) a ověří, že nástroj oddíl nesmazal.
5b. **Otevře soubor na zdrojovém svazku a zkusí migraci** – ověří, že nástroj převzetí
   odmítne (`FSCTL_LOCK_VOLUME` musí selhat), vysvětlí to a na cílový disk nic nezapíše.
   Tím je otestovaná i ta část převzetí, kterou nelze ověřit bez Windows.
6. Vyčistí cíl a spustí `--dry-run` – očekává plán a ověří, že na cíl nic nezapsal.
7. Spustí skutečnou migraci (`--source`, `--target`, `--yes`).
8. Ověří: počet a typy oddílů na cíli, přítomnost a SHA-256 všech souborů,
   prázdný soubor, a že zdrojový disk zůstal beze změny.
9. Ověří **navrácení zdrojového disku Windows**: původní svazek je po skončení opět
   dostupný pod stejným písmenem, je zapisovatelný a data na něm jsou pořád.
9. Uklidí – odpojí a smaže VHDX (není-li `-KeepVhds` / `-SkipCleanup`).

### Co skript ověří z převzetí zdroje

| Vlastnost | Jak |
|---|---|
| Zdroj s připojenými svazky a písmeny | Zdroj se před migrací připojí jako `S:` – přesně scénář z praxe |
| Převzetí proběhne | Migrace doběhne; bez převzetí by se nástroj vůbec nespustil |
| Odmítnutí při obsazeném svazku | Držený popisovač → očekává se odmítnutí a nedotčený cíl |
| Navrácení zdrojového disku | Po migraci se ověří dostupnost `S:`, zápis a přítomnost dat |

### Co skript **neověří**

* Start operačního systému z cílového disku.
* Chování `DISM`, `bcdboot` a `reagentc` na skutečné instalaci Windows
  (testovaný disk obsahuje jen data, nikoli instalaci Windows).
* Chování při odpojení USB během operace (lze zkusit ručně podle níže uvedeného
  seznamu).
* Zdroj s **používaným stránkovacím souborem** – takový disk nelze na testovacím
  stroji rozumně vytvořit.
* Zdroj, který je **součástí svazku přes více disků** – vyžadovalo by Storage Spaces.

### Bezpečnost skriptu

Skript pracuje výhradně s disky, které sám vytvořil, a před každým použitím čísla
disku znovu ověří, že jde o virtuální disk očekávané velikosti. Pokud by na stroji
byl virtuální disk stejné velikosti, skript se zastaví místo toho, aby hádal.

---

## Krok 2: ověření, že cílový disk nastartuje (ruční scénář)

Toto je jediný způsob, jak poctivě ověřit, že výsledek je skutečně bootovatelný.
Skript to nedělá, protože k tomu je potřeba virtuální počítač.

### Příprava

1. Ve virtualizačním prostředí (Hyper-V, VMware, VirtualBox) vytvořte VM s Windows
   10/11. Můžete použít zkušební obraz, licence není potřeba – ověřuje se startování,
   nikoli aktivace.
2. VM vypněte a připojte k ní **druhý, menší** virtuální disk (např. zdrojový 60 GB,
   cílový 40 GB). Cílový musí být zcela nový, bez oddílů.
3. Spusťte VM a přihlaste se jako správce.

### Vlastní test

1. Zkopírujte `AfrowaveDiskMigrator.exe` do VM (např. přes sdílenou složku).
2. Spusťte jej **jako správce**.
3. Ověřte na seznamu disků:
   * systémový disk VM je označen `CHRÁNĚN`,
   * cílový disk je označen `zdroj i cíl`,
   * zdrojový disk s instalací Windows je označen `zdroj (cíl ne: …)` –
     **to je klíčová kontrola**: offline instalace Windows musí jít vybrat jako zdroj.
4. Nejdřív `--dry-run` a projděte plán.
5. Potom skutečnou migraci a potvrďte `ANO`.
6. Po dokončení VM vypněte.
7. **Odpojte zdrojový disk** (cíl převzal jeho identifikátory, oba najednou by si
   Windows pletly).
8. Nastartujte VM z cílového disku.

### Co sledovat

* Systém nastartuje a nabídne přihlášení.
* Písmena jednotek zůstala stejná jako na zdrojovém disku.
* Uživatelská data i nainstalované aplikace jsou dostupné.
* `reagentc /info /target C:\Windows` hlásí zaregistrované prostředí pro obnovení.
* Ve **Správě disků** je cílový disk stejné velikosti jako zdroj a je bez chyb.
* Události ve Windows neobsahují chyby svazku.

### Opakujte pro oba režimy zavádění

* **UEFI** – VM s GPT a ESP (výchozí u moderních Windows).
* **BIOS/CSM** – VM založená s MBR diskem a aktivním oddílem.

Výsledek zapište do `docs/TESTOVANI.md`, ať je zřejmé, co bylo skutečně ověřeno.

---

## Ruční kontroly, které stojí za to zkusit

Tyto situace automatický test nepokrývá a jsou popsány jako neověřené:

| Situace | Co očekávat |
|---|---|
| Vytáhnout USB s cílovým diskem během kopírování | Chyba, jasná zpráva, cíl označen jako nepoužitelný |
| Stisknout Ctrl+C během kopírování | Hlášení, že cíl může být neúplný a nesmí se použít |
| Zdroj NTFS s „dirty“ příznakem (odpojit napájený disk) | Varování; zmenšení musí být odmítnuto s nabídkou většího cíle |
| Zamčený BitLocker svazek na zdroji | Přesune se sektorově; zmenšení odmítnuto |
| Disk, ze kterého nástroj běží, jako cíl | Nesmí být vůbec nabídnut |
| Zdroj a cíl označený jako stejný disk | Odmítnuto ještě před zápisem |
| Cílový disk s existujícími oddíly | Odmítnuto, oddíly zůstanou nedotčené |
| Cílový disk s poškozenou GPT | Odmítnuto jako nejednoznačný stav |
| Zdrojový disk po skončení | Nesmí být read-only ani jinak změněný |
