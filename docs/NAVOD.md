# Návod k použití

Návod pro technika, který má přenést obsah disku na jiný disk.

> **Experimentální verze.** Nástroj zatím neprošel testem na skutečném Windows
> s fyzickými disky. Než jej poprvé použijete na zákaznická data, projděte
> [testovací scénář](../tools/windows-vm-test/README.md).

---

## 1. Než začnete

### Co budete potřebovat

* Servisní počítač s Windows 10/11 (64 bitů).
* USB flash disk s `AfrowaveDiskMigrator.exe`.
* Zdrojový disk (ten, jehož obsah chcete zachovat).
* Cílový disk (nový nebo určený k přepsání).
* Pro zmenšení oddílu: volné místo na jiném disku než zdrojovém a cílovém
  (podrobnosti v části 4).

### Důležité předpoklady

* **Cílový disk musí být bez oddílů** – nový disk z výroby nebo disk, který už někdo
  vyprázdnil. Nástroj oddíly nikdy nemaže, aby si disk uvolnil.
* **Zdrojový disk musí být vypnutý řádně, ne hibernovaný.** Pokud byl počítač
  uspán nebo vypnut pomocí rychlého startu, doporučujeme jej nejprve spustit
  a provést úplné vypnutí. Nástroj na hibernaci upozorní.
* Nemusíte řešit, jaká písmena jednotek mají svazky na zdrojovém ani cílovém disku.
  Nástroj pracuje s fyzickými disky, nikoli s písmeny.
* Cílový disk nemusí být naformátovaný ani inicializovaný – naopak, čím „prázdnější“,
  tím lépe.

### Co nástroj nikdy neudělá

* Nezapíše ani bajt na zdrojový disk.
* Nevybere za vás žádný disk.
* Nesmaže oddíly na cílovém disku, aby si jej uvolnil.
* Nenabídne jako zdroj ani cíl disk servisního počítače, disk, ze kterého běží,
  ani disk s hibernačním či stránkovacím souborem běžícího systému.
* Nezmění zavádění ani konfiguraci servisního počítače.
* Neobchází šifrování, které nedokáže bezpečně zpřístupnit.
* Netvrdí, že výsledný disk nastartuje, pokud zavádění neumí připravit.

---

## 2. Spuštění

Klikněte na `AfrowaveDiskMigrator.exe` pravým tlačítkem a zvolte
**Spustit jako správce**. Windows zobrazí dotaz UAC – potvrďte jej.

Nástroj se ukončí s jasnou zprávou, pokud neběží jako správce.

Hned na začátku uvidíte přehled disků:

```
Dostupné disky
--------------
  #   Model / zařízení          Velikost  Rozhraní  Stav
  0   Samsung SSD 970          500 GiB   NVMe      CHRÁNĚN – nelze použít
      └ obsahuje systémový svazek běžícího servisního počítače
  1   WDC WD5000AZLX            465 GiB   SATA      zdroj
      └ obsahuje 4 oddílů – může být zdrojem
  2   CT240BX500SSD1            223 GiB   SATA      cíl
      └ bez oddílů – může být cílem

  Zdroj = disk, který obsahuje oddíly. Cíl = disk bez oddílů.
  Nástroj oddíly nikdy nemaže – pokud cíl oddíly obsahuje, vyberte jiný disk.
  Disky servisního počítače se nezobrazují jako zdroj ani cíl.
```

Ve sloupci **Stav** je rovnou vidět, do kterého seznamu disk patří:

| Stav | Význam |
|---|---|
| `zdroj` | Obsahuje oddíly – nabídne se jen jako zdroj. |
| `cíl` | Je bez oddílů – nabídne se jen jako cíl. |
| `CHRÁNĚN – nelze použít` | Servisní počítač. Nenabídne se vůbec. |
| `nejednoznačný stav – nelze použít` | Poškozená nebo nesmyslná tabulka oddílů. Nenabídne se vůbec. |

Seznamy **zdroj** a **cíl** jsou tedy ostře oddělené: v prvním se objeví jen disky
s obsahem, ve druhém jen prázdné disky. Když máte připojený jeden plný a jeden nový
disk, nabídne se v každém seznamu právě jedna položka.

> Pokud se v seznamu neobjeví žádný chráněný disk, nástroj vás na to upozorní.
> Zkontrolujte, že skutečně běžíte na servisním počítači, a pokračujte opatrně.

---

## 3. Výběr disků

### Zdrojový disk

Zadejte číslo disku, jehož obsah chcete přenést. Nabídnou se jen disky, které
obsahují oddíly.

Nástroj zdrojový disk **okamžitě přepne do režimu pouze pro čtení** – ještě než se
do něj podívá. Důvod: rozpoznání operačního systému vyžaduje svazky připojit, a kdyby
byl disk zapisovatelný, mohl by do něj běžící Windows sáhnout (přehrát NTFS žurnál,
aktualizovat `System Volume Information`). Takže se na zákazníkův disk nezapíše ani
při analýze. Po skončení se režim zase zruší.

Následně nástroj zobrazí:

* model, sériové číslo, velikost, velikost sektoru, rozhraní,
* typ tabulky oddílů (GPT / MBR),
* jestli byl rozpoznán operační systém a jaký,
* seznam všech oddílů včetně skrytých, jejich souborové systémy a obsazené místo.

### Cílový disk

Zadejte číslo disku, který se má stát novým diskem s přeneseným obsahem.
Nabídnou se jen disky bez oddílů.

Platí tři podmínky:

1. Disk **nesmí obsahovat žádné oddíly**. Pokud obsahuje, nástroj migraci odmítne
   a vypíše, co na něm našel. Oddíly nikdy nemaže.
2. Disk nesmí být totožný se zdrojovým.
3. Disk nesmí patřit servisnímu počítači.

Odmítnuty jsou i disky, jejichž tabulka oddílů je poškozená nebo nejednoznačná –
například hybridní MBR, zbytky GPT bez ochranného záznamu nebo nečitelná tabulka.
V nejistém stavu se nikdy nezapisuje.

> **Cílový disk bude inicializován.** Odpovědnost za jeho výběr nese technik;
> kontrola neprázdnosti je jen dodatečná pojistka.

---

## 4. Dočasné úložiště (jen když je potřeba)

Pokud se obsah na cílový disk vejde **beze zmenšení oddílů**, dočasné úložiště
se nepotřebuje a nástroj se na něj neptá.

Pokud je cílový disk menší a některý oddíl NTFS se musí zmenšit, nástroj
přenáší obsah přes dočasný obraz WIM. Na úložiště se zeptá **teprve tehdy, když
je opravdu potřeba** – u migrace beze zmenšení se na něj neptá vůbec.

```
Dočasné úložiště
----------------
  Pro zmenšení oddílu je potřeba dočasný obraz WIM.
  1) E:\ – volno 186.4 GiB
  2) F:\ – volno 61.2 GiB
  3) zadat cestu ručně (včetně síťového disku)

  Zvolte svazek pro dočasné soubory (doporučeno 1 = E:\) [1]:
```

Pravidla:

* Svazek **nesmí** ležet na zdrojovém ani cílovém disku a nesmí být součástí
  servisního počítače. Nástroj to ověří i u ručně zadané cesty.
* Musí být naformátovaný jako NTFS, ReFS nebo exFAT (FAT32 neumí soubory nad 4 GB).
* Lze zadat i síťovou cestu (`\\server\share`), například na NAS.
  Pozor: rychlost přenosu pak závisí na síti.
* Potřebné místo nástroj spočítá a vypíše ještě před potvrzením migrace,
  například `potřeba přibližně 96 GiB`.
* Obraz se po dokončení sám smaže. Nástroj maže jen soubory, které sám vytvořil
  ve složce `AfrowaveDiskMigrator`.

Pokud žádný vhodný svazek neexistuje, nástroj migraci odmítne a vysvětlí proč.
Alternativou je použít cílový disk, na který se obsah vejde bez zmenšení.

> **Pozor u nečistě odpojeného NTFS:** pokud byl zdrojový systém ukončen havárií,
> svazek NTFS zůstane označen jako „dirty“ a nelze jej připojit pouze pro čtení.
> Zmenšení takového oddílu nástroj odmítne a nabídne buď větší cílový disk
> (kopie po shlucích připojení nepotřebuje), nebo kontrolu zdroje na původním počítači.

---

## 5. Kontrola navrženého plánu

Toto je nejdůležitější obrazovka. Nástroj zobrazí celý plán, než cokoli zapíše:

```
Navržený postup migrace
-----------------------
  Zdroj: Disk 1: WDC WD5000AZLX  (465 GiB, SN …4F2A)
  Cíl:   Disk 2: CT240BX500SSD1  (223 GiB, SN …9C11)

   #  Oddíl             Systém    Zdroj      Cíl     Způsob přenosu
   1  EFI system part.  FAT32     100 MiB    100 MiB sektorová kopie 1:1
   2  Microsoft reserved neform.   16 MiB     16 MiB  vytvořit prázdný oddíl
   3  Základní datový    NTFS      400 GiB    221 GiB obraz WIM (DISM) – umožní zmenšení
   4  Windows Recovery   NTFS      700 MiB    700 MiB chytrá kopie NTFS (jen obsazené shluky)

  Celkem se zapíše:        121.6 GiB
  Použitelná kapacita cíle: 222.5 GiB

Zavádění systému
----------------
  Režim: UEFI
  • Na cílovém EFI System Partition bude znovu vytvořeno zavádění systému (bcdboot /f UEFI).
  • Prostředí pro obnovení bude znovu zaregistrováno na oddíle #4 (reagentc).
```

Co zkontrolovat:

1. **Model a sériové číslo obou disků** – opravdu jde o ty správné disky?
2. **Počet a pořadí oddílů** – sedí s tím, co jste očekávali?
3. **Způsob přenosu** – u neznámých souborových systémů musí být sektorová kopie.
4. **Zavádění systému** – je zvolen správný režim (UEFI / BIOS)?
5. **Upozornění** – přečtěte si je všechna, obsahují důležité informace.

Pokud je něco v nepořádku, migraci nepouštějte. Nástroj odmítne migraci sám,
pokud by měla skončit nefunkčním diskem.

---

## 6. Potvrzení

Po zobrazení plánu následuje **jedno** potvrzení – napište `ANO`:

```
Potvrzení
---------
  Zdroj: Disk 1: WDC WD5000AZLX  (SN WD-WCC4E1234567)
  Cíl:   Disk 2: CT240BX500SSD1  (SN 2201E4A1B2C3)

  !    Spuštěním zápisu převezme cílový disk obsah zdrojového disku.
  !    Zdrojový disk zůstane nedotčen. Cílový disk je bez oddílů a bude inicializován.

  Potvrzení spuštění migrace:
  Napište "ANO" pro potvrzení:
```

Teprve poté nástroj:

1. Odpojí všechny svazky cílového disku.
2. Přepne zdrojový disk do režimu pouze pro čtení.
3. **Znovu ověří identitu obou disků** – přes handle otevřený pouze pro čtení.
   Pokud se od vytvoření plánu změnily (záměna, přepojení, změna pořadí
   `PhysicalDrive`), nástroj skončí, aniž by zapsal.
4. **Znovu ověří, že cílový disk je stále bez oddílů.**
5. Otevře cílový disk pro zápis a ověří, že otevřené zařízení odpovídá ověřené
   identitě.
6. Zepíše novou tabulku oddílů na cílový disk.

---

## 7. Průběh

```
== Zápis tabulky oddílů na cílový disk ===================================
  → oddíl #1: sektorová kopie 1:1
  OK   Oddíl #1 přenesen (100 MiB).
  → oddíl #3: obraz WIM (DISM) – umožní zmenšení
  #3 Základní dato [###########...........]  54.2 %  89.3 GiB / 164.8 GiB  112.4 MiB/s  zbývá 11:18
```

U každého oddílu vidíte procenta, přenesená data, rychlost a odhad zbývajícího času.

**Přerušení:** stiskněte Ctrl+C. Nástroj přeruší i běžící systémový nástroj
(například `DISM`) a ukončí se. Cílový disk pak zůstane částečně zapsaný –
nástroj to výslovně napíše a **disk se nesmí použít**; migraci je nutné provést
znovu od začátku. Obnovování přerušených migrací zatím neexistuje.

---

## 8. Kontrola po přenosu

Po dokončení nástroj automaticky:

* znovu přečte zapsaná data a porovná je se zdrojem pomocí SHA-256,
* ověří, že tabulka oddílů na cíli odpovídá plánu,
* znovu vytvoří zavádění systému pomocí nástrojů Microsoft (`bcdboot`,
  `bootsect`, `reagentc`).

Výsledek vypadá takto:

```
Kontrola po přenosu
-------------------
  OK   Oddíl #1: SHA-256 shoda pro 100 MiB (zdroj 9EE4F5B5CA40DDEC…).
  OK   Oddíl #3: SHA-256 shoda pro 120.8 GiB přenesených dat (obsazené shluky a okrajové oblasti).
  OK   Tabulka oddílů na cílovém disku odpovídá plánu (4 oddílů, Gpt).

  OK   Zapsáno 121.6 GiB za 18:42.

  i    Skutečné spuštění cílového systému nebylo tímto nástrojem otestováno.
  i    Před předáním počítače ověřte start systému z cílového disku.
```

> **Nástroj nikdy netvrdí, že cílový systém funguje.** Ověřuje data a konfiguraci
> zavádění, ale skutečné spuštění operačního systému na cílovém disku otestovat
> neumí. Poslední krok je vždy na vás.

---

## 9. Po migraci

1. **Odpojte zdrojový disk.** Cílový disk přebírá identifikátory zdroje, aby
   fungovalo existující zavádění a zůstala stejná písmena jednotek. Když zůstanou
   oba disky připojené, mohou si je Windows plést.
2. Zapněte počítač a ověřte start systému.
3. Zkontrolujte, že jsou dostupná všechna data a že funguje obnova systému.

### Režim kontroly bez zápisu

Chcete-li nejprve vidět, co by se stalo, spusťte nástroj s parametrem `--dry-run`.
Vypíše celý plán a simulovaný průběh, ale na disky nezapíše nic.

```
AfrowaveDiskMigrator.exe --dry-run
```

---

## 10. Řešení potíží

| Zpráva | Co to znamená a co dělat |
|---|---|
| `Nástroj musí běžet jako správce` | Spusťte EXE pravým tlačítkem → Spustit jako správce. |
| `Cílový disk není prázdný` | Disk obsahuje oddíly nebo zbytky tabulky. Nástroj je nemaže – vyberte jiný disk, nebo jej vyprázdněte mimo tento nástroj. |
| `Disk N nelze pro tuto roli použít` | Následuje výpis důvodů (chráněný disk, oddíly na cíli, nejednoznačná tabulka…). |
| `Obsah se na cílový disk nevejde ani po zmenšení` | Cílový disk je příliš malý. Zpráva u každého oddílu uvádí nejmenší možnou velikost. Použijte větší cílový disk. |
| `… není čistě odpojen a musí se zmenšit` | Zdroj NTFS nebyl řádně odpojen. Použijte větší cílový disk, nebo zdroj nechte zkontrolovat na původním počítači. |
| `Zavádění systému nelze na tomto počítači obnovit` | Chybí `bcdboot`/`bootsect`. Nástroj odmítá vytvořit disk, který by nenastartoval. |
| `Pro tuto migraci je nutné zvolit dočasné úložiště` | Zmenšuje se NTFS oddíl a je potřeba místo pro WIM obraz. Připojte dostatečně velký svazek, nebo použijte větší cílový disk. |
| `Dočasné úložiště … nemá dostatek místa` | Připojte větší úložiště. Nástroj uvádí potřebnou i volnou kapacitu. |
| `Cílový disk se od načtení plánu změnil` | S disky se mezi plánem a potvrzením něco stalo. Spusťte nástroj znovu a plán znovu zkontrolujte. |
| `Zmenšení oddílu vyžaduje nástroj DISM, který není k dispozici` | Na servisním počítači chybí `Dism.exe`. Použijte standardní instalaci Windows 10/11. |
| `Oddíl … není čistě odpojen (NTFS dirty bit)` | Zdrojový systém nebyl řádně vypnut. Data se přenesou, ale cílový systém při prvním startu spustí kontrolu disku. |
| `Windows na oddílu … je v režimu hibernace` | Zdrojový počítač byl uspán. Nechte jej řádně vypnout (ne hibernovat) a migraci opakujte. |
| `Zdrojový disk obsahuje kontejner` | Zdrojový disk je dynamický disk nebo Storage Spaces. Tento nástroj takové disky nepřenáší – odmítá, aby neriskoval data. |

### Protokol

Každé spuštění zapisuje protokol vedle EXE (nebo do `%TEMP%`, pokud na USB nelze
zapisovat). Cestu k protokolu nástroj vypíše na konci. Protokol neobsahuje žádné
šifrovací klíče – ty se do něj nikdy nezapisují.

---

## 11. Vyzkoušení bez rizika

Nástroj je možné vyzkoušet na simulovaných discích (obrazových souborech) – ideální
pro školení a pro ověření, že rozumíte obrazovkám:

```bash
# vytvoří tři simulované disky v adresáři "disks"
dotnet run --project tools/Afrowave.SampleDisks -c Release -- disks

# spustí nástroj nad simulovanými disky (nikdy nesáhne na skutečný disk)
AfrowaveDiskMigrator.exe --simulate disks
```

Přidejte `--shrink` při generování, chcete-li simulovat situaci, kdy se obsah
nevejde a oddíl se musí zmenšit.

---

## 12. Přehled parametrů příkazové řádky

| Parametr | Význam |
|---|---|
| `-h`, `--help` | Nápověda |
| `-v`, `--version` | Verze nástroje |
| `-s`, `--source <n>` | Předvybere zdrojový disk |
| `-t`, `--target <n>` | Předvybere cílový disk |
| `--temp <cesta>` | Dočasné úložiště pro WIM obraz |
| `--log <cesta>` | Kam zapsat protokol |
| `--dry-run` | Kontrolní režim – nic se nezapíše |
| `-y`, `--yes` | Přeskočí potvrzení (jen s `--source` a `--target`) |
| `--new-ids` | Cílový disk dostane nové identifikátory místo původních |
| `--no-verify` | Přeskočí kontrolu zapsaných dat |
| `--zero-free` | Vynuluje volné shluky na cílovém disku |
| `--ignore-temp-check` | Pokračuje i při nedostatku místa pro WIM obraz |
| `--simulate <adresář>` | Simulační režim nad soubory `disk*.img` |

Bez parametrů se vždy otevře interaktivní nabídka – to je doporučený způsob použití.

### Návratové kódy

Pro skriptování a automatizaci:

| Kód | Význam |
|---|---|
| 0 | úspěch (i když uživatel potvrzení odmítl – na disky se nepsalo) |
| 1 | neočekávaná chyba |
| 2 | nepodporovaná platforma |
| 3 | nástroj neběží jako správce |
| 4 | nebyl nalezen žádný disk |
| 5 | zdrojový disk nelze použít |
| 6 | migrace odmítnuta (plán není bezpečně proveditelný) |
| 7 | migrace selhala nebo byla přerušena |
