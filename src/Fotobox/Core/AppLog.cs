using System.Globalization;
using System.IO;
using System.Text;

namespace Fotobox.Core;

/// <summary>
/// Einfacher, thread-sicherer Datei-Logger (logs\fotobox-yyyyMMdd.log). Wirft nie.
/// Tagesdateien älter als 30 Tage werden beim Start gelöscht.
/// </summary>
public static class AppLog
{
    /// <summary>Aufbewahrung der Tagesdateien in Tagen.</summary>
    internal const int RetentionDays = 30;

    private static readonly object Gate = new();
    private static string? _directory;

    /// <summary>Setzt den Log-Ordner (null = %LOCALAPPDATA%\Fotobox\logs) und legt ihn an. Wirft nie.</summary>
    public static void Initialize(string? directory = null)
    {
        try
        {
            lock (Gate)
            {
                _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
                System.IO.Directory.CreateDirectory(_directory);
                DeleteOldLogs(_directory, DateTime.Now, RetentionDays);
            }
        }
        catch
        {
            // Logging darf die Anwendung nie beeinträchtigen.
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>Jeder Eintrag wird sofort geschrieben (kein Puffer); dient als Sync-Punkt beim Beenden.</summary>
    public static void Flush()
    {
        lock (Gate)
        {
        }
    }

    private static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fotobox", "logs");

    /// <summary>Zeitstempel einer Logzeile, bewusst kulturunabhängig (Zeittrenner, Kalender).</summary>
    public static string FormatTimestamp(DateTime time) =>
        time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Dateiname der Tagesdatei, bewusst kulturunabhängig (z. B. kein buddhistisches Jahr).</summary>
    public static string LogFileName(DateTime time) =>
        "fotobox-" + time.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";

    /// <summary>
    /// Löscht Tagesdateien <c>fotobox-yyyyMMdd.log</c> im Ordner (nicht rekursiv), deren Datum im Namen vor
    /// <c>now.Date - keepDays</c> liegt. Dateien mit anderem Namensschema bleiben. Wirft nie, loggt bewusst nicht.
    /// Gibt die Anzahl gelöschter Dateien zurück.
    /// </summary>
    internal static int DeleteOldLogs(string directory, DateTime now, int keepDays)
    {
        var deleted = 0;
        try
        {
            var limit = now.Date.AddDays(-keepDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "fotobox-*.log"))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    var datePart = Path.GetFileNameWithoutExtension(name)["fotobox-".Length..];
                    if (!DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        continue;
                    // Die Suche nach "*.log" kann unter Windows auch längere Endungen treffen; nur das exakte Schema löschen.
                    if (!string.Equals(name, LogFileName(date), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (date >= limit)
                        continue;

                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    // Einzelne Datei (gesperrt, schreibgeschützt …) überspringen.
                }
            }
        }
        catch
        {
            // Ordner fehlt oder nicht lesbar: nichts aufzuräumen.
        }
        return deleted;
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var now = DateTime.Now;
            var sb = new StringBuilder();
            sb.Append(FormatTimestamp(now)).Append(" [").Append(level).Append("] [T")
              .Append(Environment.CurrentManagedThreadId).Append("] ").Append(message);
            if (ex is not null)
                sb.AppendLine().Append(ex);
            sb.AppendLine();

            lock (Gate)
            {
                var dir = _directory ??= DefaultDirectory();
                System.IO.Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, LogFileName(now)), sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Absichtlich verschluckt.
        }
    }
}
