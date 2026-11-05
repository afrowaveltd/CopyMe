using System.Text;

namespace Afrowave.DiskMigrator.Cli;

/// <summary>
/// Minimal terminal user interface. Deliberately plain: a technician has to be able to read
/// every line on a low resolution service monitor, so the layout stays inside 78 columns.
/// </summary>
internal sealed class ConsoleUi
{
    private const int Width = 78;

    public ConsoleUi()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // Redirected output; the encoding is then irrelevant.
        }

        UseColor = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
    }

    public bool UseColor { get; }

    public void Clear()
    {
        if (Console.IsOutputRedirected) return;

        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            // No console attached.
        }
    }

    public void Banner(string version)
    {
        Rule();
        WriteLine("  Afrowave DiskMigrator " + version, ConsoleColor.Cyan);
        WriteLine("  Bezpečný přenos obsahu disku na jiný disk", ConsoleColor.Gray);
        Rule();
    }

    public void Rule(char character = '-')
    {
        WriteLine(new string(character, Width), ConsoleColor.DarkGray);
    }

    public void Title(string text)
    {
        WriteLine(string.Empty);
        WriteLine("== " + text + " " + new string('=', Math.Max(0, Width - text.Length - 4)), ConsoleColor.Cyan);
    }

    public void Section(string text)
    {
        WriteLine(string.Empty);
        WriteLine(text, ConsoleColor.White);
        WriteLine(new string('-', Math.Min(Width, Math.Max(8, text.Length))), ConsoleColor.DarkGray);
    }

    public void Line(string text = "") => WriteLine(text);

    public void Detail(string text) => WriteLine("  " + text, ConsoleColor.Gray);

    public void Dim(string text) => WriteLine("  " + text, ConsoleColor.DarkGray);

    public void Success(string text) => WriteLine("  OK   " + text, ConsoleColor.Green);

    public void Warning(string text) => WriteLine("  !    " + text, ConsoleColor.Yellow);

    public void Error(string text) => WriteLine("  CHYBA " + text, ConsoleColor.Red);

    public void Info(string text) => WriteLine("  i    " + text, ConsoleColor.Cyan);

    public void WriteLine(string text, ConsoleColor? color = null)
    {
        if (UseColor && color is { } c)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = c;
            Console.WriteLine(text);
            Console.ForegroundColor = previous;
            return;
        }

        Console.WriteLine(text);
    }

    public void Write(string text, ConsoleColor? color = null)
    {
        if (UseColor && color is { } c)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = c;
            Console.Write(text);
            Console.ForegroundColor = previous;
            return;
        }

        Console.Write(text);
    }

    /// <summary>Reads a free-form answer, returning the default when the line is empty.</summary>
    public string Ask(string prompt, string? defaultValue = null)
    {
        WriteLine(string.Empty);
        Write("  " + prompt + (defaultValue is null ? ": " : $" [{defaultValue}]: "), ConsoleColor.White);
        string? answer = ReadLine();
        if (answer is null) return defaultValue ?? string.Empty;
        return answer.Length == 0 ? defaultValue ?? string.Empty : answer.Trim();
    }

    /// <summary>Asks for a menu choice; rejects anything that is not one of the offered keys.</summary>
    public string AskKey(string prompt, IReadOnlyCollection<string> allowed, string? defaultValue = null)
    {
        while (true)
        {
            string answer = Ask(prompt, defaultValue).ToUpperInvariant();
            if (allowed.Contains(answer)) return answer;
            Error($"Neplatná volba. Povolené hodnoty: {string.Join(", ", allowed)}");
        }
    }

    public bool AskYesNo(string prompt, bool defaultYes = false)
    {
        string answer = AskKey(prompt + " (a/n)", ["A", "N"], defaultYes ? "A" : "N");
        return answer == "A";
    }

    /// <summary>Reads an integer inside a range, re-asking until it is valid.</summary>
    public int AskNumber(string prompt, int minimum, int maximum, int? defaultValue = null)
    {
        while (true)
        {
            string answer = Ask(prompt, defaultValue?.ToString());
            if (int.TryParse(answer, out int value) && value >= minimum && value <= maximum)
            {
                return value;
            }

            Error($"Zadejte prosím číslo v rozsahu {minimum} až {maximum}.");
        }
    }

    /// <summary>
    /// Destructive confirmation: the technician must retype a phrase. Returns false on anything else.
    /// </summary>
    public bool ConfirmByTyping(string phrase, string instruction)
    {
        WriteLine(string.Empty);
        WriteLine("  " + instruction, ConsoleColor.Yellow);
        Write("  Napište \"" + phrase + "\" pro potvrzení: ", ConsoleColor.White);
        string? answer = ReadLine();
        return answer is not null && string.Equals(answer.Trim(), phrase, StringComparison.OrdinalIgnoreCase);
    }

    public void WaitForEnter(string message = "Pokračujte stiskem Enter…")
    {
        WriteLine(string.Empty);
        Write("  " + message, ConsoleColor.DarkGray);
        ReadLine();
    }

    /// <summary>Reads a line and never lets Ctrl+C or end-of-input crash the tool.</summary>
    private static string? ReadLine()
    {
        try
        {
            return Console.ReadLine();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Formats a progress bar line that fits the console width.</summary>
    public string ProgressLine(string label, double fraction, params string?[] optionalParts) =>
        Core.Text.ProgressLineFormatter.Format(label, fraction, Width, optionalParts);

    public void WriteProgress(string text)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        int previousWidth = 0;
        try
        {
            previousWidth = Console.CursorLeft;
        }
        catch (IOException)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append('\r');
        builder.Append(text);
        if (text.Length < previousWidth)
        {
            builder.Append(new string(' ', previousWidth - text.Length));
        }

        Console.Write(builder.ToString());
    }

    public void EndProgress()
    {
        if (!Console.IsOutputRedirected)
        {
            Console.WriteLine();
        }
    }
}
