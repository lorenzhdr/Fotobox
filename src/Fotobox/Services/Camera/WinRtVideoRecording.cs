using System.IO;
using Fotobox.Core;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;

namespace Fotobox.Services.Camera;

/// <summary>
/// Eine laufende MP4-Aufnahme einer <see cref="MediaCapture"/>-Session (Gegenstück zu <see cref="WinRtCameraService"/>).
/// Das Beenden ist idempotent und threadsicher: Alle Aufrufer (Gast-Stopp, Kamera-Abbau, Dispose) warten auf dieselbe
/// Operation. Sie hängt bewusst nicht am Abbruchtoken eines einzelnen Aufrufers, sondern hat eine eigene Frist.
/// </summary>
internal sealed class WinRtVideoRecording : IVideoRecording
{
    /// <summary>Frist für das Abschließen der Datei (Encoder leeren, MP4-Index schreiben).</summary>
    internal static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(15);

    private readonly MediaCapture _capture;
    private readonly string _filePath;
    private readonly Action<WinRtVideoRecording> _completed;
    private readonly object _lock = new();
    private Task? _finishTask;
    private int _finishRequested;
    private int _faulted;

    internal WinRtVideoRecording(MediaCapture capture, string filePath, bool hasAudio, Action<WinRtVideoRecording> completed)
    {
        _capture = capture;
        _filePath = filePath;
        HasAudio = hasAudio;
        _completed = completed;
    }

    public bool HasAudio { get; }

    public event EventHandler<string>? Faulted;

    /// <summary>Das MediaCapture, auf dem diese Aufnahme läuft (Abgleich mit dem Sender von Ereignissen).</summary>
    internal MediaCapture Capture => _capture;

    /// <summary>Meldet das Ende durch das Gerät, höchstens einmal und nur, solange niemand die Aufnahme beendet hat.</summary>
    internal void RaiseFaulted(string reason)
    {
        if (Volatile.Read(ref _finishRequested) != 0)
            return;
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
            return;

        AppLog.Warn($"Videoaufnahme vom Gerät beendet: {reason}");
        try
        {
            Faulted?.Invoke(this, reason);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Faulted-Handler der Videoaufnahme hat eine Ausnahme ausgelöst.", ex);
        }
    }

    public Task StopAsync(CancellationToken ct = default) => FinishAsync().WaitAsync(ct);

    /// <summary>Beendet die Aufnahme (einmal, danach derselbe Task). Hat eine eigene Frist; wirft, wenn keine Datei entstanden ist.</summary>
    internal Task FinishAsync()
    {
        lock (_lock)
        {
            if (_finishTask is null)
            {
                Volatile.Write(ref _finishRequested, 1);
                // Threadpool (MTA), unabhängig vom aufrufenden Kontext und vom Token eines einzelnen Aufrufers.
                _finishTask = Task.Run(FinishCoreAsync);
            }

            return _finishTask;
        }
    }

    private async Task FinishCoreAsync()
    {
        Exception? stopError = null;
        var timedOut = false;
        try
        {
            await _capture.StopRecordAsync().AsTask().WaitAsync(FinishTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timedOut = true;
            AppLog.Warn("Das Beenden der Videoaufnahme hat die Frist überschritten.");
        }
        catch (Exception ex)
        {
            stopError = ex;
            AppLog.Warn("Videoaufnahme konnte nicht sauber beendet werden.", ex);
        }
        finally
        {
            _completed(this);
        }

        if (timedOut)
            throw new IOException("Das Beenden der Videoaufnahme dauert zu lange. Das Video ist evtl. unvollständig.");

        long length;
        try
        {
            var info = new FileInfo(_filePath);
            length = info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            length = 0;
        }

        if (length == 0)
            throw new IOException("Es wurde kein Video aufgenommen (Datei fehlt oder ist leer).", stopError);

        if (stopError is not null)
            AppLog.Warn("Die Videodatei bleibt trotz Fehler beim Beenden erhalten (Teilvideo, evtl. nicht abspielbar).");
        else
            AppLog.Info($"Videoaufnahme abgeschlossen ({length / 1024} KB).");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FinishAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Videoaufnahme beim Freigeben nicht sauber beendet.", ex);
        }
    }

    /// <summary>
    /// MP4-Profil (H.264 + AAC) passend zum aktiven Kameraformat, damit nicht umskaliert wird. Ohne Ton kein Audio-Profil.
    /// Breite/Höhe werden auf gerade Werte abgerundet (Encoder-Anforderung).
    /// </summary>
    internal static MediaEncodingProfile CreateProfile(VideoQuality quality, CameraResolution resolution, double fps, bool withAudio)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        var width = resolution.Width & ~1;
        var height = resolution.Height & ~1;
        var rate = VideoBitrate.ClampFps(fps);

        var video = profile.Video;
        video.Width = (uint)Math.Max(width, 2);
        video.Height = (uint)Math.Max(height, 2);
        video.FrameRate.Numerator = (uint)Math.Round(rate * 1000);
        video.FrameRate.Denominator = 1000;
        video.Bitrate = (uint)VideoBitrate.ForVideo(quality, width, height, rate);
        video.PixelAspectRatio.Numerator = 1;
        video.PixelAspectRatio.Denominator = 1;

        if (!withAudio)
            profile.Audio = null;
        return profile;
    }
}
