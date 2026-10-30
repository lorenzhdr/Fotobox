using System.IO;
using System.Reflection;
using System.Text;
using Fotobox.Core;
using Microsoft.ML.OnnxRuntime;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>
/// Beschleunigung der KI-Erkennung (Windows ML): entscheidet beim Start, auf welchem Gerät welches Modell läuft, und bedient die
/// Admin-Oberfläche (Status, Geräteliste, Messung, Installation). Alle Ereignisse und Fortschrittsmeldungen können von Hintergrund-Threads
/// kommen; die Oberfläche muss selbst auf den UI-Thread wechseln. Keine Umschaltung zur Laufzeit: Einstellungen wirken nach einem Neustart.
/// </summary>
public interface IAccelerationService : IModelPlacement, IAsyncDisposable
{
    /// <summary>Aktuelle Momentaufnahme (nie null, thread-sicher).</summary>
    AccelerationState State { get; }

    /// <summary>Der Zustand hat sich geändert (Plan, Rückfall, Messung, Geräteliste). Beliebiger Thread.</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Startet einmalig im Hintergrund (Threadpool): Katalog, Geräteliste, Plan; im Modus Auto ohne passenden Cache zusätzlich die
    /// Hintergrundmessung. Mehrfache Aufrufe liefern dieselbe Aufgabe. Wirft nie; blockiert den Aufrufer nicht.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Misst alle Geräte mit allen Modellen (Frist je Gerät 20 s, gesamt 60 s) und aktualisiert den Cache des Auto-Modus. Läuft gerade die
    /// Hintergrundmessung, wird sie abgebrochen und ersetzt; läuft schon eine Messung der Oberfläche, bekommt der Aufrufer deren Ergebnis.
    /// Wirft nie; Abbruch liefert <see cref="BenchmarkResult.Cancelled"/>.
    /// </summary>
    Task<BenchmarkResult> RunBenchmarkAsync(IProgress<BenchmarkProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Installiert die Hersteller-Ausführungsanbieter (Internet nötig). Danach ist ein Neustart nötig (<see cref="AccelerationState.RestartRequired"/>).</summary>
    Task<ProviderInstallResult> InstallVendorProvidersAsync(IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Zustand der Ausführungsanbieter des Windows-ML-Katalogs (WinRT-Aufruf: nur auf dem Threadpool aufrufen).</summary>
    IReadOnlyList<ProviderState> GetProviderStates();
}

/// <summary>Anpassbare Umgebung des <see cref="AccelerationService"/> (Tests). Nicht gesetzte Werte nehmen den Standard.</summary>
public sealed class AccelerationOptions
{
    /// <summary>Pfad der Cache-Datei; null = <c>beschleunigung.json</c> im Konfigurationsordner der App.</summary>
    public string? CacheFilePath { get; init; }

    /// <summary>Wartezeit vor der Hintergrundmessung (Standard <see cref="AccelerationTimeouts.BackgroundBenchmarkDelay"/>).</summary>
    public TimeSpan? BackgroundBenchmarkDelay { get; init; }

    /// <summary>Programmversion für den Fingerabdruck; null = Version der Anwendung.</summary>
    public string? AppVersion { get; init; }

    /// <summary>ONNX-Runtime-Version für den Fingerabdruck; null = <c>OrtEnv.Instance().GetVersionString()</c>.</summary>
    public Func<string>? OrtVersion { get; init; }

    /// <summary>Wartezeit des Detektors auf den Plan (Standard <see cref="AccelerationTimeouts.PlanWait"/>).</summary>
    public TimeSpan? PlanWait { get; init; }
}

/// <inheritdoc cref="IAccelerationService" />
public sealed class AccelerationService : IAccelerationService
{
    private const int MaxFallbacks = 50;

    private readonly ISettingsStore _settings;
    private readonly IAcceleratorCatalog _catalog;
    private readonly IAcceleratorBenchmark _benchmark;
    private readonly TimeProvider _time;
    private readonly AccelerationOptions _options;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _startLock = new();
    private readonly object _stateLock = new();
    private readonly object _benchLock = new();
    private readonly TaskCompletionSource _planReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<ModelId> _lateModels = [];

    private Task? _startTask;
    private volatile bool _startRequested;
    private AccelerationState _state = AccelerationState.Initial;
    private IReadOnlyDictionary<ModelId, AcceleratorDevice?>? _plan;
    private AcceleratorMode _startedMode;
    private string? _startedGpuKey;
    private bool _installedProviders;

    private Task<BenchmarkResult>? _benchTask;
    private CancellationTokenSource? _benchCts;
    private bool _benchBackground;
    private int _disposed;

    public AccelerationService(ISettingsStore settings, IAcceleratorCatalog catalog, IAcceleratorBenchmark benchmark, TimeProvider time, AccelerationOptions? options = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _benchmark = benchmark ?? throw new ArgumentNullException(nameof(benchmark));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? new AccelerationOptions();
    }

    public event EventHandler? StateChanged;

    public AccelerationState State
    {
        get { lock (_stateLock) return _state; }
    }

    // ------------------------------------------------------------------ Start und Plan

    public Task StartAsync()
    {
        lock (_startLock)
        {
            _startRequested = true;
            return _startTask ??= Task.Run(StartCoreAsync);
        }
    }

    private async Task StartCoreAsync()
    {
        IReadOnlyList<AcceleratorDevice> devices = [AcceleratorDevice.CpuDevice];
        try
        {
            var performance = _settings.Current.Performance ?? new PerformanceSettings();
            _startedMode = performance.Accelerator;
            _startedGpuKey = performance.GpuDeviceKey;
            _settings.SettingsChanged += OnSettingsChanged;
            AppLog.Info($"Beschleunigung: Modus {_startedMode}" + (_startedMode == AcceleratorMode.Gpu ? (_startedGpuKey is null ? ", leistungsstärkste Grafikkarte" : ", gewählte Grafikkarte") : "") + ".");

            devices = await _catalog.DiscoverAsync(_disposeCts.Token).ConfigureAwait(false);
            LogDevices(devices);

            var providers = SafeProviders();
            if (providers.Count > 0)
                AppLog.Info("Beschleunigung: Ausführungsanbieter im Katalog: " + string.Join(", ", providers.Select(p => $"{p.Name} ({p.ReadyState})")));
            var fingerprint = CurrentFingerprint(devices, providers);
            BenchmarkResult? cached = null;
            if (_startedMode == AcceleratorMode.Auto && devices.Any(d => !d.IsCpu))
            {
                cached = AccelerationCache.TryRead(CachePath, fingerprint);
                AppLog.Info(cached is null
                    ? "Beschleunigung: Kein passender Messwert-Cache (neu gemessen wird im Hintergrund)."
                    : $"Beschleunigung: Messwerte aus dem Cache (gemessen {cached.MeasuredAt.ToLocalTime():yyyy-MM-dd HH:mm}).");
            }

            var decision = AccelerationPlanner.Decide(_startedMode, _startedGpuKey, devices, cached);
            ApplyPlan(decision, devices);
            AppLog.Info("Beschleunigung: Plan " + DescribePlan(decision.Plan) + (decision.Notice is null ? "" : " – " + decision.Notice));
            if (decision.NeedsMeasurement)
                ScheduleBackgroundBenchmark();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Der Start ist fehlgeschlagen, es läuft die CPU.", ex);
            lock (_stateLock)
            {
                if (_plan is null)
                    ApplyPlanLocked(new PlanDecision(AccelerationPlanner.AllModels.ToDictionary(m => m, _ => (AcceleratorDevice?)null),
                        "Die Beschleunigung konnte nicht gestartet werden – die Erkennung läuft auf der CPU.", false, false), devices);
            }
            Raise();
        }
        finally
        {
            _planReady.TrySetResult();
        }
    }

    private void ApplyPlan(PlanDecision decision, IReadOnlyList<AcceleratorDevice> devices)
    {
        lock (_stateLock)
            ApplyPlanLocked(decision, devices);
        Raise();
    }

    private void ApplyPlanLocked(PlanDecision decision, IReadOnlyList<AcceleratorDevice> devices)
    {
        _plan = decision.Plan;
        var fallbacks = new List<AccelerationFallback>(_state.Fallbacks);
        var fellBack = new HashSet<ModelId>(fallbacks.Select(f => f.Model));
        // Detektoren, die schon vor dem Plan geladen haben, laufen auf der CPU, obwohl der Plan ein Gerät nennt.
        foreach (var model in _lateModels)
        {
            if (decision.Plan.TryGetValue(model, out var device) && device is { IsCpu: false })
            {
                fallbacks.Add(new AccelerationFallback(model, device.Name, "Der Plan war beim Laden der Erkennung noch nicht fertig.", false, _time.GetUtcNow()));
                fellBack.Add(model);
                AppLog.Warn($"Beschleunigung: {model} wurde vor dem fertigen Plan auf der CPU geladen (Plan: {device.Name}).");
            }
        }
        _lateModels.Clear();

        _state = _state with
        {
            Mode = _startedMode,
            PlanReady = true,
            Plan = BuildPlacements(decision.Plan, fellBack),
            Fallbacks = fallbacks,
            Devices = devices,
            Notice = decision.Notice,
            PlanFromCache = decision.FromCache,
        };
    }

    private static List<ModelPlacement> BuildPlacements(IReadOnlyDictionary<ModelId, AcceleratorDevice?> plan, ISet<ModelId> fellBack) =>
        AccelerationPlanner.AllModels.Select(model =>
        {
            plan.TryGetValue(model, out var device);
            var falling = fellBack.Contains(model);
            // Ein zurückgefallenes Modell läuft tatsächlich auf der CPU.
            return falling || device is null || device.IsCpu
                ? new ModelPlacement(model, AcceleratorDevice.CpuDevice.Name, AcceleratorKind.Cpu, AcceleratorDevice.CpuKey, falling)
                : new ModelPlacement(model, device.Name, device.Kind, device.Key, false);
        }).ToList();

    // ------------------------------------------------------------------ Plan für die Detektoren

    public AcceleratorDevice? GetDevice(ModelId model)
    {
        if (!_startRequested)
            return null;

        if (!_planReady.Task.IsCompleted)
        {
            // Nur Threadpool-Aufrufer (die Detektoren laden dort): kurz auf den Plan warten, sonst CPU.
            try
            {
                if (!_planReady.Task.Wait(_options.PlanWait ?? AccelerationTimeouts.PlanWait))
                {
                    RegisterLate(model);
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        var plan = _plan;
        return plan is not null && plan.TryGetValue(model, out var device) && device is { IsCpu: false } ? device : null;
    }

    private void RegisterLate(ModelId model)
    {
        bool raise = false;
        lock (_stateLock)
        {
            if (_plan is null)
            {
                _lateModels.Add(model);
            }
            else if (_plan.TryGetValue(model, out var device) && device is { IsCpu: false })
            {
                // Der Plan wurde zwischen Wartefrist und hier fertig; der Detektor läuft trotzdem auf der CPU.
                ReportFallbackLocked(model, device, "Der Plan war beim Laden der Erkennung noch nicht fertig.", false);
                raise = true;
            }
        }
        if (raise)
            Raise();
        AppLog.Warn($"Beschleunigung: Der Plan war für {model} nicht rechtzeitig fertig – die Erkennung lädt auf der CPU.");
    }

    public void ReportFallback(ModelId model, AcceleratorDevice device, string reason, bool atRuntime)
    {
        try
        {
            lock (_stateLock)
                ReportFallbackLocked(model, device, reason, atRuntime);
            Raise();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Ein Rückfall konnte nicht verbucht werden.", ex);
        }
    }

    private void ReportFallbackLocked(ModelId model, AcceleratorDevice device, string reason, bool atRuntime)
    {
        var fallbacks = new List<AccelerationFallback>(_state.Fallbacks)
        {
            new(model, device.Name, reason, atRuntime, _time.GetUtcNow()),
        };
        if (fallbacks.Count > MaxFallbacks)
            fallbacks.RemoveRange(0, fallbacks.Count - MaxFallbacks);

        var plan = _state.Plan.Select(p => p.Model == model
            ? new ModelPlacement(model, AcceleratorDevice.CpuDevice.Name, AcceleratorKind.Cpu, AcceleratorDevice.CpuKey, true)
            : p).ToList();
        _state = _state with { Fallbacks = fallbacks, Plan = plan };
    }

    // ------------------------------------------------------------------ Messung

    private void ScheduleBackgroundBenchmark()
    {
        var delay = _options.BackgroundBenchmarkDelay ?? AccelerationTimeouts.BackgroundBenchmarkDelay;
        AppLog.Info($"Beschleunigung: Hintergrundmessung startet in {delay.TotalSeconds:0} s.");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _time, _disposeCts.Token).ConfigureAwait(false);
                await StartBenchmarkAsync(background: true, null, _disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Beenden
            }
            catch (Exception ex)
            {
                AppLog.Warn("Beschleunigung: Die Hintergrundmessung ist fehlgeschlagen.", ex);
            }
        });
    }

    public Task<BenchmarkResult> RunBenchmarkAsync(IProgress<BenchmarkProgress>? progress = null, CancellationToken ct = default) =>
        StartBenchmarkAsync(background: false, progress, ct);

    private async Task<BenchmarkResult> StartBenchmarkAsync(bool background, IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Task<BenchmarkResult> running;
            bool runningIsBackground;
            CancellationTokenSource? runningCts;
            bool started = false;
            lock (_benchLock)
            {
                if (_benchTask is null || _benchTask.IsCompleted)
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, ct);
                    _benchCts = cts;
                    _benchBackground = background;
                    _benchTask = Task.Run(() => RunBenchmarkCoreAsync(background, progress, cts.Token));
                    started = true;
                }
                running = _benchTask;
                runningIsBackground = _benchBackground;
                runningCts = _benchCts;
            }

            if (started || background || !runningIsBackground)
                return await running.ConfigureAwait(false); // eigene bzw. gleiche Messung mitnutzen

            // Die Oberfläche löst die Hintergrundmessung ab.
            try
            {
                runningCts?.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Die abgebrochene Hintergrundmessung läuft im schlimmsten Fall im Hintergrund aus.
            }
        }
        return new BenchmarkResult(_time.GetUtcNow(), [], Cancelled: true);
    }

    private async Task<BenchmarkResult> RunBenchmarkCoreAsync(bool background, IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        UpdateState(s => s with { BenchmarkRunning = true, BenchmarkIsBackground = background });
        try
        {
            var devices = _catalog.Devices;
            AppLog.Info($"Beschleunigung: Messung gestartet ({devices.Count} Gerät(e){(background ? ", im Hintergrund" : "")}).");
            var result = await _benchmark.RunAsync(devices, background, progress, ct).ConfigureAwait(false);
            LogBenchmark(result);

            if (!result.Cancelled)
            {
                var fingerprint = CurrentFingerprint(devices, SafeProviders());
                if (AccelerationCache.TryWrite(CachePath, fingerprint, result))
                    AppLog.Info("Beschleunigung: Messwerte gespeichert (gelten ab dem nächsten Start im Modus Auto).");
            }

            UpdateState(s =>
            {
                var recommended = false;
                if (!result.Cancelled && _startedMode == AcceleratorMode.Auto && _plan is { } plan)
                    recommended = !AccelerationPlanner.SamePlan(plan, AccelerationPlanner.FromBenchmark(result, devices));
                return s with { LastBenchmark = result.Cancelled ? s.LastBenchmark : result, RestartRecommended = recommended || (result.Cancelled && s.RestartRecommended) };
            });
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die Messung ist fehlgeschlagen.", ex);
            return new BenchmarkResult(_time.GetUtcNow(), [], Cancelled: true);
        }
        finally
        {
            UpdateState(s => s with { BenchmarkRunning = false, BenchmarkIsBackground = false });
        }
    }

    // ------------------------------------------------------------------ Installation, Einstellungen

    public async Task<ProviderInstallResult> InstallVendorProvidersAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ProviderInstallResult result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, ct);
            result = await _catalog.InstallVendorProvidersAsync(progress, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die Installation der Ausführungsanbieter ist fehlgeschlagen.", ex);
            return new ProviderInstallResult(false, "Die Installation ist fehlgeschlagen: " + ex.Message, []);
        }

        if (result.Success)
        {
            IReadOnlyList<AcceleratorDevice> devices;
            try
            {
                devices = _catalog.RefreshDevices();
            }
            catch
            {
                devices = _catalog.Devices;
            }
            _installedProviders = _installedProviders || result.Providers.Count > 0;
            LogDevices(devices);
            UpdateState(s => s with { Devices = devices, RestartRequired = s.RestartRequired || _installedProviders });
        }
        return result;
    }

    public IReadOnlyList<ProviderState> GetProviderStates() => SafeProviders();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            var performance = _settings.Current.Performance;
            var changed = performance.Accelerator != _startedMode
                          || (performance.Accelerator == AcceleratorMode.Gpu && !string.Equals(performance.GpuDeviceKey, _startedGpuKey, StringComparison.Ordinal));
            UpdateState(s => s with { RestartRequired = changed || _installedProviders });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Eine Einstellungsänderung konnte nicht ausgewertet werden.", ex);
        }
    }

    // ------------------------------------------------------------------ Hilfen

    private string CachePath => _options.CacheFilePath ?? Path.Combine(_settings.ConfigDirectory, AccelerationCache.FileName);

    private IReadOnlyList<ProviderState> SafeProviders()
    {
        try
        {
            return _catalog.GetProviderStates();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Der Zustand der Ausführungsanbieter war nicht abrufbar.", ex);
            return [];
        }
    }

    private string CurrentFingerprint(IEnumerable<AcceleratorDevice> devices, IEnumerable<ProviderState> providers)
    {
        string ort;
        try
        {
            ort = (_options.OrtVersion ?? (() => OrtEnv.Instance().GetVersionString()))();
        }
        catch
        {
            ort = "?";
        }
        var app = _options.AppVersion
                  ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? typeof(AccelerationService).Assembly.GetName().Version?.ToString()
                  ?? "?";
        return AccelerationCache.Fingerprint(devices, providers, app, ort);
    }

    private void UpdateState(Func<AccelerationState, AccelerationState> change)
    {
        lock (_stateLock)
            _state = change(_state);
        Raise();
    }

    private void Raise()
    {
        var handler = StateChanged;
        if (handler is null || Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Ein Beobachter des Zustands hat eine Ausnahme ausgelöst.", ex);
        }
    }

    private static void LogDevices(IReadOnlyList<AcceleratorDevice> devices)
    {
        foreach (var d in devices)
        {
            var extras = new StringBuilder();
            if (d.Kind == AcceleratorKind.Gpu)
                extras.Append(d.IsDiscrete ? ", diskret" : ", integriert");
            if (!string.IsNullOrEmpty(d.Version))
                extras.Append(", Version ").Append(d.Version);
            AppLog.Info($"Beschleunigung: Gerät {d.Name} ({d.Kind}, {d.EpName}{extras})");
        }
    }

    private static string DescribePlan(IReadOnlyDictionary<ModelId, AcceleratorDevice?> plan) =>
        string.Join(", ", AccelerationPlanner.AllModels.Select(m =>
            $"{m}={(plan.TryGetValue(m, out var d) && d is { IsCpu: false } ? d.Name : "CPU")}"));

    private static void LogBenchmark(BenchmarkResult result)
    {
        foreach (var group in result.Entries.GroupBy(e => e.DeviceKey))
        {
            var parts = group.Select(e => e.Succeeded
                ? $"{e.Model} {e.AverageMs:0.0} ms (Laden {e.LoadMs:0} ms)"
                : $"{e.Model} Fehler: {e.Error}");
            AppLog.Info($"Beschleunigung: Messung {group.First().DeviceName}: {string.Join("; ", parts)}");
        }
        if (result.Cancelled)
            AppLog.Info("Beschleunigung: Messung abgebrochen.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            _settings.SettingsChanged -= OnSettingsChanged;
            // Abbrechen, aber nie auf native Aufrufe warten (das Beenden hat nur wenige Sekunden).
            await _disposeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Fehler beim Beenden.", ex);
        }
    }
}
