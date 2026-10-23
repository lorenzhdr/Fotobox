namespace Fotobox.Services.Camera;

/// <summary>Einheitliche Texte zu Kamerazuständen (Dienst, Gast-Problemanzeige und Admin-Ansicht).</summary>
public static class CameraMessages
{
    public const string NoDeviceTitle = "Keine Kamera gefunden";
    public const string NoDeviceHint = "Bitte die Kamera anschließen.";
    public const string AccessDeniedTitle = "Kein Zugriff auf die Kamera";
    public const string AccessDeniedHint =
        "Bitte unter Windows-Einstellungen → Datenschutz und Sicherheit → Kamera den Zugriff für Desktop-Apps erlauben.";
    public const string UnavailableTitle = "Die Kamera ist nicht verfügbar";
    public const string UnavailableHint = "Beim Starten der Kamera ist ein Fehler aufgetreten.";

    public const string NoDevice = NoDeviceTitle + ". " + NoDeviceHint;
    public const string AccessDenied = AccessDeniedTitle + ". " + AccessDeniedHint;
    public const string ConnectionLost = "Die Verbindung zur Kamera wurde unterbrochen.";
}
