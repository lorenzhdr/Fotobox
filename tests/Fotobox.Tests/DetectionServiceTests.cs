using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Services.Detection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Kamera-Attrappe: zählt Abonnenten von FrameArrived und löst Frames selbst aus.</summary>
internal sealed class DetectionFakeCamera : ICameraService
{
    private EventHandler<CameraFrame>? _handlers;
    private int _subscribers;
    public int Subscribers => Volatile.Read(ref _subscribers);

    public event EventHandler<CameraFrame>? FrameArrived
    {
        add { _handlers += value; Interlocked.Increment(ref _subscribers); }
        remove { _handlers -= value; Interlocked.Decrement(ref _subscribers); }
    }

    public void Raise(CameraFrame frame) => _handlers?.Invoke(this, frame);

    public CameraStatus Status => CameraStatus.Running;
    public string? StatusMessage => null;
    public CameraResolution? ActiveResolution => null;
    public event EventHandler? StatusChanged { add { } remove { } }
    public Task<IReadOnlyList<CameraDevice>> GetDevicesAsync() => Task.FromResult<IReadOnlyList<CameraDevice>>([]);
    public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) => Task.FromResult<IReadOnlyList<CameraResolution>>([]);
    public Task StartAsync(CameraSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Detektor-Attrappe mit steuerbarem Laden und Analysieren; merkt sich, ob sie während einer laufenden Analyse entsorgt wurde.</summary>
internal sealed class DetectionFakeDetector(DetectionKind kind) : IPersonDetector
{
    private int _running, _disposed, _detectCalls, _disposedWhileRunning;

    public DetectionKind Kind { get; } = kind;
    public Func<Task>? OnInitialize { get; set; }
    public Func<DetectionFrame, Task<IReadOnlyList<Detection>>>? OnDetect { get; set; }
    public ConcurrentQueue<DetectionFrame> Frames { get; } = new();
    public ConcurrentQueue<(int Width, int Height)> FrameSizes { get; } = new();

    public int DetectCalls => Volatile.Read(ref _detectCalls);
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public bool DisposedWhileRunning => Volatile.Read(ref _disposedWhileRunning) != 0;
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public Task InitializeAsync(CancellationToken ct = default) => OnInitialize?.Invoke() ?? Task.CompletedTask;

    public async Task<IReadOnlyList<Detection>> DetectAsync(DetectionFrame frame, double minConfidence, CancellationToken ct = default)
    {
        // Erst die Größe eintragen, dann zählen: Tests warten auf DetectCalls und lesen danach FrameSizes.
        FrameSizes.Enqueue((frame.Width, frame.Height));
        Interlocked.Increment(ref _detectCalls);
        Interlocked.Increment(ref _running);
        try
        {
            if (OnDetect is { } detect)
                return await detect(frame).ConfigureAwait(false);
            return [new Detection(new NormRect(0.4, 0.2, 0.2, 0.6), 0.9, Kind)];
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _running) != 0)
            Interlocked.Exchange(ref _disposedWhileRunning, 1);
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}

public class DetectionServiceTests
{
    private static readonly TimeSpan Real = TimeSpan.FromSeconds(10);

    private readonly DetectionFakeCamera _camera = new();
    private readonly FakeTimeProvider _time = new();
    private readonly Dictionary<DetectionKind, List<DetectionFakeDetector>> _created = new()
    {
        [DetectionKind.Body] = [],
        [DetectionKind.Face] = [],
    };
    private readonly Dictionary<DetectionKind, Action<DetectionFakeDetector>> _configure = new();

    private static readonly CameraFrame Frame = new()
    {
        Width = 1280, Height = 720, Stride = 1280 * 4, Pixels = new byte[1280 * 720 * 4],
    };

    private PersonDetectionService NewService() => new(_camera, kind =>
    {
        var detector = new DetectionFakeDetector(kind);
        _configure.GetValueOrDefault(kind)?.Invoke(detector);
        lock (_created)
            _created[kind].Add(detector);
        return detector;
    }, _time);

    private static FunSettings Settings(DetectionMethod method = DetectionMethod.Body, int perSecond = 5) =>
        new() { Enabled = true, Method = method, AnalysesPerSecond = perSecond };

    private DetectionFakeDetector Body(int index = 0) { lock (_created) return _created[DetectionKind.Body][index]; }
    private DetectionFakeDetector Face(int index = 0) { lock (_created) return _created[DetectionKind.Face][index]; }
    private int Created(DetectionKind kind) { lock (_created) return _created[kind].Count; }

    private static async Task WaitFor(Func<bool> condition, string what = "Bedingung")
    {
        var deadline = DateTime.UtcNow + Real;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Nicht eingetreten: " + what);
            await Task.Delay(10);
        }
    }

    private static async Task WaitReady(IPersonDetectionRequest request, DetectionKind kind) =>
        await WaitFor(() => request.Status.Active == kind, "Detektor " + kind + " bereit");

    private static T OnlyOne<T>(IEnumerable<T> items) => Assert.Single(items);

    // ------------------------------------------------------------------ Abo

    [Fact]
    public async Task NoSubscription_WithoutRequest_AndSubscribedOnlyWhileRequestsAreOpen()
    {
        await using var service = NewService();
        Assert.Equal(0, _camera.Subscribers);

        var a = service.Request(Settings());
        Assert.Equal(1, _camera.Subscribers);
        var b = service.Request(Settings());
        Assert.Equal(1, _camera.Subscribers); // nur ein Abo für beide

        a.Dispose();
        Assert.Equal(1, _camera.Subscribers);
        b.Dispose();
        Assert.Equal(0, _camera.Subscribers);

        var c = service.Request(Settings());
        Assert.Equal(1, _camera.Subscribers);
        c.Dispose();
        c.Dispose(); // mehrfach erlaubt
        Assert.Equal(0, _camera.Subscribers);
    }

    [Fact]
    public async Task NoRequest_FramesAreIgnored_AndNoDetectorIsCreated()
    {
        await using var service = NewService();

        _camera.Raise(Frame);

        Assert.Equal(0, Created(DetectionKind.Body));
        Assert.Equal(0, Created(DetectionKind.Face));
    }

    // ------------------------------------------------------------------ Analyse und Ergebnis

    [Fact]
    public async Task Frame_IsDownscaledTo640_AndResultIsDelivered()
    {
        await using var service = NewService();
        using var request = service.Request(Settings());
        var updates = new ConcurrentQueue<DetectionUpdate>();
        request.Updated += (_, u) => updates.Enqueue(u);
        await WaitReady(request, DetectionKind.Body);

        _camera.Raise(Frame);

        await WaitFor(() => !updates.IsEmpty, "Update");
        updates.TryDequeue(out var update);
        var detection = Assert.Single(update!.Detections);
        Assert.Equal(DetectionKind.Body, detection.Kind);
        Assert.Equal(1280, update.SourceWidth);
        Assert.Equal(720, update.SourceHeight);
        Assert.Equal(DetectionKind.Body, update.Status.Active);
        Assert.Equal((640, 360), OnlyOne(Body().FrameSizes));
    }

    [Fact]
    public async Task SmallFrame_IsNotUpscaled()
    {
        await using var service = NewService();
        using var request = service.Request(Settings());
        await WaitReady(request, DetectionKind.Body);

        _camera.Raise(new CameraFrame { Width = 320, Height = 240, Stride = 320 * 4, Pixels = new byte[320 * 240 * 4] });

        await WaitFor(() => Body().DetectCalls == 1, "Analyse");
        Assert.Equal((320, 240), OnlyOne(Body().FrameSizes));
    }

    [Fact]
    public async Task InvalidFrame_IsIgnored()
    {
        await using var service = NewService();
        using var request = service.Request(Settings());
        await WaitReady(request, DetectionKind.Body);

        _camera.Raise(new CameraFrame { Width = 100, Height = 100, Stride = 400, Pixels = new byte[10] });
        _camera.Raise(new CameraFrame { Width = 0, Height = 0, Stride = 0, Pixels = [] });
        await Task.Delay(100);

        Assert.Equal(0, Body().DetectCalls);
        // Der Dienst läuft weiter.
        _camera.Raise(Frame);
        await WaitFor(() => Body().DetectCalls == 1, "Analyse");
    }

    [Fact]
    public async Task FramesBeforeDetectorIsReady_AreSkipped()
    {
        var gate = new TaskCompletionSource();
        _configure[DetectionKind.Body] = d => d.OnInitialize = () => gate.Task;
        await using var service = NewService();
        using var request = service.Request(Settings());
        Assert.Equal(DetectionMethod.Body, request.Status.Requested);
        Assert.Null(request.Status.Active);

        _camera.Raise(Frame);
        await Task.Delay(50);
        Assert.Equal(0, Body().DetectCalls);

        gate.SetResult();
        await WaitReady(request, DetectionKind.Body);
    }

    // ------------------------------------------------------------------ Rate und Überspringen

    [Fact]
    public async Task RateLimit_DropsFramesUntilIntervalHasPassed()
    {
        await using var service = NewService();
        using var request = service.Request(Settings(perSecond: 5)); // alle 200 ms
        await WaitReady(request, DetectionKind.Body);

        _camera.Raise(Frame);
        await WaitFor(() => Body().DetectCalls == 1, "erste Analyse");
        await Task.Delay(150); // Analyse sicher abgeschlossen (Echtzeit), Spielzeit steht still

        _time.Advance(TimeSpan.FromMilliseconds(100));
        _camera.Raise(Frame);
        await Task.Delay(100);
        Assert.Equal(1, Body().DetectCalls);

        _time.Advance(TimeSpan.FromMilliseconds(150)); // zusammen 250 ms
        _camera.Raise(Frame);
        await WaitFor(() => Body().DetectCalls == 2, "zweite Analyse");
    }

    [Fact]
    public async Task FrameWhileAnalysisRuns_IsSkipped_NothingStacks()
    {
        var release = new TaskCompletionSource();
        _configure[DetectionKind.Body] = d => d.OnDetect = async _ => { await release.Task; return []; };
        await using var service = NewService();
        using var request = service.Request(Settings());
        await WaitReady(request, DetectionKind.Body);

        _camera.Raise(Frame);
        await WaitFor(() => Body().IsRunning, "Analyse läuft");
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(300)); // Rate wäre erfüllt, aber die Analyse läuft noch
            _camera.Raise(Frame);
        }
        await Task.Delay(50);
        Assert.Equal(1, Body().DetectCalls);

        release.SetResult();
        await WaitFor(() => !Body().IsRunning);
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        _camera.Raise(Frame);
        await WaitFor(() => Body().DetectCalls == 2, "nächste Analyse nach Ende");
    }

    [Fact]
    public async Task AnalysesPerSecond_IsMeasured()
    {
        await using var service = NewService();
        using var request = service.Request(Settings(perSecond: 5));
        var updates = new ConcurrentQueue<DetectionUpdate>();
        request.Updated += (_, u) => updates.Enqueue(u);
        await WaitReady(request, DetectionKind.Body);

        for (var i = 0; i < 6; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(200));
            var before = Body().DetectCalls;
            // Kommt das Bild, während der Dienst den vorigen Lauf noch abschließt (Busy), verwirft er es, ohne die Drossel zu setzen:
            // dann zur selben Spielzeit erneut senden (ein schon analysiertes Bild drosselt jedes weitere, es zählt also nie doppelt).
            await WaitFor(() =>
            {
                if (Body().DetectCalls == before)
                    _camera.Raise(Frame);
                return Body().DetectCalls == before + 1 && !Body().IsRunning;
            }, "Analyse " + i);
            await Task.Delay(30);
        }

        await WaitFor(() => updates.Count == 6, "alle Updates");
        var last = updates.ToArray()[^1];
        Assert.InRange(last.Status.AnalysesPerSecond, 4.0, 6.0); // 5 Analysen/s in Spielzeit
    }

    // ------------------------------------------------------------------ Anforderungsstapel

    [Fact]
    public async Task OnlyTopmostRequest_GetsUpdates()
    {
        await using var service = NewService();
        using var first = service.Request(Settings());
        using var second = service.Request(Settings());
        var firstUpdates = new ConcurrentQueue<DetectionUpdate>();
        var secondUpdates = new ConcurrentQueue<DetectionUpdate>();
        first.Updated += (_, u) => firstUpdates.Enqueue(u);
        second.Updated += (_, u) => secondUpdates.Enqueue(u);
        await WaitReady(second, DetectionKind.Body);

        _camera.Raise(Frame);
        await WaitFor(() => !secondUpdates.IsEmpty, "Update für die oberste");
        Assert.Empty(firstUpdates);

        second.Dispose();
        await Task.Delay(100); // Analyse abgeschlossen
        _time.Advance(TimeSpan.FromSeconds(1));
        _camera.Raise(Frame);
        await WaitFor(() => !firstUpdates.IsEmpty, "Update für die darunterliegende nach Dispose der obersten");
        Assert.Single(secondUpdates);
    }

    [Fact]
    public async Task AfterDisposeOfRequest_NoMoreEvents()
    {
        var release = new TaskCompletionSource();
        _configure[DetectionKind.Body] = d => d.OnDetect = async _ => { await release.Task; return []; };
        await using var service = NewService();
        var request = service.Request(Settings());
        var updates = new ConcurrentQueue<DetectionUpdate>();
        request.Updated += (_, u) => updates.Enqueue(u);
        await WaitReady(request, DetectionKind.Body);
        _camera.Raise(Frame);
        await WaitFor(() => Body().IsRunning);

        request.Dispose(); // Analyse läuft noch
        release.SetResult();
        await WaitFor(() => !Body().IsRunning);
        await Task.Delay(100);

        Assert.Empty(updates);
    }

    [Fact]
    public async Task Events_AreDeliveredOnTheCallersSynchronizationContext_AndDroppedAfterDispose()
    {
        var context = new QueueingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        await using var service = NewService();
        IPersonDetectionRequest request;
        try { request = service.Request(Settings()); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        var updateThreads = new ConcurrentQueue<int>();
        var updates = new ConcurrentQueue<DetectionUpdate>();
        request.Updated += (_, u) => { updates.Enqueue(u); updateThreads.Enqueue(Environment.CurrentManagedThreadId); };

        await WaitFor(() => context.Count > 0, "Statusereignis wartet im Kontext");
        Assert.Empty(updates);
        context.Pump(); // Status Starting → Active
        await WaitFor(() => request.Status.Active == DetectionKind.Body);

        _camera.Raise(Frame);
        await WaitFor(() => context.Count > 0, "Update wartet im Kontext");
        Assert.Empty(updates); // noch nicht zugestellt
        request.Dispose();
        context.Pump(); // zugestellt wird nach Dispose nicht mehr
        Assert.Empty(updates);
    }

    private sealed class QueueingContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public int Count => _queue.Count;
        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
        public void Pump()
        {
            while (_queue.TryDequeue(out var item))
                item.Callback(item.State);
        }
    }

    // ------------------------------------------------------------------ Fallback

    [Fact]
    public async Task BodyInitThrows_FallsBackToFace()
    {
        _configure[DetectionKind.Body] = d => d.OnInitialize = () => throw new InvalidOperationException("kein Modell");
        await using var service = NewService();
        using var request = service.Request(Settings());
        var statuses = new ConcurrentQueue<DetectionStatus>();
        request.StatusChanged += (_, s) => statuses.Enqueue(s);

        await WaitReady(request, DetectionKind.Face);

        var status = request.Status;
        Assert.Equal(DetectionMethod.Body, status.Requested);
        Assert.NotNull(status.FallbackReason);
        Assert.Contains("Gesichtserkennung", status.FallbackReason);
        Assert.Null(status.UnavailableReason);
        Assert.True(Body().IsDisposed);
        Assert.Contains(statuses, s => s.Active == DetectionKind.Face && s.FallbackReason is not null);

        _camera.Raise(Frame);
        await WaitFor(() => Face().DetectCalls == 1, "Analyse mit Gesicht");
        Assert.Equal(0, Body().DetectCalls);
    }

    [Fact]
    public async Task BodyDetectThrows_FallsBackToFace_AndFreesBodyDetector()
    {
        _configure[DetectionKind.Body] = d => d.OnDetect = _ => throw new InvalidOperationException("Run fehlgeschlagen");
        await using var service = NewService();
        using var request = service.Request(Settings());
        await WaitReady(request, DetectionKind.Body);

        _camera.Raise(Frame);

        await WaitReady(request, DetectionKind.Face);
        Assert.NotNull(request.Status.FallbackReason);
        await WaitFor(() => Body().IsDisposed, "Body-Detektor freigegeben");
        Assert.False(Body().DisposedWhileRunning);
    }

    [Fact]
    public async Task Fallback_IsPermanentForNewRequests_UntilMethodChanges()
    {
        _configure[DetectionKind.Body] = d => d.OnInitialize = () => throw new InvalidOperationException("kein Modell");
        await using var service = NewService();
        var first = service.Request(Settings());
        await WaitReady(first, DetectionKind.Face);
        first.Dispose();

        using var second = service.Request(Settings()); // wieder Body gewünscht
        await WaitFor(() => second.Status.Active == DetectionKind.Face, "bleibt bei Gesicht");
        Assert.Equal(1, Created(DetectionKind.Body)); // kein neuer Ladeversuch
        Assert.NotNull(second.Status.FallbackReason);

        // Methode wechselt: Gesicht, dann wieder Körper → neuer Versuch.
        second.UpdateSettings(Settings(DetectionMethod.Face));
        await WaitFor(() => second.Status is { Requested: DetectionMethod.Face, FallbackReason: null }, "Gesicht gewünscht");
        second.UpdateSettings(Settings(DetectionMethod.Body));
        await WaitFor(() => Created(DetectionKind.Body) == 2, "zweiter Ladeversuch des Modells");
    }

    [Fact]
    public async Task BothUnavailable_ReportsUnavailable_AndStopsAnalyzing()
    {
        _configure[DetectionKind.Body] = d => d.OnInitialize = () => throw new InvalidOperationException("kein Modell");
        _configure[DetectionKind.Face] = d => d.OnInitialize = () => throw new NotSupportedException("nicht unterstützt");
        await using var service = NewService();
        using var request = service.Request(Settings());

        await WaitFor(() => request.Status.UnavailableReason is not null, "nicht verfügbar");

        Assert.Null(request.Status.Active);
        Assert.Null(request.Status.FallbackReason);
        _camera.Raise(Frame);
        await Task.Delay(100);
        Assert.Equal(0, Body().DetectCalls);
        Assert.Equal(0, Face().DetectCalls);
    }

    [Fact]
    public async Task FaceRequested_ButUnavailable_ReportsUnavailable()
    {
        _configure[DetectionKind.Face] = d => d.OnInitialize = () => throw new NotSupportedException("nicht unterstützt");
        await using var service = NewService();
        using var request = service.Request(Settings(DetectionMethod.Face));

        await WaitFor(() => request.Status.UnavailableReason is not null, "nicht verfügbar");

        Assert.Equal(0, Created(DetectionKind.Body));
        Assert.Null(request.Status.FallbackReason);
    }

    [Fact]
    public async Task FaceDetectFailing_ThreeTimesInARow_MakesDetectionUnavailable()
    {
        _configure[DetectionKind.Face] = d => d.OnDetect = _ => throw new InvalidOperationException("kaputt");
        await using var service = NewService();
        using var request = service.Request(Settings(DetectionMethod.Face));
        await WaitReady(request, DetectionKind.Face);

        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            var before = Face().DetectCalls;
            _camera.Raise(Frame);
            await WaitFor(() => Face().DetectCalls == before + 1, "Analyse " + i);
            await Task.Delay(50);
        }

        await WaitFor(() => request.Status.UnavailableReason is not null, "nach drei Fehlschlägen nicht verfügbar");
    }

    // ------------------------------------------------------------------ Timeout und Freigabe-Regel

    [Fact]
    public async Task AnalysisTimeout_DiscardsResult_FallsBack_AndKeepsBusyUntilReallyDone()
    {
        var release = new TaskCompletionSource();
        _configure[DetectionKind.Body] = d => d.OnDetect = async _ => { await release.Task; return [new Detection(NormRect.Full, 1, DetectionKind.Body)]; };
        await using var service = NewService();
        using var request = service.Request(Settings());
        var updates = new ConcurrentQueue<DetectionUpdate>();
        request.Updated += (_, u) => updates.Enqueue(u);
        await WaitReady(request, DetectionKind.Body);

        _camera.Raise(Frame);
        await WaitFor(() => Body().IsRunning, "Analyse läuft");
        // Die Frist läuft in Spielzeit; erst vorrücken, wenn der Dienst sein Warten angemeldet hat.
        // In kleinen Schritten (unter der 1,5-s-Wartefrist der Freigabe), damit nur die Analysefrist abläuft.
        while (Created(DetectionKind.Face) == 0)
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(15);
        }

        await WaitReady(request, DetectionKind.Face);
        Assert.NotNull(request.Status.FallbackReason);
        // Der hängende Körper-Durchlauf läuft noch: Er wird nicht unter den Füßen weggeräumt …
        Assert.True(Body().IsRunning);
        Assert.False(Body().IsDisposed);
        Assert.False(Body().DisposedWhileRunning);

        // … und sein verspätetes Ergebnis wird verworfen.
        release.SetResult();
        await WaitFor(() => Body().IsDisposed, "Detektor nach dem Ende freigegeben");
        Assert.False(Body().DisposedWhileRunning);
        Assert.Empty(updates);
    }

    [Fact]
    public async Task ServiceDispose_DuringBlockedAnalysis_WaitsAndNeverDisposesWhileRunning()
    {
        var release = new TaskCompletionSource();
        _configure[DetectionKind.Body] = d => d.OnDetect = async _ => { await release.Task; return []; };
        var service = NewService();
        var request = service.Request(Settings());
        await WaitReady(request, DetectionKind.Body);
        _camera.Raise(Frame);
        await WaitFor(() => Body().IsRunning, "Analyse läuft");

        var disposing = service.DisposeAsync().AsTask();
        await Task.Delay(150);

        Assert.False(disposing.IsCompleted);
        Assert.False(Body().IsDisposed);
        Assert.Equal(0, _camera.Subscribers);

        release.SetResult();
        await disposing.WaitAsync(Real);
        Assert.True(Body().IsDisposed);
        Assert.False(Body().DisposedWhileRunning);
    }

    [Fact]
    public async Task ServiceDispose_WithStuckAnalysis_GivesUpAfterGraceAndHandsOverRelease()
    {
        var release = new TaskCompletionSource();
        _configure[DetectionKind.Body] = d => d.OnDetect = async _ => { await release.Task; return []; };
        var service = NewService();
        var request = service.Request(Settings());
        await WaitReady(request, DetectionKind.Body);
        _camera.Raise(Frame);
        await WaitFor(() => Body().IsRunning, "Analyse läuft");

        var disposing = service.DisposeAsync().AsTask();
        // Spielzeit über die Wartefrist hinaus: Der Dienst übergibt dem Detektor die Entsorgung (der sichert sich selbst ab).
        while (!disposing.IsCompleted)
        {
            _time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(20);
        }
        await disposing.WaitAsync(Real);

        release.SetResult();
    }

    [Fact]
    public async Task MethodChange_DisposesOldDetector_AndLoadsNewOne()
    {
        await using var service = NewService();
        using var request = service.Request(Settings());
        await WaitReady(request, DetectionKind.Body);

        request.UpdateSettings(Settings(DetectionMethod.Face));

        await WaitReady(request, DetectionKind.Face);
        await WaitFor(() => Body().IsDisposed, "alter Detektor freigegeben");
        Assert.Null(request.Status.FallbackReason);
        Assert.Equal(DetectionMethod.Face, request.Status.Requested);
    }

    [Fact]
    public async Task SettingsUpdate_WithSameMethod_KeepsDetector_AndAppliesNewRate()
    {
        await using var service = NewService();
        using var request = service.Request(Settings(perSecond: 1));
        await WaitReady(request, DetectionKind.Body);

        _camera.Raise(Frame);
        await WaitFor(() => Body().DetectCalls == 1);
        await Task.Delay(100);
        _time.Advance(TimeSpan.FromMilliseconds(250)); // bei 1/s noch zu früh
        _camera.Raise(Frame);
        await Task.Delay(100);
        Assert.Equal(1, Body().DetectCalls);

        request.UpdateSettings(Settings(perSecond: 10)); // 100 ms
        _camera.Raise(Frame);
        await WaitFor(() => Body().DetectCalls == 2, "mit höherer Rate");
        Assert.Equal(1, Created(DetectionKind.Body));
    }

    [Fact]
    public async Task DetectorIsKept_WhenLastRequestEnds_AndReusedByNextRequest()
    {
        await using var service = NewService();
        var first = service.Request(Settings());
        await WaitReady(first, DetectionKind.Body);
        first.Dispose();
        await Task.Delay(50);

        Assert.False(Body().IsDisposed);
        using var second = service.Request(Settings());
        Assert.Equal(DetectionKind.Body, second.Status.Active); // sofort bereit, kein Neuladen
        Assert.Equal(1, Created(DetectionKind.Body));
    }

    [Fact]
    public async Task ServiceDispose_ReleasesDetector_Unsubscribes_AndLaterRequestsAreInert()
    {
        var service = NewService();
        var request = service.Request(Settings());
        await WaitReady(request, DetectionKind.Body);

        await service.DisposeAsync();
        await service.DisposeAsync(); // mehrfach erlaubt

        Assert.True(Body().IsDisposed);
        Assert.Equal(0, _camera.Subscribers);
        using var late = service.Request(Settings());
        Assert.Equal(0, _camera.Subscribers);
        _camera.Raise(Frame);
        Assert.Equal(0, Body().DetectCalls);
    }

    [Fact]
    public async Task DetectorFactoryThrowing_CountsAsUnavailableMethod()
    {
        var service = new PersonDetectionService(_camera, _ => throw new InvalidOperationException("Fabrik"), _time);
        await using var disposable = service;
        using var request = service.Request(Settings());

        Assert.NotNull(request.Status.UnavailableReason);
        Assert.Null(request.Status.Active);
    }
}
