namespace Fotobox.Core;

/// <summary>
/// Zentrale Stelle für den Kamerastart der Gastansicht: Der Ton wird beim Sessionstart festgelegt, deshalb muss vor jedem
/// <see cref="ICameraService.StartAsync"/> <see cref="ICameraService.ConfigureAudio"/> mit dem aktuellen Stand gesetzt werden.
/// </summary>
public static class CameraServiceExtensions
{
    /// <summary>Ton, den die Kamera-Session für diese Videoeinstellungen öffnen soll; <c>null</c> = ohne Mikrofon.</summary>
    public static CameraAudio? AudioFor(VideoSettings? video) =>
        video is { UsesAudio: true } ? new CameraAudio(video.MicrophoneId) : null;

    /// <summary>
    /// Setzt den Ton nach <paramref name="settings"/> (<see cref="AppSettings.Video"/>) und den gespeicherten Reglerstand
    /// (<see cref="ICameraService.ConfigureControls"/>, <see cref="AppSettings.Camera"/>) und startet die Kamera mit
    /// <see cref="AppSettings.Camera"/>. Immer diesen Weg für gespeicherte Einstellungen nehmen, damit Ton und Regler nie vergessen
    /// werden (<see cref="ICameraService.StartAsync"/> allein ändert den Reglerstand nicht).
    /// </summary>
    public static Task StartWithAudioAsync(this ICameraService camera, AppSettings settings, CancellationToken ct = default)
    {
        camera.ConfigureAudio(AudioFor(settings.Video));
        camera.ConfigureControls(settings.Camera);
        return camera.StartAsync(settings.Camera, ct);
    }
}
