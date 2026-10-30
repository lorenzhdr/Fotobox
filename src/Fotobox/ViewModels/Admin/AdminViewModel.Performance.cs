using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;
using Fotobox.Services.Detection.Acceleration;

namespace Fotobox.ViewModels.Admin;

/// <summary>Eintrag der Grafikkartenauswahl; <c>Key == null</c> bedeutet „leistungsstärkste Grafikkarte“.</summary>
public sealed record GpuOption(string? Key, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Eine Zeile des Betriebsstands: Funktion, Gerät und ggf. der Grund eines Rückfalls auf die CPU.</summary>
public sealed record AccelerationStatusLine(string Text, bool IsFallback, string? Detail)
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>Eine Zelle der Messtabelle (ein Modell auf einem Gerät).</summary>
public sealed record BenchmarkCell(string Text, bool IsFastest, bool IsError, string? ToolTip);

/// <summary>Eine Zeile der Messtabelle: ein Modell, eine Zelle je Gerät (Reihenfolge wie die Spaltenköpfe).</summary>
public sealed record BenchmarkRow(string Model, IReadOnlyList<BenchmarkCell> Cells);

/// <summary>Ein Ausführungsanbieter des Windows-ML-Katalogs mit Status.</summary>
public sealed record ProviderRow(string Name, string Status, bool IsReady);

// Tab „Leistung“ – Hardwarebeschleunigung der KI-Erkennung (Modus, Status, Messung, Zusatz-Beschleuniger, Grafikeinstellung)
public sealed partial class AdminViewModel
{
    /// <summary>Länger darf die Abfrage der Anbieter des Katalogs nicht dauern (WinRT-Aufruf, nur auf dem Threadpool).</summary>
    internal static readonly TimeSpan ProviderStatesTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Hinweis, dass Änderungen am Beschleuniger erst nach einem Neustart gelten.</summary>
    internal const string PerformanceRestartText = "Änderungen wirken nach einem Neustart der Fotobox.";
    internal const string PerformanceRestartRecommendedText = "Neue Messwerte liegen vor – sie wirken nach einem Neustart der Fotobox.";
    internal const string PerformanceInstalledRestartText = "Neue Beschleuniger wurden installiert – sie werden nach einem Neustart der Fotobox genutzt.";
    internal const string AutoGpuOptionText = "Leistungsstärkste Grafikkarte";
    internal const string MissingGpuOptionText = "Nicht gefunden: gespeicherte Karte";
    internal const string UnknownGpuOptionText = "Gespeicherte Grafikkarte";

    private IAccelerationService? _acceleration;
    private AccelerationState _accel = AccelerationState.Initial;
    private GpuOption? _selectedGpuOption;
    // Beim Öffnen bzw. nach „Standardwerte“/Import gespeicherte Karte: bleibt in der Liste (auch nach dem Wechsel auf eine andere Karte), damit
    // man zur gespeicherten Wahl zurückkehren kann; wie _missingMonitorName.
    private string? _missingGpuKey;
    private AsyncRelayCommand? _startBenchmarkCommand;
    private RelayCommand? _cancelBenchmarkCommand;
    private AsyncRelayCommand? _installProvidersCommand;
    private RelayCommand? _cancelInstallCommand;

    private CancellationTokenSource? _benchmarkCts;
    private bool _benchmarkRunning;
    private double _benchmarkPercent;
    private string _benchmarkProgressText = string.Empty;
    private string _benchmarkStatusText = string.Empty;
    private BenchmarkResult? _ownBenchmark;
    private BenchmarkResult? _shownBenchmark;
    private string _benchmarkTimeText = string.Empty;

    private CancellationTokenSource? _installCts;
    private bool _installRunning;
    private bool _installNeedsRestart;
    private double _installPercent;
    private string _installStatusText = string.Empty;
    private string _providerStatusText = string.Empty;

    // Wird im Konstruktor aufgerufen. Ohne Dienst (Tests) bleibt der Tab bedienbar (Auswahl im Entwurf), zeigt aber „nicht verfügbar“.
    private void InitPerformance(IAccelerationService? acceleration)
    {
        _acceleration = acceleration;
        _missingGpuKey = _draft.Performance.GpuDeviceKey;
        if (acceleration is not null)
        {
            _accel = acceleration.State;
            // Das Ereignis kommt von beliebigen Threads (auch aus dem Run-Lock eines Detektors): der Handler postet nur.
            acceleration.StateChanged += OnAccelerationStateChanged;
        }
        RebuildGpuOptions();
        RebuildStatusLines();
        RebuildBenchmarkTable();
    }

    private void DisposePerformance()
    {
        if (_acceleration is { } acceleration)
            acceleration.StateChanged -= OnAccelerationStateChanged;
        // Messung und Installation brauchen keine Oberfläche mehr: abbrechen (Dienst wirft nie, blockiert nie).
        CancelQuietly(_benchmarkCts);
        CancelQuietly(_installCts);
    }

    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ---------------------------------------------------------------- Threading

    // Ereignisse und Fortschritt kommen von Hintergrund-Threads: nur auf den UI-Kontext posten, nie darauf warten.
    // Ohne UI-Kontext (Tests) läuft die Aktion direkt.
    private void PostPerformance(Action action)
    {
        void Run()
        {
            if (_disposed)
                return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Der Tab „Leistung“ konnte nicht aktualisiert werden.", ex);
            }
        }

        if (_sync is null)
            Run();
        else
            _sync.Post(_ => Run(), null);
    }

    private void OnAccelerationStateChanged(object? sender, EventArgs e) => PostPerformance(RefreshAcceleration);

    private sealed class UiProgress<T>(AdminViewModel owner, Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => owner.PostPerformance(() => handler(value));
    }

    // ---------------------------------------------------------------- Hardwarebeschleunigung: Entwurfswerte

    /// <summary>Es gibt einen Beschleunigungsdienst (sonst zeigt der Tab „nicht verfügbar“).</summary>
    public bool PerformanceServiceAvailable => _acceleration is not null;

    public bool PerformanceServiceUnavailable => _acceleration is null;

    public bool IsAcceleratorAuto
    {
        get => _draft.Performance.Accelerator == AcceleratorMode.Auto;
        set => SetAcceleratorMode(AcceleratorMode.Auto, value);
    }

    public bool IsAcceleratorCpu
    {
        get => _draft.Performance.Accelerator == AcceleratorMode.Cpu;
        set => SetAcceleratorMode(AcceleratorMode.Cpu, value);
    }

    public bool IsAcceleratorGpu
    {
        get => _draft.Performance.Accelerator == AcceleratorMode.Gpu;
        set => SetAcceleratorMode(AcceleratorMode.Gpu, value);
    }

    public bool IsAcceleratorNpu
    {
        get => _draft.Performance.Accelerator == AcceleratorMode.Npu;
        set => SetAcceleratorMode(AcceleratorMode.Npu, value);
    }

    private void SetAcceleratorMode(AcceleratorMode mode, bool selected)
    {
        if (!selected || _draft.Performance.Accelerator == mode)
            return;
        _draft.Performance.Accelerator = mode;
        RaiseAcceleratorSelection();
    }

    private void RaiseAcceleratorSelection()
    {
        OnPropertyChanged(nameof(IsAcceleratorAuto));
        OnPropertyChanged(nameof(IsAcceleratorCpu));
        OnPropertyChanged(nameof(IsAcceleratorGpu));
        OnPropertyChanged(nameof(IsAcceleratorNpu));
        OnPropertyChanged(nameof(IsGpuListEnabled));
        OnPropertyChanged(nameof(IsNpuSelectable));
        OnPropertyChanged(nameof(PerformanceRestartHint));
        OnPropertyChanged(nameof(HasPerformanceRestartHint));
    }

    /// <summary>Die Grafikkartenliste ist nur bei der Wahl „Grafikkarte (GPU)“ bedienbar.</summary>
    public bool IsGpuListEnabled => IsAcceleratorGpu;

    /// <summary>Die Devices (Geräteliste) sind bekannt, sobald der Plan fertig ist (vorher steht nur die CPU darin).</summary>
    private bool DevicesKnown => _acceleration is not null && _accel.PlanReady;

    private bool HasNpu => _accel.Devices.Any(d => d.Kind == AcceleratorKind.Npu);

    /// <summary>
    /// Die Wahl „KI-Beschleuniger (NPU)“ ist bedienbar: Bis die Geräteliste da ist und solange eine NPU vorhanden ist; ist der Entwurf
    /// schon auf NPU gestellt, bleibt die Wahl sichtbar bedienbar (gespeicherte Wahl nicht stillschweigend ändern).
    /// </summary>
    public bool IsNpuSelectable => !DevicesKnown || HasNpu || IsAcceleratorNpu;

    /// <summary>Erklärung unter der NPU-Wahl, wenn keine NPU gefunden wurde.</summary>
    public string? NpuHintText => DevicesKnown && !HasNpu
        ? "Es wurde kein KI-Beschleuniger (NPU) gefunden. Unter „Zusätzliche Beschleuniger“ können Sie herstellerspezifische Beschleuniger installieren; danach erscheint eine vorhandene NPU hier."
        : null;

    public bool HasNpuHint => NpuHintText is not null;

    // ---- Grafikkartenauswahl

    public ObservableCollection<GpuOption> GpuOptions { get; } = [];

    public GpuOption? SelectedGpuOption
    {
        get => _selectedGpuOption;
        set
        {
            // ItemsSource-Wechsel meldet null zurück: nie in den Entwurf schreiben (die gespeicherte Wahl bleibt erhalten).
            if (value is null || value == _selectedGpuOption)
                return;
            _selectedGpuOption = value;
            OnPropertyChanged();
            if (IsLoading)
                return;
            _draft.Performance.GpuDeviceKey = value.Key;
            OnPropertyChanged(nameof(PerformanceRestartHint));
            OnPropertyChanged(nameof(HasPerformanceRestartHint));
        }
    }

    /// <summary>Hinweis unter der Grafikkartenauswahl (null = keiner).</summary>
    public string? GpuHintText
    {
        get
        {
            if (!DevicesKnown)
                return null;
            if (_accel.Devices.All(d => d.Kind != AcceleratorKind.Gpu))
                return "Es wurde keine Grafikkarte gefunden – die Erkennung läuft auf dem Prozessor.";
            if (_draft.Performance.GpuDeviceKey is { } key && _accel.Devices.All(d => d.Kind != AcceleratorKind.Gpu || d.Key != key))
                return "Die gespeicherte Grafikkarte ist gerade nicht vorhanden. Beim nächsten Start läuft die Erkennung auf dem Prozessor, bis Sie eine andere Karte wählen. Die Wahl bleibt gespeichert.";
            return null;
        }
    }

    public bool HasGpuHint => GpuHintText is not null;

    private void RebuildGpuOptions()
    {
        var gpus = AcceleratorSelector.RankGpus(_accel.Devices.Where(d => d.Kind == AcceleratorKind.Gpu));
        var options = new List<GpuOption> { new(null, AutoGpuOptionText) };
        options.AddRange(gpus.Select(g => new GpuOption(g.Key, AccelerationDisplay.GpuLabel(g))));

        var savedKey = _draft.Performance.GpuDeviceKey;
        // Die beim Öffnen gespeicherte Karte bleibt auch dann in der Liste, wenn inzwischen eine andere gewählt ist.
        foreach (var key in new[] { _missingGpuKey, savedKey }.Distinct(StringComparer.Ordinal))
        {
            if (key is not null && options.All(o => !string.Equals(o.Key, key, StringComparison.Ordinal)))
                options.Add(new GpuOption(key, DevicesKnown ? MissingGpuOptionText : UnknownGpuOptionText));
        }

        var selected = options.First(o => string.Equals(o.Key, savedKey, StringComparison.Ordinal));
        if (GpuOptions.SequenceEqual(options))
        {
            // Unverändert: Liste (und ein geöffnetes Aufklappfeld) in Ruhe lassen.
            if (!Equals(_selectedGpuOption, selected))
            {
                _selectedGpuOption = GpuOptions.First(o => o.Key == selected.Key);
                OnPropertyChanged(nameof(SelectedGpuOption));
            }
            return;
        }

        BeginLoading();
        try
        {
            GpuOptions.Clear();
            foreach (var option in options)
                GpuOptions.Add(option);
            _selectedGpuOption = selected;
            OnPropertyChanged(nameof(SelectedGpuOption));
        }
        finally
        {
            EndLoading();
        }
    }

    // ---------------------------------------------------------------- Betriebsstand

    public ObservableCollection<AccelerationStatusLine> AccelerationStatusLines { get; } = [];

    public bool HasAccelerationStatusLines => AccelerationStatusLines.Count > 0;

    /// <summary>Text anstelle der Statuszeilen: „Wird ermittelt …“ bzw. „nicht verfügbar“; null, sobald der Plan steht.</summary>
    public string? AccelerationPendingText =>
        _acceleration is null
            ? "Die Beschleunigung steht in dieser Ansicht nicht zur Verfügung."
            : _accel.PlanReady ? null : "Wird ermittelt …";

    public bool HasAccelerationPendingText => AccelerationPendingText is not null;

    /// <summary>Hinweis des Dienstes zum Plan (z. B. Gerät fehlt, Rückfall auf die CPU).</summary>
    public string? AccelerationNotice => _accel.PlanReady && !string.IsNullOrWhiteSpace(_accel.Notice) ? _accel.Notice : null;

    public bool HasAccelerationNotice => AccelerationNotice is not null;

    /// <summary>Woher die Zuordnung stammt (Messung, Hintergrundmessung läuft …); null = nichts zu sagen.</summary>
    public string? AccelerationSourceText
    {
        get
        {
            if (!_accel.PlanReady)
                return null;
            // Grafikkarte/KI-Einheit werden erst genutzt, wenn eine Messung sie als brauchbar ausgewiesen hat (der Dienst nennt das im Hinweis).
            if (_accel.Mode is AcceleratorMode.Gpu or AcceleratorMode.Npu)
                return _accel.BenchmarkRunning && _accel.BenchmarkIsBackground
                    ? "Das gewählte Gerät wird im Hintergrund geprüft. Bis dahin läuft die Erkennung auf dem Prozessor."
                    : null;
            if (_accel.Mode != AcceleratorMode.Auto)
                return null;
            if (_accel.PlanFromCache && _accel.LastBenchmark is { } cached)
                return $"Die Zuordnung beruht auf der letzten Messung ({AccelerationDisplay.FormatTime(cached.MeasuredAt)}).";
            if (_accel.BenchmarkRunning && _accel.BenchmarkIsBackground)
                return "Noch nicht gemessen – die Messung läuft im Hintergrund. Bis dahin läuft die Erkennung auf dem Prozessor.";
            if (_accel.LastBenchmark is { } last)
                return $"Letzte Messung: {AccelerationDisplay.FormatTime(last.MeasuredAt)}.";
            if (_accel.Devices.All(d => d.IsCpu))
                return null;
            return "Noch nicht gemessen – die Messung startet im Hintergrund. Bis dahin läuft die Erkennung auf dem Prozessor.";
        }
    }

    public bool HasAccelerationSourceText => AccelerationSourceText is not null;

    private void RebuildStatusLines()
    {
        var lines = new List<AccelerationStatusLine>();
        if (_accel.PlanReady)
        {
            foreach (var placement in _accel.Plan)
            {
                var name = AccelerationDisplay.ModelName(placement.Model);
                if (placement.FellBack)
                {
                    var fallback = _accel.Fallbacks.LastOrDefault(f => f.Model == placement.Model);
                    var detail = fallback is null
                        ? "Rückfall auf den Prozessor."
                        : $"Rückfall von „{fallback.DeviceName}“ auf den Prozessor ({(fallback.AtRuntime ? "während des Betriebs" : "beim Laden")}): {fallback.Reason}";
                    lines.Add(new AccelerationStatusLine($"{name}: {AccelerationDisplay.PlacementText(AcceleratorKind.Cpu, string.Empty)}", true, detail));
                }
                else
                {
                    lines.Add(new AccelerationStatusLine($"{name}: {AccelerationDisplay.PlacementText(placement.Kind, placement.DeviceName)}", false, null));
                }
            }
        }

        if (!AccelerationStatusLines.SequenceEqual(lines))
        {
            AccelerationStatusLines.Clear();
            foreach (var line in lines)
                AccelerationStatusLines.Add(line);
        }
    }

    // ---------------------------------------------------------------- Neustart-Hinweis

    /// <summary>
    /// „Wirkt nach einem Neustart“: sobald der Entwurf vom laufenden Stand abweicht (Modus, Grafikkarte), der Dienst eine nötige
    /// Änderung meldet (gespeicherte Änderung, Installation) oder neue Messwerte (Auto, Grafikkarte, KI-Einheit) einen anderen Plan ergäben.
    /// Vor dem fertigen Plan zählt der gespeicherte Stand (der Dienst kennt den laufenden Modus noch nicht).
    /// </summary>
    public string? PerformanceRestartHint
    {
        get
        {
            var draft = _draft.Performance;
            var saved = _store.Current.Performance;
            var differs = _accel.RestartRequired
                          || draft.Accelerator != saved.Accelerator
                          || (_accel.PlanReady && draft.Accelerator != _accel.Mode)
                          || (draft.Accelerator == AcceleratorMode.Gpu
                              && !string.Equals(draft.GpuDeviceKey, saved.GpuDeviceKey, StringComparison.Ordinal));
            if (differs)
            {
                // Nur die Installation steht aus: eigener Text, damit klar ist, warum ein Neustart nötig ist.
                var onlyInstall = _installNeedsRestart && draft.Accelerator == saved.Accelerator
                                  && (!_accel.PlanReady || draft.Accelerator == _accel.Mode)
                                  && (draft.Accelerator != AcceleratorMode.Gpu || string.Equals(draft.GpuDeviceKey, saved.GpuDeviceKey, StringComparison.Ordinal));
                return onlyInstall ? PerformanceInstalledRestartText : PerformanceRestartText;
            }
            if (_installNeedsRestart)
                return PerformanceInstalledRestartText;
            if (_accel.Mode != AcceleratorMode.Cpu && draft.Accelerator == _accel.Mode && _accel.RestartRecommended)
                return PerformanceRestartRecommendedText;
            return null;
        }
    }

    public bool HasPerformanceRestartHint => PerformanceRestartHint is not null;

    // ---------------------------------------------------------------- Messung

    public bool IsBenchmarkRunning => _benchmarkRunning;

    public bool IsBenchmarkIdle => !_benchmarkRunning;

    /// <summary>Fortschritt der Messung von 0 bis 100.</summary>
    public double BenchmarkPercent
    {
        get => _benchmarkPercent;
        private set => SetProperty(ref _benchmarkPercent, value);
    }

    public string BenchmarkProgressText
    {
        get => _benchmarkProgressText;
        private set => SetProperty(ref _benchmarkProgressText, value);
    }

    /// <summary>Ergebnis der letzten vom Admin gestarteten Messung („abgeschlossen“, „abgebrochen“ …).</summary>
    public string BenchmarkStatusText
    {
        get => _benchmarkStatusText;
        private set
        {
            if (SetProperty(ref _benchmarkStatusText, value))
                OnPropertyChanged(nameof(HasBenchmarkStatusText));
        }
    }

    public bool HasBenchmarkStatusText => _benchmarkStatusText.Length > 0;

    /// <summary>Hinweis, dass eine Hintergrundmessung läuft (die „Jetzt messen“ ersetzt).</summary>
    public string? BenchmarkBackgroundText =>
        !_benchmarkRunning && _accel.BenchmarkRunning && _accel.BenchmarkIsBackground
            ? "Im Hintergrund läuft gerade eine Messung. „Jetzt messen“ ersetzt sie."
            : null;

    public bool HasBenchmarkBackgroundText => BenchmarkBackgroundText is not null;

    public ObservableCollection<string> BenchmarkColumns { get; } = [];

    public ObservableCollection<BenchmarkRow> BenchmarkRows { get; } = [];

    public bool HasBenchmarkTable => BenchmarkRows.Count > 0;

    public bool HasNoBenchmarkTable => BenchmarkRows.Count == 0;

    /// <summary>„Gemessen am …“ zur angezeigten Tabelle.</summary>
    public string BenchmarkTimeText
    {
        get => _benchmarkTimeText;
        private set => SetProperty(ref _benchmarkTimeText, value);
    }

    /// <summary>Misst alle Geräte mit allen Modellen („Jetzt messen“); Fortschritt und Abbruch siehe <see cref="CancelBenchmarkCommand"/>.</summary>
    public IAsyncRelayCommand StartBenchmarkCommand =>
        _startBenchmarkCommand ??= new AsyncRelayCommand(StartBenchmarkAsync, CanStartBenchmark);

    public IRelayCommand CancelBenchmarkCommand =>
        _cancelBenchmarkCommand ??= new RelayCommand(CancelBenchmark, () => _benchmarkRunning);

    private bool CanStartBenchmark() => _acceleration is not null && !_disposed && !_benchmarkRunning && !_installRunning;

    private void CancelBenchmark()
    {
        if (!_benchmarkRunning)
            return;
        BenchmarkProgressText = "Messung wird abgebrochen …";
        CancelQuietly(_benchmarkCts);
    }

    private async Task StartBenchmarkAsync()
    {
        if (_acceleration is not { } acceleration || !CanStartBenchmark())
            return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _benchmarkCts = cts;
        _benchmarkRunning = true;
        BenchmarkPercent = 0;
        BenchmarkProgressText = "Messung wird vorbereitet …";
        BenchmarkStatusText = string.Empty;
        RaiseBenchmarkState();

        BenchmarkResult? result = null;
        string? failure = null;
        try
        {
            var progress = new UiProgress<BenchmarkProgress>(this, OnBenchmarkProgress);
            result = await acceleration.RunBenchmarkAsync(progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Wie ein Abbruch behandeln.
        }
        catch (Exception ex)
        {
            AppLog.Warn("Die Messung der Beschleunigung ist fehlgeschlagen.", ex);
            failure = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_benchmarkCts, cts))
                _benchmarkCts = null;
            cts.Dispose();
        }

        if (_disposed)
            return;

        _benchmarkRunning = false;
        if (failure is not null)
        {
            BenchmarkStatusText = "Die Messung ist fehlgeschlagen: " + failure;
        }
        else if (result is null || result.Cancelled)
        {
            BenchmarkStatusText = "Die Messung wurde abgebrochen.";
        }
        else if (result.Entries.Count == 0)
        {
            BenchmarkStatusText = "Die Messung hat kein Ergebnis geliefert.";
        }
        else
        {
            _ownBenchmark = result;
            BenchmarkStatusText = "Die Messung ist abgeschlossen.";
        }
        RefreshAcceleration();
        RaiseBenchmarkState();
    }

    private void OnBenchmarkProgress(BenchmarkProgress progress)
    {
        if (!_benchmarkRunning)
            return;
        var total = Math.Max(progress.Total, 1);
        var done = Math.Clamp(progress.Done, 0, total);
        BenchmarkPercent = 100.0 * done / total;
        var what = progress.Model is { } model
            ? $"{progress.DeviceName} – {AccelerationDisplay.ModelName(model)}"
            : progress.DeviceName;
        BenchmarkProgressText = string.IsNullOrEmpty(what) ? $"Messe … ({done}/{total})" : $"Messe {what} ({done}/{total})";
    }

    private void RaiseBenchmarkState()
    {
        OnPropertyChanged(nameof(IsBenchmarkRunning));
        OnPropertyChanged(nameof(IsBenchmarkIdle));
        OnPropertyChanged(nameof(BenchmarkBackgroundText));
        OnPropertyChanged(nameof(HasBenchmarkBackgroundText));
        NotifyPerformanceCommands();
    }

    private BenchmarkResult? PickBenchmark()
    {
        var service = _accel.LastBenchmark;
        var own = _ownBenchmark;
        if (own is null)
            return service;
        if (service is null)
            return own;
        return own.MeasuredAt > service.MeasuredAt ? own : service;
    }

    private void RebuildBenchmarkTable()
    {
        var result = PickBenchmark();
        if (ReferenceEquals(result, _shownBenchmark))
            return;
        _shownBenchmark = result;

        var columns = new List<string>();
        var rows = new List<BenchmarkRow>();
        if (result is { Entries.Count: > 0 })
        {
            var keys = result.Entries.Select(e => e.DeviceKey).Distinct(StringComparer.Ordinal).ToList();
            var names = keys.Select(k => result.Entries.First(e => e.DeviceKey == k).DeviceName).ToList();
            for (var i = 0; i < keys.Count; i++)
            {
                // Gleicher Name, mehrere Anbieter (z. B. DirectML und ein Hersteller-Anbieter): Anbieter zur Unterscheidung nennen.
                var name = names[i];
                if (names.Count(n => n == name) > 1 && _accel.Devices.FirstOrDefault(d => d.Key == keys[i]) is { } device)
                    name += $" ({AccelerationDisplay.ProviderLabel(device.EpName)})";
                columns.Add(name);
            }

            foreach (var model in result.Entries.Select(e => e.Model).Distinct().OrderBy(m => (int)m))
            {
                var entries = keys.Select(k => result.Entries.FirstOrDefault(e => e.Model == model && e.DeviceKey == k)).ToList();
                var succeeded = entries.Where(e => e is { Succeeded: true }).ToList();
                // Hervorheben nur, wenn es etwas zu vergleichen gibt.
                var fastest = succeeded.Count > 1 ? succeeded.Min(e => e!.AverageMs) : (double?)null;
                var cells = entries.Select(e => e switch
                {
                    null => new BenchmarkCell("–", false, false, "Nicht gemessen."),
                    { Succeeded: false } => new BenchmarkCell("–", false, true, e.Error),
                    _ => new BenchmarkCell(AccelerationDisplay.FormatMs(e.AverageMs), fastest is { } best && e.AverageMs <= best,
                        false, $"Laden: {e.LoadMs.ToString("0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} ms"),
                }).ToList();
                rows.Add(new BenchmarkRow(AccelerationDisplay.ModelName(model), cells));
            }
        }

        BenchmarkColumns.Clear();
        foreach (var column in columns)
            BenchmarkColumns.Add(column);
        BenchmarkRows.Clear();
        foreach (var row in rows)
            BenchmarkRows.Add(row);
        BenchmarkTimeText = result is { Entries.Count: > 0 } ? $"Gemessen am {AccelerationDisplay.FormatTime(result.MeasuredAt)}" : string.Empty;
        OnPropertyChanged(nameof(HasBenchmarkTable));
        OnPropertyChanged(nameof(HasNoBenchmarkTable));
    }

    // ---------------------------------------------------------------- Zusätzliche Beschleuniger

    public ObservableCollection<ProviderRow> ProviderRows { get; } = [];

    /// <summary>Zustand der Anbieterliste: „Status wird abgefragt …“, „Status unbekannt“ oder Hinweis auf eine leere Liste; sonst leer.</summary>
    public string ProviderStatusText
    {
        get => _providerStatusText;
        private set
        {
            if (SetProperty(ref _providerStatusText, value))
                OnPropertyChanged(nameof(HasProviderStatusText));
        }
    }

    public bool HasProviderStatusText => _providerStatusText.Length > 0;

    public bool IsInstalling => _installRunning;

    public bool IsNotInstalling => !_installRunning;

    /// <summary>Fortschritt der Installation von 0 bis 100.</summary>
    public double InstallPercent
    {
        get => _installPercent;
        private set => SetProperty(ref _installPercent, value);
    }

    public string InstallStatusText
    {
        get => _installStatusText;
        private set
        {
            if (SetProperty(ref _installStatusText, value))
                OnPropertyChanged(nameof(HasInstallStatusText));
        }
    }

    public bool HasInstallStatusText => _installStatusText.Length > 0;

    /// <summary>Lädt und registriert die Hersteller-Beschleuniger (braucht Internet).</summary>
    public IAsyncRelayCommand InstallProvidersCommand =>
        _installProvidersCommand ??= new AsyncRelayCommand(InstallProvidersAsync, CanInstallProviders);

    public IRelayCommand CancelInstallCommand =>
        _cancelInstallCommand ??= new RelayCommand(CancelInstall, () => _installRunning);

    // Nicht während einer Messung (auch nicht der im Hintergrund): Die Geräteliste ändert sich durch die Installation.
    private bool CanInstallProviders() =>
        _acceleration is not null && !_disposed && !_installRunning && !_benchmarkRunning && !_accel.BenchmarkRunning;

    private void CancelInstall()
    {
        if (!_installRunning)
            return;
        InstallStatusText = "Installation wird abgebrochen …";
        CancelQuietly(_installCts);
    }

    private async Task InstallProvidersAsync()
    {
        if (_acceleration is not { } acceleration || !CanInstallProviders())
            return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _installCts = cts;
        _installRunning = true;
        InstallPercent = 0;
        InstallStatusText = "Beschleuniger werden geladen und installiert … Das kann einige Minuten dauern.";
        RaiseInstallState();

        ProviderInstallResult? result = null;
        string? failure = null;
        try
        {
            var progress = new UiProgress<double>(this, OnInstallProgress);
            result = await acceleration.InstallVendorProvidersAsync(progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Wie ein Abbruch behandeln.
        }
        catch (Exception ex)
        {
            AppLog.Warn("Die Installation der Beschleuniger ist fehlgeschlagen.", ex);
            failure = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_installCts, cts))
                _installCts = null;
            cts.Dispose();
        }

        if (_disposed)
            return;

        _installRunning = false;
        if (failure is not null)
        {
            InstallStatusText = "Die Installation ist fehlgeschlagen: " + failure;
        }
        else if (result is null || result.Cancelled)
        {
            InstallStatusText = "Die Installation wurde abgebrochen.";
        }
        else if (!result.Success)
        {
            InstallStatusText = string.IsNullOrWhiteSpace(result.Message) ? "Die Installation ist fehlgeschlagen." : result.Message;
        }
        else
        {
            InstallPercent = 100;
            InstallStatusText = string.IsNullOrWhiteSpace(result.Message) ? "Die Installation ist abgeschlossen." : result.Message;
            _installNeedsRestart = _installNeedsRestart || result.Providers.Count > 0;
        }
        RefreshAcceleration();
        RaiseInstallState();
        if (result is { Success: true })
            await Guard(LoadProviderStatesAsync(), "Anbieterliste");
    }

    private void OnInstallProgress(double fraction)
    {
        if (!_installRunning)
            return;
        InstallPercent = 100.0 * Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
    }

    private void RaiseInstallState()
    {
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(IsNotInstalling));
        OnPropertyChanged(nameof(PerformanceRestartHint));
        OnPropertyChanged(nameof(HasPerformanceRestartHint));
        NotifyPerformanceCommands();
    }

    /// <summary>Fragt die Anbieter des Katalogs ab (WinRT: nur auf dem Threadpool, mit Frist). Fehler → „Status unbekannt“. Wirft nicht.</summary>
    private async Task LoadProviderStatesAsync()
    {
        if (_disposed)
            return;
        if (_acceleration is not { } acceleration)
        {
            ProviderStatusText = "Der Status ist in dieser Ansicht nicht verfügbar.";
            return;
        }

        ProviderStatusText = "Status wird abgefragt …";
        IReadOnlyList<ProviderState>? states = null;
        try
        {
            states = await Task.Run(acceleration.GetProviderStates).WaitAsync(ProviderStatesTimeout, _time, _lifetime.Token);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                AppLog.Warn("Der Status der Ausführungsanbieter war nicht abrufbar.", ex);
        }

        if (_disposed)
            return;

        ProviderRows.Clear();
        if (states is null)
        {
            ProviderStatusText = "Status unbekannt.";
        }
        else if (states.Count == 0)
        {
            ProviderStatusText = "Für diesen Computer sind keine zusätzlichen Beschleuniger bekannt.";
        }
        else
        {
            foreach (var state in states)
                ProviderRows.Add(new ProviderRow(state.Name, DescribeProvider(state), state.IsReady));
            ProviderStatusText = string.Empty;
        }
    }

    private static string DescribeProvider(ProviderState state) => state.ReadyState switch
    {
        "Ready" => "Installiert",
        "NotReady" => "Noch nicht installiert",
        "NotPresent" => "Nicht vorhanden",
        _ => state.IsReady ? "Installiert" : string.IsNullOrEmpty(state.ReadyState) ? "Unbekannt" : state.ReadyState,
    };

    // ---------------------------------------------------------------- Windows-Grafikeinstellung

    /// <summary>Fotobox in den Windows-Grafikeinstellungen auf „Hohe Leistung“ stellen (wirkt ab dem nächsten Start).</summary>
    public bool PreferHighPerformanceGpu
    {
        get => _draft.Performance.PreferHighPerformanceGpu;
        set => SetProperty(_draft.Performance.PreferHighPerformanceGpu, value, v => _draft.Performance.PreferHighPerformanceGpu = v);
    }

    // ---------------------------------------------------------------- Aktualisierung

    /// <summary>Liest den Dienst neu (UI-Thread) und baut alle abgeleiteten Anzeigen neu auf.</summary>
    private void RefreshAcceleration()
    {
        _accel = _acceleration?.State ?? AccelerationState.Initial;
        RebuildGpuOptions();
        RebuildStatusLines();
        RebuildBenchmarkTable();
        OnPropertyChanged(nameof(HasAccelerationStatusLines));
        OnPropertyChanged(nameof(AccelerationPendingText));
        OnPropertyChanged(nameof(HasAccelerationPendingText));
        OnPropertyChanged(nameof(AccelerationNotice));
        OnPropertyChanged(nameof(HasAccelerationNotice));
        OnPropertyChanged(nameof(AccelerationSourceText));
        OnPropertyChanged(nameof(HasAccelerationSourceText));
        OnPropertyChanged(nameof(IsNpuSelectable));
        OnPropertyChanged(nameof(NpuHintText));
        OnPropertyChanged(nameof(HasNpuHint));
        OnPropertyChanged(nameof(GpuHintText));
        OnPropertyChanged(nameof(HasGpuHint));
        OnPropertyChanged(nameof(PerformanceRestartHint));
        OnPropertyChanged(nameof(HasPerformanceRestartHint));
        OnPropertyChanged(nameof(BenchmarkBackgroundText));
        OnPropertyChanged(nameof(HasBenchmarkBackgroundText));
        NotifyPerformanceCommands();
    }

    /// <summary>Nach „Standardwerte“ und Import: Auswahl und Anzeigen aus dem (ersetzten) Entwurf neu aufbauen.</summary>
    private void RebuildPerformanceFromDraft()
    {
        _accel = _acceleration?.State ?? AccelerationState.Initial;
        _missingGpuKey = _draft.Performance.GpuDeviceKey;
        RebuildGpuOptions();
        RaiseAcceleratorSelection();
        OnPropertyChanged(nameof(GpuHintText));
        OnPropertyChanged(nameof(HasGpuHint));
        OnPropertyChanged(nameof(PreferHighPerformanceGpu));
    }

    // Reiner Anzeigezustand des Tabs (Betriebsstand, Messung, Installation): keine Änderung des Entwurfs, blendet „gespeichert“ nicht aus.
    private static bool IsPerformanceViewStateProperty(string? propertyName) =>
        propertyName is nameof(HasAccelerationStatusLines) or nameof(AccelerationPendingText) or nameof(HasAccelerationPendingText)
            or nameof(AccelerationNotice) or nameof(HasAccelerationNotice) or nameof(AccelerationSourceText) or nameof(HasAccelerationSourceText)
            or nameof(IsNpuSelectable) or nameof(NpuHintText) or nameof(HasNpuHint) or nameof(GpuHintText) or nameof(HasGpuHint)
            or nameof(PerformanceRestartHint) or nameof(HasPerformanceRestartHint)
            or nameof(BenchmarkBackgroundText) or nameof(HasBenchmarkBackgroundText) or nameof(IsBenchmarkRunning) or nameof(IsBenchmarkIdle)
            or nameof(BenchmarkPercent) or nameof(BenchmarkProgressText) or nameof(BenchmarkStatusText) or nameof(HasBenchmarkStatusText)
            or nameof(HasBenchmarkTable) or nameof(HasNoBenchmarkTable) or nameof(BenchmarkTimeText)
            or nameof(ProviderStatusText) or nameof(HasProviderStatusText) or nameof(IsInstalling) or nameof(IsNotInstalling)
            or nameof(InstallPercent) or nameof(InstallStatusText) or nameof(HasInstallStatusText);

    private void NotifyPerformanceCommands()
    {
        _startBenchmarkCommand?.NotifyCanExecuteChanged();
        _cancelBenchmarkCommand?.NotifyCanExecuteChanged();
        _installProvidersCommand?.NotifyCanExecuteChanged();
        _cancelInstallCommand?.NotifyCanExecuteChanged();
    }
}
