using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Detection.Acceleration;
using Fotobox.Services.Diagnostics;
using Fotobox.ViewModels.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Leistungsanzeige (Ansichtsmodell): sichtbar nur auf Tastendruck, misst nur dann, nie zwei Messungen zugleich, Frist, Fehler, Einstellungen
/// ohne Neustart, passive App-Werte. Zeit und Messquelle sind Attrappen (Zeit läuft nur per <c>Advance</c>); alle Namen erfunden.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PerformanceOverlayViewModelTests : IDisposable
{
    private const double Gb = 1024.0 * 1024 * 1024;

    private readonly TrackingTimeProvider _time = new();
    private readonly PerfSettingsStore _settings = new();
    private readonly FakeMetricsSource _source = new();
    private readonly FakeDetectionService _detection = new();
    private readonly FakeGestureService _gestures = new();
    private readonly FakeFrameStats _cameraFrames = new();
    private readonly DisplayFrameCounter _displayFrames = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        _source.Release();
        foreach (var d in _disposables)
            d.Dispose();
    }

    // xunit setzt je Test einen eigenen Kontext; Timer-Aufrufe und Einstellungswechsel sollen aber synchron im Test laufen.
    private static void NoContext() => SynchronizationContext.SetSynchronizationContext(null);

    private PerformanceOverlayViewModel Create(IAccelerationService? acceleration = null, bool withSource = true, bool withFrames = true)
    {
        NoContext();
        var fun = new FunOverlayViewModel(_detection, new FakeSoundPlayer(), _time);
        var peace = new PeaceOverlayViewModel(_gestures, _time, new FakeSoundPlayer());
        _disposables.Add(peace);
        var vm = new PerformanceOverlayViewModel(_settings, _time, fun, peace, withSource ? _source : null, acceleration,
            withFrames ? _cameraFrames : null, withFrames ? _displayFrames : null);
        _disposables.Add(vm);
        FunVm = fun;
        PeaceVm = peace;
        return vm;
    }

    private FunOverlayViewModel FunVm { get; set; } = null!;
    private PeaceOverlayViewModel PeaceVm { get; set; } = null!;

    private static void Show(PerformanceOverlayViewModel vm)
    {
        NoContext();
        vm.Toggle();
    }

    /// <summary>Ein Takt der Anzeige (Intervall 1 s) samt Abwarten der dabei gestarteten Messung.</summary>
    private async Task TickAsync(PerformanceOverlayViewModel vm, int seconds = 1)
    {
        NoContext();
        _time.Advance(TimeSpan.FromSeconds(seconds));
        await vm.PendingSample;
    }

    private static PerformanceOverlayRow Row(PerformanceOverlayViewModel vm, string label) => vm.Rows.Single(r => r.Label == label);

    private static async Task WaitFor(Func<bool> condition, string what = "Bedingung")
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Nicht erreicht: " + what);
            await Task.Delay(10);
        }
    }

    // ---- Sichtbarkeit und Messen --------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartsHidden_AndDoesNotMeasureWhileHidden()
    {
        var vm = Create();

        _time.Advance(TimeSpan.FromSeconds(30));
        await vm.PendingSample;

        Assert.False(vm.IsVisible);
        Assert.Equal(0, _source.Calls);
        Assert.Empty(vm.Rows);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public async Task Toggle_ShowsAndMeasuresOnEveryTick_HidingStopsTheTimerAndTheMeasuring()
    {
        var vm = Create();
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Show(vm);
        await vm.PendingSample;
        Assert.True(vm.IsVisible);
        Assert.Contains(nameof(PerformanceOverlayViewModel.IsVisible), changes);
        Assert.Equal(1, _source.Calls);   // sofort beim Einblenden
        await WaitFor(() => _time.ActiveTimers == 1, "ein Takt-Timer");

        await TickAsync(vm);
        await TickAsync(vm);
        Assert.Equal(3, _source.Calls);

        Show(vm);   // Toggle: wieder aus
        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Rows);
        await WaitFor(() => _time.ActiveTimers == 0, "kein Timer mehr");
        _time.Advance(TimeSpan.FromSeconds(10));
        await vm.PendingSample;
        Assert.Equal(3, _source.Calls);
    }

    [Fact]
    public async Task AfterARestart_TheOverlayIsHiddenAgain_NothingIsKept()
    {
        var first = Create();
        Show(first);
        await first.PendingSample;
        Assert.True(first.IsVisible);

        var second = Create();   // neuer Programmlauf

        Assert.False(second.IsVisible);
        Assert.Empty(second.Rows);
    }

    [Fact]
    public async Task ShowAgain_StartsFresh_NoOldValues()
    {
        var vm = Create();
        _source.Next = new SystemMetricsSnapshot(CpuPercent: 42);
        Show(vm);
        await vm.PendingSample;
        await TickAsync(vm);
        Assert.Equal("42 %", Row(vm, "CPU").Text);

        Show(vm);   // aus
        Show(vm);   // wieder ein
        await vm.PendingSample;

        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "CPU").Text);   // noch keine neue Messung ausgewertet
    }

    [Fact]
    public async Task NeverRunsTwoMeasurementsAtOnce_AHangingRunBlocksNewOnes_AndShowsDashes()
    {
        var vm = Create();
        _source.Next = new SystemMetricsSnapshot(CpuPercent: 50);
        Show(vm);
        await vm.PendingSample;                       // Messung 1 fertig
        _source.Hold();                               // Messung 2 hängt
        _time.Advance(TimeSpan.FromSeconds(1));       // t = 1 s: Anzeige zeigt Messung 1, Messung 2 wird angestoßen
        await WaitFor(() => _source.Calls == 2, "Messung 2 gestartet");
        Assert.Equal("50 %", Row(vm, "CPU").Text);

        _time.Advance(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _source.Calls);               // nichts Neues, solange die alte läuft
        Assert.Equal(1, _source.MaxConcurrent);
        Assert.Equal("50 %", Row(vm, "CPU").Text);    // Frist (3 s) noch nicht um

        _time.Advance(TimeSpan.FromSeconds(1));       // Frist überschritten
        await vm.PendingSample;
        _time.Advance(TimeSpan.FromSeconds(1));       // nächster Takt zeigt „–“
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "CPU").Text);
        Assert.Equal(2, _source.Calls);
        Assert.Equal(1, _source.MaxConcurrent);

        _source.Release();                            // der Treiber antwortet doch noch
        // Ab dem Ende der hängenden Messung läuft die Anzeige wieder (die neue Messung startet auf dem Threadpool, daher „mindestens“).
        await WaitFor(() =>
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            return _source.Calls >= 3;
        }, "neue Messung nach dem Ende der hängenden");
        Assert.Equal(1, _source.MaxConcurrent);
    }

    [Fact]
    public async Task AFailingSource_NeverCrashes_ShowsDashes_AndIsAskedAgain()
    {
        var vm = Create();
        _source.Throw = new InvalidOperationException("Test");
        Show(vm);
        await vm.PendingSample;

        await TickAsync(vm);
        Assert.True(vm.IsVisible);
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "CPU").Text);

        _source.Throw = null;
        _source.Next = new SystemMetricsSnapshot(CpuPercent: 7);
        await TickAsync(vm);
        await TickAsync(vm);
        Assert.Equal("7 %", Row(vm, "CPU").Text);
        Assert.True(_source.Calls >= 4);
    }

    [Fact]
    public async Task NullValues_AreNotAvailable_AfterTheSecondSample_AndDashesInTheFirst()
    {
        var vm = Create();
        _source.Next = new SystemMetricsSnapshot(CpuPercent: 10);   // alles andere null
        Show(vm);
        await vm.PendingSample;

        await TickAsync(vm);   // zeigt Messung 1 (die erste, ohne Raten)
        Assert.Equal("10 %", Row(vm, "CPU").Text);
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "NPU").Text);

        await TickAsync(vm);   // zeigt Messung 2
        Assert.Equal(PerformanceOverlayRows.Unavailable, Row(vm, "NPU").Text);
        Assert.Equal(PerformanceOverlayRows.Unavailable, Row(vm, "CPU-Temperatur").Text);
        Assert.Equal(PerformanceOverlayRows.Unavailable, Row(vm, "RAM").Text);
    }

    [Fact]
    public async Task RowsContainWarnLevelsAndFormats()
    {
        var vm = Create();
        _source.Next = new SystemMetricsSnapshot(CpuPercent: 90, CpuClockMhz: 3200, CpuTemperatureC: 61,
            RamUsedBytes: 7.4 * Gb, RamTotalBytes: 16 * Gb);
        Show(vm);
        await vm.PendingSample;
        await TickAsync(vm);
        await TickAsync(vm);

        Assert.Equal(new PerformanceOverlayRow("CPU", "90 %", 0.9, PerformanceWarnLevel.Critical), Row(vm, "CPU"));
        Assert.Equal("3,2 GHz", Row(vm, "CPU-Takt").Text);
        Assert.Equal("61 °C", Row(vm, "CPU-Temperatur").Text);
        Assert.Equal("7,4/16,0 GB", Row(vm, "RAM").Text);
        Assert.Equal(PerformanceWarnLevel.Normal, Row(vm, "RAM").Level);
    }

    // ---- Einstellungen ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisabledValue_HasNoRow_AndTheChangeWorksImmediately_WithoutRestart()
    {
        var vm = Create();
        _source.Next = new SystemMetricsSnapshot(CpuPercent: 10, NpuPercent: 20);
        Show(vm);
        await vm.PendingSample;
        await TickAsync(vm);
        await TickAsync(vm);
        Assert.Contains(vm.Rows, r => r.Label == "NPU");
        var calls = _source.Calls;

        _settings.Current.PerformanceOverlay.ShowNpu = false;
        _settings.RaiseChanged();

        Assert.DoesNotContain(vm.Rows, r => r.Label == "NPU");   // ohne weiteren Takt
        Assert.Contains(vm.Rows, r => r.Label == "CPU");
        Assert.Equal(calls, _source.Calls);

        _settings.Current.PerformanceOverlay.ShowNpu = true;
        _settings.RaiseChanged();
        Assert.Contains(vm.Rows, r => r.Label == "NPU");
    }

    [Fact]
    public void StyleFontAndOpacity_FollowTheSettings_AndRaiseChanges()
    {
        var vm = Create();
        Show(vm);
        var changed = new HashSet<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(vm.ShowBars);

        _settings.Current.PerformanceOverlay.Style = PerformanceOverlayStyle.Bars;
        _settings.Current.PerformanceOverlay.FontSize = 24;
        _settings.Current.PerformanceOverlay.BackgroundOpacityPercent = 25;
        _settings.RaiseChanged();

        Assert.True(vm.ShowBars);
        Assert.Equal(24, vm.FontSize);
        Assert.Equal(0.25, vm.BackgroundOpacity, 3);
        Assert.Contains(nameof(PerformanceOverlayViewModel.ShowBars), changed);
        Assert.Contains(nameof(PerformanceOverlayViewModel.FontSize), changed);
        Assert.Contains(nameof(PerformanceOverlayViewModel.BackgroundOpacity), changed);
    }

    [Fact]
    public async Task RefreshInterval_ChangeAppliesWithoutRestart()
    {
        var vm = Create();
        Show(vm);
        await vm.PendingSample;
        Assert.Equal(1, _source.Calls);

        _settings.Current.PerformanceOverlay.RefreshMs = 2000;
        _settings.RaiseChanged();

        _time.Advance(TimeSpan.FromMilliseconds(1500));
        await vm.PendingSample;
        Assert.Equal(1, _source.Calls);                     // noch kein Takt: der neue Takt ist 2 s
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await vm.PendingSample;
        Assert.Equal(2, _source.Calls);
        await WaitFor(() => _time.ActiveTimers == 1, "nur der neue Timer");   // der alte Timer ist weg
    }

    // ---- App-Werte ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CameraAndDisplayFps_AreRatesOfTheCounters()
    {
        var vm = Create();
        Show(vm);
        await vm.PendingSample;
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "Kamera").Text);   // erster Takt: noch keine Differenz

        _cameraFrames.Frames += 30;
        _displayFrames.Increment();
        for (var i = 0; i < 59; i++)
            _displayFrames.Increment();
        await TickAsync(vm);

        Assert.Equal("30 FPS", Row(vm, "Kamera").Text);
        Assert.Equal("60 FPS", Row(vm, "Anzeige").Text);

        _cameraFrames.Frames += 15;
        await TickAsync(vm);
        Assert.Equal("15 FPS", Row(vm, "Kamera").Text);
        Assert.Equal("0 FPS", Row(vm, "Anzeige").Text);
    }

    [Fact]
    public async Task WithoutFrameCounters_FpsShowsDashes()
    {
        var vm = Create(withFrames: false);
        Show(vm);
        await vm.PendingSample;
        await TickAsync(vm);

        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "Kamera").Text);
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "Anzeige").Text);
    }

    [Fact]
    public async Task TargetingRateAndTime_ComeFromFunStatus_AndAreDashesWhenInactiveOrStale()
    {
        var vm = Create();
        Show(vm);
        await vm.PendingSample;
        await TickAsync(vm);
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "Zielerfassung").Text);   // Zielerfassung aus

        NoContext();
        FunVm.SetActive(true, new FunSettings { Enabled = true }, false);
        _detection.Last.RaiseStatus(new DetectionStatus(DetectionMethod.Body, DetectionKind.Body, null, null, 8.5, 12));
        await TickAsync(vm);
        Assert.Equal("8,5 Analysen/s", Row(vm, "Zielerfassung").Text);
        Assert.Equal("12 ms", Row(vm, "Zielerfassung Zeit").Text);

        // Keine neuen Analysen mehr: nach der Frist nie ein alter Wert.
        for (var i = 0; i < 6; i++)
            await TickAsync(vm);
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "Zielerfassung").Text);
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "Zielerfassung Zeit").Text);

        // Wieder Analysen, dann Funktion aus: sofort „–“.
        _detection.Last.RaiseStatus(new DetectionStatus(DetectionMethod.Body, DetectionKind.Body, null, null, 9, 11));
        await TickAsync(vm);
        Assert.Equal("9,0 Analysen/s", Row(vm, "Zielerfassung").Text);
        NoContext();
        FunVm.SetActive(false, new FunSettings(), false);
        await TickAsync(vm);
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "Zielerfassung").Text);
    }

    [Fact]
    public async Task GestureRateAndTime_ComeFromPeaceStatus()
    {
        var vm = Create();
        Show(vm);
        await vm.PendingSample;

        NoContext();
        PeaceVm.SetMode(PeaceMode.Watching, new PeaceSettings(), false);
        _gestures.Last.RaiseStatus(new GestureStatus(true, true, null, null, 6, 25));
        await TickAsync(vm);
        Assert.Equal("6,0 Analysen/s", Row(vm, "Gesten").Text);
        Assert.Equal("25 ms", Row(vm, "Gesten Zeit").Text);

        NoContext();
        PeaceVm.SetMode(PeaceMode.Off, new PeaceSettings(), false);
        await TickAsync(vm);
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "Gesten").Text);
        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "Gesten Zeit").Text);
    }

    [Fact]
    public async Task ModelDevices_ComeFromThePlan_AndTheWindowsFaceDetectorReplacesPersons()
    {
        var gpu = new AcceleratorDevice("g1", "Grafik A", AcceleratorKind.Gpu, "Ep", VendorId: 0x1234, DeviceId: 0x5678);
        var acceleration = new FakeAccelerationService(FakeAccelerationService.Ready(
            [AcceleratorDevice.CpuDevice, gpu],
            plan:
            [
                new ModelPlacement(ModelId.Yolox, "Grafik A", AcceleratorKind.Gpu, "g1", false),
                new ModelPlacement(ModelId.YuNet, "CPU", AcceleratorKind.Cpu, "cpu", true),
                new ModelPlacement(ModelId.Palm, "CPU", AcceleratorKind.Cpu, "cpu", false),
                new ModelPlacement(ModelId.Landmark, "CPU", AcceleratorKind.Cpu, "cpu", false),
            ]));
        var vm = Create(acceleration);
        Show(vm);
        await vm.PendingSample;
        await TickAsync(vm);

        Assert.Equal("Grafik A", Row(vm, "Personen").Text);
        Assert.Equal("CPU (Rückfall)", Row(vm, "Gesichter").Text);
        Assert.Equal("CPU", Row(vm, "Hände").Text);
        Assert.Equal("CPU", Row(vm, "Handpunkte").Text);

        // Die Messung bekommt die Grafikkarte der KI.
        Assert.Equal(new GpuSelection(0x1234, 0x5678, "Grafik A"), _source.LastGpu);

        NoContext();
        FunVm.SetActive(true, new FunSettings { Enabled = true }, false);
        _detection.Last.RaiseStatus(new DetectionStatus(DetectionMethod.Body, DetectionKind.Face, "Ausweichen", null, 5, 8));
        await TickAsync(vm);
        Assert.DoesNotContain(vm.Rows, r => r.Label == "Personen");
        Assert.Equal("Windows-Gesichtserkennung", Row(vm, "Zielerfassung Gerät").Text);
    }

    [Fact]
    public async Task NoGpuInThePlan_PassesNullToTheSource()
    {
        var acceleration = new FakeAccelerationService(FakeAccelerationService.Ready([AcceleratorDevice.CpuDevice]));
        var vm = Create(acceleration);
        _source.LastGpu = new GpuSelection(1, 2, "alt");
        Show(vm);
        await vm.PendingSample;

        Assert.Null(_source.LastGpu);
    }

    [Fact]
    public async Task WithoutSource_NothingIsMeasured_SystemValuesAreDashes_AppValuesStillWork()
    {
        var vm = Create(withSource: false);
        Show(vm);
        _cameraFrames.Frames += 25;
        await TickAsync(vm);

        Assert.Equal(PerformanceOverlayRows.Dash, Row(vm, "CPU").Text);
        Assert.Equal("25 FPS", Row(vm, "Kamera").Text);
        Assert.Equal(0, _source.Calls);
    }

    // ---- Beenden ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_StopsTheTimer_AndLaterTogglesDoNothing()
    {
        var vm = Create();
        Show(vm);
        await vm.PendingSample;
        await WaitFor(() => _time.ActiveTimers == 1, "ein Takt-Timer");

        vm.Dispose();
        _time.Advance(TimeSpan.FromSeconds(10));
        Show(vm);

        await WaitFor(() => _time.ActiveTimers == 0, "kein Timer mehr");
        Assert.False(vm.IsVisible);
        Assert.Equal(1, _source.Calls);
    }

    [Fact]
    public async Task SettingsChanged_AfterDispose_IsIgnored()
    {
        var vm = Create();
        Show(vm);
        await vm.PendingSample;
        vm.Dispose();
        var rows = vm.Rows;

        _settings.RaiseChanged();

        Assert.Same(rows, vm.Rows);
    }

    // ---- Attrappen ----------------------------------------------------------------------------------------------------------

    private sealed class PerfSettingsStore : ISettingsStore
    {
        public string ConfigDirectory => "fake";
        public string OverlayDirectory => "fake/overlays";
        public AppSettings Current { get; } = new();
        public event EventHandler? SettingsChanged;
        public void Save(AppSettings settings) { }
        public void RaiseChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeFrameStats : ICameraFrameStats
    {
        public long Frames { get; set; }
        public long FramesReceived => Frames;
    }

    /// <summary>Messquelle-Attrappe: zählt Aufrufe und gleichzeitige Läufe, kann Fehler werfen oder (bis <see cref="Release"/>) hängen.</summary>
    private sealed class FakeMetricsSource : ISystemMetricsSource
    {
        private int _calls, _running, _maxConcurrent;
        private volatile ManualResetEventSlim? _armed;
        private volatile ManualResetEventSlim? _active;

        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public SystemMetricsSnapshot Next { get; set; } = new();
        public Exception? Throw { get; set; }
        public GpuSelection? LastGpu { get; set; }

        /// <summary>Der nächste Lauf hängt, bis <see cref="Release"/> aufgerufen wird.</summary>
        public void Hold() => _armed = new ManualResetEventSlim(false);

        public void Release()
        {
            _armed?.Set();
            _active?.Set();
        }

        public SystemMetricsSnapshot Sample(GpuSelection? gpu)
        {
            var running = Interlocked.Increment(ref _running);
            try
            {
                int max;
                while (running > (max = Volatile.Read(ref _maxConcurrent)))
                    Interlocked.CompareExchange(ref _maxConcurrent, running, max);
                LastGpu = gpu;
                Interlocked.Increment(ref _calls);
                if (Interlocked.Exchange(ref _armed, null) is { } hold)
                {
                    _active = hold;   // nur dieser Lauf hängt
                    hold.Wait(TimeSpan.FromSeconds(30));
                }
                if (Throw is { } failure)
                    throw failure;
                return Next;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public void Dispose() => Release();
    }
}
