using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Services.Detection.Acceleration;
using Fotobox.Services.Diagnostics;

namespace Fotobox.ViewModels.Booth;

/// <summary>
/// Leistungsanzeige der Gastansicht (Ein/Aus per Taste, nach dem Start immer aus, nie gespeichert). Misst nur, solange sie sichtbar ist:
/// ein Timer (Takt aus den Einstellungen) baut je Takt genau einmal die Zeilen aus dem zuletzt gemessenen Systemstand und den passiven
/// App-Werten (Kamera- und Anzeige-FPS, Rate und Rechenzeit der Erkennungen, Geräte je Modell; keine eigene Erkennungs-Anforderung)
/// und stößt danach – höchstens eine zugleich – eine Messung der Systemwerte auf dem Threadpool an. Die Messung hat eine Frist
/// (<see cref="SampleTimeout"/>); ein hängender Lauf blockiert weder die Oberfläche noch wird ein zweiter gestartet, die Systemwerte
/// zeigen dann „–“. Fehler führen nie zu einem Absturz. Nur auf dem UI-Thread verwenden (der Timer postet auf dessen
/// <see cref="SynchronizationContext"/>, gemerkt beim Einschalten).
/// </summary>
public sealed class PerformanceOverlayViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Frist einer Messung der Systemwerte. Läuft sie länger (Treiber hängt), wird ihr Ergebnis verworfen und die Systemwerte zeigen „–“;
    /// ein neuer Lauf startet erst, wenn der hängende wirklich zu Ende ist (nie zwei Messungen zugleich).
    /// </summary>
    public static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Ein Status der Zielerfassung bzw. Gestenerkennung, der länger nicht mehr aktualisiert wurde, gilt als veraltet (die Rate bleibt im
    /// Dienst stehen, wenn keine Bilder mehr analysiert werden): Die Anzeige zeigt dann „–“ statt eines alten Werts.
    /// </summary>
    public static readonly TimeSpan StatusStaleAfter = TimeSpan.FromSeconds(5);

    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly FunOverlayViewModel _fun;
    private readonly PeaceOverlayViewModel _peace;
    private readonly ISystemMetricsSource? _metrics;
    private readonly IAccelerationService? _acceleration;
    private readonly ICameraFrameStats? _cameraFrames;
    private readonly DisplayFrameCounter? _displayFrames;
    private readonly object _gate = new();

    // „Kein Zeitstempel“ (ein TimeProvider-Zeitstempel kann 0 sein, z. B. bei FakeTimeProvider).
    private const long NoStamp = long.MinValue;

    private SynchronizationContext? _context;
    private ITimer? _timer;
    private int _generation;
    // Zählt Ein- und Ausblenden: Ergebnisse einer Messung aus einer früheren Sichtbarkeit werden verworfen.
    private int _epoch;
    private bool _visible;
    private bool _disposed;
    private int _timerMs;

    // Messung der Systemwerte (unter _gate); _inFlight getrennt per Interlocked (wird auch am Ende des Threadpool-Laufs gelöscht).
    private int _inFlight;
    private SystemMetricsSnapshot? _snapshot;
    private int _goodSamples;
    private Task _pendingSample = Task.CompletedTask;

    // FPS-Zähler: letzter Stand und Zeitpunkt des vorigen Takts; letzte Werte für Neuaufbau zwischen den Takten (nur im Takt, auf dem UI-Thread).
    private long _lastCameraCount = -1;
    private long _lastDisplayCount = -1;
    private long _lastFpsStamp = NoStamp;
    private double? _cameraFps;
    private double? _displayFps;

    // Zeitpunkte (TimeProvider-Zeitstempel) der letzten Statusänderung von Zielerfassung und Gesten; NoStamp = noch nie.
    private long _funStamp = NoStamp;
    private long _peaceStamp = NoStamp;

    private IReadOnlyList<PerformanceOverlayRow> _rows = [];
    private bool _warnedTick;

    public PerformanceOverlayViewModel(ISettingsStore settings, TimeProvider time, FunOverlayViewModel fun, PeaceOverlayViewModel peace,
        ISystemMetricsSource? metrics = null, IAccelerationService? acceleration = null, ICameraFrameStats? cameraFrames = null,
        DisplayFrameCounter? displayFrames = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _fun = fun ?? throw new ArgumentNullException(nameof(fun));
        _peace = peace ?? throw new ArgumentNullException(nameof(peace));
        _metrics = metrics;
        _acceleration = acceleration;
        _cameraFrames = cameraFrames;
        _displayFrames = displayFrames;
        _context = SynchronizationContext.Current;

        _fun.PropertyChanged += OnFunPropertyChanged;
        _peace.PropertyChanged += OnPeacePropertyChanged;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>Die Anzeige ist sichtbar (und misst). Startet immer <c>false</c>, wird nie gespeichert.</summary>
    public bool IsVisible
    {
        get
        {
            lock (_gate)
                return _visible;
        }
    }

    /// <summary>Die aktuellen Zeilen (ersetzt als Ganzes, nur wenn sie sich geändert haben – höchstens einmal je Takt).</summary>
    public IReadOnlyList<PerformanceOverlayRow> Rows => _rows;

    /// <summary>Balken unter den Zeilen mit Auslastung/Speicher (Darstellung „Balken“).</summary>
    public bool ShowBars => Config.Style == PerformanceOverlayStyle.Bars;

    /// <summary>Schriftgröße in Bildschirmpunkten.</summary>
    public double FontSize => Math.Clamp(Config.FontSize, SettingsLimits.PerformanceOverlayFontSizeMin, SettingsLimits.PerformanceOverlayFontSizeMax);

    /// <summary>Deckkraft des schwarzen Hintergrunds (0..1).</summary>
    public double BackgroundOpacity =>
        Math.Clamp(Config.BackgroundOpacityPercent, SettingsLimits.PerformanceOverlayBackgroundOpacityMin, SettingsLimits.PerformanceOverlayBackgroundOpacityMax) / 100.0;

    /// <summary>Mindestbreite der Wertspalte (reserviert Platz für die breitesten Werte, damit die Anzeige beim Aktualisieren nicht springt).</summary>
    public double ValueMinWidth => FontSize * 8.5;

    /// <summary>Die laufende Messung (für Tests, die darauf warten); abgeschlossen, wenn keine läuft.</summary>
    internal Task PendingSample
    {
        get
        {
            lock (_gate)
                return _pendingSample;
        }
    }

    private PerformanceOverlaySettings Config => _settings.Current.PerformanceOverlay ?? new PerformanceOverlaySettings();

    /// <summary>Schaltet die Anzeige um und loggt es (ohne Messwerte).</summary>
    public void Toggle()
    {
        bool show;
        lock (_gate)
        {
            if (_disposed)
                return;
            show = !_visible;
        }
        if (show)
            Show();
        else
            Hide();
    }

    private void Show()
    {
        int generation;
        lock (_gate)
        {
            if (_disposed || _visible)
                return;
            _visible = true;
            _epoch++;
            _context = SynchronizationContext.Current ?? _context;
            // Frischer Start: nichts von einem früheren Einblenden anzeigen.
            _snapshot = null;
            _goodSamples = 0;
            _lastCameraCount = -1;
            _lastDisplayCount = -1;
            _cameraFps = null;
            _displayFps = null;
            _timerMs = RefreshMs();
            generation = StartTimerLocked();
        }
        AppLog.Info("Leistungsanzeige ein");
        OnPropertyChanged(nameof(IsVisible));
        Tick(generation);
    }

    private void Hide()
    {
        lock (_gate)
        {
            if (_disposed || !_visible)
                return;
            _visible = false;
            _epoch++;
            StopTimerLocked();
            _snapshot = null;
        }
        AppLog.Info("Leistungsanzeige aus");
        SetRows([]);
        OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>Stoppt den Timer und gibt die Abos frei. Die Messquelle gehört dem Container und wird hier nicht freigegeben.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _visible = false;
            StopTimerLocked();
        }
        _fun.PropertyChanged -= OnFunPropertyChanged;
        _peace.PropertyChanged -= OnPeacePropertyChanged;
        _settings.SettingsChanged -= OnSettingsChanged;
    }

    // ---------------------------------------------------------------- Einstellungen

    private int RefreshMs() =>
        Math.Clamp(Config.RefreshMs, SettingsLimits.PerformanceOverlayRefreshMsMin, SettingsLimits.PerformanceOverlayRefreshMsMax);

    // Beliebiger Thread: auf den UI-Kontext, damit Takt, Schrift, Deckkraft, Stil und gewählte Werte sofort (ohne Neustart) wirken.
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
        try
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                // Neuer Takt: Timer mit dem neuen Intervall neu starten (ohne Sichtbarkeit zu ändern).
                if (_visible && _timerMs != RefreshMs())
                {
                    _timerMs = RefreshMs();
                    StopTimerLocked();
                    StartTimerLocked();
                }
            }
            OnPropertyChanged(nameof(ShowBars));
            OnPropertyChanged(nameof(FontSize));
            OnPropertyChanged(nameof(BackgroundOpacity));
            OnPropertyChanged(nameof(ValueMinWidth));
            // Gewählte Werte: sofort neu aufbauen (aus dem zuletzt Gemessenen, ohne neue Messung).
            Rebuild();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Leistungsanzeige: Einstellungen konnten nicht übernommen werden.", ex);
        }
    }

    // ---------------------------------------------------------------- Status von Zielerfassung und Gesten

    private void OnFunPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FunOverlayViewModel.Status))
            Volatile.Write(ref _funStamp, _time.GetTimestamp());
    }

    private void OnPeacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PeaceOverlayViewModel.Status))
            Volatile.Write(ref _peaceStamp, _time.GetTimestamp());
    }

    private bool IsFresh(long stamp) =>
        stamp != NoStamp && _time.GetElapsedTime(stamp) <= StatusStaleAfter;

    // ---------------------------------------------------------------- Timer

    private int StartTimerLocked()
    {
        var generation = ++_generation;
        var interval = TimeSpan.FromMilliseconds(_timerMs);
        _timer = _time.CreateTimer(_ => OnTimer(generation), null, interval, interval);
        return generation;
    }

    private void StopTimerLocked()
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
        GpuSelection? gpu = null;
        int epoch;
        try
        {
            lock (_gate)
            {
                if (generation != _generation || !_visible || _disposed)
                    return;
                epoch = _epoch;
            }

            UpdateFps();
            Rebuild();
            gpu = _acceleration is null ? null : SystemMetricsLogic.SelectionFromPlan(_acceleration.State);
        }
        catch (Exception ex)
        {
            if (!_warnedTick)
            {
                _warnedTick = true;
                AppLog.Warn("Leistungsanzeige: Aktualisierung fehlgeschlagen.", ex);
            }
            return;
        }
        StartSample(epoch, gpu);
    }

    // Kamera- und Anzeige-FPS aus der Differenz der Zähler seit dem vorigen Takt.
    private void UpdateFps()
    {
        var now = _time.GetTimestamp();
        var elapsed = _lastFpsStamp == NoStamp ? TimeSpan.Zero : _time.GetElapsedTime(_lastFpsStamp, now);
        _cameraFps = Rate(_cameraFrames?.FramesReceived, ref _lastCameraCount, elapsed);
        _displayFps = Rate(_displayFrames?.Count, ref _lastDisplayCount, elapsed);
        _lastFpsStamp = now;
    }

    private static double? Rate(long? count, ref long last, TimeSpan elapsed)
    {
        if (count is not { } current)
            return null;
        var previous = last;
        last = current;
        if (previous < 0 || elapsed <= TimeSpan.Zero || current < previous)
            return null;
        return (current - previous) / elapsed.TotalSeconds;
    }

    // ---------------------------------------------------------------- Messung der Systemwerte

    private void StartSample(int epoch, GpuSelection? gpu)
    {
        var source = _metrics;
        if (source is null)
            return;
        // Läuft noch eine (auch eine überfällige, hängende) Messung, wird keine zweite gestartet.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;

        try
        {
            lock (_gate)
            {
                if (epoch != _epoch || !_visible || _disposed)
                {
                    Volatile.Write(ref _inFlight, 0);
                    return;
                }
                _pendingSample = RunSampleAsync(source, epoch, gpu);
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _inFlight, 0);
            AppLog.Warn("Leistungsanzeige: Messung konnte nicht gestartet werden.", ex);
        }
    }

    private async Task RunSampleAsync(ISystemMetricsSource source, int epoch, GpuSelection? gpu)
    {
        Task<SystemMetricsSnapshot>? work = null;
        SystemMetricsSnapshot? result = null;
        try
        {
            work = Task.Run(() => source.Sample(gpu));
            result = await work.WaitAsync(SampleTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Hängender Lauf: Ergebnis verworfen, der Merker bleibt bis zu seinem wirklichen Ende gesetzt (kein zweiter Lauf).
            AppLog.Warn("Leistungsanzeige: Messung der Systemwerte überschreitet die Frist.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Leistungsanzeige: Messung der Systemwerte fehlgeschlagen.", ex);
        }

        if (work is null || work.IsCompleted)
            Volatile.Write(ref _inFlight, 0);
        else
            _ = work.ContinueWith(static (t, state) =>
            {
                _ = t.Exception; // beobachten
                Volatile.Write(ref ((PerformanceOverlayViewModel)state!)._inFlight, 0);
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        lock (_gate)
        {
            // Ergebnis eines früheren Einblendens oder eines Laufs nach dem Ausblenden gehört niemandem mehr.
            if (epoch != _epoch || !_visible)
                return;
            _snapshot = result;
            if (result is not null)
                _goodSamples++;
        }
    }

    // ---------------------------------------------------------------- Zeilen

    private void Rebuild()
    {
        PerformanceOverlayData data;
        lock (_gate)
        {
            if (!_visible)
                return;
            data = BuildData();
        }
        SetRows(PerformanceOverlayRows.Build(Config, data));
    }

    // Unter _gate.
    private PerformanceOverlayData BuildData()
    {
        var funStatus = _fun.Status;
        var funLive = _fun.IsActive && funStatus is not null && IsFresh(Volatile.Read(ref _funStamp));
        var peaceStatus = _peace.Status;
        var peaceLive = _peace.Mode != PeaceMode.Off && IsFresh(Volatile.Read(ref _peaceStamp));
        var plan = _acceleration?.State.Plan ?? [];

        return new PerformanceOverlayData(
            _snapshot,
            _goodSamples >= 2,
            _cameraFps,
            _displayFps,
            funLive && funStatus!.AnalysesPerSecond > 0 ? funStatus.AnalysesPerSecond : null,
            funLive && funStatus!.AnalysesPerSecond > 0 && funStatus.MeanCycleMs > 0 ? funStatus.MeanCycleMs : null,
            peaceLive && peaceStatus.AnalysesPerSecond > 0 ? peaceStatus.AnalysesPerSecond : null,
            peaceLive && peaceStatus.AnalysesPerSecond > 0 && peaceStatus.MeanCycleMs > 0 ? peaceStatus.MeanCycleMs : null,
            plan,
            _fun.IsActive && funStatus?.Active == DetectionKind.Face);
    }

    // Ersetzt die Zeilen nur bei einer Änderung (ein UI-Update je Takt, kein Neuzeichnen ohne neue Werte).
    private void SetRows(IReadOnlyList<PerformanceOverlayRow> rows)
    {
        var old = _rows;
        if (ReferenceEquals(old, rows) || old.SequenceEqual(rows))
            return;
        _rows = rows;
        OnPropertyChanged(nameof(Rows));
    }
}
