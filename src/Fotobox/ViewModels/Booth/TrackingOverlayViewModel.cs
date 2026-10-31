using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Tracking;

namespace Fotobox.ViewModels.Booth;

/// <summary>
/// Tracking-Overlay der Gastansicht: zeigt auf Tastendruck, was die laufenden Erkennungen sehen (Personen-Boxen, Gesichter, Handpunkte,
/// Gesten, Bärte, Statuszeile). Rein passiv: Es fordert nie eine Erkennung an, ändert keine Anforderung und berührt keine Engine; es liest
/// nur die Rohdaten, die <see cref="PeaceOverlayViewModel.Observed"/> und <see cref="FunOverlayViewModel.Observed"/> melden, und den
/// Stand der Engines. Sichtbar ist es nur, wenn es eingeschaltet ist (Taste, nach dem Start immer aus, nie gespeichert) <b>und</b> die
/// Gastansicht im Leerlauf oder im Countdown ist (<see cref="SetStateAllowed"/>). Nur dann gibt es Abonnenten und einen Timer; sonst
/// geschieht keine Zusatzarbeit. Der Timer (<see cref="TickInterval"/>) lässt alte Daten verschwinden und liest den Stand der Engines neu.
/// Nur auf dem UI-Thread verwenden (der Timer postet auf dessen <see cref="SynchronizationContext"/>, gemerkt beim Einschalten). Nie im
/// Foto oder Video: Die Anzeige zeichnet nur ins Livebild.
/// </summary>
public sealed class TrackingOverlayViewModel : ObservableObject, IDisposable
{
    /// <summary>Takt, in dem der Stand neu gelesen wird und Daten altern.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly FunOverlayViewModel _fun;
    private readonly PeaceOverlayViewModel _peace;
    private readonly TrackingOverlayFrameBuilder _builder;

    private SynchronizationContext? _context;
    private ITimer? _timer;
    private int _generation;
    private bool _enabled;
    private bool _stateAllowed;
    private bool _visible;
    private bool _disposed;
    private bool _warned;
    private TrackingOverlayFrame _frame = TrackingOverlayFrame.Empty;

    public TrackingOverlayViewModel(ISettingsStore settings, TimeProvider time, FunOverlayViewModel fun, PeaceOverlayViewModel peace)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _fun = fun ?? throw new ArgumentNullException(nameof(fun));
        _peace = peace ?? throw new ArgumentNullException(nameof(peace));
        _builder = new TrackingOverlayFrameBuilder(time);
        _context = SynchronizationContext.Current;
    }

    /// <summary>Das Overlay ist eingeschaltet (Taste). Startet immer <c>false</c>, wird nie gespeichert.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Das Overlay ist zu sehen: eingeschaltet und die Gastansicht im Leerlauf oder im Countdown.</summary>
    public bool IsVisible => _visible;

    /// <summary>Die Statuszeile ist zu sehen: <see cref="IsVisible"/> und in den Einstellungen eingeschaltet.</summary>
    public bool ShowStatusLine => _visible && Config.ShowStatusLine;

    /// <summary>Was gerade gezeichnet wird (ersetzt als Ganzes; leer, solange unsichtbar).</summary>
    public TrackingOverlayFrame Frame => _frame;

    /// <summary>Text der Statuszeile (je Erkennung aktiv/aus und Analysen pro Sekunde).</summary>
    public string StatusText => _frame.StatusText;

    private TrackingOverlaySettings Config => _settings.Current.TrackingOverlay ?? new TrackingOverlaySettings();

    /// <summary>Schaltet das Overlay um und loggt es (ohne Messwerte).</summary>
    public void Toggle()
    {
        if (_disposed)
            return;
        _enabled = !_enabled;
        AppLog.Info(_enabled ? "Tracking-Overlay ein" : "Tracking-Overlay aus");
        OnPropertyChanged(nameof(IsEnabled));
        Evaluate();
    }

    /// <summary>
    /// Meldet, ob die Gastansicht gerade in einem Zustand ist, in dem das Overlay erscheinen darf (Leerlauf, Countdown). Beim Verlassen
    /// dieser Zustände verschwindet es samt Abonnenten und Timer, beim Wiedereintritt (und noch eingeschaltet) startet es frisch.
    /// </summary>
    public void SetStateAllowed(bool allowed)
    {
        if (_stateAllowed == allowed)
            return;
        _stateAllowed = allowed;
        Evaluate();
    }

    /// <summary>Stoppt Timer und Abonnenten. Ab dann ist das Overlay dauerhaft aus.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _enabled = false;
        Evaluate();
    }

    private void Evaluate()
    {
        var want = _enabled && _stateAllowed && !_disposed;
        if (want == _visible)
            return;
        if (want)
            Show();
        else
            Hide();
    }

    private void Show()
    {
        _visible = true;
        _context = SynchronizationContext.Current ?? _context;
        _builder.Reset();
        _peace.Observed += OnPeaceObserved;
        _fun.Observed += OnFunObserved;
        _settings.SettingsChanged += OnSettingsChanged;
        StartTimer();
        Rebuild();
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ShowStatusLine));
    }

    private void Hide()
    {
        _visible = false;
        _peace.Observed -= OnPeaceObserved;
        _fun.Observed -= OnFunObserved;
        _settings.SettingsChanged -= OnSettingsChanged;
        StopTimer();
        _builder.Reset();
        SetFrame(TrackingOverlayFrame.Empty);
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ShowStatusLine));
    }

    // ---------------------------------------------------------------- Beobachter

    // Beide auf dem UI-Thread, aus dem Abgriff der Ansichtsmodelle (dort in try/catch). Hier nur merken und neu bauen.
    private void OnPeaceObserved(object? sender, GestureObservation observation)
    {
        if (!_visible)
            return;
        try
        {
            _builder.OnGestureUpdate(observation);
        }
        catch (Exception ex)
        {
            Warn(ex);
            return;
        }
        Rebuild();
    }

    private void OnFunObserved(object? sender, DetectionUpdate update)
    {
        if (!_visible)
            return;
        try
        {
            _builder.OnDetections(update);
        }
        catch (Exception ex)
        {
            Warn(ex);
            return;
        }
        Rebuild();
    }

    // Beliebiger Thread: auf den UI-Kontext, damit geänderte Ebenen, Stil und Größe sofort wirken.
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var context = _context;
        if (context is null)
            ApplySettings();
        else
            context.Post(_ => ApplySettings(), null);
    }

    private void ApplySettings()
    {
        if (!_visible)
            return;
        Rebuild();
        OnPropertyChanged(nameof(ShowStatusLine));
    }

    // ---------------------------------------------------------------- Timer

    private void StartTimer()
    {
        if (_timer is not null)
            return;
        var generation = ++_generation;
        _timer = _time.CreateTimer(_ => OnTimer(generation), null, TickInterval, TickInterval);
    }

    private void StopTimer()
    {
        _generation++;
        var timer = _timer;
        _timer = null;
        timer?.Dispose();
    }

    // Timer-Thread (bei FakeTimeProvider der Aufrufer): auf den UI-Kontext zurück; veraltete Takte verwirft die Generation.
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
        if (generation != _generation || !_visible || _disposed)
            return;
        Rebuild();
    }

    // ---------------------------------------------------------------- Aufbau

    private void Rebuild()
    {
        if (!_visible)
            return;
        try
        {
            var live = new TrackingLive(
                _peace.Mode != PeaceMode.Off, _peace.Status, _fun.IsActive, _fun.Status,
                _peace.Hands, _peace.Moustaches, _peace.Heart, _peace.HeartDetectedNow, _peace.MiddleFingerDetectedNow,
                _fun.Snapshot.Targets);
            SetFrame(_builder.Build(_settings.Current, live));
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    // Ersetzt den Stand nur bei einer Änderung: ohne Inhalt und mit gleichem Statustext gibt es nichts neu zu zeichnen.
    private void SetFrame(TrackingOverlayFrame frame)
    {
        var old = _frame;
        if (!frame.HasContent && !old.HasContent && frame.StatusText == old.StatusText)
            return;
        var statusChanged = frame.StatusText != old.StatusText;
        _frame = frame;
        OnPropertyChanged(nameof(Frame));
        if (statusChanged)
            OnPropertyChanged(nameof(StatusText));
    }

    private void Warn(Exception ex)
    {
        if (_warned)
            return;
        _warned = true;
        AppLog.Warn("Tracking-Overlay: Aufbau fehlgeschlagen.", ex);
    }
}
