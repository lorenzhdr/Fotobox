using Fotobox.Core;

namespace Fotobox.Services.Camera;

/// <summary>
/// Bitrate der Videoaufnahme (H.264) je Qualitätsstufe und die daraus abgeleitete Speicherplatzschätzung.
/// Rein rechnerisch, ohne Hardware; genutzt vom Kameradienst (Profil) und von <c>VideoStorage</c> (Platzprüfung).
/// </summary>
public static class VideoBitrate
{
    /// <summary>Bits je Pixel und Bild: Hoch 0,15, Mittel 0,09, Niedrig 0,05 (1080p30 hoch ≈ 9,3 Mbit/s).</summary>
    public static double BitsPerPixel(VideoQuality quality) => quality switch
    {
        VideoQuality.High => 0.15,
        VideoQuality.Medium => 0.09,
        VideoQuality.Low => 0.05,
        _ => 0.09,
    };

    public const int MinVideoBitsPerSecond = 500_000;
    public const int MaxVideoBitsPerSecond = 25_000_000;
    /// <summary>Bildrate, wenn die Kamera keine meldet bzw. für die Schätzung ohne Kenntnis der Kamera.</summary>
    public const double DefaultFps = 30;
    public const double MinFps = 5, MaxFps = 60;
    /// <summary>AAC-Ton (Standardprofil der Windows-MP4-Aufnahme).</summary>
    public const int AudioBitsPerSecond = 192_000;
    /// <summary>Aufschlag für Container und Schwankungen der Encoder-Bitrate.</summary>
    public const double OverheadFactor = 1.10;
    /// <summary>Mindestens so viel Platz bleibt zusätzlich zur Aufnahme frei.</summary>
    public const long ReserveBytes = 200L * 1024 * 1024;
    /// <summary>Annahme, wenn die Auflösung der Kamera nicht bekannt ist.</summary>
    public static readonly CameraResolution FallbackResolution = new(1920, 1080);

    /// <summary>Bildrate in einen sinnvollen Bereich bringen (0/NaN → <see cref="DefaultFps"/>).</summary>
    public static double ClampFps(double fps) =>
        double.IsNaN(fps) || fps <= 0 ? DefaultFps : Math.Clamp(fps, MinFps, MaxFps);

    /// <summary>Video-Bitrate in Bit/s, auf <see cref="MinVideoBitsPerSecond"/>…<see cref="MaxVideoBitsPerSecond"/> geklemmt.</summary>
    public static int ForVideo(VideoQuality quality, int width, int height, double fps)
    {
        var raw = BitsPerPixel(quality) * Math.Max(width, 1) * Math.Max(height, 1) * ClampFps(fps);
        return (int)Math.Clamp(Math.Round(raw),MinVideoBitsPerSecond, MaxVideoBitsPerSecond);
    }

    /// <summary>Geschätzte Dateigröße einer Aufnahme von <paramref name="duration"/> (Video, optional Ton, mit Aufschlag).</summary>
    public static long EstimateBytes(VideoQuality quality, CameraResolution? resolution, TimeSpan duration, bool withAudio = true)
    {
        var res = resolution ?? FallbackResolution;
        var bitsPerSecond = (long)ForVideo(quality, res.Width, res.Height, DefaultFps) + (withAudio ? AudioBitsPerSecond : 0);
        var seconds = Math.Max(duration.TotalSeconds, 0);
        return (long)Math.Ceiling(bitsPerSecond * seconds / 8.0 * OverheadFactor);
    }

    /// <summary>Benötigter freier Platz: geschätzte Aufnahme plus <see cref="ReserveBytes"/>.</summary>
    public static long RequiredFreeBytes(VideoQuality quality, CameraResolution? resolution, TimeSpan duration, bool withAudio = true) =>
        EstimateBytes(quality, resolution, duration, withAudio) + ReserveBytes;
}
