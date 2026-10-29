namespace Fotobox.Core;

/// <summary>
/// Die geräte- und pfadabhängigen Einstellungen, die nie exportiert und nie importiert werden (der Zielrechner behält seine
/// eigenen): Kamera (Gerät, Auflösung, Reglerwerte und Profile; der Flimmerschutz <c>Camera.Powerline</c> ist übertragbar), Drucker/Papier, Foto- und Videoordner, Mikrofon, Monitor der Gastansicht, Zoom der Admin-Ansicht und Musikdatei der Kisscam. Einzige Quelle für diese
/// Felder; ein Test erzwingt, dass jedes neue Feld von <see cref="AppSettings"/> entweder hier oder in dessen Liste
/// der übertragbaren Felder eingeordnet wird.
/// </summary>
public static class DeviceSpecificSettings
{
    /// <summary>Die Felder als „Abschnitt.Eigenschaft“ (entspricht den JSON-Namen in settings.json).</summary>
    public static IReadOnlyList<string> Paths { get; } =
    [
        "Camera.DeviceId",
        "Camera.ResolutionWidth",
        "Camera.ResolutionHeight",
        "Camera.ControlsDeviceId",
        "Camera.ControlValues",
        "Camera.ControlProfiles",
        "Printer.PrinterName",
        "Printer.PaperSizeName",
        "Printer.PaperWidthMm",
        "Printer.PaperHeightMm",
        "Storage.PhotoFolder",
        "Video.MicrophoneId",
        "Video.MicrophoneName",
        "Video.Folder",
        "Appearance.GuestMonitorId",
        "Appearance.GuestMonitorName",
        "Appearance.AdminZoomPercent",
        "Heart.KisscamMusicPath",
    ];

    /// <summary>Setzt alle gerätespezifischen Felder auf den Standard eines neuen Abschnitts (nicht gewählt = <c>null</c>). Fehlende Abschnitte werden übersprungen.</summary>
    public static void Clear(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (s.Camera is { } camera)
        {
            camera.DeviceId = null;
            camera.ResolutionWidth = null;
            camera.ResolutionHeight = null;
            camera.ControlsDeviceId = null;
            camera.ControlValues = [];
            camera.ControlProfiles = [];
        }
        if (s.Printer is { } printer)
        {
            printer.PrinterName = null;
            printer.PaperSizeName = null;
            printer.PaperWidthMm = null;
            printer.PaperHeightMm = null;
        }
        if (s.Storage is { } storage)
            storage.PhotoFolder = null;
        if (s.Video is { } video)
        {
            video.MicrophoneId = null;
            video.MicrophoneName = null;
            video.Folder = null;
        }
        if (s.Appearance is { } appearance)
        {
            appearance.GuestMonitorId = null;
            appearance.GuestMonitorName = null;
            appearance.AdminZoomPercent = SettingsLimits.AdminZoomDefault;
        }
        if (s.Heart is { } heart)
            heart.KisscamMusicPath = null;
    }

    /// <summary>Kopiert die gerätespezifischen Felder von <paramref name="source"/> nach <paramref name="target"/>; alles andere bleibt unverändert.</summary>
    public static void CopyFrom(AppSettings source, AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        target.Camera.DeviceId = source.Camera.DeviceId;
        target.Camera.ResolutionWidth = source.Camera.ResolutionWidth;
        target.Camera.ResolutionHeight = source.Camera.ResolutionHeight;
        target.Camera.ControlsDeviceId = source.Camera.ControlsDeviceId;
        target.Camera.ControlValues = CameraSettings.CloneValues(source.Camera.ControlValues);
        target.Camera.ControlProfiles = CameraSettings.CloneProfiles(source.Camera.ControlProfiles);

        target.Printer.PrinterName = source.Printer.PrinterName;
        target.Printer.PaperSizeName = source.Printer.PaperSizeName;
        target.Printer.PaperWidthMm = source.Printer.PaperWidthMm;
        target.Printer.PaperHeightMm = source.Printer.PaperHeightMm;

        target.Storage.PhotoFolder = source.Storage.PhotoFolder;

        target.Video.MicrophoneId = source.Video.MicrophoneId;
        target.Video.MicrophoneName = source.Video.MicrophoneName;
        target.Video.Folder = source.Video.Folder;

        target.Appearance.GuestMonitorId = source.Appearance.GuestMonitorId;
        target.Appearance.GuestMonitorName = source.Appearance.GuestMonitorName;
        target.Appearance.AdminZoomPercent = source.Appearance.AdminZoomPercent;

        target.Heart.KisscamMusicPath = source.Heart.KisscamMusicPath;
    }
}
