namespace Afrowave.DiskMigrator.Cli;

/// <summary>Command line options. The tool is fully usable without any of them.</summary>
internal sealed class CommandLineOptions
{
    public bool ShowHelp { get; private set; }

    public bool ShowVersion { get; private set; }

    public int? SourceDiskNumber { get; private set; }

    public int? TargetDiskNumber { get; private set; }

    public string? TemporaryPath { get; private set; }

    public string? LogPath { get; private set; }

    public string? SimulateDirectory { get; private set; }

    public bool DryRun { get; private set; }

    public bool AssumeYes { get; private set; }

    public bool NewIdentifiers { get; private set; }

    public bool SkipVerification { get; private set; }

    public bool ZeroFreeClusters { get; private set; }

    public bool IgnoreTemporarySpaceCheck { get; private set; }

    public string? ParseError { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            string? inlineValue = null;

            int equals = argument.IndexOf('=');
            if (argument.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                inlineValue = argument[(equals + 1)..];
                argument = argument[..equals];
            }

            string? TakeValue(string name)
            {
                if (inlineValue is not null) return inlineValue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    i++;
                    return args[i];
                }

                options.ParseError = $"Parametr {name} vyžaduje hodnotu.";
                return null;
            }

            switch (argument.ToLowerInvariant())
            {
                case "--help" or "-h" or "/?":
                    options.ShowHelp = true;
                    return options;

                case "--version" or "-v":
                    options.ShowVersion = true;
                    return options;

                case "--source" or "-s":
                    if (int.TryParse(TakeValue("--source"), out int source)) options.SourceDiskNumber = source;
                    break;

                case "--target" or "-t":
                    if (int.TryParse(TakeValue("--target"), out int target)) options.TargetDiskNumber = target;
                    break;

                case "--temp":
                    options.TemporaryPath = TakeValue("--temp");
                    break;

                case "--log":
                    options.LogPath = TakeValue("--log");
                    break;

                case "--simulate":
                    options.SimulateDirectory = TakeValue("--simulate");
                    break;

                case "--dry-run" or "--kontrola":
                    options.DryRun = true;
                    break;

                case "--yes" or "-y":
                    options.AssumeYes = true;
                    break;

                case "--new-ids":
                    options.NewIdentifiers = true;
                    break;

                case "--no-verify":
                    options.SkipVerification = true;
                    break;

                case "--zero-free":
                    options.ZeroFreeClusters = true;
                    break;

                case "--ignore-temp-check":
                    options.IgnoreTemporarySpaceCheck = true;
                    break;

                default:
                    options.ParseError ??= $"Neznámý parametr: {argument}";
                    break;
            }

            if (options.ParseError is not null) return options;
        }

        if (options.AssumeYes && (options.SourceDiskNumber is null || options.TargetDiskNumber is null))
        {
            options.ParseError = "Parametr --yes lze použít jen společně s --source a --target.";
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            Afrowave DiskMigrator – bezpečný přenos obsahu disku
            =====================================================

            Spuštění bez parametrů otevře interaktivní nabídku. To je doporučený způsob
            použití pro techniky.

            Použití:
              AfrowaveDiskMigrator.exe [parametry]

            Parametry:
              -h, --help              Zobrazí tuto nápovědu.
              -v, --version           Zobrazí verzi nástroje.
              -s, --source <cislo>    Předvybere zdrojový disk (jinak se nabídne menu).
              -t, --target <cislo>    Předvybere cílový disk.
                  --temp <cesta>      Dočasné úložiště pro obraz WIM, např. E:\
                  --log <cesta>       Kam zapisovat protokol o průběhu.
                  --dry-run           Kontrolní režim: plán se zobrazí, ale nic se nezapíše.
              -y, --yes               Přeskočí potvrzení (jen s --source a --target).
                  --new-ids           Cílový disk dostane nové identifikátory místo původních.
                  --no-verify         Přeskočí kontrolu zapsaných dat proti zdroji.
                  --zero-free         Vynuluje volné shluky na cílovém disku.
                  --ignore-temp-check Pokračuje i při nedostatku místa pro obraz WIM.
                  --simulate <adresar> Simulační režim nad soubory disk*.img (bez fyzických disků).

            Cílový disk:
              Vybrat lze prázdný nebo obsazený, ale bezpečně rozpoznaný disk.
              U obsazeného cíle se nejprve ověří proveditelnost plánu.
              Vymazání VŠECH oddílů vyžaduje samostatné dvojí potvrzení.
              Ani --yes potvrzení vymazání nepřeskočí; --dry-run nemaže nic.

            Návratové kódy:
              0  úspěch (nebo uživatel potvrzení odmítl - na disky se nepsalo)
              1  neočekávaná chyba
              2  nepodporovaná platforma
              3  nástroj neběží jako správce
              4  nebyl nalezen žádný disk
              5  zdrojový disk nelze použít
              6  migrace odmítnuta (plán není bezpečně proveditelný)
              7  migrace selhala nebo byla přerušena

            Bezpečnost:
              * Disky vybírá vždy uživatel - nástroj žádný nevybere sám.
              * Obsazený cíl lze vymazat jen po výslovném dvojím potvrzení.
              * Zdrojový disk nástroj sám převezme: svazky vyprázdní a odpojí,
                disk přepne do režimu read-only a po skončení vše vrátí.
                Technik nemusí nic odpojovat, ani když disk už má písmena jednotek.
              * Zdrojový disk je po celou dobu migrace otevřen výhradně pro čtení.
              * Offline instalace Windows na zdrojovém disku zůstává volitelná.
              * Disky servisního počítače a disk, ze kterého nástroj běží, jsou chráněny.
              * Před zápisem se znovu ověří identita obou disků a prázdnost cíle.
              * Obsazený cíl lze vymazat až po úspěšném plánování a dodatečném ověření identity.
              * --yes nikdy nestačí k odstranění existujících oddílů.
              * Cílový disk se přepisuje až po výslovném potvrzení.
              * Když si nástroj není jistý, operaci odmítne a vysvětlí proč.
            """);
    }
}
