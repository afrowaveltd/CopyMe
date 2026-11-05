# Omezení experimentální verze 1.0

Poctivý seznam toho, co verze 1.0 **neumí** nebo co nebylo ověřeno. Nic z toho není
skryté – nástroj na omezení upozorní nebo operaci odmítne.

---

## 1. Ověření na skutečném hardwaru

**Nejzávažnější omezení – důvod, proč je verze označena jako experimentální.**
Nástroj byl vyvíjen a testován na vývojovém počítači s Linuxem. Vrstva pro Windows se
přeloží bez chyb a varování, ale:

* přístup k `\\.\PhysicalDriveN` nebyl spuštěn na reálném disku,
* připojování svazků a `FSCTL_DISMOUNT_VOLUME` nebyly spuštěny,
* `DISM`, `bcdboot`, `bootsect` a `reagentc` nebyly volány na skutečném systému,
* start cílového systému nebyl ověřen,
* pomocný PowerShell skript pro VHDX test nebyl spuštěn (jen ověřena syntaxe).

Všechna rozhodovací logika, práce s tabulkami oddílů, čtení NTFS, plánování, kopírování,
ověřování a nově i kontrola prázdnoty cílového disku otestovány **byly** – nad
obrazovými soubory, 95 automatickými testy. Postup ověření na hardware je v
[TESTOVANI.md](TESTOVANI.md), část 6.

### 1b. Cílový disk musí být bez oddílů

Toto je **záměrné omezení podle zadání**, ne nedostatek. Nástroj:

* odmítne cílový disk, který obsahuje jakékoli definované oddíly,
* nikdy je nesmaže, aby si disk uvolnil,
* odmítne i disk s nejednoznačnou tabulkou (hybridní MBR, zbytky GPT, poškozená GPT).

V praxi to znamená, že **nový disk z výroby** (typicky bez tabulky oddílů) vyhoví,
ale **disk vytažený z jiného počítače je nejprve nutné vyprázdnit** mimo tento nástroj.
USB flash disky bývají z výroby rozdělené – jako cílový disk je proto nelze použít
bez předchozího vyčištění.

---

## 1c. Převzetí zdrojového disku

Nástroj si připojený zdrojový disk přebírá sám, ale má to hranice:

* **Svazek s otevřeným souborem nelze uzamknout.** Nástroj cizí procesy neukončuje;
  vypíše, který svazek drží, a nabídne opakování. Řešením je zavřít Průzkumník,
  kancelářské aplikace a pozastavit antivirovou kontrolu.
* **Svazky rozložené přes více fyzických disků** (spanned, striped, Storage Spaces)
  nástroj odmítá – nelze je bezpečně převzít jako samostatný disk.
* **Disk s používaným stránkovacím souborem** nelze uzamknout, a tedy ani převzít.
  Takový disk by ostatně nebyl online instalací zákazníka.
* **Zápisy, které Windows provedl před spuštěním nástroje** (při automatickém
  připojení svazků), nelze vzít zpět ani zjistit. Nástroj zaručuje jen to, že od
  okamžiku převzetí na zdroj nezapisuje.
* **Pád procesu nebo výpadek napájení** obejde `finally`. Nástroj proto během migrace
  nedrží žádné zámky (uvolní je ukončení procesu) a read-only atribut nastavuje
  s `Persist = 0`, takže jej **restart počítače zruší**. Nejhorší následek je dočasně
  read-only disk do restartu.
* **Zavřené souborové systémy** na zdrojovém disku (zamčený BitLocker) se neodpojují –
  není co vyprázdnit.

## 2. Souborové systémy

| Systém | Chytrá kopie | Zmenšení | Poznámka |
|---|---|---|---|
| **NTFS** | ✅ ano | ✅ ano (DISM) | hlavní podporovaný systém |
| **FAT32 / FAT16 / FAT12** | ❌ | ❌ | jen sektorová kopie 1:1 |
| **exFAT** | ❌ | ❌ | jen sektorová kopie 1:1 |
| **ReFS** | ❌ | ❌ | rozpozná se, kopíruje sektorově |
| **ext2/3/4, Btrfs, XFS** | ❌ | ❌ | rozpozná se, kopíruje sektorově |
| **APFS, HFS+** | ❌ | ❌ | rozpozná se, kopíruje sektorově |
| **BitLocker / LUKS** | ❌ | ❌ | viz část 4 |
| **Neznámý** | ❌ | ❌ | sektorová kopie, migrace se odmítne, pokud se nevejde |

Důsledek pro praxi: datový disk naformátovaný jako FAT32 nebo exFAT o velikosti
300 GB **nelze** přenést na 200GB disk. Nástroj to odmítne a vysvětlí. NTFS je
výchozí systém Windows, takže se to týká hlavně přenosných médií a karet.

---

## 3. Operační systémy

* **Windows** – zavádění se obnovuje nástroji Microsoftu (`bcdboot`, `bootsect`,
  `reagentc`). Podporováno UEFI (GPT + ESP) i BIOS (MBR + aktivní oddíl).
* **Jiné operační systémy (Linux, macOS)** – obsah se přenese sektorově, ale
  **zavádění se nekonfiguruje**. Nástroj na to upozorní. Funkčnost výsledného disku
  nezaručuje; záleží na zachování offsetů a obsahu zaváděcích oblastí.
* **Více operačních systémů na jednom disku** – rozpoznán bude první Windows;
  zavádění ostatních systémů se nekonfiguruje.
* **Windows s vlastním zavaděčem** (GRUB, systemd-boot, rEFInd) – MBR/ESP se kopírují,
  ale konfigurace se neupravuje.

---

## 4. Šifrování

* BitLocker se **rozpozná** a oznámí se to.
* **Zašifrovaný oddíl lze přenést sektorovou kopií**, pokud se vejde v původní
  velikosti – na novém disku se odemkne stejným klíčem. To je plnohodnotná podpora.
* **Zmenšit zašifrovaný oddíl nástroj neumí** a odmítne to. Důvod: zmenšení vyžaduje
  přístup k dešifrovanému obsahu a změnu metadat svazku.
* Nástroj **si nevyžádá obnovovací klíč** a nebude se o to pokoušet. Pokud je potřeba
  zmenšit zašifrovaný disk, je nutné BitLocker nejprve zrušit nebo disk odemknout
  a zmenšit ručně.
* Šifrování se nikdy neobchází.

---

## 5. Disky, které nástroj odmítne

* **Dynamické disky / LDM** – odmítnuto i jako zdroj.
* **Storage Spaces** – odmítnuto.
* **Softwarový RAID (Intel RST, atd.)** – záleží na tom, jak ho vidí Windows; pokud
  se tváří jako disk s neznámým obsahem, migrace se odmítne.
* **Disky s poškozenou tabulkou oddílů** – pokud projde záložní GPT, nástroj to
  oznámí a pokračuje; jinak odmítne.
* **Disky menší než 1 GiB** – nelze použít jako cíl.
* **Disky offline nebo read-only** – nelze použít jako cíl.

---

## 6. Omezení zavádění systému

* **Firmware servisního počítače se nemění** – nástroj do NVRAM nezapisuje nic.
  Boot se obnovuje na cílovém disku. To je záměrné (zadání zakazuje měnit konfiguraci
  servisního počítače), ale znamená toto: pokud cílový disk vložíte do **jiného**
  počítače, jeho firmware může vyžadovat ruční volbu zaváděcího souboru
  (`\EFI\Microsoft\Boot\bootmgfw.efi`).
* **Zachování identifikátorů** (výchozí chování) zajišťuje, že existující položka
  firmwaru na původním počítači zůstane platná. Proto je nutné **zdrojový disk po
  migraci odpojit** – dva disky se shodnými identifikátory si Windows mohou plést.
  Parametrem `--new-ids` lze toto chování vypnout.
* **Secure Boot** – nijak se neupravuje. Pokud zdrojový systém bootoval se Secure
  Bootem, bude tak bootovat i cíl (soubory jsou shodné).
* **Rozšíření posledního oddílu** na větším cílovém disku se neprovádí. Volné místo
  zůstane na konci disku nevyužité; lze je přidělit ve Správě disků.

---

## 7. Jiná omezení

* **MBR limit 2 TiB** – cílový disk větší než 2 TiB nelze naformátovat jako MBR.
  Nástroj to odmítne. Zdrojový disk MBR nad 2 TiB neexistuje.
* **Maximálně 128 oddílů GPT, 4 primární + logické u MBR.** Běžné disky mají 2–6.
* **Hibernace zdrojového systému** – nástroj upozorní, ale neopraví to. Doporučuje se
  zdroj vypnout řádně. Hibernovaný systém se přenese, ale start může být rizikový.
* **NTFS s „dirty bit“** – data se přenesou, ale cílový systém při prvním startu
  pravděpodobně spustí kontrolu disku (`chkdsk`). Je to normální chování Windows.
* **Dočasné úložiště pro zmenšení** – musí být na jiném disku než zdroj a cíl
  a musí mít dost místa na WIM obraz (odhadem 80 % obsazených dat největšího oddílu).
  Nástroj to ověří předem, ale odhad se může mýlit; v takovém případě `DISM` selže
  s jasnou chybou a cíl zůstane nepoužitelný.
* **Rychlost** – závisí na discích. Chytrá kopie zpravidla dosahuje rychlosti disku.
  Kontrola SHA-256 přidává jedno čtení cíle; lze ji vypnout `--no-verify`
  (nedoporučujeme).
* **Kontrola oddílu po WIM** – ověřuje se, že svazek je čitelný NTFS a obsahuje
  očekávané množství dat. Nejde o bajtové srovnání se zdrojem, protože svazek byl
  záměrně znovu vytvořen s jinou velikostí.
* **Přerušenou migraci nelze obnovit.** Po přerušení (Ctrl+C, výpadek napájení,
  odpojení disku) je cílový disk částečně zapsaný a migraci je nutné spustit znovu
  od začátku. Nástroj to výslovně napíše.
* **Nečistě odpojený NTFS nelze zmenšit.** Read-only připojení svazku s „dirty“
  příznakem obvykle selže, takže by migrace skončila napůl zapsaným cílem. Nástroj
  takovou migraci odmítne a nabídne cílový disk bez zmenšení.
* **Soubory s velmi dlouhými cestami** – při inteligentním kopírování NTFS se přenáší
  bajtově, takže to není omezení. Týkalo by se jen kopírování po souborech, které
  nástroj nepoužívá.
* **Jedno vlákno** – kopírování je jednoduché a sekvenční. Paralelizace by mohla
  zrychlit SSD, ale zavedla by riziko a nebyla by testovatelná zde.

---

## 8. Co nástroj naopak umí spolehlivě

Aby seznam omezení nepřevážil, stručně to podstatné:

* Přenést Windows disk na menší disk včetně zavádění.
* Zachovat všechny oddíly, jejich pořadí, typy i funkce.
* Přenést NTFS metadata bajtově shodně (ACL, hardlinky, ADS, EFS, sparse).
* Přenést neznámé souborové systémy sektorově bez změny obsahu.
* Odmítnout operaci, která by skončila nefunkčním diskem, a vysvětlit proč.
* Nezapsat jediný bajt na zdrojový disk.
* Nezměnit nic na servisním počítači.
* Ověřit přenesená data a nepředstírat, že otestovala start systému.
