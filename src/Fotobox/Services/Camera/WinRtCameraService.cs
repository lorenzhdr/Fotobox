using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Fotobox.Services.Camera;

/// <summary>Kamera-Dienst auf Basis von Windows.Media.Capture (MediaCapture + MediaFrameReader).</summary>
public sealed class WinRtCameraService : ICameraService
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(2);
    // Obergrenze je Abbauschritt: Ein hängender Treiber darf Neustart und Beenden nicht unbegrenzt blockieren.
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(3);

    // Start/Stop/Resolutions sind serialisiert; alle Felder der Session werden nur unter _gate geändert.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaCapture? _capture;
    private volatile MediaFrameReader? _reader;
    private MediaFrameSource? _source;
    private string? _activeDeviceId;
    private CameraSettings? _lastSettings;
    private CancellationTokenSource? _reconnectCts;
    private int _generation;
    private int _disposed;

    private readonly object _stateLock = new();
    private CameraStatus _status = CameraStatus.Stopped;
    private string? _statusMessage;
    private CameraResolution? _activeResolution;

    // Frame-Zustand: Schreiben nur im FrameArrived-Handler (seriell), Lesen durch Capture unter _frameLock.
    private readonly object _frameLock = new();
    private byte[]? _pixels;
    private CameraFrame? _frame;
    private List<TaskCompletionSource<CameraFrame>> _waiters = [];

    public CameraStatus Status { get { lock (_stateLock) return _status; } }
    public string? StatusMessage { get { lock (_stateLock) return _statusMessage; } }
    public CameraResolution? ActiveResolution { get { lock (_stateLock) return _activeResolution; } }

    public event EventHandler? StatusChanged;
    public event EventHandler<CameraFrame>? FrameArrived;

    public async Task<IReadOnlyList<CameraDevice>> GetDevicesAsync()
    {
        try
        {
            var infos = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return infos.Select(i => new CameraDevice(i.Id, i.Name)).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Error("Kameras konnten nicht aufgelistet werden.", ex);
            return [];
        }
    }

    // Wie alle MediaCapture-Zugriffe auf dem Threadpool (MTA), nie im Kontext des aufrufenden UI-Threads.
    public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) =>
        Task.Run(() => GetResolutionsCoreAsync(deviceId));

    private async Task<IReadOnlyList<CameraResolution>> GetResolutionsCoreAsync(string deviceId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_source is not null && _activeDeviceId == deviceId)
                return CameraFormatSelector.DistinctResolutions(ToCandidates(_source.SupportedFormats));

            using var capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            });
            var source = PickColorSource(capture);
            return source is null
                ? []
                : CameraFormatSelector.DistinctResolutions(ToCandidates(source.SupportedFormats));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Auflösungen der Kamera konnten nicht ermittelt werden.", ex);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StartAsync(CameraSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Volatile.Read(ref _disposed) != 0)
            return Task.CompletedTask;

        CancelReconnect();
        var generation = Interlocked.Increment(ref _generation);

        // Auf- und Abbau einer Session laufen immer auf dem Threadpool (MTA), nie im Kontext des aufrufenden
        // UI-Threads (STA): Wird ein WinRT-Ereignis in einem anderen Apartment abgemeldet als angemeldet,
        // scheitert CsWinRT mit OutOfMemoryException bzw. stürzt mit einer Zugriffsverletzung ab.
        return Task.Run(() => StartSerializedAsync(settings, generation, ct));
    }

    private async Task StartSerializedAsync(CameraSettings settings, int generation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _lastSettings = CopyOf(settings);
            await StartCoreAsync(_lastSettings, background: false, ct).ConfigureAwait(false);

            // Einziger Wiederholmechanismus: Scheitert der Start (kein Gerät, Zugriff verweigert, Fehler),
            // versucht die Schleife es mit denselben Einstellungen erneut, bis ein neuer Start/Stopp sie ablöst.
            // Unter dem Gate gestartet, damit ein paralleler Start die Schleife nicht versehentlich abbricht.
            if (IsRetryable(Status))
                StartReconnectLoop(generation);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsRetryable(CameraStatus status) =>
        status is CameraStatus.NoDevice or CameraStatus.AccessDenied or CameraStatus.Error;

    public Task StopAsync()
    {
        CancelReconnect();
        Interlocked.Increment(ref _generation);
        return Task.Run(StopSerializedAsync);
    }

    private async Task StopSerializedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetStatus(CameraStatus.Stopped, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default)
    {
        if (Status != CameraStatus.Running)
            throw new InvalidOperationException("Die Kamera läuft nicht.");

        var tcs = new TaskCompletionSource<CameraFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_frameLock)
            _waiters.Add(tcs);

        CameraFrame frame;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CaptureTimeout);
            using var registration = timeout.Token.Register(() => tcs.TrySetCanceled());
            frame = await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Die Kamera hat kein Bild geliefert.");
        }
        finally
        {
            lock (_frameLock)
                _waiters.Remove(tcs);
        }

        // Webcams liefern teils Alpha = 0; das Foto ist immer deckend.
        var pixels = frame.Pixels;
        var words = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(0, frame.Stride * frame.Height));
        for (var i = 0; i < words.Length; i++)
            words[i] |= 0xFF000000u;

        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null,
            pixels, frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        CancelReconnect();
        Interlocked.Increment(ref _generation);
        return new ValueTask(Task.Run(StopSerializedAsync));
    }

    // ---- Session-Aufbau (unter _gate) ----

    // background = Wiederholversuch aus einem Fehlerzustand: Der Status bleibt unverändert (kein Starting),
    // damit Problemanzeige und Vorschau-Overlay nicht alle paar Sekunden flackern.
    private async Task StartCoreAsync(CameraSettings settings, bool background, CancellationToken ct)
    {
        // Starting vor dem Abbau melden, damit während des Abbaus kein Foto mehr angenommen wird.
        if (!background)
            SetStatus(CameraStatus.Starting, "Kamera wird gestartet …");
        await StopCoreAsync().ConfigureAwait(false);

        MediaCapture? capture = null;
        MediaFrameReader? reader = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var devices = await GetDevicesAsync().ConfigureAwait(false);
            if (devices.Count == 0)
            {
                SetStatus(CameraStatus.NoDevice, CameraMessages.NoDevice);
                return;
            }

            var device = devices.FirstOrDefault(d => d.Id == settings.DeviceId) ?? devices[0];
            AppLog.Info($"Kamera wird gestartet: {device.Name}");

            capture = await InitializeCaptureAsync(device.Id).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var source = PickColorSource(capture);
            if (source is null)
            {
                capture.Dispose();
                capture = null;
                SetStatus(CameraStatus.Error, "Die Kamera liefert kein Farbbild.");
                return;
            }

            await ApplyFormatAsync(source, settings).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            reader = await CreateReaderAsync(capture, source).ConfigureAwait(false);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            // Erst eintragen, dann anmelden: Die Handler verwerfen Ereignisse fremder (alter) Objekte per ReferenceEquals.
            // Abgemeldet wird bewusst nie (siehe DisposeSessionObjectsAsync).
            _capture = capture;
            _reader = reader;
            _source = source;
            _activeDeviceId = device.Id;
            reader.FrameArrived += OnFrameArrived;
            capture.Failed += OnCaptureFailed;

            var startStatus = await reader.StartAsync();
            if (startStatus != MediaFrameReaderStartStatus.Success)
            {
                await StopCoreAsync().ConfigureAwait(false);
                capture = null;
                reader = null;
                if (startStatus == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable)
                    SetStatus(CameraStatus.Error, "Die Kamera wird bereits von einer anderen Anwendung verwendet.");
                else
                    SetStatus(CameraStatus.Error, $"Die Kamera konnte nicht gestartet werden ({startStatus}).");
                return;
            }

            var format = source.CurrentFormat?.VideoFormat;
            var resolution = format is { Width: > 0, Height: > 0 }
                ? new CameraResolution((int)format.Width, (int)format.Height)
                : null;
            lock (_stateLock)
                _activeResolution = resolution;
            AppLog.Info($"Kamera läuft: {resolution}");
            SetStatus(CameraStatus.Running, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Error("Kamerazugriff verweigert.", ex);
            await CleanupAsync(capture, reader).ConfigureAwait(false);
            SetStatus(CameraStatus.AccessDenied, CameraMessages.AccessDenied);
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(capture, reader).ConfigureAwait(false);
            SetStatus(CameraStatus.Stopped, null);
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("Kamera konnte nicht gestartet werden.", ex);
            await CleanupAsync(capture, reader).ConfigureAwait(false);
            SetStatus(CameraStatus.Error, $"Die Kamera konnte nicht gestartet werden: {ex.Message}");
        }
    }

    private async Task CleanupAsync(MediaCapture? capture, MediaFrameReader? reader)
    {
        // Bereits in _capture/_reader eingetragene Objekte räumt StopCoreAsync auf; nur noch nicht eingetragene separat.
        var registered = ReferenceEquals(_capture, capture) && ReferenceEquals(_reader, reader);
        await StopCoreAsync().ConfigureAwait(false);
        if (!registered)
            await DisposeSessionObjectsAsync(capture, reader).ConfigureAwait(false);
    }

    private static async Task<MediaCapture> InitializeCaptureAsync(string deviceId)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(CreateInitSettings(deviceId, MediaCaptureSharingMode.ExclusiveControl));
            return capture;
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Exklusiver Kamerazugriff nicht möglich, versuche gemeinsamen Zugriff.", ex);
            capture.Dispose();
        }

        capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(CreateInitSettings(deviceId, MediaCaptureSharingMode.SharedReadOnly));
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private static MediaCaptureInitializationSettings CreateInitSettings(string deviceId, MediaCaptureSharingMode mode) => new()
    {
        VideoDeviceId = deviceId,
        StreamingCaptureMode = StreamingCaptureMode.Video,
        MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        SharingMode = mode,
    };

    private static MediaFrameSource? PickColorSource(MediaCapture capture)
    {
        var colorSources = capture.FrameSources.Values
            .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            .ToList();
        return colorSources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
               ?? colorSources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
               ?? colorSources.FirstOrDefault();
    }

    private static List<CameraFormatCandidate> ToCandidates(IEnumerable<MediaFrameFormat> formats) =>
        formats.Select(f =>
        {
            var v = f.VideoFormat;
            var rate = f.FrameRate;
            var fps = rate is { Denominator: > 0 } ? (double)rate.Numerator / rate.Denominator : 0;
            return new CameraFormatCandidate((int)(v?.Width ?? 0), (int)(v?.Height ?? 0), fps);
        }).ToList();

    private static async Task ApplyFormatAsync(MediaFrameSource source, CameraSettings settings)
    {
        try
        {
            var formats = source.SupportedFormats.ToList();
            var index = CameraFormatSelector.SelectIndex(ToCandidates(formats), settings.ResolutionWidth, settings.ResolutionHeight);
            if (index >= 0)
                await source.SetFormatAsync(formats[index]);
        }
        catch (Exception ex)
        {
            // z. B. kein exklusiver Zugriff: mit dem Standardformat weiterarbeiten.
            AppLog.Warn("Kameraformat konnte nicht gesetzt werden.", ex);
        }
    }

    private static async Task<MediaFrameReader> CreateReaderAsync(MediaCapture capture, MediaFrameSource source)
    {
        try
        {
            return await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Frame-Reader mit Bgra8-Ausgabe nicht möglich, verwende Originalformat.", ex);
            return await capture.CreateFrameReaderAsync(source);
        }
    }

    // ---- Session-Abbau (unter _gate) ----

    private async Task StopCoreAsync()
    {
        var capture = _capture;
        var reader = _reader;
        _capture = null;
        _reader = null;
        _source = null;
        _activeDeviceId = null;

        List<TaskCompletionSource<CameraFrame>> waiters;
        lock (_frameLock)
        {
            waiters = _waiters;
            _waiters = [];
            _frame = null;
            _pixels = null;
        }
        foreach (var w in waiters)
            w.TrySetException(new InvalidOperationException("Die Kamera wurde gestoppt."));

        lock (_stateLock)
            _activeResolution = null;

        await DisposeSessionObjectsAsync(capture, reader).ConfigureAwait(false);
    }

    // Die Ereignisse (FrameArrived, Failed) werden NICHT abgemeldet: Das explizite „-=“ auf WinRT-Ereignissen
    // scheitert in CsWinRT (UnsubscribeFromNative) mit OutOfMemoryException bzw. stürzt mit 0xC0000005 ab.
    // Dispose gibt die Handler mit dem Objekt frei; Nachzügler-Ereignisse verwerfen die Handler selbst, weil
    // der Sender nicht mehr dem aktuellen _reader/_capture entspricht.
    private async Task DisposeSessionObjectsAsync(MediaCapture? capture, MediaFrameReader? reader)
    {
        if (reader is not null)
        {
            try { await reader.StopAsync().AsTask().WaitAsync(TeardownTimeout).ConfigureAwait(false); }
            catch (TimeoutException) { AppLog.Warn("Frame-Reader reagiert beim Stoppen nicht, er wird trotzdem freigegeben."); }
            catch (Exception ex) { AppLog.Warn("Frame-Reader konnte nicht gestoppt werden.", ex); }
            await DisposeWithTimeoutAsync(reader, "Frame-Reader").ConfigureAwait(false);
        }

        if (capture is not null)
            await DisposeWithTimeoutAsync(capture, "MediaCapture").ConfigureAwait(false);
    }

    // Dispose läuft auf dem Threadpool; hängt der Treiber, wird nach der Frist weitergemacht (mit Log),
    // damit der Neustart die Sperre nicht unbegrenzt hält.
    private static async Task DisposeWithTimeoutAsync(IDisposable instance, string name)
    {
        try
        {
            await Task.Run(instance.Dispose).WaitAsync(TeardownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AppLog.Warn($"{name} wurde nicht rechtzeitig freigegeben, der Abbau läuft im Hintergrund weiter.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{name} konnte nicht freigegeben werden.", ex);
        }
    }

    // ---- Frames ----

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!ReferenceEquals(sender, _reader))
            return;

        try
        {
            using var reference = sender.TryAcquireLatestFrame();
            var source = reference?.VideoMediaFrame?.SoftwareBitmap;
            if (source is null)
                return;

            SoftwareBitmap? converted = null;
            try
            {
                var bitmap = source;
                // Nur fremde Pixelformate umwandeln; der Alphamodus ist egal (Vorschau ignoriert Alpha, das Foto wird deckend gesetzt).
                if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                {
                    converted = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    bitmap = converted;
                }

                var width = bitmap.PixelWidth;
                var height = bitmap.PixelHeight;
                var stride = width * 4;
                CameraFrame frame;
                List<TaskCompletionSource<CameraFrame>>? waiters = null;
                CameraFrame? waiterFrame = null;

                lock (_frameLock)
                {
                    if (!ReferenceEquals(sender, _reader))
                        return;

                    if (_pixels is null || _frame is null || _frame.Width != width || _frame.Height != height)
                    {
                        _pixels = new byte[stride * height];
                        _frame = new CameraFrame { Width = width, Height = height, Stride = stride, Pixels = _pixels };
                    }

                    bitmap.CopyToBuffer(_pixels.AsBuffer());
                    frame = _frame;

                    if (_waiters.Count > 0)
                    {
                        waiters = _waiters;
                        _waiters = [];
                        waiterFrame = new CameraFrame
                        {
                            Width = width, Height = height, Stride = stride, Pixels = (byte[])_pixels.Clone(),
                        };
                    }
                }

                if (waiters is not null)
                {
                    foreach (var w in waiters)
                        w.TrySetResult(waiterFrame!);
                }

                // Puffer wird erst vom nächsten (seriellen) FrameArrived wieder beschrieben.
                FrameArrived?.Invoke(this, frame);
            }
            finally
            {
                converted?.Dispose();
                source.Dispose(); // gehört uns: pro Frame freigeben, sonst hängt der native Puffer bis zur GC
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Fehler beim Verarbeiten eines Kamerabilds.", ex);
        }
    }

    // ---- Verbindungsverlust ----

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs e)
    {
        // Ereignisse beim Abbau oder von einer bereits ersetzten Session sind nicht mehr relevant.
        if (!ReferenceEquals(sender, _capture))
            return;

        AppLog.Error($"Kamera meldet Fehler: {e.Message} (Code {e.Code})");
        _ = Task.Run(() => HandleFailureAsync(sender));
    }

    private async Task HandleFailureAsync(MediaCapture failed)
    {
        try
        {
            int generation;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(_capture, failed))
                    return;
                generation = Volatile.Read(ref _generation);
                // Status zuerst ändern, damit während des Abbaus kein Foto mehr angenommen wird.
                SetStatus(CameraStatus.Error, CameraMessages.ConnectionLost);
                await StopCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            StartReconnectLoop(generation);
        }
        catch (Exception ex)
        {
            AppLog.Error("Fehlerbehandlung der Kamera fehlgeschlagen.", ex);
        }
    }

    private void StartReconnectLoop(int generation)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _reconnectCts, cts);
        CancelAndDispose(previous);
        var ct = cts.Token;
        _ = Task.Run(() => ReconnectLoopAsync(generation, ct));
    }

    private async Task ReconnectLoopAsync(int generation, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ReconnectInterval, ct).ConfigureAwait(false);
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref _disposed) != 0 || generation != Volatile.Read(ref _generation))
                        return;
                    var settings = _lastSettings;
                    if (settings is null)
                        return;

                    AppLog.Info("Kamera: Wiederverbindungsversuch.");
                    await StartCoreAsync(settings, background: true, ct).ConfigureAwait(false);
                    if (Status == CameraStatus.Running)
                        return;
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Durch Start/Stop/Dispose abgebrochen.
        }
        catch (Exception ex)
        {
            AppLog.Error("Wiederverbindung der Kamera fehlgeschlagen.", ex);
        }
    }

    private void CancelReconnect() => CancelAndDispose(Interlocked.Exchange(ref _reconnectCts, null));

    private static void CancelAndDispose(CancellationTokenSource? cts)
    {
        if (cts is null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        // Bewusst nicht disposed: der Loop liest das Token ggf. noch (kein Timer, keine unmanaged Ressourcen).
    }

    // ---- Status ----

    private void SetStatus(CameraStatus status, string? message)
    {
        lock (_stateLock)
        {
            if (_status == status && _statusMessage == message)
                return;
            _status = status;
            _statusMessage = message;
        }

        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("StatusChanged-Handler hat eine Ausnahme ausgelöst.", ex);
        }
    }

    private static CameraSettings CopyOf(CameraSettings s) => new()
    {
        DeviceId = s.DeviceId,
        ResolutionWidth = s.ResolutionWidth,
        ResolutionHeight = s.ResolutionHeight,
        MirrorPreview = s.MirrorPreview,
        MirrorPhoto = s.MirrorPhoto,
    };
}
