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
    /// <summary>Im schnellen Gesichtsmodus läuft die Handstufe nur in jedem n-ten Zyklus.</summary>
    private const int FastHandDivisor = 2;
    private const int MaxFastRate = 20;
    private readonly FaceTracker _faceTracker;
    private readonly PeaceEngine _engine;
    private readonly MiddleFingerEngine _mfEngine;
    private readonly MoustacheTracker _tracker;

    private IGestureRequest? _request;
    private GestureRequestOptions? _options;
    private ITimer? _timer;
    private SynchronizationContext? _context;
    private int _generation;
    private bool _disposed;

    private PeaceSettings _settings = new();
    // Mittelfinger: Einstellungen (null = aus), von außen gemeldete Ziele (z. B. laufende Selbstzerstörung) und ob gerade etwas aufzuheben ist.
    private MiddleFingerSettings? _mf;
    private MiddleFingerTargets _mfExternal;
    private bool _mfWatching;
    private PeaceMode _mode;
    private IReadOnlyList<MoustacheView> _moustaches = [];
    private IReadOnlyList<HandView> _hands = [];
    private IReadOnlyList<FaceLandmarks> _faces = [];
    private double _imageWidth;
    private double _imageHeight;
    private bool _peaceDetectedNow;
    private bool _moustacheActive;
    private bool _lastShown;
    private GestureStatus _status = GestureStatus.Starting;
    private bool _moustacheAvailable;
    private int _faceCount;

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
        _mfEngine = new MiddleFingerEngine(time);
        _tracker = new MoustacheTracker(time, random);
        _engine.Triggered += OnEngineTriggered;
        _engine.Changed += (_, _) => RefreshHands();
        _mfEngine.Triggered += OnMiddleFingerEngineTriggered;
        _mfEngine.Changed += (_, _) => OnPropertyChanged(nameof(MiddleFingerDetectedNow));
    }

    /// <summary>Instanz ohne Erkennung und Ton (für Ansichtsmodelle, die das Feature nie aktivieren, z. B. in Tests).</summary>
    public static PeaceOverlayViewModel CreateInert() =>
        new(new InertGestureDetectionService(), TimeProvider.System, new InertSound()) { IsInert = true };

    /// <summary>Platzhalter ohne Erkennung und Ton (<see cref="CreateInert"/>).</summary>
    public bool IsInert { get; private init; }

    /// <summary>Teilt diese Instanz den Bart-Zustand mit der Foto-Planung (nur die Gastansicht)?</summary>
    internal bool HasGuestState => _guestState is not null;

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

    /// <summary>Gesichter der letzten Analyse mit ihren fünf Punkten (nur für die Punkteanzeige der Admin-Vorschau).</summary>
    public IReadOnlyList<FaceLandmarks> Faces
    {
        get => _faces;
        private set => SetProperty(ref _faces, value);
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

    /// <summary>Anzahl der Gesichter der letzten Analyse (Statuszeile der Admin-Vorschau).</summary>
    public int FaceCount
    {
        get => _faceCount;
        private set => SetProperty(ref _faceCount, value);
    }

    /// <summary>
    /// Zeigt gerade (innerhalb der Aussetzer-Toleranz) irgendeine Hand den Mittelfinger? Nur für die Admin-Vorschau; die Auswertung
    /// läuft in jeder Betriebsart außer <see cref="PeaceMode.Off"/>, solange Hände angefordert sind.
    /// </summary>
    public bool MiddleFingerDetectedNow => _mfEngine.DetectedNow;

    /// <summary>
    /// Gibt es gerade etwas, das ein Mittelfinger aufheben würde (sichtbarer Bart oder von außen gemeldete Ziele), und ist die Erkennung
    /// dafür eingeschaltet? Nur dann werden im Countdown Hände angefordert (CPU) und der Mittelfinger gezählt.
    /// </summary>
    public bool MiddleFingerWatching => _mfWatching;

    /// <summary>
    /// Zeigt noch ein Bart etwas an: aktive Sitzung oder die Verschwinden-Animation nach <see cref="VanishMoustaches"/> (0,3 s). Hält
    /// die Betriebsart der Gastansicht am Leben, damit die Animation nicht abbricht. Zeitabhängig, ohne Änderungsmeldung.
    /// </summary>
    public bool HasMoustacheVisuals => _tracker.IsActive || _tracker.IsVanishing;

    /// <summary>
    /// Ein Mittelfinger wurde lange genug gehalten, während etwas aufzuheben war (auf dem UI-Thread). Das Ansichtsmodell hebt selbst
    /// nichts auf: Der Abonnent entscheidet (Gastansicht: Selbstzerstörung abbrechen und <see cref="VanishMoustaches"/>).
    /// </summary>
    public event EventHandler? MiddleFingerTriggered;

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
    /// <para>
    /// Mittelfinger: Mit <paramref name="middleFinger"/> (null = aus) und den von außen gemeldeten <paramref name="external"/>-Zielen
    /// (z. B. <see cref="MiddleFingerTargets.SelfDestruct"/> im Selbstzerstörungs-Countdown; ein aktiver Bart zählt von selbst) wird
    /// <see cref="MiddleFingerWatching"/> bestimmt. Nur dann fordert <see cref="PeaceMode.FollowMoustache"/> auch Hände an.
    /// </para>
    /// </summary>
    public void SetMode(PeaceMode mode, PeaceSettings settings, bool mirror,
        MiddleFingerSettings? middleFinger = null, MiddleFingerTargets external = MiddleFingerTargets.None)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed)
            return;
        _ = mirror;

        _settings = settings.Clone();
        _mf = middleFinger?.Clone();
        _mfExternal = external;
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
        _engine.GesturesEnabled = mode == PeaceMode.Watching;
        _tracker.SetHoldExpiry(mode == PeaceMode.FollowMoustache);
        _mfEngine.Configure(_mf ?? new MiddleFingerSettings { Enabled = false }, _settings.AnalysesPerSecond);
        UpdateMiddleFingerWatching();

        var options = BuildOptions();
        _faceTracker.LostAfter = TimeSpan.FromSeconds(PeaceEngine.AnalysisIntervalFactor / Math.Max(1, options.AnalysesPerSecond));

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
        {
            _engine.Reset();
            _mfEngine.Reset();
        }
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
            _settings.MoustacheVisibility, _settings.MoustacheDurationSeconds, _settings.RandomStyles);
        AppLog.Info("Peaceerkennung: Schnurrbart gestartet.");
        StartTimer();
        RefreshMoustaches();
    }

    /// <summary>Admin „Bart testen“: Bart ohne Kamera auf einem Platzhaltergesicht bzw. am erkannten Gesicht (nie im echten Foto).</summary>
    public void StartTestMoustache() => StartTestMoustache(TestMoustacheSeconds);

    /// <summary>Wie <see cref="StartTestMoustache()"/> mit eigener Dauer (Sekunden, z. B. länger für den Test der Mittelfingererkennung).</summary>
    public void StartTestMoustache(int seconds)
    {
        if (_disposed)
            return;

        _context ??= SynchronizationContext.Current;
        var w = _imageWidth > 0 ? _imageWidth : 1920;
        var h = _imageHeight > 0 ? _imageHeight : 1080;
        _testSession = true;
        _testPlaceholder = !HasFreshRealFaces();
        _tracker.StartSession(MoustacheTargets.Everyone, _settings.MoustacheStyle, null, MoustacheVisibility.FixedDuration, Math.Max(1, seconds), _settings.RandomStyles);
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

    /// <summary>
    /// Hebt alle sichtbaren Bärte auf (Mittelfinger): Sie verschwinden mit einer 0,3-s-Animation, der Bart-Zustand für die
    /// Foto-Planung ist sofort leer (ein Foto danach bekommt keinen Bart) und der schnelle Gesichtsmodus endet.
    /// </summary>
    /// <returns>true, wenn ein Bart sichtbar war.</returns>
    public bool VanishMoustaches()
    {
        if (_disposed)
            return false;
        var had = _tracker.Dismiss();
        RefreshMoustaches();
        return had;
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

    /// <summary>
    /// Bedarf der Anforderung zur Betriebsart: Im Leerlauf Gesichter und Hände mit der eingestellten Rate; solange ein Bart sichtbar
    /// ist (schneller Gesichtsmodus), laufen die Gesichter doppelt so schnell (mindestens <see cref="FollowRate"/>) und die Handstufe
    /// nur in jedem zweiten Zyklus, also weiter mit der eingestellten Handrate (die <see cref="PeaceEngine"/>-Fristen gelten dafür).
    /// In <see cref="PeaceMode.FollowMoustache"/> kommen Hände nur hinzu, solange <see cref="MiddleFingerWatching"/> gilt (mit Bart wie im
    /// schnellen Gesichtsmodus, ohne Bart mit der eingestellten Rate).
    /// </summary>
    private GestureRequestOptions BuildOptions()
    {
        var watching = _mode == PeaceMode.Watching;
        // Solange etwas aufzuheben ist, reicht für den Mittelfinger die niedrigere der beiden Mindestwerte.
        var minHand = _mfWatching && _mf is not null ? Math.Min(_settings.MinHandScore, _mf.MinHandScore) : _settings.MinHandScore;

        // Ohne Hände bleibt es bei billigen Gesichtern (Hände brauchen ohnehin die Gesichter als Suchbereiche).
        if (!watching && !_mfWatching)
            return new GestureRequestOptions(GestureNeeds.Faces, FollowRate, MinFaceScore, minHand);

        if (_tracker.IsActive)
        {
            var cycleRate = Math.Clamp(Math.Max(FollowRate, FastHandDivisor * Math.Max(1, _settings.AnalysesPerSecond)), 1, MaxFastRate);
            return new GestureRequestOptions(GestureNeeds.Faces | GestureNeeds.Hands, cycleRate, MinFaceScore, minHand, FastHandDivisor);
        }
        return new GestureRequestOptions(GestureNeeds.Faces | GestureNeeds.Hands, _settings.AnalysesPerSecond, MinFaceScore, minHand);
    }

    // Gibt es etwas aufzuheben? Setzt die Engine scharf bzw. aus; true, wenn sich das geändert hat (dann muss die Anforderung neu bestimmt werden).
    private bool UpdateMiddleFingerWatching()
    {
        var watching = _mode != PeaceMode.Off && _mf is not null
            && _mf.Wants(_mfExternal | (_tracker.IsActive ? MiddleFingerTargets.Moustache : MiddleFingerTargets.None));
        if (watching == _mfWatching)
            return false;
        _mfWatching = watching;
        _mfEngine.Applicable = watching;
        OnPropertyChanged(nameof(MiddleFingerWatching));
        return true;
    }

    // Schaltet die Anforderung zwischen normalem und schnellem Gesichtsmodus um, wenn ein Bart beginnt oder endet.
    private void RefreshRequest()
    {
        if (_request is null || _mode == PeaceMode.Off)
            return;
        var options = BuildOptions();
        if (options == _options)
            return;
        try
        {
            _request.Update(options);
            _options = options;
            _faceTracker.LostAfter = TimeSpan.FromSeconds(PeaceEngine.AnalysisIntervalFactor / Math.Max(1, options.AnalysesPerSecond));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Anforderung der Peaceerkennung konnte nicht aktualisiert werden.", ex);
        }
    }

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
        var mfWasWatching = _mfWatching;
        _mfWatching = false;
        _mfEngine.Applicable = false;
        _mfEngine.Reset();
        _mfExternal = MiddleFingerTargets.None;
        if (mfWasWatching)
            OnPropertyChanged(nameof(MiddleFingerWatching));
        _faceTracker.Reset();
        _tracker.EndSession();
        // Ausgeschaltet: Ein laufendes Verschwinden (Mittelfinger) endet mit, sonst erschiene bei erneuter Aktivierung binnen 0,3 s ein Geisterbart.
        _tracker.ClearVanishing();
        _testSession = false;
        _testPlaceholder = false;
        _realFaces = [];
        _realFacesAt = DateTimeOffset.MinValue;
        if (_guestState is not null)
            _guestState.Current = null;
        Hands = [];
        Moustaches = [];
        FaceCount = 0;
        Faces = [];
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

    private void OnMiddleFingerEngineTriggered(object? sender, EventArgs e)
    {
        try
        {
            MiddleFingerTriggered?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Mittelfingererkennung: Aktion konnte nicht ausgeführt werden.", ex);
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

        // Wiederholt ein Update nur die Gesichter eines früheren Updates desselben Zyklus (schneller Gesichtsmodus), werden sie nicht
        // erneut verfolgt oder dem Bart gemeldet (das verfälschte Geschwindigkeit und Glättung); sie dienen nur der Handzuordnung.
        var tracked = update.FacesFresh ? _faceTracker.Update(update.Faces, aspect) : _realFaces;
        if (update.FacesFresh)
        {
            _realFaces = tracked;
            _realFacesAt = _time.GetUtcNow();
            FaceCount = tracked.Count;
            Faces = update.Faces;
            _tracker.OnFaces(tracked, w, h, update.FrameTime);
        }
        // Ein reines Gesichts-Update sagt nichts über die Hände: die Engine bekommt sie erst mit dem Ergebnis der Handstufe.
        if (update.HandsIncluded)
        {
            // Ohne angeforderte Hände liefert der Dienst leere Handlisten; die Peace-Engine darf sie nicht als beobachtete Ruhe
            // werten, sonst würde sie ohne Handdaten wieder scharf (z. B. ein über Foto und Vorschau gehaltenes Peace nach dem Bart).
            if (_options is { } options && (options.Needs & GestureNeeds.Hands) != 0)
                _engine.OnUpdate(update.Hands, tracked, aspect);
            _mfEngine.OnUpdate(update.Hands, aspect);
        }
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
        var wasActive = MoustacheActive;
        MoustacheActive = _tracker.IsActive;
        var mfChanged = UpdateMiddleFingerWatching();
        if (wasActive != _tracker.IsActive || mfChanged)
            RefreshRequest();
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
            _mfEngine.Tick();
            RefreshMoustaches();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Peaceerkennung: Tick fehlgeschlagen.", ex);
            return;
        }

        if (_mode == PeaceMode.Off && !_tracker.IsActive && !_tracker.IsVanishing)
            StopTimer();
    }

    private sealed class InertSound : IFunSoundPlayer
    {
        public void Play(FunSoundCue cue) { }
        public void Stop() { }
    }
}
