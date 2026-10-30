using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Leistungseinstellungen (Beschleuniger der KI-Erkennung): Standards, Normalisierung, JSON.</summary>
public sealed class PerformanceSettingsTests
{
    private static AppSettings Normalized(Action<AppSettings> configure)
    {
        var s = new AppSettings();
        configure(s);
        return SettingsNormalizer.Normalize(s);
    }

    [Fact]
    public void Defaults_AreAutoWithoutGpuKey_AndHighPerformancePreferred()
    {
        var p = Normalized(_ => { }).Performance;

        Assert.Equal(AcceleratorMode.Auto, p.Accelerator);
        Assert.Null(p.GpuDeviceKey);
        Assert.True(p.PreferHighPerformanceGpu);
    }

    [Fact]
    public void UndefinedAccelerator_BecomesAuto_DefinedValuesStay()
    {
        Assert.Equal(AcceleratorMode.Auto, Normalized(s => s.Performance.Accelerator = (AcceleratorMode)99).Performance.Accelerator);
        foreach (var mode in Enum.GetValues<AcceleratorMode>())
            Assert.Equal(mode, Normalized(s => s.Performance.Accelerator = mode).Performance.Accelerator);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  ep|0001|0002|karte  ", "ep|0001|0002|karte")]
    [InlineData("ep|\u0007|karte", null)]
    public void GpuDeviceKey_IsTrimmed_AndEmptyOrControlCharsBecomeNull(string? input, string? expected)
    {
        Assert.Equal(expected, Normalized(s => s.Performance.GpuDeviceKey = input).Performance.GpuDeviceKey);
    }

    [Fact]
    public void GpuDeviceKey_AtLimitStays_BeyondLimitIsDropped()
    {
        var atLimit = new string('a', SettingsLimits.GpuDeviceKeyMaxLength);

        Assert.Equal(atLimit, Normalized(s => s.Performance.GpuDeviceKey = atLimit).Performance.GpuDeviceKey);
        Assert.Null(Normalized(s => s.Performance.GpuDeviceKey = atLimit + "a").Performance.GpuDeviceKey);
    }

    [Fact]
    public void MissingSection_IsReplacedByDefaults()
    {
        var s = Normalized(x => x.Performance = null!);

        Assert.NotNull(s.Performance);
        Assert.Equal(AcceleratorMode.Auto, s.Performance.Accelerator);
    }

    [Fact]
    public void Json_RoundTrip_KeepsValues_AndUnknownEnumNameBecomesAuto()
    {
        var s = new AppSettings();
        s.Performance.Accelerator = AcceleratorMode.Gpu;
        s.Performance.GpuDeviceKey = "schluessel";
        s.Performance.PreferHighPerformanceGpu = false;

        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, AppSettings.JsonOptions), AppSettings.JsonOptions)!;
        Assert.Equal(AcceleratorMode.Gpu, copy.Performance.Accelerator);
        Assert.Equal("schluessel", copy.Performance.GpuDeviceKey);
        Assert.False(copy.Performance.PreferHighPerformanceGpu);

        var unknown = JsonSerializer.Deserialize<AppSettings>("{\"Performance\":{\"Accelerator\":\"Quantum\"}}", AppSettings.JsonOptions)!;
        Assert.Equal(AcceleratorMode.Auto, SettingsNormalizer.Normalize(unknown).Performance.Accelerator);
    }

    [Fact]
    public void ExistingSettingsFileWithoutSection_GetsDefaults()
    {
        var s = SettingsNormalizer.Normalize(JsonSerializer.Deserialize<AppSettings>("{\"Version\":2}", AppSettings.JsonOptions)!);

        Assert.Equal(AcceleratorMode.Auto, s.Performance.Accelerator);
        Assert.True(s.Performance.PreferHighPerformanceGpu);
    }
}
