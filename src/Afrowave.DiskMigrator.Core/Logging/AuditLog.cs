using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Afrowave.DiskMigrator.Core.Logging;

/// <summary>Writes the audit trail of a migration run.</summary>
public interface IAuditLog : IDisposable
{
    void Write(string category, string message);
}

public sealed class NullAuditLog : IAuditLog
{
    public static readonly NullAuditLog Instance = new();

    public void Write(string category, string message)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Append-only text log. Every line passes through <see cref="LogRedaction"/> so that a
/// recovery key can never end up on disk, even if a caller accidentally passes one.
/// </summary>
public sealed partial class FileAuditLog : IAuditLog, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    public FileAuditLog(string path)
    {
        Path = path;
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
        {
            AutoFlush = true,
        };

        Write("START", $"Afrowave DiskMigrator, záznam spuštěn {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>Full path of the log file.</summary>
    public string Path { get; }

    public void Write(string category, string message)
    {
        string safe = LogRedaction.Redact(message);
        string line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}",
            DateTime.Now,
            category,
            safe);

        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [END] záznam ukončen");
            _writer.Dispose();
        }
    }
}

/// <summary>
/// Removes anything from a log line that could be a secret. This tool does not currently
/// ask for encryption keys, but the redaction is always applied so that adding such a
/// feature later can never leak a key by accident.
/// </summary>
public static partial class LogRedaction
{
    [GeneratedRegex(@"\b\d{6}(?:-\d{6}){7}\b")]
    private static partial Regex RecoveryPasswordPattern();

    [GeneratedRegex(@"(?i)\b(recovery|heslo|password|key|klíč)\s*[:=]\s*\S+")]
    private static partial Regex KeyValuePattern();

    public static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        string result = RecoveryPasswordPattern().Replace(message, "<skryto: obnovovací klíč>");
        result = KeyValuePattern().Replace(result, "<skryto>");
        return result;
    }
}
