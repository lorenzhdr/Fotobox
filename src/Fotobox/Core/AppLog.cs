using System.IO;
using System.Text;

namespace Fotobox.Core;

/// <summary>Einfacher, thread-sicherer Datei-Logger (logs\fotobox-yyyyMMdd.log). Wirft nie.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _directory;

    /// <summary>Aktueller Log-Ordner.</summary>
    public static string Directory
    {
        get
        {
            lock (Gate)
                return _directory ??= DefaultDirectory();
        }
    }

    /// <summary>Setzt den Log-Ordner (null = %LOCALAPPDATA%\Fotobox\logs) und legt ihn an. Wirft nie.</summary>
    public static void Initialize(string? directory = null)
    {
        try
        {
            lock (Gate)
            {
                _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
                System.IO.Directory.CreateDirectory(_directory);
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

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var now = DateTime.Now;
            var sb = new StringBuilder();
            sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] [T")
              .Append(Environment.CurrentManagedThreadId).Append("] ").Append(message);
            if (ex is not null)
                sb.AppendLine().Append(ex);
            sb.AppendLine();

            lock (Gate)
            {
                var dir = _directory ??= DefaultDirectory();
                System.IO.Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"fotobox-{now:yyyyMMdd}.log"), sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Absichtlich verschluckt.
        }
    }
}
