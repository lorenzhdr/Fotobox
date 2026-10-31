using System.Globalization;
using Fotobox.Core;
using Fotobox.Services.Detection.Acceleration;
using Fotobox.Services.Diagnostics;
using Fotobox.ViewModels.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Leistungsanzeige, reiner Teil: Formate, Warnstufen, Zeilenaufbau, Adapterwahl aus dem Plan (ohne Uhr und Hardware).</summary>
public sealed class PerformanceOverlayRowsTests
{
    private const double Gb = 1024.0 * 1024 * 1024;

    private static PerformanceOverlayData Data(SystemMetricsSnapshot? system = null, bool settled = true,
        IReadOnlyList<ModelPlacement>? plan = null, bool windowsFace = false, double? cameraFps = null, double? displayFps = null,
        double? targetingRate = null, double? targetingMs = null, double? gestureRate = null, double? gestureMs = null) =>
        new(system, settled, cameraFps, displayFps, targetingRate, targetingMs, gestureRate, gestureMs, plan ?? [], windowsFace);

    private static PerformanceOverlaySettings Only(Action<PerformanceOverlaySettings> enable)
    {
        var s = new PerformanceOverlaySettings
        {
            ShowCpu = false, ShowCpuClock = false, ShowCpuTemperature = false, ShowGpu = false, ShowGpuClock = false,
            ShowGpuTemperature = false, ShowGpuMemory = false, ShowNpu = false, ShowRam = false, ShowCameraFps = false,
            ShowDisplayFps = false, ShowAnalysisRate = false, ShowCycleTime = false, ShowModelDevices = false,
        };
        enable(s);
        return s;
    }

    private static PerformanceOverlayRow Single(PerformanceOverlaySettings s, PerformanceOverlayData d)
    {
        var rows = PerformanceOverlayRows.Build(s, d);
        return Assert.Single(rows);
    }

    // ---- Warnstufen -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, PerformanceWarnLevel.Normal)]
    [InlineData(69, PerformanceWarnLevel.Normal)]
    [InlineData(69.9, PerformanceWarnLevel.Normal)]
    [InlineData(70, PerformanceWarnLevel.Warn)]
    [InlineData(89, PerformanceWarnLevel.Warn)]
    [InlineData(89.9, PerformanceWarnLevel.Warn)]
    [InlineData(90, PerformanceWarnLevel.Critical)]
    [InlineData(100, PerformanceWarnLevel.Critical)]
    public void WarnLevel_Thresholds(double percent, PerformanceWarnLevel expected) =>
        Assert.Equal(expected, PerformanceOverlayRows.WarnLevel(percent));

    // ---- Formate ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Formats_AreGerman()
    {
        Assert.Equal("34 %", PerformanceOverlayRows.FormatPercent(34));
        Assert.Equal("100 %", PerformanceOverlayRows.FormatPercent(100));
        Assert.Equal("3,2 GHz", PerformanceOverlayRows.FormatGhz(3200));
        Assert.Equal("0,8 GHz", PerformanceOverlayRows.FormatGhz(800));
        Assert.Equal("61 °C", PerformanceOverlayRows.FormatCelsius(61));
        Assert.Equal("7,4/16,0 GB", PerformanceOverlayRows.FormatMemory(7.4 * Gb, 16 * Gb));
        Assert.Equal("7,4 GB", PerformanceOverlayRows.FormatMemory(7.4 * Gb, null));
        Assert.Equal("30 FPS", PerformanceOverlayRows.FormatFps(30));
        Assert.Equal("30 FPS", PerformanceOverlayRows.FormatFps(29.6));
        Assert.Equal("12 ms", PerformanceOverlayRows.FormatMs(12));
        Assert.Equal("4,6 ms", PerformanceOverlayRows.FormatMs(4.6));
        Assert.Equal("8,5 Analysen/s", PerformanceOverlayRows.FormatRate(8.5));
    }

    [Fact]
    public void Formats_IgnoreTheCurrentCulture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("3,2 GHz", PerformanceOverlayRows.FormatGhz(3200));
            Assert.Equal("7,4/16,0 GB", PerformanceOverlayRows.FormatMemory(7.4 * Gb, 16 * Gb));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    // ---- Zeilen -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Rows_ShowOnlyTheEnabledValues_InAFixedOrder()
    {
        var all = new PerformanceOverlaySettings();
        var system = new SystemMetricsSnapshot(CpuPercent: 10, RamUsedBytes: Gb, RamTotalBytes: 2 * Gb);

        var rows = PerformanceOverlayRows.Build(all, Data(system));
        Assert.Equal(
            ["CPU", "CPU-Takt", "CPU-Temperatur", "GPU", "GPU-Takt", "GPU-Temperatur", "GPU-Speicher", "NPU", "RAM", "Kamera", "Anzeige",
             "Zielerfassung", "Gesten", "Zielerfassung Zeit", "Gesten Zeit", "Personen", "Gesichter", "Hände", "Handpunkte"],
            rows.Select(r => r.Label).ToArray());

        var cpuOnly = PerformanceOverlayRows.Build(Only(s => s.ShowCpu = true), Data(system));
        Assert.Equal(["CPU"], cpuOnly.Select(r => r.Label).ToArray());

        Assert.Empty(PerformanceOverlayRows.Build(Only(_ => { }), Data(system)));
    }

    [Fact]
    public void Row_Percent_HasBarAndWarnLevel()
    {
        var row = Single(Only(s => s.ShowCpu = true), Data(new SystemMetricsSnapshot(CpuPercent: 91)));

        Assert.Equal(new PerformanceOverlayRow("CPU", "91 %", 0.91, PerformanceWarnLevel.Critical), row);

        Assert.Equal(PerformanceWarnLevel.Warn, Single(Only(s => s.ShowGpu = true), Data(new SystemMetricsSnapshot(GpuPercent: 70))).Level);
        Assert.Equal(PerformanceWarnLevel.Normal, Single(Only(s => s.ShowNpu = true), Data(new SystemMetricsSnapshot(NpuPercent: 3))).Level);
    }

    [Fact]
    public void Row_Memory_ShowsUsedOfTotal_WithBarAndWarnLevel()
    {
        var row = Single(Only(s => s.ShowRam = true), Data(new SystemMetricsSnapshot(RamUsedBytes: 14.4 * Gb, RamTotalBytes: 16 * Gb)));

        Assert.Equal("14,4/16,0 GB", row.Text);
        Assert.Equal(0.9, row.BarFraction!.Value, 3);
        Assert.Equal(PerformanceWarnLevel.Critical, row.Level);

        var gpuMemory = Single(Only(s => s.ShowGpuMemory = true), Data(new SystemMetricsSnapshot(GpuMemoryUsedBytes: 1 * Gb, GpuMemoryTotalBytes: 4 * Gb)));
        Assert.Equal("1,0/4,0 GB", gpuMemory.Text);
        Assert.Equal(PerformanceWarnLevel.Normal, gpuMemory.Level);

        var noTotal = Single(Only(s => s.ShowGpuMemory = true), Data(new SystemMetricsSnapshot(GpuMemoryUsedBytes: 1 * Gb)));
        Assert.Equal("1,0 GB", noTotal.Text);
        Assert.Equal(PerformanceWarnLevel.Normal, noTotal.Level);
    }

    [Fact]
    public void Row_ClockAndTemperature_AreFormatted_AndHaveNoBarOrWarning()
    {
        var system = new SystemMetricsSnapshot(CpuClockMhz: 3200, CpuTemperatureC: 95, GpuClockMhz: 1500, GpuTemperatureC: 61);
        var rows = PerformanceOverlayRows.Build(Only(s => { s.ShowCpuClock = true; s.ShowCpuTemperature = true; s.ShowGpuClock = true; s.ShowGpuTemperature = true; }), Data(system));

        Assert.Equal(["3,2 GHz", "95 °C", "1,5 GHz", "61 °C"], rows.Select(r => r.Text).ToArray());
        Assert.All(rows, r =>
        {
            Assert.Null(r.BarFraction);
            Assert.Equal(PerformanceWarnLevel.Normal, r.Level);   // nur Auslastung und Speicher warnen
        });
    }

    [Fact]
    public void MissingSystemValue_AfterASettledSample_IsNotAvailable_AndKeepsTheBarRow()
    {
        var s = Only(x => { x.ShowCpu = true; x.ShowNpu = true; x.ShowRam = true; x.ShowCpuTemperature = true; });
        var rows = PerformanceOverlayRows.Build(s, Data(new SystemMetricsSnapshot(CpuPercent: 5), settled: true));

        Assert.Equal("5 %", rows[0].Text);
        Assert.Equal(PerformanceOverlayRows.Unavailable, rows[1].Text);   // CPU-Temperatur
        Assert.Equal(PerformanceOverlayRows.Unavailable, rows[2].Text);   // NPU
        Assert.Equal(PerformanceOverlayRows.Unavailable, rows[3].Text);   // RAM
        Assert.Equal(0, rows[2].BarFraction);                              // Balkenzeile behält Höhe
        Assert.Equal(0, rows[3].BarFraction);
        Assert.Null(rows[1].BarFraction);
    }

    [Fact]
    public void MissingSystemValue_InTheFirstSample_IsADash_NotUnavailable()
    {
        var row = Single(Only(s => s.ShowCpu = true), Data(new SystemMetricsSnapshot(), settled: false));

        Assert.Equal(PerformanceOverlayRows.Dash, row.Text);
    }

    [Fact]
    public void NoSnapshot_ShowsADash_ForEverySystemValue_ButAppValuesStay()
    {
        var rows = PerformanceOverlayRows.Build(new PerformanceOverlaySettings(), Data(system: null, cameraFps: 30, displayFps: 60));

        Assert.Equal(PerformanceOverlayRows.Dash, rows.Single(r => r.Label == "CPU").Text);
        Assert.Equal(PerformanceOverlayRows.Dash, rows.Single(r => r.Label == "RAM").Text);
        Assert.Equal(PerformanceOverlayRows.Dash, rows.Single(r => r.Label == "NPU").Text);
        Assert.Equal("30 FPS", rows.Single(r => r.Label == "Kamera").Text);
        Assert.Equal("60 FPS", rows.Single(r => r.Label == "Anzeige").Text);
    }

    [Fact]
    public void AppValues_InactiveFunctionShowsADash_ActiveOnesShowRateAndTime()
    {
        var s = Only(x => { x.ShowAnalysisRate = true; x.ShowCycleTime = true; });

        var inactive = PerformanceOverlayRows.Build(s, Data());
        Assert.All(inactive, r => Assert.Equal(PerformanceOverlayRows.Dash, r.Text));

        var active = PerformanceOverlayRows.Build(s, Data(targetingRate: 8.5, targetingMs: 12, gestureRate: 4, gestureMs: 25.2));
        Assert.Equal(["8,5 Analysen/s", "4,0 Analysen/s", "12 ms", "25 ms"], active.Select(r => r.Text).ToArray());
    }

    [Fact]
    public void ModelDevices_ShowThePlanPerModel_AndMarkFallbacks()
    {
        IReadOnlyList<ModelPlacement> plan =
        [
            new(ModelId.Yolox, "Grafik A", AcceleratorKind.Gpu, "k1", false),
            new(ModelId.YuNet, "CPU", AcceleratorKind.Cpu, "cpu", true),
            new(ModelId.Palm, "CPU", AcceleratorKind.Cpu, "cpu", false),
        ];
        var rows = PerformanceOverlayRows.Build(Only(s => s.ShowModelDevices = true), Data(plan: plan));

        Assert.Equal(
            [("Personen", "Grafik A"), ("Gesichter", "CPU (Rückfall)"), ("Hände", "CPU"), ("Handpunkte", PerformanceOverlayRows.Dash)],
            rows.Select(r => (r.Label, r.Text)).ToArray());
    }

    [Fact]
    public void ModelDevices_WindowsFaceDetector_ReplacesTheYoloxRow()
    {
        IReadOnlyList<ModelPlacement> plan = [new(ModelId.Yolox, "Grafik A", AcceleratorKind.Gpu, "k1", false)];
        var rows = PerformanceOverlayRows.Build(Only(s => s.ShowModelDevices = true), Data(plan: plan, windowsFace: true));

        Assert.DoesNotContain(rows, r => r.Label == "Personen");
        Assert.Equal("Windows-Gesichtserkennung", rows.Single(r => r.Label == "Zielerfassung Gerät").Text);
    }

    // ---- Grafikkarte aus dem Plan -----------------------------------------------------------------------------------------

    private static AccelerationState State(IReadOnlyList<ModelPlacement> plan, IReadOnlyList<AcceleratorDevice> devices) =>
        AccelerationState.Initial with { Plan = plan, Devices = devices, PlanReady = true };

    [Fact]
    public void SelectionFromPlan_TakesIdsAndNameFromTheDeviceList()
    {
        var gpu = new AcceleratorDevice("g1", "Grafik A", AcceleratorKind.Gpu, "Ep", VendorId: 0x1234, DeviceId: 0x5678);
        var state = State(
            [new ModelPlacement(ModelId.Yolox, "CPU", AcceleratorKind.Cpu, "cpu", true), new ModelPlacement(ModelId.Palm, "Grafik A", AcceleratorKind.Gpu, "g1", false)],
            [AcceleratorDevice.CpuDevice, gpu]);

        Assert.Equal(new GpuSelection(0x1234, 0x5678, "Grafik A"), SystemMetricsLogic.SelectionFromPlan(state));
    }

    [Fact]
    public void SelectionFromPlan_UnknownDevice_UsesTheNameOnly()
    {
        var state = State([new ModelPlacement(ModelId.Palm, "Grafik A", AcceleratorKind.Gpu, "weg", false)], [AcceleratorDevice.CpuDevice]);

        Assert.Equal(new GpuSelection(0, 0, "Grafik A"), SystemMetricsLogic.SelectionFromPlan(state));
    }

    [Fact]
    public void SelectionFromPlan_NoGpuInThePlan_IsNull()
    {
        var npu = new AcceleratorDevice("n1", "KI-Einheit", AcceleratorKind.Npu, "Ep", VendorId: 1, DeviceId: 2);
        var state = State(
            [new ModelPlacement(ModelId.Yolox, "CPU", AcceleratorKind.Cpu, "cpu", false), new ModelPlacement(ModelId.Palm, "KI-Einheit", AcceleratorKind.Npu, "n1", false)],
            [AcceleratorDevice.CpuDevice, npu]);

        Assert.Null(SystemMetricsLogic.SelectionFromPlan(state));
        Assert.Null(SystemMetricsLogic.SelectionFromPlan(AccelerationState.Initial));
    }
}
