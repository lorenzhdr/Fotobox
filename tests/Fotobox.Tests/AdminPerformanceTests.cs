using System.ComponentModel;
using System.Globalization;
using System.IO;
using Fotobox.Core;
using Fotobox.Services.Detection.Acceleration;
using Fotobox.ViewModels.Admin;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Admin-Tab „Leistung“ (Hardwarebeschleunigung): Entwurf, Auswahl, Betriebsstand, Messung, Installation, Threading und Schließen.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminPerformanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-performance-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly List<AdminViewModel> _created = [];

    private readonly AcceleratorDevice _gpu = AccelDevices.Gpu("Testkarte Stark", discrete: true, index: 0, memory: 8192, device: 1);
    private readonly AcceleratorDevice _weakGpu = AccelDevices.Gpu("Testkarte Schwach", discrete: false, index: 1, memory: 512, device: 2);
    private readonly AcceleratorDevice _npu = AccelDevices.Npu("Test-KI-Einheit");

    public AdminPerformanceTests()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        Directory.CreateDirectory(Path.Combine(_dir, "overlays"));
        _store = new AdminViewModelTests.FakeStore(_dir);
        _pin = new AdminViewModelTests.FakePinService(_store);
    }

    public void Dispose()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        foreach (var vm in _created)
            vm.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Der Ansichtsmodell-Konstruktor merkt sich den aktuellen Synchronisationskontext; xUnit setzt je Test einen eigenen: ohne Kontext
    // laufen die geposteten Aktualisierungen direkt (deterministisch).
    private AdminViewModel Create(IAccelerationService? acceleration, TimeProvider? time = null, SynchronizationContext? context = null)
    {
        SynchronizationContext.SetSynchronizationContext(context);
        var vm = new AdminViewModel(_store, _pin, new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), _ui, time,
            displays: null, acceleration: acceleration);
        _created.Add(vm);
        return vm;
    }

    private static List<string> Lines(AdminViewModel vm) => vm.AccelerationStatusLines.Select(l => l.Text).ToList();

    private static FakeAccelerationService ReadyService(params AcceleratorDevice[] devices) =>
        new(FakeAccelerationService.Ready(AccelDevices.WithCpu(devices)));

    // ------------------------------------------------------------------ Entwurf

    [Fact]
    public void ModeRadios_WriteIntoDraft_AndOnlyTheChosenOneIsTrue()
    {
        var vm = Create(ReadyService(_gpu));
        Assert.True(vm.IsAcceleratorAuto);

        vm.IsAcceleratorGpu = true;
        Assert.Equal(AcceleratorMode.Gpu, vm.Draft.Performance.Accelerator);
        Assert.True(vm.IsAcceleratorGpu);
        Assert.False(vm.IsAcceleratorAuto);
        Assert.True(vm.IsGpuListEnabled);

        // WPF meldet beim Abwählen false zurück: darf nichts ändern.
        vm.IsAcceleratorAuto = false;
        Assert.Equal(AcceleratorMode.Gpu, vm.Draft.Performance.Accelerator);

        vm.IsAcceleratorCpu = true;
        Assert.Equal(AcceleratorMode.Cpu, vm.Draft.Performance.Accelerator);
        Assert.False(vm.IsGpuListEnabled);
        Assert.Equal(AcceleratorMode.Auto, _store.Current.Performance.Accelerator);   // Store unberührt
    }

    [Fact]
    public void PreferHighPerformanceGpu_ReadsAndWritesDraft()
    {
        var vm = Create(ReadyService());
        Assert.True(vm.PreferHighPerformanceGpu);

        vm.PreferHighPerformanceGpu = false;

        Assert.False(vm.Draft.Performance.PreferHighPerformanceGpu);
        Assert.True(_store.Current.Performance.PreferHighPerformanceGpu);
    }

    [Fact]
    public async Task ApplyCommand_SavesPerformanceSettings()
    {
        var vm = Create(ReadyService(_gpu));
        vm.IsAcceleratorGpu = true;
        vm.SelectedGpuOption = vm.GpuOptions.Single(o => o.Key == _gpu.Key);
        vm.PreferHighPerformanceGpu = false;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(AcceleratorMode.Gpu, _store.Current.Performance.Accelerator);
        Assert.Equal(_gpu.Key, _store.Current.Performance.GpuDeviceKey);
        Assert.False(_store.Current.Performance.PreferHighPerformanceGpu);
    }

    // ------------------------------------------------------------------ Grafikkarten

    [Fact]
    public void GpuOptions_StartWithStrongestAutoEntry_ThenGpusStrongestFirst_WithoutCpuAndNpu()
    {
        var vm = Create(ReadyService(_weakGpu, _npu, _gpu));

        Assert.Equal(3, vm.GpuOptions.Count);
        Assert.Null(vm.GpuOptions[0].Key);
        Assert.Equal(AdminViewModel.AutoGpuOptionText, vm.GpuOptions[0].Display);
        Assert.Equal(_gpu.Key, vm.GpuOptions[1].Key);   // dediziert vor integriert
        Assert.Equal(_weakGpu.Key, vm.GpuOptions[2].Key);
        Assert.Contains("Testkarte Stark", vm.GpuOptions[1].Display);
        Assert.Contains("dediziert", vm.GpuOptions[1].Display);
        Assert.Contains("8 GB", vm.GpuOptions[1].Display);
        Assert.Contains("integriert", vm.GpuOptions[2].Display);
        Assert.Contains("512 MB", vm.GpuOptions[2].Display);
        Assert.Same(vm.GpuOptions[0], vm.SelectedGpuOption);   // null = leistungsstärkste
    }

    [Fact]
    public void SelectingGpu_WritesKeyIntoDraft_AndAutoEntryWritesNull()
    {
        var vm = Create(ReadyService(_gpu, _weakGpu));

        vm.SelectedGpuOption = vm.GpuOptions.Single(o => o.Key == _weakGpu.Key);
        Assert.Equal(_weakGpu.Key, vm.Draft.Performance.GpuDeviceKey);

        vm.SelectedGpuOption = vm.GpuOptions[0];
        Assert.Null(vm.Draft.Performance.GpuDeviceKey);
    }

    [Fact]
    public void SavedGpuKey_ThatIsNotInTheList_StaysSelected_AndIsNeverOverwritten()
    {
        _store.Current.Performance.GpuDeviceKey = "abwesende-testkarte";
        var service = new FakeAccelerationService();   // Plan noch nicht fertig
        var vm = Create(service);

        // Vor dem fertigen Plan ist die Geräteliste unbekannt: neutrale Beschriftung, kein Warntext.
        Assert.Equal("abwesende-testkarte", vm.SelectedGpuOption!.Key);
        Assert.Equal(AdminViewModel.UnknownGpuOptionText, vm.SelectedGpuOption.Display);
        Assert.False(vm.HasGpuHint);

        service.Update(_ => FakeAccelerationService.Ready(AccelDevices.WithCpu(_gpu)));

        Assert.Equal(3, vm.GpuOptions.Count);   // Auto, Karte, fehlende Karte
        var missing = vm.GpuOptions.Last();
        Assert.Equal("abwesende-testkarte", missing.Key);
        Assert.Equal(AdminViewModel.MissingGpuOptionText, missing.Display);
        Assert.Same(missing, vm.SelectedGpuOption);
        Assert.Equal("abwesende-testkarte", vm.Draft.Performance.GpuDeviceKey);
        Assert.True(vm.HasGpuHint);

        // Ein Austausch der Liste meldet null zurück (ItemsSource-Wechsel): Entwurf bleibt.
        vm.SelectedGpuOption = null;
        service.Raise();
        Assert.Equal("abwesende-testkarte", vm.Draft.Performance.GpuDeviceKey);

        // Wählt der Admin eine echte Karte und wieder zurück, bleibt der Eintrag erreichbar – auch wenn dazwischen Zustandsmeldungen
        // des Dienstes eintreffen (Messung, Rückfall …).
        vm.SelectedGpuOption = vm.GpuOptions.Single(o => o.Key == _gpu.Key);
        Assert.Equal(_gpu.Key, vm.Draft.Performance.GpuDeviceKey);
        service.Update(s => s with { BenchmarkRunning = true, BenchmarkIsBackground = true });
        service.Update(s => s with { BenchmarkRunning = false, BenchmarkIsBackground = false });
        Assert.Equal(3, vm.GpuOptions.Count);
        Assert.Equal("abwesende-testkarte", vm.GpuOptions.Last().Key);
        Assert.Equal(_gpu.Key, vm.SelectedGpuOption!.Key);
        vm.SelectedGpuOption = vm.GpuOptions.Last();
        Assert.Equal("abwesende-testkarte", vm.Draft.Performance.GpuDeviceKey);
    }

    [Fact]
    public void NoGpuFound_ExplainsItInThePlanReadyState()
    {
        var vm = Create(ReadyService());

        Assert.True(vm.HasGpuHint);
        Assert.Contains("keine Grafikkarte", vm.GpuHintText);
        Assert.Single(vm.GpuOptions);
    }

    // ------------------------------------------------------------------ NPU

    [Fact]
    public void Npu_IsDisabledWithExplanation_WhenPlanIsReadyAndThereIsNone()
    {
        var vm = Create(ReadyService(_gpu));

        Assert.False(vm.IsNpuSelectable);
        Assert.True(vm.HasNpuHint);
        Assert.Contains("Beschleuniger installieren", vm.NpuHintText);
    }

    [Fact]
    public void Npu_IsSelectable_WithNpu_BeforePlanReady_OrWhenAlreadyChosen()
    {
        Assert.True(Create(ReadyService(_npu)).IsNpuSelectable);

        var pending = Create(new FakeAccelerationService());
        Assert.True(pending.IsNpuSelectable);
        Assert.False(pending.HasNpuHint);   // vorher unbekannt: keine Behauptung

        _store.Current.Performance.Accelerator = AcceleratorMode.Npu;
        var saved = Create(ReadyService(_gpu));
        Assert.True(saved.IsNpuSelectable);   // die gespeicherte Wahl bleibt sichtbar
        Assert.True(saved.IsAcceleratorNpu);
    }

    // ------------------------------------------------------------------ Betriebsstand

    [Fact]
    public void Status_BeforePlanReady_ShowsPendingText_AndNoLines()
    {
        _store.Current.Performance.Accelerator = AcceleratorMode.Cpu;   // Initialzustand ignoriert die Einstellungen
        var service = new FakeAccelerationService();
        var vm = Create(service);

        Assert.Equal("Wird ermittelt …", vm.AccelerationPendingText);
        Assert.True(vm.HasAccelerationPendingText);
        Assert.Empty(vm.AccelerationStatusLines);
        Assert.Null(vm.AccelerationSourceText);

        service.Update(_ => FakeAccelerationService.Ready(AccelDevices.WithCpu(_gpu)));

        Assert.False(vm.HasAccelerationPendingText);
        Assert.Equal(4, vm.AccelerationStatusLines.Count);
    }

    [Fact]
    public void Status_ListsEachModelWithGermanNameDeviceAndKind()
    {
        var plan = new List<ModelPlacement>
        {
            new(ModelId.Yolox, _gpu.Name, AcceleratorKind.Gpu, _gpu.Key, false),
            new(ModelId.YuNet, "CPU", AcceleratorKind.Cpu, AcceleratorDevice.CpuKey, false),
            new(ModelId.Palm, _npu.Name, AcceleratorKind.Npu, _npu.Key, false),
            new(ModelId.Landmark, "CPU", AcceleratorKind.Cpu, AcceleratorDevice.CpuKey, false),
        };
        var vm = Create(new FakeAccelerationService(FakeAccelerationService.Ready(AccelDevices.WithCpu(_gpu, _npu), plan: plan)));

        Assert.Equal(
        [
            "Personenerkennung (Zielerfassung): Testkarte Stark (GPU)",
            "Gesichtserkennung: Prozessor (CPU)",
            "Handflächen: Test-KI-Einheit (NPU)",
            "Handpunkte: Prozessor (CPU)",
        ], Lines(vm));
        Assert.All(vm.AccelerationStatusLines, l => Assert.False(l.IsFallback));
    }

    [Fact]
    public void Status_ShowsFallbackWithReason_AndTheServiceNotice()
    {
        var plan = FakeAccelerationService.CpuPlan().Select(p => p.Model == ModelId.Palm ? p with { FellBack = true } : p).ToList();
        var state = FakeAccelerationService.Ready(AccelDevices.WithCpu(_gpu), plan: plan) with
        {
            Fallbacks = [new AccelerationFallback(ModelId.Palm, _gpu.Name, "Testfehler beim Laden", false, DateTimeOffset.UtcNow)],
            Notice = "Die gewählte Grafikkarte fehlt.",
        };
        var vm = Create(new FakeAccelerationService(state));

        var line = vm.AccelerationStatusLines.Single(l => l.IsFallback);
        Assert.StartsWith("Handflächen:", line.Text);
        Assert.True(line.HasDetail);
        Assert.Contains("Testkarte Stark", line.Detail);
        Assert.Contains("Testfehler beim Laden", line.Detail);
        Assert.Contains("beim Laden", line.Detail);
        Assert.True(vm.HasAccelerationNotice);
        Assert.Equal("Die gewählte Grafikkarte fehlt.", vm.AccelerationNotice);
    }

    [Fact]
    public void Status_ShowsRuntimeFallbackAfterTheServiceReportsIt()
    {
        var service = ReadyService(_gpu);
        var vm = Create(service);
        Assert.DoesNotContain(vm.AccelerationStatusLines, l => l.IsFallback);

        service.Update(s => s with
        {
            Plan = s.Plan.Select(p => p.Model == ModelId.Landmark ? p with { FellBack = true } : p).ToList(),
            Fallbacks = [new AccelerationFallback(ModelId.Landmark, _gpu.Name, "Treiber zurückgesetzt", true, DateTimeOffset.UtcNow)],
        });

        var line = vm.AccelerationStatusLines.Single(l => l.IsFallback);
        Assert.StartsWith("Handpunkte:", line.Text);
        Assert.Contains("während des Betriebs", line.Detail);
        Assert.Contains("Treiber zurückgesetzt", line.Detail);
    }

    [Fact]
    public void SourceText_DistinguishesCacheBackgroundMeasurementAndNotMeasured()
    {
        var devices = AccelDevices.WithCpu(_gpu);
        var measured = new BenchmarkResult(new DateTimeOffset(2026, 3, 14, 10, 30, 0, TimeSpan.Zero), AccelDevices.Result((_gpu, 5, 5, 5, 5)).Entries);
        var service = new FakeAccelerationService(FakeAccelerationService.Ready(devices));
        var vm = Create(service);

        Assert.Contains("Noch nicht gemessen", vm.AccelerationSourceText);

        service.Update(s => s with { BenchmarkRunning = true, BenchmarkIsBackground = true });
        Assert.Contains("läuft im Hintergrund", vm.AccelerationSourceText);

        service.Update(s => s with { BenchmarkRunning = false, BenchmarkIsBackground = false, PlanFromCache = true, LastBenchmark = measured });
        Assert.Contains("letzten Messung", vm.AccelerationSourceText);

        // Nur CPU: nichts zu messen, nichts zu sagen. Andere Modi: kein Auto-Hinweis.
        Assert.Null(Create(ReadyService()).AccelerationSourceText);
        Assert.Null(Create(new FakeAccelerationService(FakeAccelerationService.Ready(devices, AcceleratorMode.Cpu))).AccelerationSourceText);
    }

    // ------------------------------------------------------------------ Neustart-Hinweis

    [Fact]
    public void RestartHint_AppearsAsSoonAsTheDraftDiffersFromTheRunningMode()
    {
        var vm = Create(ReadyService(_gpu));
        Assert.False(vm.HasPerformanceRestartHint);

        vm.IsAcceleratorCpu = true;
        Assert.Equal(AdminViewModel.PerformanceRestartText, vm.PerformanceRestartHint);

        vm.IsAcceleratorAuto = true;   // zurück auf den laufenden Modus
        Assert.False(vm.HasPerformanceRestartHint);

        vm.IsAcceleratorGpu = true;
        Assert.True(vm.HasPerformanceRestartHint);
    }

    [Fact]
    public void RestartHint_ForGpuKeyChange_OnlyInGpuMode()
    {
        _store.Current.Performance.Accelerator = AcceleratorMode.Gpu;
        var vm = Create(new FakeAccelerationService(FakeAccelerationService.Ready(AccelDevices.WithCpu(_gpu, _weakGpu), AcceleratorMode.Gpu)));
        Assert.False(vm.HasPerformanceRestartHint);

        vm.SelectedGpuOption = vm.GpuOptions.Single(o => o.Key == _weakGpu.Key);

        Assert.True(vm.HasPerformanceRestartHint);
    }

    [Fact]
    public void RestartHint_FollowsTheServiceFlags_AndRecommendationOnlyInAuto()
    {
        var service = ReadyService(_gpu);
        var vm = Create(service);

        service.Update(s => s with { RestartRecommended = true });
        Assert.Equal(AdminViewModel.PerformanceRestartRecommendedText, vm.PerformanceRestartHint);

        vm.IsAcceleratorCpu = true;   // empfohlene Messwerte sind für „Nur CPU“ ohne Bedeutung, aber die Änderung selbst wirkt erst nach dem Neustart
        Assert.Equal(AdminViewModel.PerformanceRestartText, vm.PerformanceRestartHint);

        service.Update(s => s with { RestartRecommended = false, RestartRequired = true });
        Assert.True(vm.HasPerformanceRestartHint);
    }

    [Fact]
    public void RestartHint_ForGpuMode_FollowsTheRecommendationAfterTheBackgroundCheck()
    {
        _store.Current.Performance.Accelerator = AcceleratorMode.Gpu;
        var service = new FakeAccelerationService(FakeAccelerationService.Ready(AccelDevices.WithCpu(_gpu), AcceleratorMode.Gpu));
        var vm = Create(service);
        Assert.False(vm.HasPerformanceRestartHint);

        // Die Grafikkarte wurde im Hintergrund geprüft: ab dem nächsten Start wird sie genutzt.
        service.Update(s => s with { RestartRecommended = true });

        Assert.Equal(AdminViewModel.PerformanceRestartRecommendedText, vm.PerformanceRestartHint);
    }

    [Fact]
    public void SourceText_ForGpuMode_NamesTheBackgroundCheck()
    {
        _store.Current.Performance.Accelerator = AcceleratorMode.Gpu;
        var service = new FakeAccelerationService(FakeAccelerationService.Ready(AccelDevices.WithCpu(_gpu), AcceleratorMode.Gpu));
        var vm = Create(service);
        Assert.Null(vm.AccelerationSourceText);

        service.Update(s => s with { BenchmarkRunning = true, BenchmarkIsBackground = true });

        Assert.Contains("geprüft", vm.AccelerationSourceText);
    }

    [Fact]
    public async Task RestartHint_AfterSavingAChangedMode_StaysUntilRestart()
    {
        var service = ReadyService(_gpu);
        var vm = Create(service);
        vm.IsAcceleratorCpu = true;

        await vm.ApplyCommand.ExecuteAsync(null);
        // Der echte Dienst meldet nach dem Speichern RestartRequired.
        service.Update(s => s with { RestartRequired = true });

        Assert.True(vm.HasPerformanceRestartHint);
    }

    [Fact]
    public void RestartHint_IsNotShownForPreferHighPerformanceGpu_AndNotBeforePlanReady()
    {
        var vm = Create(new FakeAccelerationService());
        Assert.False(vm.HasPerformanceRestartHint);   // Initialzustand (Mode = Auto) wird nicht verglichen

        vm.PreferHighPerformanceGpu = false;
        Assert.False(vm.HasPerformanceRestartHint);   // hat einen eigenen Hinweis in der Karte
    }

    // ------------------------------------------------------------------ Messung

    [Fact]
    public async Task Benchmark_ReportsProgress_ShowsTable_AndMarksTheFastestCell()
    {
        var service = ReadyService(_gpu);
        service.BenchmarkGate = new TaskCompletionSource<BenchmarkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(service);
        Assert.True(vm.StartBenchmarkCommand.CanExecute(null));
        Assert.True(vm.HasNoBenchmarkTable);

        var running = vm.StartBenchmarkCommand.ExecuteAsync(null);

        Assert.True(vm.IsBenchmarkRunning);
        Assert.False(vm.IsBenchmarkIdle);
        Assert.False(vm.StartBenchmarkCommand.CanExecute(null));
        Assert.True(vm.CancelBenchmarkCommand.CanExecute(null));

        service.LastBenchmarkProgress!.Report(new BenchmarkProgress(2, 8, "Testkarte Stark", ModelId.YuNet));
        Assert.Equal(25, vm.BenchmarkPercent);
        Assert.Equal("Messe Testkarte Stark – Gesichtserkennung (2/8)", vm.BenchmarkProgressText);
        service.LastBenchmarkProgress!.Report(new BenchmarkProgress(4, 8, "Testkarte Stark", null));
        Assert.Equal(50, vm.BenchmarkPercent);
        Assert.Equal("Messe Testkarte Stark (4/8)", vm.BenchmarkProgressText);

        var result = AccelDevices.Result((AcceleratorDevice.CpuDevice, 10.04, 8.5, 6.0, null), (_gpu, 5.66, 9.0, 3.0, 4.0));
        service.Update(s => s with { LastBenchmark = result });
        service.BenchmarkGate.SetResult(result);
        await running;

        Assert.False(vm.IsBenchmarkRunning);
        Assert.True(vm.StartBenchmarkCommand.CanExecute(null));
        Assert.Equal("Die Messung ist abgeschlossen.", vm.BenchmarkStatusText);
        Assert.True(vm.HasBenchmarkTable);
        Assert.Equal(["CPU", "Testkarte Stark"], vm.BenchmarkColumns);
        Assert.Equal(
        [
            "Personenerkennung (Zielerfassung)", "Gesichtserkennung", "Handflächen", "Handpunkte",
        ], vm.BenchmarkRows.Select(r => r.Model));

        var yolox = vm.BenchmarkRows[0].Cells;
        Assert.Equal("10,0 ms", yolox[0].Text);
        Assert.Equal("5,7 ms", yolox[1].Text);
        Assert.False(yolox[0].IsFastest);
        Assert.True(yolox[1].IsFastest);
        var yunet = vm.BenchmarkRows[1].Cells;
        Assert.True(yunet[0].IsFastest);   // CPU 8,5 < GPU 9,0
        // Fehlgeschlagen: Strich mit Grund im Tooltip, nie „schnellster“; ein einzelner gültiger Wert wird nicht hervorgehoben.
        var landmark = vm.BenchmarkRows[3].Cells;
        Assert.Equal("–", landmark[0].Text);
        Assert.True(landmark[0].IsError);
        Assert.Equal("Testfehler", landmark[0].ToolTip);
        Assert.False(landmark[1].IsFastest);
        Assert.StartsWith("Gemessen am ", vm.BenchmarkTimeText);
    }

    [Fact]
    public void FormatMs_UsesGermanDecimalCommaIndependentOfCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("5,7 ms", AccelerationDisplay.FormatMs(5.66));
            Assert.Equal("8 GB", AccelerationDisplay.FormatMemory(8192));
            Assert.Equal("1,5 GB", AccelerationDisplay.FormatMemory(1536));
            Assert.Equal("512 MB", AccelerationDisplay.FormatMemory(512));
            Assert.Equal(string.Empty, AccelerationDisplay.FormatMemory(0));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ModelNames_AreGermanDistinctAndCoverEveryModel()
    {
        var names = Enum.GetValues<ModelId>().Select(AccelerationDisplay.ModelName).ToList();

        Assert.Equal(["Personenerkennung (Zielerfassung)", "Gesichtserkennung", "Handflächen", "Handpunkte"], names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public async Task Benchmark_ShowsTheLastMeasurementAlreadyWhenOpened()
    {
        var cached = AccelDevices.Result((AcceleratorDevice.CpuDevice, 10, 10, 10, 10), (_gpu, 4, 4, 4, 4));
        var service = new FakeAccelerationService(FakeAccelerationService.Ready(AccelDevices.WithCpu(_gpu)) with { LastBenchmark = cached });
        var vm = Create(service);
        await Task.CompletedTask;

        Assert.True(vm.HasBenchmarkTable);
        Assert.Equal(4, vm.BenchmarkRows.Count);
        Assert.All(vm.BenchmarkRows, r => Assert.True(r.Cells[1].IsFastest));
    }

    [Fact]
    public async Task Benchmark_Cancel_PassesTheTokenAndShowsCancelledState()
    {
        var service = ReadyService(_gpu);
        service.BenchmarkGate = new TaskCompletionSource<BenchmarkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(service);
        var running = vm.StartBenchmarkCommand.ExecuteAsync(null);
        Assert.False(service.LastBenchmarkToken.IsCancellationRequested);

        vm.CancelBenchmarkCommand.Execute(null);
        await running;

        Assert.True(service.LastBenchmarkToken.IsCancellationRequested);
        Assert.False(vm.IsBenchmarkRunning);
        Assert.Equal("Die Messung wurde abgebrochen.", vm.BenchmarkStatusText);
        Assert.True(vm.HasNoBenchmarkTable);
        Assert.True(vm.StartBenchmarkCommand.CanExecute(null));
        Assert.False(vm.CancelBenchmarkCommand.CanExecute(null));
    }

    [Fact]
    public async Task Benchmark_WithoutResult_ReportsIt_AndFailureIsCaught()
    {
        var service = ReadyService(_gpu);
        var vm = Create(service);

        await vm.StartBenchmarkCommand.ExecuteAsync(null);
        Assert.Equal("Die Messung hat kein Ergebnis geliefert.", vm.BenchmarkStatusText);
        Assert.False(vm.IsBenchmarkRunning);

        var failing = new ThrowingService(new InvalidOperationException("Testausfall"));
        var vm2 = Create(failing);
        await vm2.StartBenchmarkCommand.ExecuteAsync(null);
        Assert.Contains("Testausfall", vm2.BenchmarkStatusText);
        Assert.False(vm2.IsBenchmarkRunning);
    }

    [Fact]
    public void BackgroundMeasurement_IsAnnounced_AndDoesNotBlockStarting()
    {
        var service = ReadyService(_gpu);
        var vm = Create(service);

        service.Update(s => s with { BenchmarkRunning = true, BenchmarkIsBackground = true });

        Assert.True(vm.HasBenchmarkBackgroundText);
        Assert.True(vm.StartBenchmarkCommand.CanExecute(null));   // ersetzt die Hintergrundmessung
        Assert.False(vm.InstallProvidersCommand.CanExecute(null));   // aber keine Installation währenddessen
    }

    // ------------------------------------------------------------------ Installation

    [Fact]
    public async Task Install_Success_ShowsProgressAndResult_ReloadsProviders_AndAsksForRestart()
    {
        var service = ReadyService(_gpu);
        service.InstallGate = new TaskCompletionSource<ProviderInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Providers = () => [new ProviderState("Testanbieter A", "pkg.a", "NotReady", false)];
        var vm = Create(service);
        await vm.InitializeAsync();
        Assert.Equal("Noch nicht installiert", vm.ProviderRows.Single().Status);

        var running = vm.InstallProvidersCommand.ExecuteAsync(null);
        Assert.True(vm.IsInstalling);
        Assert.False(vm.InstallProvidersCommand.CanExecute(null));
        Assert.False(vm.StartBenchmarkCommand.CanExecute(null));   // gegenseitig gesperrt
        Assert.True(vm.CancelInstallCommand.CanExecute(null));

        service.LastInstallProgress!.Report(0.4);
        Assert.Equal(40, vm.InstallPercent);

        service.Providers = () => [new ProviderState("Testanbieter A", "pkg.a", "Ready", true)];
        service.InstallGate.SetResult(new ProviderInstallResult(true, "Die Ausführungsanbieter wurden installiert.", ["Testanbieter A"]));
        await running;

        Assert.False(vm.IsInstalling);
        Assert.Equal("Die Ausführungsanbieter wurden installiert.", vm.InstallStatusText);
        Assert.Equal(100, vm.InstallPercent);
        Assert.Equal("Installiert", vm.ProviderRows.Single().Status);
        Assert.True(vm.ProviderRows.Single().IsReady);
        Assert.Equal(AdminViewModel.PerformanceInstalledRestartText, vm.PerformanceRestartHint);
        Assert.True(vm.StartBenchmarkCommand.CanExecute(null));
    }

    [Fact]
    public async Task Install_Failure_ShowsTheMessage_AndNeedsNoRestart()
    {
        var service = ReadyService(_gpu);
        service.InstallOutcome = new ProviderInstallResult(false, "Die Installation ist fehlgeschlagen: kein Netz.", []);
        var vm = Create(service);

        await vm.InstallProvidersCommand.ExecuteAsync(null);

        Assert.Equal("Die Installation ist fehlgeschlagen: kein Netz.", vm.InstallStatusText);
        Assert.False(vm.HasPerformanceRestartHint);
        Assert.False(vm.IsInstalling);
        Assert.True(vm.InstallProvidersCommand.CanExecute(null));
    }

    [Fact]
    public async Task Install_NothingToInstall_NeedsNoRestart()
    {
        var service = ReadyService(_gpu);
        service.InstallOutcome = new ProviderInstallResult(true, "Es gibt keine weiteren Ausführungsanbieter für diesen Computer.", []);
        var vm = Create(service);

        await vm.InstallProvidersCommand.ExecuteAsync(null);

        Assert.Contains("keine weiteren", vm.InstallStatusText);
        Assert.False(vm.HasPerformanceRestartHint);
    }

    [Fact]
    public async Task Install_Cancel_PassesTheTokenAndShowsCancelled()
    {
        var service = ReadyService(_gpu);
        service.InstallGate = new TaskCompletionSource<ProviderInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(service);
        var running = vm.InstallProvidersCommand.ExecuteAsync(null);

        vm.CancelInstallCommand.Execute(null);
        await running;

        Assert.True(service.LastInstallToken.IsCancellationRequested);
        Assert.Equal("Die Installation wurde abgebrochen.", vm.InstallStatusText);
        Assert.False(vm.IsInstalling);
        Assert.False(vm.HasPerformanceRestartHint);
    }

    [Fact]
    public async Task Install_IsBlockedWhileMeasuring_AndTheOtherWayRound()
    {
        var service = ReadyService(_gpu);
        service.BenchmarkGate = new TaskCompletionSource<BenchmarkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(service);

        var running = vm.StartBenchmarkCommand.ExecuteAsync(null);
        Assert.False(vm.InstallProvidersCommand.CanExecute(null));
        vm.CancelBenchmarkCommand.Execute(null);
        await running;

        Assert.True(vm.InstallProvidersCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------ Anbieterliste

    [Fact]
    public async Task Providers_AreQueriedOffTheCallingThread()
    {
        var service = ReadyService(_gpu);
        service.Providers = () =>
        [
            new ProviderState("Testanbieter A", "pkg.a", "Ready", true),
            new ProviderState("Testanbieter B", "pkg.b", "NotPresent", false),
        ];
        var vm = Create(service);

        // Von einem eigenen (Nicht-Pool-)Thread aufgerufen, der den „UI-Thread“ vertritt.
        var callerThread = 0;
        await Task.Factory.StartNew(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            return vm.InitializeAsync();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        Assert.NotEqual(callerThread, service.ProviderThreadId);
        Assert.True(service.ProviderOnThreadPool);
        Assert.Equal(["Testanbieter A", "Testanbieter B"], vm.ProviderRows.Select(r => r.Name));
        Assert.Equal(["Installiert", "Nicht vorhanden"], vm.ProviderRows.Select(r => r.Status));
        Assert.False(vm.HasProviderStatusText);
    }

    [Fact]
    public async Task Providers_FailureShowsUnknownStatus()
    {
        var service = ReadyService(_gpu);
        service.Providers = () => throw new InvalidOperationException("Testfehler");
        var vm = Create(service);

        await vm.InitializeAsync();

        Assert.Equal("Status unbekannt.", vm.ProviderStatusText);
        Assert.Empty(vm.ProviderRows);
    }

    [Fact]
    public async Task Providers_HangingQuery_TimesOutToUnknownStatus_WithoutBlocking()
    {
        var time = new FakeTimeProvider();
        var release = new ManualResetEventSlim();
        var service = ReadyService(_gpu);
        service.Providers = () =>
        {
            release.Wait(TimeSpan.FromSeconds(30));
            return [];
        };
        var vm = Create(service, time);

        var init = vm.InitializeAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.ProviderThreadId == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        time.Advance(AdminViewModel.ProviderStatesTimeout + TimeSpan.FromSeconds(1));
        await init.WaitAsync(TimeSpan.FromSeconds(10));
        release.Set();

        Assert.Equal("Status unbekannt.", vm.ProviderStatusText);
    }

    [Fact]
    public async Task Providers_EmptyListSaysSo()
    {
        var vm = Create(ReadyService(_gpu));

        await vm.InitializeAsync();

        Assert.True(vm.HasProviderStatusText);
        Assert.Contains("keine zusätzlichen Beschleuniger", vm.ProviderStatusText);
    }

    // ------------------------------------------------------------------ Ohne Dienst

    [Fact]
    public void WithoutService_TheTabStillWorks_ButSaysUnavailable_AndButtonsAreDisabled()
    {
        var vm = Create(null);

        Assert.True(vm.PerformanceServiceUnavailable);
        Assert.Contains("nicht zur Verfügung", vm.AccelerationPendingText);
        Assert.False(vm.StartBenchmarkCommand.CanExecute(null));
        Assert.False(vm.InstallProvidersCommand.CanExecute(null));
        Assert.Single(vm.GpuOptions);

        vm.IsAcceleratorCpu = true;
        Assert.Equal(AcceleratorMode.Cpu, vm.Draft.Performance.Accelerator);
        Assert.Equal(AdminViewModel.PerformanceRestartText, vm.PerformanceRestartHint);   // Entwurf ≠ gespeichert
    }

    // ------------------------------------------------------------------ Standardwerte

    [Fact]
    public async Task ResetToDefaults_RestoresPerformanceDefaults_AndRebuildsTheSelection()
    {
        _store.Current.Performance.Accelerator = AcceleratorMode.Gpu;
        _store.Current.Performance.GpuDeviceKey = "abwesende-testkarte";
        _store.Current.Performance.PreferHighPerformanceGpu = false;
        var vm = Create(ReadyService(_gpu));
        Assert.Equal(AdminViewModel.MissingGpuOptionText, vm.SelectedGpuOption!.Display);
        _ui.ConfirmResult = true;

        await vm.ResetToDefaultsAsync();

        Assert.Equal(AcceleratorMode.Auto, vm.Draft.Performance.Accelerator);
        Assert.Null(vm.Draft.Performance.GpuDeviceKey);
        Assert.True(vm.Draft.Performance.PreferHighPerformanceGpu);
        Assert.True(vm.IsAcceleratorAuto);
        Assert.True(vm.PreferHighPerformanceGpu);
        Assert.Null(vm.SelectedGpuOption!.Key);
        Assert.Equal(2, vm.GpuOptions.Count);   // die fehlende Karte verschwindet mit der Auswahl
    }

    // ------------------------------------------------------------------ Threading

    // Mit dem Warteschlangen-Kontext als aktuellem Kontext dürfen Tests nicht per await warten (die Fortsetzung würde dorthin gepostet).
    private static void WaitFor(Task task) => Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "Aufgabe wurde nicht rechtzeitig fertig.");

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback, object?)> _queue = new();
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
            {
                _queue.Enqueue((d, state));
                PostCount++;
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("Send ist nicht erlaubt (Deadlock-Gefahr).");

        public void Pump()
        {
            while (true)
            {
                (SendOrPostCallback, object?) item;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                        return;
                    item = _queue.Dequeue();
                }
                item.Item1(item.Item2);
            }
        }
    }

    [Fact]
    public void StateChanged_FromABackgroundThread_OnlyPosts_AndTheViewUpdatesOnThePump()
    {
        var context = new QueueContext();
        var service = new FakeAccelerationService();
        var vm = Create(service, context: context);
        var uiThread = Environment.CurrentManagedThreadId;
        var changedOn = new List<int>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, _) =>
        {
            lock (changedOn)
                changedOn.Add(Environment.CurrentManagedThreadId);
        };
        Assert.True(vm.HasAccelerationPendingText);

        // Der Dienst feuert von einem Hintergrundthread (so auch aus dem Run-Lock eines Detektors): Der Aufruf kehrt zurück, ohne zu warten.
        var raised = Task.Run(() => service.Update(_ => FakeAccelerationService.Ready(AccelDevices.WithCpu(_gpu))));
        WaitFor(raised);

        Assert.Equal(1, context.PostCount);
        Assert.True(vm.HasAccelerationPendingText);   // noch nichts passiert: nur gepostet
        lock (changedOn)
            Assert.Empty(changedOn);

        context.Pump();

        Assert.False(vm.HasAccelerationPendingText);
        Assert.Equal(4, vm.AccelerationStatusLines.Count);
        lock (changedOn)
        {
            Assert.NotEmpty(changedOn);
            Assert.All(changedOn, id => Assert.Equal(uiThread, id));
        }
    }

    [Fact]
    public void StateChanged_ManyBackgroundRaises_DoNotDeadlock()
    {
        var context = new QueueContext();
        var service = ReadyService(_gpu);
        var vm = Create(service, context: context);

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
                service.Raise();
        })).ToArray();

        WaitFor(Task.WhenAll(tasks));
        context.Pump();
        Assert.Equal(4, vm.AccelerationStatusLines.Count);
    }

    [Fact]
    public void Progress_FromABackgroundThread_IsPostedToTheUiContext()
    {
        var context = new QueueContext();
        var service = ReadyService(_gpu);
        service.BenchmarkGate = new TaskCompletionSource<BenchmarkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(service, context: context);
        var running = vm.StartBenchmarkCommand.ExecuteAsync(null);
        context.Pump();

        WaitFor(Task.Run(() => service.LastBenchmarkProgress!.Report(new BenchmarkProgress(1, 4, "Testkarte Stark", null))));
        Assert.Equal(0, vm.BenchmarkPercent);   // noch nicht auf dem UI-Kontext

        context.Pump();
        Assert.Equal(25, vm.BenchmarkPercent);

        service.BenchmarkGate.SetResult(new BenchmarkResult(DateTimeOffset.UtcNow, []));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!running.IsCompleted && DateTime.UtcNow < deadline)
        {
            context.Pump();
            Thread.Sleep(10);
        }
        context.Pump();
        Assert.True(running.IsCompleted);
    }

    // ------------------------------------------------------------------ Schließen

    [Fact]
    public async Task Dispose_CancelsARunningMeasurement_AndUnsubscribes_WithoutBlocking()
    {
        var service = ReadyService(_gpu);
        service.BenchmarkGate = new TaskCompletionSource<BenchmarkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(service);
        Assert.Equal(1, service.SubscriberCount);
        var running = vm.StartBenchmarkCommand.ExecuteAsync(null);
        Assert.False(service.LastBenchmarkToken.IsCancellationRequested);

        vm.Dispose();

        Assert.True(service.LastBenchmarkToken.IsCancellationRequested);
        Assert.Equal(0, service.SubscriberCount);
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        service.Raise();   // nach dem Schließen wirkungslos, wirft nicht
    }

    [Fact]
    public async Task Dispose_CancelsARunningInstallation()
    {
        var service = ReadyService(_gpu);
        service.InstallGate = new TaskCompletionSource<ProviderInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(service);
        var running = vm.InstallProvidersCommand.ExecuteAsync(null);

        vm.Dispose();

        Assert.True(service.LastInstallToken.IsCancellationRequested);
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StatusRefresh_DoesNotHideTheSavedMessage()
    {
        var service = ReadyService(_gpu);
        var vm = Create(service);
        vm.IsAcceleratorCpu = true;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.HasSaveStatus);

        service.Update(s => s with { RestartRequired = true });

        Assert.True(vm.HasSaveStatus);
        Assert.False(vm.HasUnsavedChanges);
    }

    /// <summary>Dienst, dessen Messung wirft (der echte Dienst wirft nie; die Ansicht bleibt trotzdem stabil).</summary>
    private sealed class ThrowingService(Exception failure) : IAccelerationService
    {
        public AccelerationState State { get; } = FakeAccelerationService.Ready(AccelDevices.WithCpu());
        public event EventHandler? StateChanged { add { } remove { } }
        public Task<BenchmarkResult> RunBenchmarkAsync(IProgress<BenchmarkProgress>? progress = null, CancellationToken ct = default) => throw failure;
        public Task<ProviderInstallResult> InstallVendorProvidersAsync(IProgress<double>? progress = null, CancellationToken ct = default) => throw failure;
        public IReadOnlyList<ProviderState> GetProviderStates() => throw failure;
        public AcceleratorDevice? GetDevice(ModelId model) => null;
        public void ReportFallback(ModelId model, AcceleratorDevice device, string reason, bool atRuntime) { }
        public Task StartAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
