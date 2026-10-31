using System.Reflection;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;
using Fotobox.Core.Fun;

namespace Fotobox.ViewModels.Booth;

/// <summary>
/// Bindbare Sicht auf die Zielerfassung (<see cref="FunEngine"/>): Hält Erkennungsanforderung, Tick-Timer und Signalton und
/// stellt <see cref="Snapshot"/> sowie die Darstellungseinstellungen für <c>TargetOverlay</c> und <c>FunText</c> bereit.
/// Die Gastansicht (<see cref="BoothViewModel"/>) und die Admin-Vorschau nutzen je eine eigene Instanz. Nur auf dem UI-Thread
/// verwenden: <see cref="SetActive"/> merkt sich dessen <see cref="SynchronizationContext"/> für Erkennungs- und Timer-Ereignisse.
/// </summary>
public sealed class FunOverlayViewModel : ObservableObject, IDisposable
{
    /// <summary>Takt der Engine (zeitgesteuerte Übergänge, Verweildauer, verlorene Ziele).</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private static readonly PropertyInfo[] SettingProperties =
        typeof(FunSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .ToArray();

    private readonly IPersonDetectionService _detection;
    private readonly IFunSoundPlayer _sound;
    private readonly TimeProvider _time;
    private readonly FunEngine _engine;

    private IPersonDetectionRequest? _request;
    private ITimer? _timer;
    private SynchronizationContext? _context;
    private int _generation;

    private FunSettings _settings = new();
    private bool _mirror;
    private bool _active;
    private bool _simulating;
    private bool _configured;

    private FunSnapshot _snapshot = FunSnapshot.Empty;
    private int _imageWidth;
    private int _imageHeight;
    private NormRect _visibleRegion = NormRect.Full;
    private DetectionStatus? _status;

    public FunOverlayViewModel(IPersonDetectionService detection, IFunSoundPlayer sound, TimeProvider time)
    {
        _detection = detection;
        _sound = sound;
        _time = time;
        _engine = new FunEngine(time);
        _engine.Changed += (_, _) => OnEngineChanged();
        _engine.SoundCue += (_, cue) => _sound.Play(cue, _settings.LockOnIntensityPercent);
    }

    /// <summary>Instanz ohne Erkennung und Ton (nur für Ansichtsmodelle, die das Feature nie aktivieren, z. B. in Tests).</summary>
    public static FunOverlayViewModel CreateInert() => new(new InertDetection(), new InertSound(), TimeProvider.System);

    /// <summary>Ohne echte Dienste erzeugt (<see cref="CreateInert"/>); für den DI-Test der Composition Root.</summary>
    internal bool IsInert => _detection is InertDetection;

    /// <summary>Die Erkennung läuft (offene Anforderung); die Anzeige gehört dazu.</summary>
    public bool IsActive => _active;

    /// <summary>Overlay und Text dürfen gezeichnet werden: aktiv oder eine Simulation läuft noch.</summary>
    public bool IsShown => _active || _simulating;

    /// <summary>Aktueller Stand der Zielerfassung (ungespiegelte Boxen in normierten Koordinaten).</summary>
    public FunSnapshot Snapshot => _snapshot;

    /// <summary>Auflösung des analysierten Kamerabilds (0 = noch unbekannt, dann 16:9).</summary>
    public int ImageWidth => _imageWidth;
    public int ImageHeight => _imageHeight;

    public bool Mirror => _mirror;
    public TargetBoxStyle BoxStyle => _settings.BoxStyle;
    public TargetColor Color => _settings.Color;
    public LockOnAnimation Animation => _settings.Animation;
    /// <summary>Dauer der Einrast-Animation in Millisekunden (für <c>TargetOverlay</c>).</summary>
    public int LockOnMilliseconds => _settings.LockOnMilliseconds;
    /// <summary>Intensität der Einrast-Animation in Prozent (für <c>TargetOverlay</c>).</summary>
    public int LockOnIntensityPercent => _settings.LockOnIntensityPercent;
    public FunTextPosition TextPosition => _settings.TextPosition;
    public FunTextStyle TextStyle => _settings.TextStyle;
    public HintStyle TextHintStyle => _settings.TextHintStyle;

    /// <summary>Der Text wird gerade gezeigt (nur, solange <see cref="IsShown"/>).</summary>
    public bool TextVisible => IsShown && _snapshot.ShowText && _snapshot.Text.Length > 0;

    /// <summary>Aktueller Text (leer, wenn keiner gezeigt wird).</summary>
    public string Text => TextVisible ? _snapshot.Text : "";

    /// <summary>
    /// Rohdaten jeder Analyse (auf dem UI-Thread, vor der Engine), nur zum passiven Beobachten durch das Tracking-Overlay: Der Abonnent
    /// liest nur und berührt die Engine nie; eine Ausnahme darin wird abgefangen und geloggt. Ohne Abonnent kostet der Abgriff nichts.
    /// </summary>
    public event EventHandler<DetectionUpdate>? Observed;

    /// <summary>Zustand des Erkennungsdienstes (Laden, Ausweichen auf Gesicht …); null ohne offene Anforderung.</summary>
    public DetectionStatus? Status => _status;

    /// <summary>
    /// Sichtbarer Bildausschnitt in der Ansicht (setzt <c>TargetOverlay</c> per OneWayToSource); die Engine ignoriert Erkennungen
    /// außerhalb.
    /// </summary>
    public NormRect VisibleRegion
    {
        get => _visibleRegion;
        set
        {
            if (_visibleRegion == value)
                return;
            _visibleRegion = value;
            _engine.SetVisibleRegion(value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Schaltet die Erkennung ein oder aus. Aktiv: offene Anforderung, Engine konfiguriert, Tick-Timer läuft (wiederholter Aufruf
    /// mit gleichen Einstellungen tut nichts; geänderte Einstellungen wirken sofort, ein anderer <see cref="FunSettings.Scope"/>
    /// oder eine andere <see cref="FunSettings.Method"/> setzt die Engine zurück). Inaktiv: Anforderung freigeben, Timer aus, Engine
    /// leeren, Ton stoppen (eine laufende Simulation endet ebenfalls).
    /// </summary>
    public void SetActive(bool active, FunSettings settings, bool mirror)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!active)
        {
            Deactivate();
            return;
        }

        var applied = settings.Clone();
        if (!_active)
        {
            _context = SynchronizationContext.Current;
            var wasSimulating = _simulating;
            Apply(applied, mirror, resetOnChange: false);
            if (!wasSimulating)
                _engine.Reset();

            try
            {
                var request = _detection.Request(applied);
                request.Updated += OnUpdated;
                request.StatusChanged += OnStatusChanged;
                _request = request;
                _status = request.Status;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Personenerkennung konnte nicht angefordert werden.", ex);
            }

            _active = true;
            StartTimer();
            RaiseStateChanged();
            return;
        }

        if (SameSettings(_settings, applied) && _mirror == mirror)
            return;

        var settingsChanged = !SameSettings(_settings, applied);
        Apply(applied, mirror, resetOnChange: true);
        if (settingsChanged)
        {
            try
            {
                _request?.UpdateSettings(applied);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Einstellungen der Personenerkennung konnten nicht aktualisiert werden.", ex);
            }
        }
    }

    /// <summary>
    /// Übernimmt Einstellungen, ohne die Erkennung zu starten (für die Admin-Vorschau mit Entwurf). Ist die Erkennung aktiv, wirkt es
    /// wie <see cref="SetActive"/>.
    /// </summary>
    public void Configure(FunSettings settings, bool mirror)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_active)
            SetActive(true, settings, mirror);
        else
            Apply(settings.Clone(), mirror, resetOnChange: true);
    }

    /// <summary><see cref="Configure"/> mit den angegebenen Einstellungen, dann <see cref="Simulate()"/>.</summary>
    public void Simulate(FunSettings settings, bool mirror)
    {
        Configure(settings, mirror);
        Simulate();
    }

    /// <summary>
    /// Test-Knopf: ein simuliertes Ziel durchläuft Einrasten und Text (die Engine ignoriert währenddessen echte Erkennungen). Geht
    /// auch ohne Kamera und offene Anforderung; der Tick-Timer läuft dann, bis die Simulation vorbei ist. Nutzt die zuletzt
    /// übernommenen Einstellungen.
    /// </summary>
    public void Simulate()
    {
        if (!_configured)
            Apply(_settings, _mirror, resetOnChange: false);
        if (!_active)
            _context = SynchronizationContext.Current;

        _simulating = true;
        StartTimer();
        _engine.Simulate();
        RaiseStateChanged();
    }

    public void Dispose() => Deactivate();

    // ---- intern

    private void Deactivate()
    {
        if (!_active && !_simulating && _request is null && _timer is null)
            return;

        _active = false;
        _simulating = false;
        _generation++;

        var request = _request;
        _request = null;
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
                AppLog.Warn("Personenerkennung konnte nicht freigegeben werden.", ex);
            }
        }
        _status = null;

        StopTimer();
        _engine.Reset();
        _sound.Stop();
        _snapshot = _engine.Snapshot;
        RaiseStateChanged();
    }

    private void Apply(FunSettings settings, bool mirror, bool resetOnChange)
    {
        var previous = _settings;
        var reset = resetOnChange && _configured && (previous.Scope != settings.Scope || previous.Method != settings.Method);

        _settings = settings;
        _mirror = mirror;
        _configured = true;
        if (reset)
            _engine.Reset();
        _engine.Configure(settings, mirror);

        OnPropertyChanged(nameof(Mirror));
        if (previous.BoxStyle != settings.BoxStyle) OnPropertyChanged(nameof(BoxStyle));
        if (previous.Color != settings.Color) OnPropertyChanged(nameof(Color));
        if (previous.Animation != settings.Animation) OnPropertyChanged(nameof(Animation));
        if (previous.LockOnMilliseconds != settings.LockOnMilliseconds) OnPropertyChanged(nameof(LockOnMilliseconds));
        if (previous.LockOnIntensityPercent != settings.LockOnIntensityPercent) OnPropertyChanged(nameof(LockOnIntensityPercent));
        if (previous.TextPosition != settings.TextPosition) OnPropertyChanged(nameof(TextPosition));
        if (previous.TextStyle != settings.TextStyle) OnPropertyChanged(nameof(TextStyle));
        if (previous.TextHintStyle != settings.TextHintStyle) OnPropertyChanged(nameof(TextHintStyle));
    }

    private void OnUpdated(object? sender, DetectionUpdate update)
    {
        if (!_active || !ReferenceEquals(sender, _request))
            return;

        if (update.SourceWidth > 0 && update.SourceHeight > 0
            && (update.SourceWidth != _imageWidth || update.SourceHeight != _imageHeight))
        {
            _imageWidth = update.SourceWidth;
            _imageHeight = update.SourceHeight;
            OnPropertyChanged(nameof(ImageWidth));
            OnPropertyChanged(nameof(ImageHeight));
        }
        var statusChanged = !Equals(_status, update.Status);
        _status = update.Status;
        if (statusChanged)
            OnPropertyChanged(nameof(Status)); // Analyserate und Methode für die Statuszeile der Admin-Vorschau
        NotifyObserved(update);
        _engine.OnDetections(update.Detections);
    }

    // Passiver Abgriff für das Tracking-Overlay: nur der Verweis auf das Update, vor der Engine, in try/catch (ein Fehler des Beobachters
    // darf die Engine nie überspringen). Ohne Abonnent geschieht nichts.
    private void NotifyObserved(DetectionUpdate update)
    {
        var handler = Observed;
        if (handler is null)
            return;
        try
        {
            handler(this, update);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Tracking-Overlay: Beobachtung der Personenerkennung fehlgeschlagen.", ex);
        }
    }

    private void OnStatusChanged(object? sender, DetectionStatus status)
    {
        if (!_active || !ReferenceEquals(sender, _request))
            return;
        _status = status;
        OnPropertyChanged(nameof(Status));
    }

    private void OnEngineChanged()
    {
        var previousVisible = TextVisible;
        var previousText = Text;
        _snapshot = _engine.Snapshot;
        OnPropertyChanged(nameof(Snapshot));
        if (previousVisible != TextVisible)
            OnPropertyChanged(nameof(TextVisible));
        if (previousText != Text)
            OnPropertyChanged(nameof(Text));
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(TextVisible));
        OnPropertyChanged(nameof(Text));
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
        if (generation != _generation || _timer is null)
            return;

        try
        {
            _engine.Tick();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Zielerfassung: Tick fehlgeschlagen.", ex);
            return;
        }

        if (_simulating && _snapshot.Targets.Count == 0
            && _snapshot.Phase is not (FunPhase.LockingOn or FunPhase.ShowingText))
        {
            // Simulation vorbei (Ziel und Text sind weg); ohne offene Anforderung auch der Timer, damit nichts im Leerlauf läuft.
            _simulating = false;
            if (!_active)
                StopTimer();
            RaiseStateChanged();
        }
    }

    /// <summary>Gleiche Einstellungswerte (alle öffentlichen, gespeicherten Eigenschaften).</summary>
    private static bool SameSettings(FunSettings a, FunSettings b)
    {
        foreach (var property in SettingProperties)
        {
            if (!Equals(property.GetValue(a), property.GetValue(b)))
                return false;
        }
        return true;
    }

    private sealed class InertDetection : IPersonDetectionService
    {
        public IPersonDetectionRequest Request(FunSettings settings) => new InertRequest();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InertRequest : IPersonDetectionRequest
    {
        public event EventHandler<DetectionUpdate>? Updated { add { } remove { } }
        public event EventHandler<DetectionStatus>? StatusChanged { add { } remove { } }
        public DetectionStatus Status { get; } = DetectionStatus.Starting(DetectionMethod.Body);
        public void UpdateSettings(FunSettings settings) { }
        public void Dispose() { }
    }

    private sealed class InertSound : IFunSoundPlayer
    {
        public void Play(FunSoundCue cue) { }
        public void Stop() { }
    }
}
