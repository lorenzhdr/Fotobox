using System.IO;
using Fotobox.Core;
using Fotobox.Services.Detection.Acceleration;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Planung, Geräteschlüssel, GPU-Rangfolge und Cache-Datei (reine Logik, ohne Hardware).</summary>
public sealed class AccelerationPlannerTests
{
    // ------------------------------------------------------------------ Schlüssel

    [Fact]
    public void BuildKey_IsStable_NormalizedAndIgnoresCaseAndWhitespace()
    {
        var a = AcceleratorSelector.BuildKey("DmlExecutionProvider", 0x10DE, 0x2204, "Test   Karte  X");
        var b = AcceleratorSelector.BuildKey("dmlexecutionprovider", 0x10DE, 0x2204, " test karte x ");

        Assert.Equal(a, b);
        Assert.Equal("dmlexecutionprovider|10de|2204|test karte x", a);
    }

    [Fact]
    public void BuildKey_DiffersByProviderVendorDeviceAndDescription()
    {
        var baseKey = AcceleratorSelector.BuildKey("Dml", 1, 2, "Karte");

        Assert.NotEqual(baseKey, AcceleratorSelector.BuildKey("Anderer", 1, 2, "Karte"));
        Assert.NotEqual(baseKey, AcceleratorSelector.BuildKey("Dml", 3, 2, "Karte"));
        Assert.NotEqual(baseKey, AcceleratorSelector.BuildKey("Dml", 1, 4, "Karte"));
        Assert.NotEqual(baseKey, AcceleratorSelector.BuildKey("Dml", 1, 2, "Karte 2"));
    }

    [Fact]
    public void BuildKey_StripsControlCharacters_AndStaysWithinTheSettingsLimit()
    {
        var key = AcceleratorSelector.BuildKey(new string('e', 500), uint.MaxValue, uint.MaxValue, "Karte\u0007\nmit\tSteuerzeichen" + new string('x', 500));

        Assert.DoesNotContain(key, char.IsControl);
        Assert.True(key.Length <= SettingsLimits.GpuDeviceKeyMaxLength, key.Length.ToString());
    }

    [Fact]
    public void BuildKey_HandlesMissingDescription()
    {
        Assert.Equal("dml|0001|0002|", AcceleratorSelector.BuildKey("Dml", 1, 2, null));
    }

    [Theory]
    [InlineData("128 MB", 128)]
    [InlineData("8 GB", 8192)]
    [InlineData("8GB", 8192)]
    [InlineData("1,5 GB", 1536)]
    [InlineData("2048 KB", 2)]
    [InlineData("1073741824", 1024)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("unbekannt", 0)]
    public void ParseMemoryMegabytes_ReadsValuesWithUnits(string? text, long expected)
    {
        Assert.Equal(expected, AcceleratorCatalog.ParseMemoryMegabytes(text));
    }

    // ------------------------------------------------------------------ Rangfolge der Grafikkarten

    [Fact]
    public void StrongestGpu_DiscreteBeatsIntegrated_ThenIndex_ThenMemory()
    {
        var integrated = AccelDevices.Gpu("Integriert", discrete: false, index: 1, memory: 8, device: 1);
        var discreteB = AccelDevices.Gpu("Diskret B", discrete: true, index: 1, memory: 4, device: 2);
        var discreteA = AccelDevices.Gpu("Diskret A", discrete: true, index: 0, memory: 2, device: 3);
        var discreteC = AccelDevices.Gpu("Diskret C", discrete: true, index: 0, memory: 16, device: 4);

        Assert.Same(integrated, AcceleratorSelector.SelectGpu([integrated], null));
        Assert.Same(discreteB, AcceleratorSelector.SelectGpu([integrated, discreteB], null));
        Assert.Same(discreteA, AcceleratorSelector.SelectGpu([integrated, discreteB, discreteA], null)); // kleinerer Windows-Index gewinnt
        Assert.Same(discreteC, AcceleratorSelector.SelectGpu([integrated, discreteB, discreteA, discreteC], null)); // bei Gleichstand mehr Speicher
    }

    [Fact]
    public void StrongestGpu_VendorProviderOfTheSameAdapterWinsTheTie_AndInheritsMetadata()
    {
        var dml = AccelDevices.Gpu("Karte", discrete: true, index: 0, memory: 8, vendor: 5, device: 6);
        var vendorEp = AccelDevices.Gpu("Karte", ep: "TestVendorProvider", vendor: 5, device: 6); // ohne eigene Adapter-Metadaten
        var other = AccelDevices.Gpu("Andere", discrete: true, index: 1, memory: 8, vendor: 7, device: 8);

        Assert.Same(vendorEp, AcceleratorSelector.SelectGpu([dml, vendorEp, other], null));
        Assert.Same(vendorEp, AcceleratorSelector.SelectGpu([other, vendorEp, dml], null));
    }

    [Fact]
    public void SelectGpu_WithKey_ReturnsExactlyThatDevice_OrNullWhenMissing()
    {
        var a = AccelDevices.Gpu("Karte A", discrete: true, index: 0, device: 1);
        var b = AccelDevices.Gpu("Karte B", discrete: false, index: 1, device: 2);
        var devices = AccelDevices.WithCpu(a, b);

        Assert.Same(b, AcceleratorSelector.SelectGpu(devices, b.Key));
        Assert.Null(AcceleratorSelector.SelectGpu(devices, "gibt-es|0000|0000|nicht"));
        Assert.Null(AcceleratorSelector.SelectGpu(AccelDevices.WithCpu(), null));
    }

    [Fact]
    public void SelectNpu_ReturnsTheFirstNpu()
    {
        var npu = AccelDevices.Npu("KI-Einheit");

        Assert.Same(npu, AcceleratorSelector.SelectNpu(AccelDevices.WithCpu(AccelDevices.Gpu("Karte"), npu)));
        Assert.Null(AcceleratorSelector.SelectNpu(AccelDevices.WithCpu(AccelDevices.Gpu("Karte"))));
    }

    // ------------------------------------------------------------------ Modi

    [Fact]
    public void Decide_Cpu_PutsEverythingOnTheCpu()
    {
        var decision = AccelerationPlanner.Decide(AcceleratorMode.Cpu, null, AccelDevices.WithCpu(AccelDevices.Gpu("Karte")), null);

        Assert.All(AccelerationPlanner.AllModels, m => Assert.Null(decision.Plan[m]));
        Assert.Null(decision.Notice);
        Assert.False(decision.NeedsMeasurement);
    }

    [Fact]
    public void Decide_Gpu_UsesTheChosenOrStrongestGpu()
    {
        var weak = AccelDevices.Gpu("Schwach", discrete: false, index: 1, device: 1);
        var strong = AccelDevices.Gpu("Stark", discrete: true, index: 0, device: 2);
        var devices = AccelDevices.WithCpu(weak, strong);

        var auto = AccelerationPlanner.Decide(AcceleratorMode.Gpu, null, devices, null);
        var chosen = AccelerationPlanner.Decide(AcceleratorMode.Gpu, weak.Key, devices, null);

        Assert.All(AccelerationPlanner.AllModels, m => Assert.Same(strong, auto.Plan[m]));
        Assert.All(AccelerationPlanner.AllModels, m => Assert.Same(weak, chosen.Plan[m]));
        Assert.Null(auto.Notice);
    }

    [Fact]
    public void Decide_Gpu_ChosenKeyMissing_FallsBackToCpuWithNotice()
    {
        var decision = AccelerationPlanner.Decide(AcceleratorMode.Gpu, "fehlt|0000|0000|karte", AccelDevices.WithCpu(AccelDevices.Gpu("Karte")), null);

        Assert.All(AccelerationPlanner.AllModels, m => Assert.Null(decision.Plan[m]));
        Assert.Contains("gewählte Grafikkarte", decision.Notice);
    }

    [Fact]
    public void Decide_Gpu_NoGpuAtAll_FallsBackToCpuWithNotice()
    {
        var decision = AccelerationPlanner.Decide(AcceleratorMode.Gpu, null, AccelDevices.WithCpu(), null);

        Assert.All(AccelerationPlanner.AllModels, m => Assert.Null(decision.Plan[m]));
        Assert.NotNull(decision.Notice);
    }

    [Fact]
    public void Decide_Npu_UsesFirstNpu_OrFallsBackWithNotice()
    {
        var npu = AccelDevices.Npu("KI-Einheit");

        var found = AccelerationPlanner.Decide(AcceleratorMode.Npu, null, AccelDevices.WithCpu(AccelDevices.Gpu("Karte"), npu), null);
        var missing = AccelerationPlanner.Decide(AcceleratorMode.Npu, null, AccelDevices.WithCpu(AccelDevices.Gpu("Karte")), null);

        Assert.All(AccelerationPlanner.AllModels, m => Assert.Same(npu, found.Plan[m]));
        Assert.All(AccelerationPlanner.AllModels, m => Assert.Null(missing.Plan[m]));
        Assert.Contains("NPU", missing.Notice);
    }

    [Fact]
    public void Decide_Auto_OnlyCpu_NeedsNoMeasurement()
    {
        var decision = AccelerationPlanner.Decide(AcceleratorMode.Auto, null, AccelDevices.WithCpu(), null);

        Assert.False(decision.NeedsMeasurement);
        Assert.All(AccelerationPlanner.AllModels, m => Assert.Null(decision.Plan[m]));
    }

    [Fact]
    public void Decide_Auto_WithoutCache_StartsOnCpuAndAsksForMeasurement()
    {
        var decision = AccelerationPlanner.Decide(AcceleratorMode.Auto, null, AccelDevices.WithCpu(AccelDevices.Gpu("Karte")), null);

        Assert.True(decision.NeedsMeasurement);
        Assert.False(decision.FromCache);
        Assert.All(AccelerationPlanner.AllModels, m => Assert.Null(decision.Plan[m]));
    }

    [Fact]
    public void Decide_Auto_WithCache_PicksTheFastestDevicePerModel()
    {
        var gpu = AccelDevices.Gpu("Karte", device: 1);
        var npu = AccelDevices.Npu("KI-Einheit");
        var devices = AccelDevices.WithCpu(gpu, npu);
        var cached = AccelDevices.Result(
            (AcceleratorDevice.CpuDevice, 16, 6, 18, 10),
            (gpu, 6, 7, 7, 9),   // schneller bei Yolox/Palm, langsamer bei YuNet, zu knapp bei Landmark
            (npu, 8, 9, 3, 9));

        var decision = AccelerationPlanner.Decide(AcceleratorMode.Auto, null, devices, cached);

        Assert.True(decision.FromCache);
        Assert.False(decision.NeedsMeasurement);
        Assert.Same(gpu, decision.Plan[ModelId.Yolox]);
        Assert.Null(decision.Plan[ModelId.YuNet]);
        Assert.Same(npu, decision.Plan[ModelId.Palm]);
        Assert.Null(decision.Plan[ModelId.Landmark]);
    }

    [Fact]
    public void FromBenchmark_RequiresAtLeastFifteenPercentSpeedup()
    {
        var gpu = AccelDevices.Gpu("Karte");
        var devices = AccelDevices.WithCpu(gpu);

        // CPU 10 ms: 8,6 ms ist 16 % schneller (genug), 8,7 ms nur 14,9 % (zu knapp), 8,5 ms genau 17,6 %.
        var plan = AccelerationPlanner.FromBenchmark(AccelDevices.Result(
            (AcceleratorDevice.CpuDevice, 10, 10, 10, 10),
            (gpu, 8.6, 8.7, 8.5, 10)), devices);

        Assert.Same(gpu, plan[ModelId.Yolox]);
        Assert.Null(plan[ModelId.YuNet]);
        Assert.Same(gpu, plan[ModelId.Palm]);
        Assert.Null(plan[ModelId.Landmark]);
        Assert.Equal(1.15, AccelerationPlanner.MinimumSpeedup);
    }

    [Fact]
    public void FromBenchmark_IgnoresFailedDevices_AndUsesTheFastestValidOneIfTheCpuFailed()
    {
        var gpu = AccelDevices.Gpu("Karte", device: 1);
        var broken = AccelDevices.Gpu("Defekt", device: 2);
        var devices = AccelDevices.WithCpu(gpu, broken);

        var plan = AccelerationPlanner.FromBenchmark(AccelDevices.Result(
            (AcceleratorDevice.CpuDevice, 10, null, 10, 10),
            (gpu, 5, 9, null, 12),
            (broken, null, 1, 1, 1)), devices);

        Assert.Same(gpu, plan[ModelId.Yolox]);      // Defekt hat keinen Wert
        Assert.Same(broken, plan[ModelId.YuNet]);   // CPU ohne Wert: das schnellste gültige Gerät zählt
        Assert.Same(broken, plan[ModelId.Palm]);    // Karte ohne Wert
        Assert.Same(broken, plan[ModelId.Landmark]);
    }

    [Fact]
    public void FromBenchmark_IgnoresDevicesThatAreNoLongerPresent()
    {
        var gone = AccelDevices.Gpu("Weg", device: 9);

        var plan = AccelerationPlanner.FromBenchmark(AccelDevices.Result(
            (AcceleratorDevice.CpuDevice, 10, 10, 10, 10),
            (gone, 1, 1, 1, 1)), AccelDevices.WithCpu());

        Assert.All(AccelerationPlanner.AllModels, m => Assert.Null(plan[m]));
    }

    [Fact]
    public void SamePlan_ComparesDeviceKeysPerModel()
    {
        var gpu = AccelDevices.Gpu("Karte");
        var cpuPlan = AccelerationPlanner.AllModels.ToDictionary(m => m, _ => (AcceleratorDevice?)null);
        var gpuPlan = AccelerationPlanner.AllModels.ToDictionary(m => m, _ => (AcceleratorDevice?)gpu);
        var mixed = new Dictionary<ModelId, AcceleratorDevice?>(cpuPlan) { [ModelId.Palm] = gpu };

        Assert.True(AccelerationPlanner.SamePlan(cpuPlan, new Dictionary<ModelId, AcceleratorDevice?>(cpuPlan)));
        Assert.False(AccelerationPlanner.SamePlan(cpuPlan, gpuPlan));
        Assert.False(AccelerationPlanner.SamePlan(cpuPlan, mixed));
    }

    // ------------------------------------------------------------------ Cache-Datei

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "fotobox-test-" + Guid.NewGuid().ToString("N"), AccelerationCache.FileName);

    [Fact]
    public void Cache_RoundTrip_KeepsEntries_AndLeavesNoTempFile()
    {
        var path = TempFile();
        var gpu = AccelDevices.Gpu("Karte");
        var result = AccelDevices.Result((AcceleratorDevice.CpuDevice, 10, 10, 10, 10), (gpu, 5, null, 5, 5));
        try
        {
            Assert.True(AccelerationCache.TryWrite(path, "abc", result));

            var read = AccelerationCache.TryRead(path, "abc");
            Assert.NotNull(read);
            Assert.Equal(result.Entries.Count, read.Entries.Count);
            Assert.Equal(5, read.Find(gpu.Key, ModelId.Yolox)!.AverageMs);
            Assert.Equal("Testfehler", read.Find(gpu.Key, ModelId.YuNet)!.Error);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Cache_OtherFingerprintMissingFileOrBrokenContent_AreAllIgnored()
    {
        var path = TempFile();
        try
        {
            Assert.Null(AccelerationCache.TryRead(path, "abc")); // fehlt

            Assert.True(AccelerationCache.TryWrite(path, "abc", AccelDevices.Result((AcceleratorDevice.CpuDevice, 1, 1, 1, 1))));
            Assert.Null(AccelerationCache.TryRead(path, "anderer-abdruck"));

            File.WriteAllText(path, "{ das ist kein json");
            Assert.Null(AccelerationCache.TryRead(path, "abc"));

            File.WriteAllText(path, "{\"Format\":99,\"Fingerprint\":\"abc\",\"Entries\":[]}");
            Assert.Null(AccelerationCache.TryRead(path, "abc"));

            File.WriteAllText(path, "{\"Format\":1,\"Fingerprint\":\"abc\",\"Entries\":[{\"DeviceKey\":\"cpu\",\"DeviceName\":\"CPU\",\"Model\":\"Unbekannt\",\"LoadMs\":1,\"AverageMs\":1}]}");
            Assert.Null(AccelerationCache.TryRead(path, "abc"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Fingerprint_ChangesWithDevicesVersionsProvidersAppAndOrt()
    {
        var gpu = AccelDevices.Gpu("Karte");
        var gpuNewDriver = gpu with { Version = "2.0" };
        var providers = new[] { new ProviderState("Anbieter", "Paket.1", "Ready", true) };

        var baseline = AccelerationCache.Fingerprint([AcceleratorDevice.CpuDevice, gpu], providers, "1.0", "ort1");

        Assert.Equal(baseline, AccelerationCache.Fingerprint([AcceleratorDevice.CpuDevice, gpu], providers, "1.0", "ort1"));
        Assert.NotEqual(baseline, AccelerationCache.Fingerprint([AcceleratorDevice.CpuDevice], providers, "1.0", "ort1"));
        Assert.NotEqual(baseline, AccelerationCache.Fingerprint([AcceleratorDevice.CpuDevice, gpuNewDriver], providers, "1.0", "ort1"));
        Assert.NotEqual(baseline, AccelerationCache.Fingerprint([AcceleratorDevice.CpuDevice, gpu], [], "1.0", "ort1"));
        Assert.NotEqual(baseline, AccelerationCache.Fingerprint([AcceleratorDevice.CpuDevice, gpu], providers, "1.1", "ort1"));
        Assert.NotEqual(baseline, AccelerationCache.Fingerprint([AcceleratorDevice.CpuDevice, gpu], providers, "1.0", "ort2"));
    }

    [Fact]
    public void Fingerprint_DoesNotDependOnDeviceOrder()
    {
        var a = AccelDevices.Gpu("Karte A", device: 1);
        var b = AccelDevices.Gpu("Karte B", device: 2);

        Assert.Equal(
            AccelerationCache.Fingerprint([AcceleratorDevice.CpuDevice, a, b], [], "1", "1"),
            AccelerationCache.Fingerprint([AcceleratorDevice.CpuDevice, b, a], [], "1", "1"));
    }
}
