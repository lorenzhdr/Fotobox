using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Detection.Peace;

namespace Fotobox.ViewModels.Booth;

/// <summary>Betriebsart der Peaceerkennung in der Gastansicht.</summary>
public enum PeaceMode
{
    /// <summary>Keine Erkennung (z. B. Admin offen, Kamera aus, Aufnahme läuft).</summary>
    Off,
    /// <summary>Hände und Gesichter beobachten (Leerlauf).</summary>
    Watching,
    /// <summary>Nur Gesichter verfolgen, damit der Bart dem Gesicht folgt (Countdown, Vorschau).</summary>
    FollowMoustache,
}

/// <summary>
/// Bindbare Sicht auf die Peaceerkennung (Gastansicht und Admin-Vorschau, je eine Instanz): Hält Anforderung, Engine, Bart-Verfolgung,
/// Tick-Timer und Töne. Nur auf dem UI-Thread verwenden; <see cref="SetMode"/> merkt sich dessen <see cref="SynchronizationContext"/>
/// für die Timer-Ticks.
/// </summary>
public sealed class PeaceOverlayViewModel : ObservableObject, IDisposable
{
    /// <summary>Takt von Engine und Bart-Verfolgung.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Dauer des Bart-Tests im Admin.</summary>
    public const int TestMoustacheSeconds = 8;

    private const double MinFaceScore = 0.6;
    private static readonly TimeSpan FacesFreshness = TimeSpan.FromSeconds(1);

    private static readonly TrackedFace PlaceholderFace = new(-1, new FaceLandmarks(
        new NormRect(0.4, 0.25, 0.2, 0.5), 0.9,
        RightEye: new NormPoint(0.45, 0.4), LeftEye: new NormPoint(0.55, 0.4),
        Nose: new NormPoint(0.5, 0.5),
        MouthRight: new NormPoint(0.46, 0.6), MouthLeft: new NormPoint(0.54, 0.6)));

    private readonly IGestureDetectionService _service;
    private readonly TimeProvider _time;
    private readonly IFunSoundPlayer _sound;
    private readonly GuestMoustacheState? _guestState;
    /// <summary>Analyserate, solange der Bart im Countdown dem Gesicht folgt (nur Gesichter, billig).</summary>
    private const int FollowRate = 10;
    private readonly FaceTracker _faceTracker;
    private readonly PeaceEngine _engine;
    private readonly MoustacheTracker _tracker;

    private IGestureRequest? _request;
    private GestureRequestOptions? _options;
    private ITimer? _timer;
    private SynchronizationContext? _context;
    private int _generation;
    private bool _disposed;

    private PeaceSettings _settings = new();
    private PeaceMode _mode;
    private IReadOnlyList<MoustacheView> _moustaches = [];
    private IReadOnlyList<HandView> _hands = [];
    private double _imageWidth;
    private double _imageHeight;
    private bool _peaceDetectedNow;
    private bool _moustacheActive;
    private bool _lastShown;
    private GestureStatus _status = GestureStatus.Starting;
    private bool _moustacheAvailable;

    // Gesichter der letzten Analyse (ohne Platzhalter) für den Bart-Test und das Seeding einer neuen Sitzung.
    private IReadOnlyList<TrackedFace> _realFaces = [];
    private DateTimeOffset _realFacesAt = DateTimeOffset.MinValue;
    private bool _testSession;
    private bool _testPlaceholder;

    /// <param name="guestState">Gemeinsamer Bart-Zustand für die Foto-Planung; nur die Gastansicht übergibt ihn (die Admin-Vorschau nie).</param>
    /// <param name="random">Zufall für Aktionswahl und Bartstil: liefert für eine Obergrenze n einen Wert in 0..n-1 (null = Standard).</param>
    public PeaceOverlayViewModel(IGestureDetectionService service, TimeProvider time, IFunSoundPlayer sound,
        GuestMoustacheState? guestState = null, Func<int, int>? random = null)
    {
        _service = service;
        _time = time;
        _sound = sound;
        _guestState = guestState;
        _faceTracker = new FaceTracker(time);
        _engine = new PeaceEngine(time, random);
        _tracker = new MoustacheTracker(time, random);
        _engine.Triggered += OnEngineTriggered;
        _engine.Changed += (_, _) => RefreshHands();
    }

    /// <summary>Instanz ohne Erkennung und Ton (für Ansichtsmodelle, die das Feature nie aktivieren, z. B. in Tests).</summary>
    public static PeaceOverlayViewModel CreateInert() => new(new InertGestureDetectionService(), TimeProvider.System, new InertSound());

    public PeaceMode Mode => _mode;

    /// <summary>Bärte im Livebild (ungespiegelte Posen; Spiegelung übernimmt <c>PeaceOverlay</c>).</summary>
    public IReadOnlyList<MoustacheView> Moustaches
    {
        get => _moustaches;
        private set
        {
            if (SetProperty(ref _moustaches, value))
                RefreshShown();
        }
    }

    /// <summary>Verfolgte Hände (Admin-Vorschau, Fortschrittsring).</summary>
    public IReadOnlyList<HandView> Hands
    {
        get => _hands;
        private set
        {
            if (SetProperty(ref _hands, value))
                RefreshShown();
        }
    }

    /// <summary>Auflösung des Kamerabilds; 0 = unbekannt.</summary>
    public double ImageWidth
    {
        get => _imageWidth;
        private set => SetProperty(ref _imageWidth, value);
    }

    public double ImageHeight
    {
        get => _imageHeight;
        private set => SetProperty(ref _imageHeight, value);
    }

    /// <summary>Gibt es etwas zu zeichnen?</summary>
    public bool IsShown => Moustaches.Count > 0 || Hands.Count > 0;

    /// <summary>Zeigt gerade mindestens eine Hand das Peace-Zeichen (Abzeichen in der Admin-Vorschau)?</summary>
    public bool PeaceDetectedNow
    {
        get => _peaceDetectedNow;
        private set => SetProperty(ref _peaceDetectedNow, value);
    }

    /// <summary>Ist ein Schnurrbart aktiv (Bärte werden verfolgt)?</summary>
    public bool MoustacheActive
    {
        get => _moustacheActive;
        private set => SetProperty(ref _moustacheActive, value);
    }

    public GestureStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Steht die Gesichtserkennung bereit (sonst fällt die Aktion Schnurrbart aus der Auswahl)?</summary>
    public bool MoustacheAvailable
    {
        get => _moustacheAvailable;
        private set => SetProperty(ref _moustacheAvailable, value);
    }

    /// <summary>Eine Peace-Aktion soll ausgeführt werden (auf dem UI-Thread; für alle Aktionen, bei Schnurrbart startet der Aufrufer <see cref="StartMoustache"/>).</summary>
    public event EventHandler<PeaceTrigger>? ActionRequested;

    /// <summary>
    /// Betriebsart und Einstellungen setzen. Ein Wechsel zwischen <see cref="PeaceMode.Watching"/> und
    /// <see cref="PeaceMode.FollowMoustache"/> ändert nur die Anforderung (<see cref="IGestureRequest.Update"/>), nie Engine, Tracker
    /// oder Anforderung selbst, damit ein Bart den Countdown übersteht. <see cref="PeaceMode.Off"/> schließt die Anforderung, leert
    /// die Erkennung und beendet eine laufende Bart-Sitzung. Bei <see cref="PeaceMode.FollowMoustache"/> werden nur Gesichter
    /// verfolgt und nie Aktionen ausgelöst, auch wenn kein Bart aktiv ist (der Aufrufer wählt dann besser <see cref="PeaceMode.Off"/>).
    /// Der Parameter <paramref name="mirror"/> ist für die Darstellung gedacht (übernimmt <c>PeaceOverlay</c>) und hier ohne Wirkung.
    /// Wiederholte Aufrufe mit geänderten Einstellungen wirken sofort.
    /// </summary>
    public void SetMode(PeaceMode mode, PeaceSettings settings, bool mirror)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed)
            return;
        _ = mirror;

        _settings = settings.Clone();
        if (mode == PeaceMode.Off)
        {
            var changed = _mode != PeaceMode.Off;
            _mode = PeaceMode.Off;
            Deactivate();
            if (changed)
                OnPropertyChanged(nameof(Mode));
            return;
        }

        _context ??= SynchronizationContext.Current;
        var wasOff = _mode == PeaceMode.Off;
        var modeChanged = _mode != mode;
        _mode = mode;

        _engine.Configure(_settings, MoustacheAvailable);
        var rate = mode == PeaceMode.Watching ? Math.Max(1, _settings.AnalysesPerSecond) : FollowRate;
        _faceTracker.LostAfter = TimeSpan.FromSeconds(PeaceEngine.AnalysisIntervalFactor / rate);
        _engine.GesturesEnabled = mode == PeaceMode.Watching;
        _tracker.SetHoldExpiry(mode == PeaceMode.FollowMoustache);

        var options = mode == PeaceMode.Watching
            ? new GestureRequestOptions(GestureNeeds.Faces | GestureNeeds.Hands, _settings.AnalysesPerSecond, MinFaceScore, _settings.MinHandScore)
            : new GestureRequestOptions(GestureNeeds.Faces, FollowRate, MinFaceScore, _settings.MinHandScore);

        if (_request is null)
        {
            try
            {
                var request = _service.Request(options);
                request.Updated += OnUpdated;
                request.StatusChanged += OnStatusChanged;
                _request = request;
                _options = options;
                ApplyStatus(request.Status);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Peaceerkennung konnte nicht angefordert werden.", ex);
            }
        }
        else if (options != _options)
        {
            try
            {
                _request.Update(options);
                _options = options;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Anforderung der Peaceerkennung konnte nicht aktualisiert werden.", ex);
            }
        }

        if (wasOff)
            _engine.Reset();
        StartTimer();
        if (modeChanged)
            OnPropertyChanged(nameof(Mode));
    }

    /// <summary>Startet den Schnurrbart für die auslösende Hand (Aktion „Schnurrbart“).</summary>
    public void StartMoustache(PeaceTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (_disposed)
            return;

        _context ??= SynchronizationContext.Current;
        EndTestSession();
        _tracker.StartSession(_settings.MoustacheTargets, _settings.MoustacheStyle, trigger.FaceTrackId,
            _settings.MoustacheVisibility, _settings.MoustacheDurationSeconds);
        AppLog.Info("Peaceerkennung: Schnurrbart gestartet.");
        StartTimer();
        RefreshMoustaches();
    }

    /// <summary>Admin „Bart testen“: Bart ohne Kamera auf einem Platzhaltergesicht bzw. am erkannten Gesicht (nie im echten Foto).</summary>
    public void StartTestMoustache()
    {
        if (_disposed)
            return;

        _context ??= SynchronizationContext.Current;
        var w = _imageWidth > 0 ? _imageWidth : 1920;
        var h = _imageHeight > 0 ? _imageHeight : 1080;
        _testSession = true;
        _testPlaceholder = !HasFreshRealFaces();
        _tracker.StartSession(MoustacheTargets.Everyone, _settings.MoustacheStyle, null, MoustacheVisibility.FixedDuration, TestMoustacheSeconds);
        if (_testPlaceholder)
            _tracker.OnFaces([PlaceholderFace], w, h);
        StartTimer();
        RefreshMoustaches();
    }

    /// <summary>Das Foto ist fertig (Zustand Vorschau): Ein Bart „bis zum nächsten Foto“ endet.</summary>
    public void OnPhotoCompleted()
    {
        if (_testSession)
            return;
        _tracker.OnPhotoCompleted();
        RefreshMoustaches();
    }

    /// <summary>Tick der Selbstzerstörung (<paramref name="secondsRemaining"/> Sekunden übrig); nur bei eingeschaltetem Ton.</summary>
    public void PlaySelfDestructTick(int secondsRemaining)
    {
        if (!_settings.SelfDestructSoundEnabled)
            return;
        _sound.Play(secondsRemaining <= 3 ? FunSoundCue.SelfDestructTickFast : FunSoundCue.SelfDestructTick);
    }

    public void PlayExplosion()
    {
        if (!_settings.SelfDestructSoundEnabled)
            return;
        _sound.Play(FunSoundCue.Explosion);
    }

    public void StopSounds() => _sound.Stop();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _mode = PeaceMode.Off;
        Deactivate();
    }

    // ---- intern

    private bool HasFreshRealFaces() =>
        _realFaces.Count > 0 && _time.GetUtcNow() - _realFacesAt <= FacesFreshness;

    private void Deactivate()
    {
        _generation++;
        var request = _request;
        _request = null;
        _options = null;
        if (request is not null)
        {
            request.Updated -= OnUpdated;
            request.StatusChanged -= OnStatusChanged;
            try
            {
                request.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Peaceerkennung konnte nicht freigegeben werden.", ex);
            }
        }

        StopTimer();
        _engine.GesturesEnabled = false;
        _engine.Reset();
        _faceTracker.Reset();
        _tracker.EndSession();
        _testSession = false;
        _testPlaceholder = false;
        _realFaces = [];
        _realFacesAt = DateTimeOffset.MinValue;
        if (_guestState is not null)
            _guestState.Current = null;
        Hands = [];
        Moustaches = [];
        PeaceDetectedNow = false;
        MoustacheActive = false;
    }

    private void EndTestSession()
    {
        if (!_testSession && !_testPlaceholder)
            return;
        _testSession = false;
        if (_testPlaceholder)
        {
            _testPlaceholder = false;
            // Platzhalter aus dem Gedächtnis des Trackers nehmen, damit er keine echte Sitzung „anfüttert“.
            _tracker.OnFaces(HasFreshRealFaces() ? _realFaces : [], Math.Max(1, _imageWidth), Math.Max(1, _imageHeight));
        }
    }

    private void OnEngineTriggered(object? sender, PeaceTrigger trigger)
    {
        try
        {
            ActionRequested?.Invoke(this, trigger);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Peaceerkennung: Aktion konnte nicht ausgeführt werden.", ex);
        }
    }

    private void OnUpdated(object? sender, GestureUpdate update)
    {
        if (_disposed || _mode == PeaceMode.Off || !ReferenceEquals(sender, _request))
            return;

        if (update.SourceWidth > 0 && update.SourceHeight > 0)
        {
            ImageWidth = update.SourceWidth;
            ImageHeight = update.SourceHeight;
        }
        ApplyStatus(update.Status);

        var w = _imageWidth > 0 ? _imageWidth : 1920;
        var h = _imageHeight > 0 ? _imageHeight : 1080;
        var aspect = w / h;

        var tracked = _faceTracker.Update(update.Faces, aspect);
        _realFaces = tracked;
        _realFacesAt = _time.GetUtcNow();

        _tracker.OnFaces(tracked, w, h);
        _engine.OnUpdate(update.Hands, tracked, aspect);
        RefreshMoustaches();
    }

    private void OnStatusChanged(object? sender, GestureStatus status)
    {
        if (_disposed || !ReferenceEquals(sender, _request))
            return;
        ApplyStatus(status);
    }

    private void ApplyStatus(GestureStatus status)
    {
        Status = status;
        var available = status.FaceUnavailableReason is null;
        if (MoustacheAvailable != available)
        {
            MoustacheAvailable = available;
            _engine.Configure(_settings, available);
        }
    }

    private void RefreshHands()
    {
        var snapshot = _engine.Snapshot;
        Hands = snapshot.Hands;
        PeaceDetectedNow = snapshot.Hands.Any(h => h.IsPeace);
    }

    private void RefreshMoustaches()
    {
        var views = _tracker.Views;
        if (!_moustaches.SequenceEqual(views))
            Moustaches = views;
        MoustacheActive = _tracker.IsActive;
        if (!_tracker.IsActive)
        {
            _testSession = false;
            _testPlaceholder = false;
        }
        if (_guestState is not null)
            _guestState.Current = _tracker.IsActive && !_testSession ? _tracker.ExportPlanInput() : null;
    }

    private void RefreshShown()
    {
        var shown = IsShown;
        if (shown == _lastShown)
            return;
        _lastShown = shown;
        OnPropertyChanged(nameof(IsShown));
    }

    private void StartTimer()
    {
        if (_timer is not null)
            return;
        var generation = ++_generation;
        _timer = _time.CreateTimer(_ => OnTimer(generation), null, TickInterval, TickInterval);
    }

    private void StopTimer()
    {
        var timer = _timer;
        _timer = null;
        timer?.Dispose();
    }

    // Timer-Thread (bzw. bei FakeTimeProvider der Aufrufer): auf den UI-Kontext zurück; veraltete Ticks verwirft die Generation.
    private void OnTimer(int generation)
    {
        var context = _context;
        if (context is null)
            Tick(generation);
        else
            context.Post(_ => Tick(generation), null);
    }

    private void Tick(int generation)
    {
        if (generation != _generation || _timer is null || _disposed)
            return;

        try
        {
            if (_testPlaceholder && _tracker.IsActive && !HasFreshRealFaces())
            {
                // Das Platzhaltergesicht „steht“ weiter im Bild, solange keine echten Gesichter da sind.
                _tracker.OnFaces([PlaceholderFace], _imageWidth > 0 ? _imageWidth : 1920, _imageHeight > 0 ? _imageHeight : 1080);
            }
            _tracker.Tick();
            _engine.Tick();
            RefreshMoustaches();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Peaceerkennung: Tick fehlgeschlagen.", ex);
            return;
        }

        if (_mode == PeaceMode.Off && !_tracker.IsActive)
            StopTimer();
    }

    private sealed class InertSound : IFunSoundPlayer
    {
        public void Play(FunSoundCue cue) { }
        public void Stop() { }
    }
}
