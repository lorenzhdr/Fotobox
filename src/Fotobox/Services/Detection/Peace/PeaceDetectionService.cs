using System.IO;
using System.Runtime.InteropServices;
using Fotobox.Core;
using Fotobox.Core.Peace;

namespace Fotobox.Services.Detection.Peace;

/// <summary>
/// Gesichts- und Handerkennung für die Peaceerkennung (Singleton). Hört nur auf <see cref="ICameraService.FrameArrived"/>, solange
/// mindestens eine Anforderung offen ist; es gilt die zuletzt gestellte. Der Kamera-Handler kehrt sofort zurück, wenn gerade eine
/// Analyse läuft, die Rate erreicht ist oder eine Foto-Erkennung die Live-Analyse pausiert; sonst kopiert er das Bild (höchstens
/// 1920 px Breite) in einen Wechselpuffer und analysiert auf dem Threadpool (Frist 2 s, höchstens eine Analyse zugleich). Die
/// Detektoren laden lazy beim ersten Bedarf. Drei Fehlschläge oder Zeitüberschreitungen in Folge machen eine Stufe unbrauchbar
/// (Grund in <see cref="GestureStatus"/> und Log); die Gastansicht bleibt dabei still. Detektoren werden nie während einer laufenden
/// Analyse freigegeben (sie sichern das selbst per <see cref="DeferredReleaseGate"/>).
/// </summary>
public sealed partial class PeaceDetectionService : IGestureDetectionService
{
    public static readonly TimeSpan AnalysisTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DisposeWait = TimeSpan.FromMilliseconds(1500);
    public const int MaxCopyWidth = 1920;
    public const int FailureLimit = 3;
    public const int MaxPalmRuns = 3;
    public const int MaxLandmarkRuns = 4;
    /// <summary>Jede n-te Analyse sucht zusätzlich im ganzen Bild nach Handflächen.</summary>
    public const int FullImageInterval = 4;
    public const double PhotoMinFaceScore = 0.6;
    public const string FaceMissingReason = "Das Gesichtsmodell fehlt – der Schnurrbart ist nicht verfügbar.";
    public const string HandMissingReason = "Das Handmodell fehlt – die Peaceerkennung ist nicht verfügbar.";
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(4);
    /// <summary>Abstand der Zusammenfassungen der Analysedauern im Log.</summary>
    public static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(30);

    private readonly ICameraService _camera;
    private readonly Func<IFaceLandmarkDetector> _faceFactory;
    private readonly Func<IHandLandmarkDetector> _handFactory;
    private readonly TimeProvider _time;

    private readonly object _lock = new();
    private readonly List<GestureRequestImpl> _requests = [];
    private readonly Stage _faceStage = new(isFace: true);
    private readonly Stage _handStage = new(isFace: false);
    private readonly Queue<long> _completions = new();
    private readonly Queue<double> _cycleMs = new(); // parallel zu _completions: Dauer des jeweiligen Zyklus
    private readonly AnalysisStats _stats = new();
    private long? _statsStart;
    private readonly SemaphoreSlim _liveGate = new(1, 1);
    private IFaceLandmarkDetector? _face;
    private IHandLandmarkDetector? _hand;
    private bool _subscribed, _disposed;
    private long _lastCompletion;
    private int _pauseCount;

    // Vom Kamera-Handler ohne Sperre gelesen.
    private volatile GestureRequestImpl? _top;
    private int _busy;

    // Nur vom Kamera-Handler (seriell).
    private long _lastStartTimestamp;
    private bool _hasStarted;
    private long _cycle;
    private readonly byte[]?[] _buffers = new byte[]?[2];
    private int _bufferIndex;
    private int[]? _xMap;
    private int _xMapSource, _xMapTarget;

    public PeaceDetectionService(ICameraService camera, Func<IFaceLandmarkDetector> faceFactory, Func<IHandLandmarkDetector> handFactory, TimeProvider time)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _faceFactory = faceFactory ?? throw new ArgumentNullException(nameof(faceFactory));
        _handFactory = handFactory ?? throw new ArgumentNullException(nameof(handFactory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Anzahl offener Anforderungen (für Tests und Diagnose).</summary>
    internal int OpenRequestCount
    {
        get { lock (_lock) return _requests.Count; }
    }

    /// <summary>true, solange der Dienst auf <see cref="ICameraService.FrameArrived"/> hört.</summary>
    internal bool IsSubscribed
    {
        get { lock (_lock) return _subscribed; }
    }

    // ---------------------------------------------------------------- Anforderungen

    public IGestureRequest Request(GestureRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = new GestureRequestImpl(this, options, SynchronizationContext.Current);
        var pending = new List<Action>();
        lock (_lock)
        {
            if (_disposed)
            {
                request.Close();
                return request;
            }
            _requests.Add(request);
            if (!_subscribed)
            {
                _camera.FrameArrived += OnFrame;
                _subscribed = true;
            }
            ReevaluateLocked(pending, silentRequest: request);
        }
        Flush(pending);
        return request;
    }

    private void RemoveRequest(GestureRequestImpl request)
    {
        var pending = new List<Action>();
        lock (_lock)
        {
            if (!_requests.Remove(request))
                return;
            if (_requests.Count == 0 && _subscribed)
            {
                _camera.FrameArrived -= OnFrame;
                _subscribed = false;
            }
            ReevaluateLocked(pending, silentRequest: null);
        }
        Flush(pending);
    }

    private void OnOptionsUpdated(GestureRequestImpl request)
    {
        var pending = new List<Action>();
        lock (_lock)
        {
            if (_disposed || _requests.Count == 0 || !ReferenceEquals(_requests[^1], request))
                return;
            ReevaluateLocked(pending, silentRequest: null);
        }
        Flush(pending);
    }

    private static void Flush(List<Action> pending)
    {
        foreach (var action in pending)
        {
            try { action(); }
            catch (Exception ex) { AppLog.Warn("Ereignis der Peaceerkennung konnte nicht zugestellt werden.", ex); }
        }
    }

    // ---------------------------------------------------------------- Zustand und Stufen

    /// <summary>Bestimmt die geltende Anforderung, startet benötigte Stufen und meldet Statusänderungen. Unter <c>_lock</c>.</summary>
    private void ReevaluateLocked(List<Action> pending, GestureRequestImpl? silentRequest)
    {
        var top = _requests.Count > 0 ? _requests[^1] : null;
        _top = top;
        if (top is null || _disposed)
            return; // keine Anforderung: Detektoren bleiben für schnelles Wiederanfordern erhalten

        var needs = top.Options.Needs;
        if ((needs & (GestureNeeds.Faces | GestureNeeds.Hands)) != 0)
            EnsureFaceStageLocked();
        if ((needs & GestureNeeds.Hands) != 0)
            EnsureHandStageLocked();

        var status = BuildStatusLocked();
        var significant = top.SetStatus(status);
        if (significant && !ReferenceEquals(top, silentRequest))
            pending.Add(() => top.DispatchStatus(status));
    }

    private GestureStatus BuildStatusLocked() => new(
        _faceStage.Ready && !_faceStage.Broken,
        _handStage.Ready && !_handStage.Broken,
        _faceStage.Reason,
        _handStage.Reason,
        CurrentRateLocked(),
        MeanCycleMsLocked());

    private double MeanCycleMsLocked() => _cycleMs.Count == 0 ? 0 : _cycleMs.Average();

    private double CurrentRateLocked()
    {
        if (_completions.Count < 2)
            return 0;
        var seconds = _time.GetElapsedTime(_completions.Peek(), _lastCompletion).TotalSeconds;
        return seconds > 0 ? (_completions.Count - 1) / seconds : 0;
    }

    private void EnsureFaceStageLocked()
    {
        if (_faceStage.Started || _faceStage.Broken)
            return;
        _faceStage.Started = true;
        IFaceLandmarkDetector detector;
        try
        {
            detector = _faceFactory();
        }
        catch (Exception ex)
        {
            MarkBrokenLocked(_faceStage, ex, duringInit: true);
            return;
        }
        _face = detector;
        _faceStage.Init = Task.Run(() => InitializeStageAsync(_faceStage, detector, () => detector.InitializeAsync(CancellationToken.None), () => ReferenceEquals(_face, detector)));
    }

    private void EnsureHandStageLocked()
    {
        if (_handStage.Started || _handStage.Broken)
            return;
        _handStage.Started = true;
        IHandLandmarkDetector detector;
        try
        {
            detector = _handFactory();
        }
        catch (Exception ex)
        {
            MarkBrokenLocked(_handStage, ex, duringInit: true);
            return;
        }
        _hand = detector;
        _handStage.Init = Task.Run(() => InitializeStageAsync(_handStage, detector, () => detector.InitializeAsync(CancellationToken.None), () => ReferenceEquals(_hand, detector)));
    }

    private async Task InitializeStageAsync(Stage stage, IAsyncDisposable detector, Func<Task> init, Func<bool> isCurrent)
    {
        Exception? error = null;
        try
        {
            // Eigener Task.Run: Auch ein Detektor, der synchron blockiert, hält die Frist nicht auf.
            await Task.Run(init).WaitAsync(InitTimeout, _time).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var pending = new List<Action>();
        lock (_lock)
        {
            if (_disposed || !isCurrent())
                return;
            if (error is null)
                stage.Ready = true;
            else
                MarkBrokenLocked(stage, error, duringInit: true);
            ReevaluateLocked(pending, silentRequest: null);
        }
        Flush(pending);
    }

    private void MarkBrokenLocked(Stage stage, Exception? error, bool duringInit)
    {
        if (stage.Broken)
            return;
        stage.Broken = true;
        stage.Ready = false;
        var missing = error is FileNotFoundException or DirectoryNotFoundException;
        if (stage.IsFace)
        {
            stage.Reason = missing ? FaceMissingReason
                : duringInit ? $"Das Gesichtsmodell konnte nicht geladen werden ({ShortCause(error)}) – der Schnurrbart ist nicht verfügbar."
                : $"Die Gesichtserkennung ist ausgefallen ({ShortCause(error)}) – der Schnurrbart ist nicht verfügbar.";
        }
        else
        {
            stage.Reason = missing ? HandMissingReason
                : duringInit ? $"Das Handmodell konnte nicht geladen werden ({ShortCause(error)}) – die Peaceerkennung ist nicht verfügbar."
                : $"Die Handerkennung ist ausgefallen ({ShortCause(error)}) – die Peaceerkennung ist nicht verfügbar.";
        }
        AppLog.Warn(stage.IsFace ? "Peaceerkennung: Gesichtsstufe ist nicht verfügbar." : "Peaceerkennung: Handstufe ist nicht verfügbar.", error);

        // Die kaputte Stufe nie wieder benutzen; der Detektor sichert die Freigabe selbst gegen laufende Aufrufe.
        object? detector = stage.IsFace ? _face : _hand;
        if (stage.IsFace)
            _face = null;
        else
            _hand = null;
        if (detector is IAsyncDisposable disposable)
            _ = DisposeQuietlyAsync(disposable);
    }

    private static string ShortCause(Exception? error) => error switch
    {
        null => "unbekannt",
        TimeoutException => "Zeitüberschreitung",
        _ => error.GetType().Name,
    };

    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try { await disposable.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn("Erkennungsstufe konnte nicht sauber freigegeben werden.", ex); }
    }

    /// <summary>Verbucht einen Fehlschlag der Stufe; nach <see cref="FailureLimit"/> in Folge gilt sie als defekt.</summary>
    private void RegisterFailure(Stage stage, Exception error)
    {
        var pending = new List<Action>();
        lock (_lock)
        {
            if (_disposed || stage.Broken)
                return;
            stage.Failures++;
            if (stage.Failures >= FailureLimit)
            {
                MarkBrokenLocked(stage, error, duringInit: false);
                ReevaluateLocked(pending, silentRequest: null);
            }
            else if (stage.Failures == 1)
            {
                AppLog.Warn(stage.IsFace ? "Peaceerkennung: Gesichtsanalyse fehlgeschlagen." : "Peaceerkennung: Handanalyse fehlgeschlagen.", error);
            }
        }
        Flush(pending);
    }

    // ---------------------------------------------------------------- Kamera-Handler

    /// <summary>Läuft synchron auf dem Kamera-Thread: so kurz wie möglich, nie blockieren.</summary>
    private void OnFrame(object? sender, CameraFrame frame)
    {
        try
        {
            var top = _top;
            if (top is null || frame is null || Volatile.Read(ref _pauseCount) > 0)
                return;
            var frameTime = _time.GetUtcNow(); // Eingangszeit des Bildes, für die Vorhersage der Bartlage

            var options = top.Options;
            var needs = options.Needs;
            if ((needs & (GestureNeeds.Faces | GestureNeeds.Hands)) == 0)
                return;
            var wantHands = (needs & GestureNeeds.Hands) != 0;
            // Hände brauchen die Handstufe; die Gesichtsstufe wartet man ab, solange sie lädt (Fehlt sie, geht es ohne).
            var runnable = wantHands
                ? (_handStage.Ready && (_faceStage.Ready || _faceStage.Broken)) || (_handStage.Broken && _faceStage.Ready)
                : _faceStage.Ready;
            if (!runnable)
                return;

            var perSecond = Math.Clamp(options.AnalysesPerSecond, 1, 30);
            var interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / perSecond);
            if (_hasStarted && _time.GetElapsedTime(_lastStartTimestamp) < interval)
                return;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return;
            // Während einer Foto-Erkennung hält diese das Gate: dann gar nicht erst starten.
            if (!_liveGate.Wait(0))
            {
                Volatile.Write(ref _busy, 0);
                return;
            }

            BgraImage? copy;
            try
            {
                copy = CopyFrame(frame);
            }
            catch
            {
                ReleaseLive();
                throw;
            }
            if (copy is null)
            {
                ReleaseLive();
                return;
            }

            _lastStartTimestamp = _time.GetTimestamp();
            _hasStarted = true;
            var cycle = ++_cycle;
            _ = AnalyzeAsync(copy, frame.Width, frame.Height, options, cycle, frameTime);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Bild für die Peaceerkennung konnte nicht übernommen werden.", ex);
        }
    }

    private void ReleaseLive()
    {
        Volatile.Write(ref _busy, 0);
        _liveGate.Release();
    }

    /// <summary>Kopiert das Bild in einen von zwei Wechselpuffern; über <see cref="MaxCopyWidth"/> Breite per Nearest verkleinert.</summary>
    private BgraImage? CopyFrame(CameraFrame f)
    {
        int w = f.Width, h = f.Height, stride = f.Stride;
        if (w <= 0 || h <= 0 || stride < w * 4 || f.Pixels is null || f.Pixels.Length < (long)(h - 1) * stride + w * 4)
            return null;

        int dw = Math.Min(MaxCopyWidth, w);
        int dh = Math.Max(1, (int)Math.Round((double)h * dw / w));
        var length = dw * dh * 4;

        _bufferIndex ^= 1;
        var buffer = _buffers[_bufferIndex];
        if (buffer is null || buffer.Length != length)
            _buffers[_bufferIndex] = buffer = new byte[length];

        if (dw == w && dh == h)
        {
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(f.Pixels, y * stride, buffer, y * dw * 4, dw * 4);
        }
        else
        {
            var xMap = _xMap;
            if (xMap is null || _xMapSource != w || _xMapTarget != dw)
            {
                xMap = new int[dw];
                for (int x = 0; x < dw; x++)
                    xMap[x] = Math.Min(w - 1, (int)((x + 0.5) * w / dw));
                _xMap = xMap;
                _xMapSource = w;
                _xMapTarget = dw;
            }
            for (int y = 0; y < dh; y++)
            {
                int sy = Math.Min(h - 1, (int)((y + 0.5) * h / dh));
                var src = MemoryMarshal.Cast<byte, uint>(f.Pixels.AsSpan(sy * stride, w * 4));
                var dst = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(y * dw * 4, dw * 4));
                for (int x = 0; x < dst.Length; x++)
                    dst[x] = src[xMap[x]];
            }
        }

        return new BgraImage { Width = dw, Height = dh, Stride = dw * 4, Pixels = buffer };
    }

    // ---------------------------------------------------------------- Analyse

    private async Task AnalyzeAsync(BgraImage image, int sourceW, int sourceH, GestureRequestOptions options, long cycle, DateTimeOffset frameTime)
    {
        Task<CycleResult>? inner = null;
        try
        {
            CycleResult? result = null;
            Exception? error = null;
            var timedOut = false;
            try
            {
                // Eigener Task.Run: Auch ein Detektor, der synchron blockiert, hebelt die 2-s-Frist nicht aus.
                inner = Task.Run(() => RunCycleAsync(image, options, cycle, faces => PublishFaces(faces, sourceW, sourceH, frameTime)));
                result = await inner.WaitAsync(AnalysisTimeout, _time).ConfigureAwait(false);
            }
            catch (TimeoutException) when (inner is { IsCompleted: false })
            {
                timedOut = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (timedOut)
                RegisterFailure(CurrentStage(), new TimeoutException("Die Analyse dauerte länger als 2 Sekunden."));
            else if (error is StageFailure failure)
                RegisterFailure(failure.Stage, failure.InnerException ?? failure);
            else if (error is not null)
                RegisterFailure(CurrentStage(), error);
            else if (result is not null)
                Publish(result, sourceW, sourceH, frameTime);

            if (timedOut)
            {
                // Busy erst lösen, wenn die Analyse wirklich fertig ist (nichts stapeln).
                try { await inner!.ConfigureAwait(false); }
                catch { /* Ergebnis ist verworfen */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Peaceerkennung: unerwarteter Fehler.", ex);
        }
        finally
        {
            ReleaseLive();
        }
    }

    /// <summary>Liefert Gesichter vorab aus (schneller Gesichtsmodus: nicht auf die Handstufe warten); zählt nicht als Zyklus.</summary>
    private void PublishFaces(IReadOnlyList<FaceLandmarks> faces, int sourceW, int sourceH, DateTimeOffset frameTime)
    {
        var pending = new List<Action>();
        lock (_lock)
        {
            if (_disposed || _requests.Count == 0)
                return;
            _faceStage.Failures = 0;
            var top = _requests[^1];
            var update = new GestureUpdate(faces, [], sourceW, sourceH, BuildStatusLocked(), HandsIncluded: false, FacesFresh: true, FrameTime: frameTime);
            pending.Add(() => top.DispatchUpdate(update));
        }
        Flush(pending);
    }

    private void Publish(CycleResult result, int sourceW, int sourceH, DateTimeOffset frameTime)
    {
        var pending = new List<Action>();
        lock (_lock)
        {
            if (_disposed)
                return;
            if (result.FacesRan)
                _faceStage.Failures = 0;
            if (result.HandsRan)
                _handStage.Failures = 0;

            var now = _time.GetTimestamp();
            _completions.Enqueue(now);
            _cycleMs.Enqueue(result.Timings.Total.TotalMilliseconds);
            _lastCompletion = now;
            while (_completions.Count > 1 && _time.GetElapsedTime(_completions.Peek(), now) > RateWindow)
            {
                _completions.Dequeue();
                _cycleMs.Dequeue();
            }
            RecordStatsLocked(result, now);

            if (_requests.Count == 0)
                return;
            var top = _requests[^1];
            var status = BuildStatusLocked();
            var update = new GestureUpdate(result.Faces, result.Hands, sourceW, sourceH, status,
                result.HandsIncluded, FacesFresh: !result.FacesPublished, FrameTime: frameTime);
            if (top.SetStatus(status))
                pending.Add(() => top.DispatchStatus(status));
            pending.Add(() => top.DispatchUpdate(update));
        }
        Flush(pending);
    }

    // Sammelt die Dauer der Stufen und schreibt alle StatsInterval eine Zusammenfassung ins Log.
    private void RecordStatsLocked(CycleResult result, long now)
    {
        if (_statsStart is null)
            _statsStart = _completions.Count > 0 ? _completions.Peek() : now;
        _stats.Add(result);
        var elapsed = _time.GetElapsedTime(_statsStart.Value, now);
        if (elapsed < StatsInterval)
            return;
        AppLog.Info(_stats.Summarize(elapsed));
        _stats.Reset();
        _statsStart = now;
    }

    // ---------------------------------------------------------------- Foto-Erkennung

    public async Task<IReadOnlyList<FaceLandmarks>?> DetectFacesInPhotoAsync(BgraImage photo, TimeSpan timeout, CancellationToken ct = default)
    {
        var held = false;
        var releaseGate = true;
        Interlocked.Increment(ref _pauseCount);
        try
        {
            if (photo is null || timeout <= TimeSpan.Zero)
                return null;
            var start = _time.GetTimestamp();
            TimeSpan Remaining() => timeout - _time.GetElapsedTime(start);

            Task? init;
            var pending = new List<Action>();
            lock (_lock)
            {
                if (_disposed)
                    return null;
                EnsureFaceStageLocked();
                if (_faceStage.Broken)
                    return null;
                init = _faceStage.Init;
            }
            Flush(pending);

            if (!_faceStage.Ready)
            {
                if (init is null || Remaining() <= TimeSpan.Zero)
                    return null;
                await init.WaitAsync(Remaining(), _time, ct).ConfigureAwait(false);
                if (!_faceStage.Ready || _faceStage.Broken)
                    return null;
            }

            // Auf eine laufende Live-Analyse warten (sie hält das Gate); die Wartezeit zählt zum Timeout.
            var wait = _liveGate.WaitAsync(CancellationToken.None);
            try
            {
                await wait.WaitAsync(Remaining(), _time, ct).ConfigureAwait(false);
                held = true;
            }
            catch
            {
                _ = wait.ContinueWith(static (t, state) =>
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                        ((SemaphoreSlim)state!).Release();
                }, _liveGate, TaskScheduler.Default);
                throw;
            }

            var detector = _face;
            if (detector is null || Remaining() <= TimeSpan.Zero)
                return null;

            Task<IReadOnlyList<FaceLandmarks>>? work = null;
            try
            {
                work = Task.Run(() => detector.DetectAsync(photo, PhotoMinFaceScore, CancellationToken.None));
                return await work.WaitAsync(Remaining(), _time, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (work is { IsCompleted: false })
            {
                // Zeitüberschreitung oder Abbruch: Das Gate bleibt gesperrt, bis die Analyse wirklich fertig ist.
                releaseGate = false;
                var gate = _liveGate;
                _ = work.ContinueWith(static (_, state) => ((SemaphoreSlim)state!).Release(), gate, TaskScheduler.Default);
                held = false;
                if (ex is not (TimeoutException or OperationCanceledException))
                    AppLog.Warn("Peaceerkennung: Gesichtserkennung im Foto fehlgeschlagen.", ex);
                return null;
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    RegisterFailure(_faceStage, ex);
                return null;
            }
        }
        catch (Exception ex)
        {
            if (ex is not (TimeoutException or OperationCanceledException))
                AppLog.Warn("Peaceerkennung: Foto-Erkennung fehlgeschlagen.", ex);
            return null;
        }
        finally
        {
            if (held && releaseGate)
                _liveGate.Release();
            Interlocked.Decrement(ref _pauseCount);
        }
    }

    // ---------------------------------------------------------------- Freigabe

    public async ValueTask DisposeAsync()
    {
        try
        {
            var requests = new List<GestureRequestImpl>();
            IFaceLandmarkDetector? face;
            IHandLandmarkDetector? hand;
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _top = null;
                if (_subscribed)
                {
                    _camera.FrameArrived -= OnFrame;
                    _subscribed = false;
                }
                requests.AddRange(_requests);
                _requests.Clear();
                face = _face;
                hand = _hand;
                _face = null;
                _hand = null;
            }
            foreach (var request in requests)
                request.Close();

            var tasks = new List<Task>();
            if (face is not null)
                tasks.Add(DisposeQuietlyAsync(face));
            if (hand is not null)
                tasks.Add(DisposeQuietlyAsync(hand));
            try
            {
                await Task.WhenAll(tasks).WaitAsync(DisposeWait, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AppLog.Warn("Peaceerkennung konnte nicht rechtzeitig freigegeben werden.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Peaceerkennung: Fehler beim Beenden.", ex);
        }
    }

    // ---------------------------------------------------------------- Hilfstypen

    private sealed class Stage(bool isFace)
    {
        public bool IsFace { get; } = isFace;
        // Nur unter _lock geschrieben; Ready wird vom Kamera-Handler ohne Sperre gelesen.
        public volatile bool Ready;
        public volatile bool Broken;
        public bool Started;
        public string? Reason;
        public int Failures;
        public Task? Init;
    }

    private sealed class StageFailure(Stage stage, Exception inner) : Exception("Stufe der Peaceerkennung fehlgeschlagen.", inner)
    {
        public Stage Stage { get; } = stage;
    }

    /// <summary>Dauer der Teilschritte eines Zyklus (Palm- und Landmark-Läufe jeweils summiert).</summary>
    internal readonly record struct CycleTimings(TimeSpan Face, TimeSpan Palm, TimeSpan Landmark, TimeSpan Total);

    private sealed record CycleResult(IReadOnlyList<FaceLandmarks> Faces, IReadOnlyList<HandLandmarks> Hands, bool FacesRan, bool HandsRan,
        bool HandsIncluded, bool FacesPublished, CycleTimings Timings);

    /// <summary>Mittelwert und Maximum der Stufendauern über ein Messfenster (nur Zyklen, in denen die Stufe lief).</summary>
    private sealed class AnalysisStats
    {
        private sealed class Series
        {
            public int Count;
            public double SumMs, MaxMs;

            public void Add(TimeSpan value)
            {
                var ms = value.TotalMilliseconds;
                Count++;
                SumMs += ms;
                if (ms > MaxMs)
                    MaxMs = ms;
            }

            public void Clear() => (Count, SumMs, MaxMs) = (0, 0, 0);

            public string Format() => Count == 0 ? "-" : $"{SumMs / Count:0} ms (max {MaxMs:0})";
        }

        private readonly Series _face = new(), _palm = new(), _landmark = new(), _total = new();

        public void Add(CycleResult result)
        {
            if (result.FacesRan)
                _face.Add(result.Timings.Face);
            if (result.HandsRan)
            {
                _palm.Add(result.Timings.Palm);
                _landmark.Add(result.Timings.Landmark);
            }
            _total.Add(result.Timings.Total);
        }

        public void Reset()
        {
            _face.Clear();
            _palm.Clear();
            _landmark.Clear();
            _total.Clear();
        }

        /// <summary>Eine Zeile: Zyklen, tatsächliche Analysen/s sowie Mittelwert und Maximum je Stufe.</summary>
        public string Summarize(TimeSpan window)
        {
            var rate = window.TotalSeconds > 0 ? _total.Count / window.TotalSeconds : 0;
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Peaceerkennung: {_total.Count} Analysen in {window.TotalSeconds:0} s ({rate:0.0}/s), Dauer je Analyse {_total.Format()}; Gesichter {_face.Format()}, Palm {_palm.Format()}, Handpunkte {_landmark.Format()}.");
        }
    }

    private sealed class GestureRequestImpl : IGestureRequest
    {
        private readonly PeaceDetectionService _owner;
        private readonly SynchronizationContext? _context;
        private volatile bool _closed;
        private GestureStatus _status;

        public GestureRequestImpl(PeaceDetectionService owner, GestureRequestOptions options, SynchronizationContext? context)
        {
            _owner = owner;
            _context = context;
            Options = options;
            _status = GestureStatus.Starting;
        }

        public volatile GestureRequestOptions Options;
        public event EventHandler<GestureUpdate>? Updated;
        public event EventHandler<GestureStatus>? StatusChanged;
        public GestureStatus Status => Volatile.Read(ref _status);

        public void Update(GestureRequestOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (_closed)
                return;
            Options = options;
            _owner.OnOptionsUpdated(this);
        }

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            _owner.RemoveRequest(this);
        }

        internal void Close() => _closed = true;

        /// <summary>Speichert den Status; true, wenn sich mehr als nur die Analyserate geändert hat.</summary>
        internal bool SetStatus(GestureStatus status)
        {
            var old = Interlocked.Exchange(ref _status, status);
            return old.FacesReady != status.FacesReady || old.HandsReady != status.HandsReady
                || old.FaceUnavailableReason != status.FaceUnavailableReason || old.HandUnavailableReason != status.HandUnavailableReason;
        }

        internal void DispatchUpdate(GestureUpdate update) => Dispatch(() => Updated?.Invoke(this, update));

        internal void DispatchStatus(GestureStatus status) => Dispatch(() => StatusChanged?.Invoke(this, status));

        private void Dispatch(Action action)
        {
            if (_context is null)
            {
                if (!_closed)
                    Run(action);
                return;
            }
            _context.Post(_ =>
            {
                if (!_closed)
                    Run(action);
            }, null);
        }

        private static void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { AppLog.Warn("Handler der Peaceerkennung hat eine Ausnahme ausgelöst.", ex); }
        }
    }
}
