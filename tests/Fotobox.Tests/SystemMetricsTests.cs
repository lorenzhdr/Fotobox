using System.Reflection;
using Fotobox.Services.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Fotobox.Tests;

/// <summary>Reine Auswertungslogik der Systemwerte (Instanznamen, Aggregation, Klemmen, Adapterauswahl) ohne Hardware.</summary>
public sealed class SystemMetricsTests
{
    private const string Name1 = "pid_1234_luid_0x00000000_0x0000E5AB_phys_0_eng_0_engtype_3D";

    private static GraphicsAdapterInfo Adapter(ulong luid, uint vendor, uint device, string? name, bool render = true, bool display = true,
        bool software = false, bool computeOnly = false) =>
        new(luid, vendor, device, name, render, display, software, computeOnly);

    // ---- Instanznamen -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ParseEngineInstance_ReadsAllParts()
    {
        Assert.True(SystemMetricsLogic.TryParseEngineInstance(Name1, out var id));
        Assert.Equal(1234, id.ProcessId);
        Assert.Equal(0x0000E5ABUL, id.Luid);
        Assert.Equal(0, id.Physical);
        Assert.Equal(0, id.Engine);
        Assert.Equal("3D", id.EngineType);
    }

    [Fact]
    public void ParseEngineInstance_HighAndLowLuidPartsAndEngineTypeWithUnderscore()
    {
        Assert.True(SystemMetricsLogic.TryParseEngineInstance("pid_4_luid_0x00000001_0xABCDEF01_phys_2_eng_13_engtype_Video_Codec", out var id));
        Assert.Equal((1UL << 32) | 0xABCDEF01UL, id.Luid);
        Assert.Equal(2, id.Physical);
        Assert.Equal(13, id.Engine);
        Assert.Equal("Video_Codec", id.EngineType);
    }

    [Fact]
    public void ParseEngineInstance_IsCaseInsensitiveForHexDigits()
    {
        Assert.True(SystemMetricsLogic.TryParseEngineInstance("pid_1_luid_0x00000000_0x0000e5ab_phys_0_eng_1_engtype_Copy", out var id));
        Assert.Equal(0xE5ABUL, id.Luid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("_Total")]
    [InlineData("garbage")]
    [InlineData("pid_x_luid_0x0_0x1_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x00000000_0x0000E5AB_phys_0_eng_0")]
    [InlineData("pid_1_luid_0xZZZZ_0x0000E5AB_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x000000000_0x0000E5AB_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_99999999999_luid_0x00000000_0x0000E5AB_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x00000000_0x0000E5AB_phys_0_eng_0_engtype_")]
    public void ParseEngineInstance_RejectsGarbage(string? name)
    {
        Assert.False(SystemMetricsLogic.TryParseEngineInstance(name, out var id));
        Assert.Equal(default, id);
    }

    [Fact]
    public void ParseAdapterInstance_ReadsLuidAndPhysicalAdapter()
    {
        Assert.True(SystemMetricsLogic.TryParseAdapterInstance("luid_0x00000000_0x0000E5AB_phys_0", out var luid, out var phys));
        Assert.Equal(0xE5ABUL, luid);
        Assert.Equal(0, phys);
        Assert.False(SystemMetricsLogic.TryParseAdapterInstance("luid_0x00000000_0x0000E5AB", out _, out _));
        Assert.False(SystemMetricsLogic.TryParseAdapterInstance(null, out _, out _));
        Assert.False(SystemMetricsLogic.TryParseAdapterInstance("Müll", out _, out _));
    }

    // ---- Aggregation ------------------------------------------------------------------------------------------------------

    [Fact]
    public void AggregateEngineUtilization_SumsProcessesPerEngine_AndTakesTheMaximumEngine()
    {
        var result = SystemMetricsLogic.AggregateEngineUtilization(
        [
            ("pid_1_luid_0x00000000_0x0000000A_phys_0_eng_0_engtype_3D", 10.0),
            ("pid_2_luid_0x00000000_0x0000000A_phys_0_eng_0_engtype_3D", 25.0),   // Engine 0 gesamt: 35
            ("pid_1_luid_0x00000000_0x0000000A_phys_0_eng_1_engtype_Copy", 20.0), // Engine 1 gesamt: 20
        ]);

        Assert.Single(result);
        Assert.Equal(35.0, result[0xA], 6);
    }

    [Fact]
    public void AggregateEngineUtilization_SeparatesAdaptersByLuid()
    {
        var result = SystemMetricsLogic.AggregateEngineUtilization(
        [
            ("pid_1_luid_0x00000000_0x0000000A_phys_0_eng_0_engtype_3D", 40.0),
            ("pid_1_luid_0x00000000_0x0000000B_phys_0_eng_0_engtype_Compute", 7.5),
            ("pid_2_luid_0x00000000_0x0000000B_phys_0_eng_0_engtype_Compute", 2.5),
        ]);

        Assert.Equal(40.0, result[0xA], 6);
        Assert.Equal(10.0, result[0xB], 6);
    }

    [Fact]
    public void AggregateEngineUtilization_SeparatesPhysicalAdaptersAndEngines_NotSummedAcrossThem()
    {
        var result = SystemMetricsLogic.AggregateEngineUtilization(
        [
            ("pid_1_luid_0x00000000_0x0000000A_phys_0_eng_0_engtype_3D", 30.0),
            ("pid_1_luid_0x00000000_0x0000000A_phys_1_eng_0_engtype_3D", 50.0),
        ]);

        Assert.Equal(50.0, result[0xA], 6);
    }

    [Fact]
    public void AggregateEngineUtilization_ClampsTheSumToZeroToHundred()
    {
        var result = SystemMetricsLogic.AggregateEngineUtilization(
        [
            ("pid_1_luid_0x00000000_0x0000000A_phys_0_eng_0_engtype_3D", 80.0),
            ("pid_2_luid_0x00000000_0x0000000A_phys_0_eng_0_engtype_3D", 70.0),
            ("pid_1_luid_0x00000000_0x0000000B_phys_0_eng_0_engtype_3D", -5.0),
        ]);

        Assert.Equal(100.0, result[0xA], 6);
        Assert.Equal(0.0, result[0xB], 6);
    }

    [Fact]
    public void AggregateEngineUtilization_SkipsGarbageAndNonFiniteValues()
    {
        var result = SystemMetricsLogic.AggregateEngineUtilization(
        [
            ("Müll", 50.0),
            ("", 50.0),
            ("pid_1_luid_0x00000000_0x0000000A_phys_0_eng_0_engtype_3D", double.NaN),
            ("pid_1_luid_0x00000000_0x0000000A_phys_0_eng_1_engtype_3D", double.PositiveInfinity),
            ("pid_1_luid_0x00000000_0x0000000A_phys_0_eng_2_engtype_3D", 12.0),
        ]);

        Assert.Single(result);
        Assert.Equal(12.0, result[0xA], 6);
        Assert.Empty(SystemMetricsLogic.AggregateEngineUtilization([]));
    }

    [Fact]
    public void AggregateAdapterMemory_SumsPerLuid_AndSkipsGarbageAndNegatives()
    {
        var result = SystemMetricsLogic.AggregateAdapterMemory(
        [
            ("luid_0x00000000_0x0000000A_phys_0", 1000.0),
            ("luid_0x00000000_0x0000000A_phys_1", 500.0),
            ("luid_0x00000000_0x0000000B_phys_0", 7.0),
            ("luid_0x00000000_0x0000000B_phys_0", -1.0),
            ("Müll", 99.0),
            ("luid_0x00000000_0x0000000C_phys_0", double.NaN),
        ]);

        Assert.Equal(1500.0, result[0xA], 6);
        Assert.Equal(7.0, result[0xB], 6);
        Assert.False(result.ContainsKey(0xC));
    }

    // ---- Klemmen und Plausibilität ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-3.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(42.5, 42.5)]
    [InlineData(100.0, 100.0)]
    [InlineData(187.0, 100.0)]
    public void ClampPercent_ClampsToZeroToHundred(double value, double expected) =>
        Assert.Equal(expected, SystemMetricsLogic.ClampPercent(value));

    [Fact]
    public void ClampPercent_NonFiniteIsNull()
    {
        Assert.Null(SystemMetricsLogic.ClampPercent(double.NaN));
        Assert.Null(SystemMetricsLogic.ClampPercent(double.PositiveInfinity));
    }

    [Fact]
    public void ComputeCpuClockMhz_ScalesBaseByPerformance_AndAllowsTurbo()
    {
        Assert.Equal(1500.0, SystemMetricsLogic.ComputeCpuClockMhz(3000, 50));
        Assert.Equal(4500.0, SystemMetricsLogic.ComputeCpuClockMhz(3000, 150));
        Assert.Null(SystemMetricsLogic.ComputeCpuClockMhz(0, 100));
        Assert.Null(SystemMetricsLogic.ComputeCpuClockMhz(3000, 0));
        Assert.Null(SystemMetricsLogic.ComputeCpuClockMhz(double.NaN, 100));
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.9, false)]
    [InlineData(1.0, true)]
    [InlineData(65.0, true)]
    [InlineData(130.0, true)]
    [InlineData(130.1, false)]
    [InlineData(-5.0, false)]
    [InlineData(double.NaN, false)]
    public void PlausibleTemperatureC_AcceptsOnlyOneToHundredThirty(double value, bool plausible) =>
        Assert.Equal(plausible, SystemMetricsLogic.PlausibleTemperatureC(value).HasValue);

    [Fact]
    public void HighestZoneCelsius_ConvertsKelvin_TakesTheHighestPlausibleZone()
    {
        // 300,15 K = 27 °C, 330,15 K = 57 °C, 0 K und 1000 K sind unplausibel.
        Assert.Equal(57.0, SystemMetricsLogic.HighestZoneCelsius([300.15, 330.15, 0, 1000])!.Value, 6);
        Assert.Null(SystemMetricsLogic.HighestZoneCelsius([]));
        Assert.Null(SystemMetricsLogic.HighestZoneCelsius([0, 5, 1000, double.NaN]));
    }

    [Fact]
    public void PlausibleClockMhzFromHz_RequiresPositiveFinite()
    {
        Assert.Equal(1800.0, SystemMetricsLogic.PlausibleClockMhzFromHz(1_800_000_000)!.Value, 6);
        Assert.Null(SystemMetricsLogic.PlausibleClockMhzFromHz(0));
        Assert.Null(SystemMetricsLogic.PlausibleClockMhzFromHz(double.NaN));
    }

    [Fact]
    public void ComputeGpuMemory_DiscreteCard_UsesDedicatedAgainstLocal()
    {
        const ulong eightGiB = 8UL * 1024 * 1024 * 1024;
        var (used, total) = SystemMetricsLogic.ComputeGpuMemory(2000, 5000, eightGiB, eightGiB, 16UL * 1024 * 1024 * 1024);
        Assert.Equal(2000.0, used);
        Assert.Equal((double)eightGiB, total);
    }

    [Fact]
    public void ComputeGpuMemory_IntegratedCard_SumsDedicatedAndSharedAgainstLocalPlusNonLocal()
    {
        var (used, total) = SystemMetricsLogic.ComputeGpuMemory(100, 400, 0, 128UL * 1024 * 1024, 1024UL * 1024 * 1024);
        Assert.Equal(500.0, used);
        Assert.Equal(128.0 * 1024 * 1024 + 1024.0 * 1024 * 1024, total);
    }

    [Fact]
    public void ComputeGpuMemory_UnifiedMemoryWithLargeLocalSegment_CountsSharedUsageToo()
    {
        // Gemeinsamer Speicher kann als große „lokale“ Größe gemeldet werden, ohne dediziertes Video-RAM.
        const ulong sixteenGiB = 16UL * 1024 * 1024 * 1024;
        var (used, total) = SystemMetricsLogic.ComputeGpuMemory(0, 1000, 0, sixteenGiB, 0);
        Assert.Equal(1000.0, used);
        Assert.Equal((double)sixteenGiB, total);
    }

    [Fact]
    public void ComputeGpuMemory_UnknownSizeOrMissingCounters_IsNull()
    {
        var (used, total) = SystemMetricsLogic.ComputeGpuMemory(null, null, 0, 0, 0);
        Assert.Null(used);
        Assert.Null(total);

        var (used2, total2) = SystemMetricsLogic.ComputeGpuMemory(10, null, 0, 0, 0);
        Assert.Equal(10.0, used2);
        Assert.Null(total2);
    }

    // ---- Adapterauswahl ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SelectGpu_WithSelection_MatchesVendorAndDevice()
    {
        var igpu = Adapter(1, 0x8086, 0x1111, "Integrierte Grafik");
        var dgpu = Adapter(2, 0x10DE, 0x2222, "Diskrete Grafik");

        Assert.Same(dgpu, SystemMetricsLogic.SelectGpu([igpu, dgpu], new GpuSelection(0x10DE, 0x2222, null)));
        Assert.Same(igpu, SystemMetricsLogic.SelectGpu([igpu, dgpu], new GpuSelection(0x8086, 0x1111, "egal")));
    }

    [Fact]
    public void SelectGpu_SameVendorAndDeviceTwice_PicksByName_ElseFirst()
    {
        var a = Adapter(1, 0x10DE, 0x2222, "Grafik A");
        var b = Adapter(2, 0x10DE, 0x2222, "Grafik B");

        Assert.Same(b, SystemMetricsLogic.SelectGpu([a, b], new GpuSelection(0x10DE, 0x2222, "grafik b")));
        Assert.Same(a, SystemMetricsLogic.SelectGpu([a, b], new GpuSelection(0x10DE, 0x2222, "Grafik C")));
        Assert.Same(a, SystemMetricsLogic.SelectGpu([a, b], new GpuSelection(0x10DE, 0x2222, null)));
    }

    [Fact]
    public void SelectGpu_WithoutSelectionOrWithoutMatch_TakesFirstRenderAndDisplayAdapter()
    {
        var npu = Adapter(1, 0x4D4F, 0x1, "NPU", render: false, display: false, computeOnly: true);
        var renderOnly = Adapter(2, 0x10DE, 0x3, "Nur Rechnen", render: true, display: false);
        var gpu = Adapter(3, 0x8086, 0x4, "Grafik");

        Assert.Same(gpu, SystemMetricsLogic.SelectGpu([npu, renderOnly, gpu], null));
        Assert.Same(gpu, SystemMetricsLogic.SelectGpu([npu, renderOnly, gpu], new GpuSelection(0xFFFF, 0xFFFF, "unbekannt")));
    }

    [Fact]
    public void SelectGpu_NeverTakesSoftwareAdapters()
    {
        var warp = Adapter(1, 0x1414, 0x8C, "Microsoft Basic Render Driver");
        var flagged = Adapter(2, 0x1234, 0x5678, "Irgendwas", software: true);
        var real = Adapter(3, 0x8086, 0x4, "Grafik");

        // Auch mit ausdrücklicher Auswahl des Software-Adapters nicht.
        Assert.Same(real, SystemMetricsLogic.SelectGpu([warp, flagged, real], new GpuSelection(0x1414, 0x8C, null)));
        Assert.Same(real, SystemMetricsLogic.SelectGpu([warp, flagged, real], new GpuSelection(0x1234, 0x5678, null)));
        Assert.Same(real, SystemMetricsLogic.SelectGpu([warp, flagged, real], null));
        Assert.Null(SystemMetricsLogic.SelectGpu([warp, flagged], null));
    }

    [Fact]
    public void SelectGpu_NoMatches_IsNull()
    {
        Assert.Null(SystemMetricsLogic.SelectGpu([], null));
        Assert.Null(SystemMetricsLogic.SelectGpu([], new GpuSelection(1, 2, "x")));
        var npu = Adapter(1, 0x4D4F, 0x1, "NPU", render: false, display: false, computeOnly: true);
        Assert.Null(SystemMetricsLogic.SelectGpu([npu], null));
    }

    [Fact]
    public void SelectNpu_TakesFirstComputeOnlyAdapter()
    {
        var gpu = Adapter(1, 0x8086, 0x4, "Grafik");
        var npu1 = Adapter(2, 0x4D4F, 0x1, "NPU 1", render: false, display: false, computeOnly: true);
        var npu2 = Adapter(3, 0x4D4F, 0x2, "NPU 2", render: false, display: false, computeOnly: true);

        Assert.Same(npu1, SystemMetricsLogic.SelectNpu([gpu, npu1, npu2]));
    }

    [Fact]
    public void SelectNpu_IgnoresSoftwareAndPlainGpus_AndReturnsNullWithoutNpu()
    {
        var gpu = Adapter(1, 0x8086, 0x4, "Grafik");
        var softCompute = Adapter(2, 0x1414, 0x8C, "Microsoft Basic Render Driver", computeOnly: true);

        Assert.Null(SystemMetricsLogic.SelectNpu([gpu, softCompute]));
        Assert.Null(SystemMetricsLogic.SelectNpu([]));
    }
}

/// <summary>Die Windows-Quelle: ohne Hardware nur der Lebenszyklus; mit Hardware (Category Hardware) echte Messung.</summary>
public sealed class WindowsSystemMetricsSourceTests(ITestOutputHelper output)
{
    [Fact]
    public void Construct_Dispose_AndSampleAfterDispose_NeverThrow()
    {
        var source = new WindowsSystemMetricsSource(); // im Konstruktor nichts Natives
        source.Dispose();
        source.Dispose();

        Assert.Equal(SystemMetricsSnapshot.Empty, source.Sample(null));
    }

    [Fact]
    public void Snapshot_Empty_HasNoValues()
    {
        var empty = SystemMetricsSnapshot.Empty;
        Assert.Null(empty.CpuPercent);
        Assert.Null(empty.GpuName);
        Assert.Null(empty.RamTotalBytes);
        Assert.Equal(new SystemMetricsSnapshot(), empty);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void RealSource_SamplesTwice_CpuAndRamAreAvailable_ReportsEveryField()
    {
        using var source = new WindowsSystemMetricsSource();

        var first = source.Sample(null);
        output.WriteLine("Sample 1: " + Describe(first));
        Assert.Null(first.CpuPercent); // Ratenzähler: der erste Wert fehlt noch
        Assert.NotNull(first.RamTotalBytes);

        Thread.Sleep(1500);
        var second = source.Sample(null);
        output.WriteLine("Sample 2: " + Describe(second));

        Thread.Sleep(1500);
        var third = source.Sample(new GpuSelection(0, 0, second.GpuName));
        output.WriteLine("Sample 3: " + Describe(third));

        Assert.NotNull(second.CpuPercent);
        InRange(second.CpuPercent, 0, 100);
        Assert.NotNull(second.RamUsedBytes);
        Assert.NotNull(second.RamTotalBytes);
        Assert.True(second.RamUsedBytes <= second.RamTotalBytes);
        InRange(second.GpuPercent, 0, 100);
        InRange(second.NpuPercent, 0, 100);
        InRange(second.GpuTemperatureC, 1, 130);
        InRange(second.CpuTemperatureC, 1, 130);
        if (second.GpuClockMhz is { } clock)
            Assert.True(clock > 0);

        var nulls = typeof(SystemMetricsSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetValue(second) is null).Select(p => p.Name);
        output.WriteLine("Null in Sample 2: " + string.Join(", ", nulls));
    }

    private static void InRange(double? value, double min, double max)
    {
        if (value is { } v)
            Assert.InRange(v, min, max);
    }

    private static string Describe(SystemMetricsSnapshot s) =>
        string.Join("; ", typeof(SystemMetricsSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => $"{p.Name}={p.GetValue(s) ?? "null"}"));
}
