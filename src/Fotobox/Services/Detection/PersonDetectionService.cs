using System.Runtime.InteropServices;
using Fotobox.Core;
using Fotobox.Core.Fun;

namespace Fotobox.Services.Detection;

// Der Typname Detection kollidiert innerhalb dieses Namespaces mit dem Namespace selbst.
using Detection = Fotobox.Core.Fun.Detection;

/// <summary>
/// Erkennungsdienst für das Fun-Feature (Singleton). Hört nur auf <see cref="ICameraService.FrameArrived"/>, solange mindestens
/// eine Anforderung offen ist; es gilt die zuletzt gestellte. Der Kamera-Handler kehrt sofort zurück, wenn gerade eine Analyse
/// läuft oder die Rate erreicht ist; sonst kopiert er das Bild verkleinert und analysiert auf dem Threadpool (höchstens eine Analyse
/// je Detektor, Frist 2 s). Wirft Ganzkörper-Laden oder -Analyse, wechselt der Dienst dauerhaft (bis zum Methodenwechsel) auf die
/// Gesichtserkennung. Detektoren werden nie während einer laufenden Analyse freigegeben.
/// </summary>
public sealed class PersonDetectionService : IPersonDetectionService
{
    public static readonly TimeSpan AnalysisTimeout = TimeSpan.FromSeconds(2);
    /// <summary>Frist für das Laden eines Detektors: Plan abwarten + Laden auf dem Gerät + Rückfall auf die CPU + Reserve (<see cref="Acceleration.AccelerationTimeouts.ServiceInit"/>).</summary>
    public static readonly TimeSpan InitTimeout = Acceleration.AccelerationTimeouts.ServiceInit;
    /// <summary>So lange wartet das Freigeben eines Detektors auf eine noch laufende Analyse, bevor es ihn (selbst sicher) entsorgt.</summary>
    public static readonly TimeSpan RetireWait = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan DetectorDisposeWait = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ServiceDisposeWait = TimeSpan.FromMilliseconds(4500);
    public const int MaxAnalysisWidth = 640;
    private const int FaceFailureLimit = 3;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(4);

    private readonly ICameraService _camera;
    private readonly Func<DetectionKind, IPersonDetector> _factory;
    private readonly TimeProvider _time;

    private readonly object _lock = new();
    private readonly List<DetectionRequest> _requests = [];
    private readonly List<Task> _retiring = [];
    private readonly Queue<long> _completions = new();
    private readonly Queue<double> _cycleMs = new(); // parallel zu _completions: Rechenzeit der jeweiligen Analyse
    private DetectorSlot? _slot;
    private DetectionMethod? _lastDesired;
    private bool _bodyBroken, _faceBroken, _subscribed, _disposed;
    private string? _fallbackReason;

    // Vom Kamera-Handler ohne Sperre gelesen.
    private volatile DetectionRequest? _top;
    private volatile DetectorSlot? _ready;

    // Nur vom Kamera-Handler (seriell) benutzt.
    private long _lastStartTimestamp;
    private bool _hasStarted;
    private readonly byte[]?[] _buffers = new byte[]?[2];
    private int _bufferIndex;
    private int[]? _xMap;
    private int _xMapSource, _xMapTarget;

    public PersonDetectionService(ICameraService camera, Func<DetectionKind, IPersonDetector> detectorFactory, TimeProvider time)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _factory = detectorFactory ?? throw new ArgumentNullException(nameof(detectorFactory));
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

    public IPersonDetectionRequest Request(FunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var request = new DetectionRequest(this, settings.Clone(), SynchronizationContext.Current);
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

    private void RemoveRequest(DetectionRequest request)
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

    private void OnSettingsUpdated(DetectionRequest request)
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
            catch (Exception ex) { AppLog.Warn("Ereignis der Personenerkennung konnte nicht zugestellt werden.", ex); }
        }
    }

    // ---------------------------------------------------------------- Zustand und Detektorwechsel

    /// <summary>
    /// Bestimmt die geltende Anforderung, wählt das passende Verfahren und stellt den Detektor um. Unter <c>_lock</c>.
    /// <paramref name="silentRequest"/>: eine neue Anforderung bekommt ihren Status still (sie liest ihn per <c>Status</c>).
    /// </summary>
    private void ReevaluateLocked(List<Action> pending, DetectionRequest? silentRequest)
    {
        var top = _requests.Count > 0 ? _requests[^1] : null;
        _top = top;
        if (top is null || _disposed)
            return; // keine Anforderung: Detektoren bleiben für schnelles Wiederanfordern erhalten

        var desired = top.Settings.Method;
        if (_lastDesired != desired)
        {
            _lastDesired = desired;
            _bodyBroken = false;
            _faceBroken = false;
            _fallbackReason = null;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var effective = EffectiveKind(desired);
            if (_slot is { } current && current.Kind != effective)
            {
                if (ReferenceEquals(_ready, current))
                    _ready = null;
                _slot = null;
                _completions.Clear();
                _cycleMs.Clear();
                StartRetire(current);
            }
            if (effective is { } kind && _slot is null)
            {
                if (!StartSlotLocked(kind))
                    continue; // Fabrik schlug fehl: Verfahren ist als defekt markiert, neu bewerten
            }
            break;
        }

        var status = BuildStatusLocked(desired);
        var significant = top.SetStatus(status);
        if (significant && !ReferenceEquals(top, silentRequest))
            pending.Add(() => top.DispatchStatus(status));
    }

    private DetectionKind? EffectiveKind(DetectionMethod desired)
    {
        if (desired == DetectionMethod.Body && !_bodyBroken)
            return DetectionKind.Body;
        return _faceBroken ? null : DetectionKind.Face;
    }

    private DetectionStatus BuildStatusLocked(DetectionMethod desired)
    {
        var effective = EffectiveKind(desired);
        DetectionKind? active = _ready is { } ready && ReferenceEquals(ready, _slot) ? ready.Kind : null;
        string? fallback = desired == DetectionMethod.Body && _bodyBroken && !_faceBroken ? _fallbackReason : null;
        string? unavailable = null;
        if (effective is null)
        {
            unavailable = desired == DetectionMethod.Body && _bodyBroken
                ? "Weder die KI-Personenerkennung noch die Gesichtserkennung ist verfügbar."
                : "Die Gesichtserkennung ist auf diesem Gerät nicht verfügbar.";
        }
        return new DetectionStatus(desired, active, fallback, unavailable, CurrentRateLocked(), _cycleMs.Count == 0 ? 0 : _cycleMs.Average());
    }

    private double CurrentRateLocked()
    {
        if (_completions.Count < 2)
            return 0;
        var seconds = _time.GetElapsedTime(_completions.Peek(), _lastCompletion).TotalSeconds;
        return seconds > 0 ? (_completions.Count - 1) / seconds : 0;
    }

    private long _lastCompletion;

    private bool StartSlotLocked(DetectionKind kind)
    {
        IPersonDetector detector;
        try
        {
            detector = _factory(kind);
        }
        catch (Exception ex)
        {
            MarkBrokenLocked(kind, ex, duringInit: true);
            return false;
        }

        var slot = new DetectorSlot(kind, detector);
        _slot = slot;
        _ready = null;
        _ = Task.Run(() => InitializeSlotAsync(slot));
        return true;
    }

    private async Task InitializeSlotAsync(DetectorSlot slot)
    {
        Exception? error = null;
        try
        {
            // Eigener Task.Run: Auch ein Detektor, der synchron blockiert, hält weder den Aufrufer noch die Frist auf.
            await Task.Run(() => slot.Detector.InitializeAsync(CancellationToken.None)).WaitAsync(InitTimeout, _time).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var pending = new List<Action>();
        lock (_lock)
        {
            if (_disposed || !ReferenceEquals(_slot, slot))
                return; // überholt (Methodenwechsel): der Detektor wurde schon freigegeben
            if (error is null)
            {
                _ready = slot;
                if (_requests.Count > 0)
                    ReevaluateLocked(pending, silentRequest: null);
            }
            else
            {
                MarkBrokenLocked(slot.Kind, error, duringInit: true);
                ReevaluateLocked(pending, silentRequest: null);
            }
        }
        Flush(pending);
    }

    private void MarkBrokenLocked(DetectionKind kind, Exception? error, bool duringInit)
    {
        if (kind == DetectionKind.Body)
        {
            _bodyBroken = true;
            _fallbackReason = duringInit
                ? "Das KI-Modell konnte nicht geladen werden – es wird die Gesichtserkennung verwendet."
                : "Die Personenerkennung per KI-Modell ist ausgefallen – es wird die Gesichtserkennung verwendet.";
            AppLog.Warn(duringInit
                ? "Ganzkörper-Erkennung konnte nicht geladen werden, Ausweichen auf Gesichtserkennung."
                : "Ganzkörper-Erkennung fehlgeschlagen, Ausweichen auf Gesichtserkennung.", error);
        }
        else
        {
            _faceBroken = true;
            AppLog.Warn(duringInit
                ? "Gesichtserkennung ist nicht verfügbar, keine Personenerkennung."
                : "Gesichtserkennung fehlgeschlagen, keine Personenerkennung.", error);
        }
    }

    // ---------------------------------------------------------------- Kamera-Handler

    /// <summary>Läuft synchron auf dem Kamera-Thread: so kurz wie möglich, nie blockieren.</summary>
    private void OnFrame(object? sender, CameraFrame frame)
    {
        try
        {
            var top = _top;
            var slot = _ready;
            if (top is null || slot is null || frame is null)
                return;

            var settings = top.Settings;
            var perSecond = Math.Clamp(settings.AnalysesPerSecond, SettingsLimits.FunAnalysesPerSecondMin, SettingsLimits.FunAnalysesPerSecondMax);
            var interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / perSecond);
            if (_hasStarted && _time.GetElapsedTime(_lastStartTimestamp) < interval)
                return;
            if (Interlocked.CompareExchange(ref slot.Busy, 1, 0) != 0)
                return;

            DetectionFrame? copy;
            try
            {
                copy = CopyDownscaled(frame);
            }
            catch
            {
                Volatile.Write(ref slot.Busy, 0);
                throw;
            }
            if (copy is null)
            {
                Volatile.Write(ref slot.Busy, 0);
                return;
            }

            _lastStartTimestamp = _time.GetTimestamp();
            _hasStarted = true;

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            slot.Running = done.Task;
            _ = AnalyzeAsync(slot, copy, settings.MinConfidence, done);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Bild für die Personenerkennung konnte nicht übernommen werden.", ex);
        }
    }

    /// <summary>Verkleinert (Nearest-Neighbor, Seitenverhältnis bleibt) auf höchstens 640 px Breite in einen von zwei wechselnden Puffern.</summary>
    private DetectionFrame? CopyDownscaled(CameraFrame f)
    {
        int w = f.Width, h = f.Height, stride = f.Stride;
        if (w <= 0 || h <= 0 || stride < w * 4 || f.Pixels is null || f.Pixels.Length < (long)(h - 1) * stride + w * 4)
            return null;

        int dw = Math.Min(MaxAnalysisWidth, w);
        int dh = Math.Max(1, (int)Math.Round((double)h * dw / w));
        var length = dw * dh * 4;

        _bufferIndex ^= 1;
        var buffer = _buffers[_bufferIndex];
        if (buffer is null || buffer.Length != length)
            _buffers[_bufferIndex] = buffer = new byte[length];

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

        return new DetectionFrame { Width = dw, Height = dh, Pixels = buffer, SourceWidth = w, SourceHeight = h };
    }

    // ---------------------------------------------------------------- Analyse

    private async Task AnalyzeAsync(DetectorSlot slot, DetectionFrame frame, double minConfidence, TaskCompletionSource done)
    {
        Task<IReadOnlyList<Detection>>? inner = null;
        try
        {
            IReadOnlyList<Detection>? result = null;
            Exception? error = null;
            var timedOut = false;
            var started = _time.GetTimestamp();
            try
            {
                // Eigener Task.Run: Auch ein Detektor, der synchron blockiert, hebelt die 2-s-Frist nicht aus.
                inner = Task.Run(() => slot.Detector.DetectAsync(frame, minConfidence, CancellationToken.None));
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

            var pending = new List<Action>();
            lock (_lock)
            {
                if (!_disposed && ReferenceEquals(_slot, slot))
                {
                    if (timedOut || error is not null)
                    {
                        if (slot.Kind == DetectionKind.Body || ++slot.Failures >= FaceFailureLimit)
                        {
                            MarkBrokenLocked(slot.Kind, error ?? new TimeoutException("Die Analyse dauerte länger als 2 Sekunden."), duringInit: false);
                            ReevaluateLocked(pending, silentRequest: null);
                        }
                        else if (slot.Failures == 1)
                        {
                            AppLog.Warn("Analyse der Personenerkennung fehlgeschlagen.", error ?? new TimeoutException("Die Analyse dauerte länger als 2 Sekunden."));
                        }
                    }
                    else
                    {
                        slot.Failures = 0;
                        PublishLocked(slot, frame, result ?? [], _time.GetElapsedTime(started).TotalMilliseconds, pending);
                    }
                }
            }
            Flush(pending);

            if (timedOut)
            {
                // Das Busy-Flag erst lösen, wenn die Analyse wirklich fertig ist (nichts stapeln).
                try { await inner!.ConfigureAwait(false); }
                catch { /* Ergebnis ist verworfen */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Personenerkennung: unerwarteter Fehler.", ex);
        }
        finally
        {
            Volatile.Write(ref slot.Busy, 0);
            done.TrySetResult();
        }
    }

    private void PublishLocked(DetectorSlot slot, DetectionFrame frame, IReadOnlyList<Detection> detections, double cycleMs, List<Action> pending)
    {
        var now = _time.GetTimestamp();
        _completions.Enqueue(now);
        _cycleMs.Enqueue(cycleMs);
        _lastCompletion = now;
        while (_completions.Count > 1 && _time.GetElapsedTime(_completions.Peek(), now) > RateWindow)
        {
            _completions.Dequeue();
            _cycleMs.Dequeue();
        }

        if (_requests.Count == 0 || _lastDesired is not { } desired)
            return;
        var top = _requests[^1];
        var status = BuildStatusLocked(desired);
        var update = new DetectionUpdate(detections, frame.SourceWidth, frame.SourceHeight, status);
        var significant = top.SetStatus(status);
        if (significant)
            pending.Add(() => top.DispatchStatus(status));
        pending.Add(() => top.DispatchUpdate(update));
    }

    // ---------------------------------------------------------------- Freigabe

    private void StartRetire(DetectorSlot slot)
    {
        var task = RetireSlotAsync(slot);
        lock (_retiring)
        {
            _retiring.RemoveAll(static t => t.IsCompleted);
            _retiring.Add(task);
        }
    }

    /// <summary>Gibt den Detektor erst nach einer laufenden Analyse frei (der Detektor selbst ist zusätzlich gegen Restläufer abgesichert).</summary>
    private async Task RetireSlotAsync(DetectorSlot slot)
    {
        try
        {
            var running = slot.Running;
            if (running is { IsCompleted: false })
            {
                try { await running.WaitAsync(RetireWait, _time).ConfigureAwait(false); }
                catch (TimeoutException) { /* der Detektor gibt nach dem Ende der Analyse selbst frei */ }
            }
            await slot.Detector.DisposeAsync().AsTask().WaitAsync(DetectorDisposeWait, _time).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Erkennungsverfahren konnte nicht sauber freigegeben werden.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            DetectorSlot? slot;
            var requests = new List<DetectionRequest>();
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _top = null;
                _ready = null;
                if (_subscribed)
                {
                    _camera.FrameArrived -= OnFrame;
                    _subscribed = false;
                }
                requests.AddRange(_requests);
                _requests.Clear();
                slot = _slot;
                _slot = null;
            }
            foreach (var request in requests)
                request.Close();
            if (slot is not null)
                StartRetire(slot);

            Task[] tasks;
            lock (_retiring)
                tasks = [.. _retiring];
            try
            {
                await Task.WhenAll(tasks).WaitAsync(ServiceDisposeWait, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AppLog.Warn("Personenerkennung konnte nicht rechtzeitig freigegeben werden.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Personenerkennung: Fehler beim Beenden.", ex);
        }
    }

    // ---------------------------------------------------------------- Hilfstypen

    private sealed class DetectorSlot(DetectionKind kind, IPersonDetector detector)
    {
        public DetectionKind Kind { get; } = kind;
        public IPersonDetector Detector { get; } = detector;
        /// <summary>1 = eine Analyse läuft (bis sie wirklich beendet ist).</summary>
        public int Busy;
        public volatile Task? Running;
        /// <summary>Aufeinanderfolgende Fehlschläge (nur unter <c>_lock</c>).</summary>
        public int Failures;
    }

    private sealed class DetectionRequest : IPersonDetectionRequest
    {
        private readonly PersonDetectionService _owner;
        private readonly SynchronizationContext? _context;
        private volatile bool _closed;
        private DetectionStatus _status;

        public DetectionRequest(PersonDetectionService owner, FunSettings settings, SynchronizationContext? context)
        {
            _owner = owner;
            _context = context;
            Settings = settings;
            _status = DetectionStatus.Starting(settings.Method);
        }

        public volatile FunSettings Settings;
        public event EventHandler<DetectionUpdate>? Updated;
        public event EventHandler<DetectionStatus>? StatusChanged;
        public DetectionStatus Status => Volatile.Read(ref _status);

        public void UpdateSettings(FunSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (_closed)
                return;
            Settings = settings.Clone();
            _owner.OnSettingsUpdated(this);
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
        internal bool SetStatus(DetectionStatus status)
        {
            var old = Interlocked.Exchange(ref _status, status);
            return old.Requested != status.Requested || old.Active != status.Active
                || old.FallbackReason != status.FallbackReason || old.UnavailableReason != status.UnavailableReason;
        }

        internal void DispatchUpdate(DetectionUpdate update) => Dispatch(() => Updated?.Invoke(this, update));

        internal void DispatchStatus(DetectionStatus status) => Dispatch(() => StatusChanged?.Invoke(this, status));

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
            catch (Exception ex) { AppLog.Warn("Handler der Personenerkennung hat eine Ausnahme ausgelöst.", ex); }
        }
    }
}
