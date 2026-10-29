namespace Fotobox.Core;

public sealed record CameraDevice(string Id, string Name)
{
    /// <summary>Anzeigename (auch für UI Automation in ComboBox-Einträgen).</summary>
    public override string ToString() => Name;
}
public sealed record CameraResolution(int Width, int Height)
{ public override string ToString() => $"{Width} × {Height}"; }
public enum CameraStatus { Stopped, Starting, Running, NoDevice, AccessDenied, Error }

/// <summary>Ein Vorschau-Frame (Bgra32). Pixel-Puffer kann nach Rückkehr des Handlers wiederverwendet werden → synchron kopieren.</summary>
public sealed class CameraFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required byte[] Pixels { get; init; }
}

public sealed record PrinterInfo(string Name, bool IsDefault);
/// <summary>Name ist der Schlüssel, der in PrinterSettings.PaperSizeName gespeichert wird.</summary>
public sealed record PaperSizeInfo(string Name, string DisplayName, double WidthMm, double HeightMm);

public sealed record PageSpec(double WidthMm, double HeightMm, int Dpi = 300)
{
    public int PixelWidth => (int)Math.Round(WidthMm / 25.4 * Dpi);
    public int PixelHeight => (int)Math.Round(HeightMm / 25.4 * Dpi);
    public bool IsLandscape => WidthMm > HeightMm;
    public PageSpec ToOrientation(bool landscape) =>
        landscape == IsLandscape ? this : this with { WidthMm = HeightMm, HeightMm = WidthMm };
    /// <summary>10 × 15 cm (4 × 6 Zoll) Hochformat.</summary>
    public static PageSpec Default { get; } = new(101.6, 152.4);
}

/// <summary>
/// Cancelled: nicht mehr warten (z. B. App-Ende), der Auftrag bleibt unberührt.
/// UserCancelled: auf Wunsch des Gastes gestoppt (nichts gesendet bzw. Auftrag storniert).
/// UserCancelFailed: Der Abbruch des Gastes ließ sich nicht ausführen (Auftrag nicht gefunden oder nicht stornierbar).
/// </summary>
public enum PrintOutcome { Completed, Failed, TimedOut, Cancelled, UserCancelled, UserCancelFailed }
public sealed record PrintResult(PrintOutcome Outcome, string? Message = null)
{ public bool Success => Outcome == PrintOutcome.Completed; }

public sealed record SavedPhotoSet(string LayoutPath, IReadOnlyList<string> OriginalPaths);
/// <summary>CameraChanged: Die gespeicherten Kameraeinstellungen weichen vom Stand vor dem Öffnen ab.</summary>
public sealed record AdminResult(bool SettingsChanged, bool ExitRequested, bool CameraChanged = false);

public sealed record MicrophoneDevice(string Id, string Name);
/// <summary>Ton der Kamera-Session; <paramref name="MicrophoneId"/> null = Windows-Standardmikrofon.</summary>
public sealed record CameraAudio(string? MicrophoneId);
/// <summary><paramref name="FilePath"/>: temporäre Zieldatei (<see cref="VideoTarget.TempPath"/>), wird überschrieben.</summary>
public sealed record VideoRecordingRequest(string FilePath, VideoQuality Quality);
/// <summary>Vorbereitetes Ziel einer Aufnahme: <paramref name="Folder"/> (ggf. Ausweichordner), temporäre Datei darin, Präfix und Zeitstempel für den endgültigen Namen.</summary>
public sealed record VideoTarget(string Folder, string TempPath, string FilePrefix, DateTime Timestamp);

/// <summary>Zu wenig freier Speicherplatz für eine Videoaufnahme in voller Länge.</summary>
public sealed class InsufficientDiskSpaceException(string message) : System.IO.IOException(message);

/// <summary>Sichtbarer Name und Standardordner der App (Programmdatei, Namespace und %LOCALAPPDATA%\Fotobox bleiben „Fotobox“).</summary>
public static class AppInfo
{
    public const string DisplayName = "Fotobox+";

    /// <summary>
    /// Version der laufenden App (Informationsversion der Assembly ohne den Quellcode-Zusatz „+sha“, z. B. „2.3.0-beta“);
    /// fehlt sie, die Assembly-Version, sonst „0.0.0“.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        try
        {
            var assembly = typeof(AppInfo).Assembly;
            var informational = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                var text = (plus >= 0 ? informational[..plus] : informational).Trim();
                if (text.Length > 0)
                    return text;
            }

            var assemblyVersion = assembly.GetName().Version;
            if (assemblyVersion is not null)
                return assemblyVersion.ToString(3);
        }
        catch
        {
            // Die Versionsangabe ist nur Information und darf nie etwas blockieren.
        }
        return "0.0.0";
    }
    public static string DefaultPhotoFolder =>
        BuildDefaultFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pictures", "Fotos");
    public static string DefaultVideoFolder =>
        BuildDefaultFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Videos", "Videos");
    /// <summary>Standardordner der Kisscam-Musik: <c>Musik\Fotobox+\Kisscam</c> (mit denselben Ausweichordnern wie Foto und Video).</summary>
    public static string DefaultKisscamMusicFolder =>
        System.IO.Path.Combine(
            BuildDefaultFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Music", "Musik"),
            "Kisscam");

    /// <summary>
    /// Standardordner immer absolut: Liefert Windows keinen Known Folder (leer, z. B. bei umgebogenem Profil), folgt
    /// <c>Benutzerprofil\<paramref name="profileSubfolder"/></c>, sonst <c>LocalAppData\Fotobox\<paramref name="appSubfolder"/></c>.
    /// Ein relativer Pfad würde später in der Videoaufnahme mit „Falscher Parameter“ scheitern.
    /// </summary>
    internal static string BuildDefaultFolder(string? knownFolder, string? userProfile, string? localAppData,
        string profileSubfolder, string appSubfolder)
    {
        if (IsAbsolute(knownFolder))
            return System.IO.Path.Combine(knownFolder!, DisplayName);
        if (IsAbsolute(userProfile))
            return System.IO.Path.Combine(userProfile!, profileSubfolder, DisplayName);
        if (IsAbsolute(localAppData))
            return System.IO.Path.Combine(localAppData!, "Fotobox", appSubfolder);
        // Letzter Ausweg (kein Profil auffindbar): Ordner neben den Programmdateien.
        return System.IO.Path.Combine(AppContext.BaseDirectory, appSubfolder);
    }

    private static bool IsAbsolute(string? path) =>
        !string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathFullyQualified(path);
}
