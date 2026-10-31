using System.IO;
using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Einstellungen von Countdown-Piepen und Kamera-Sound (<see cref="FlowSettings"/>): Standards, Grenzen, Normalisierung, Speichern.</summary>
public sealed class BoothSoundSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Defaults_AreOnWithSeventyPercent()
    {
        var flow = new AppSettings().Flow;

        Assert.True(flow.CountdownBeepEnabled);
        Assert.Equal(CountdownBeepSound.Soft, flow.CountdownBeepSound);
        Assert.Equal(70, flow.CountdownBeepVolumePercent);
        Assert.True(flow.ShutterSoundEnabled);
        Assert.Equal(ShutterSound.Slr, flow.ShutterSound);
        Assert.Equal(70, flow.ShutterVolumePercent);
        Assert.Equal(SettingsLimits.SoundVolumeDefault, flow.CountdownBeepVolumePercent);
    }

    [Fact]
    public void Limits_AreZeroToHundredInFiveSteps()
    {
        Assert.Equal(0, SettingsLimits.SoundVolumeMin);
        Assert.Equal(100, SettingsLimits.SoundVolumeMax);
        Assert.Equal(5, SettingsLimits.SoundVolumeStep);
        Assert.Equal(0, SettingsLimits.SoundVolumeMax % SettingsLimits.SoundVolumeStep);
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 5)]
    [InlineData(72, 70)]
    [InlineData(73, 75)]
    [InlineData(100, 100)]
    [InlineData(400, 100)]
    public void Normalizer_ClampsAndRoundsVolumes(int input, int expected)
    {
        var s = new AppSettings();
        s.Flow.CountdownBeepVolumePercent = input;
        s.Flow.ShutterVolumePercent = input;

        SettingsNormalizer.Normalize(s);

        Assert.Equal(expected, s.Flow.CountdownBeepVolumePercent);
        Assert.Equal(expected, s.Flow.ShutterVolumePercent);
    }

    [Fact]
    public void Normalizer_ResetsUnknownEnumValuesToDefaults()
    {
        var s = new AppSettings();
        s.Flow.CountdownBeepSound = (CountdownBeepSound)42;
        s.Flow.ShutterSound = (ShutterSound)(-3);

        SettingsNormalizer.Normalize(s);

        Assert.Equal(CountdownBeepSound.Soft, s.Flow.CountdownBeepSound);
        Assert.Equal(ShutterSound.Slr, s.Flow.ShutterSound);
    }

    [Fact]
    public void Normalizer_KeepsValidChoices()
    {
        var s = new AppSettings();
        s.Flow.CountdownBeepSound = CountdownBeepSound.WoodBlock;
        s.Flow.ShutterSound = ShutterSound.Retro;
        s.Flow.CountdownBeepEnabled = false;
        s.Flow.ShutterSoundEnabled = false;

        SettingsNormalizer.Normalize(s);

        Assert.Equal(CountdownBeepSound.WoodBlock, s.Flow.CountdownBeepSound);
        Assert.Equal(ShutterSound.Retro, s.Flow.ShutterSound);
        Assert.False(s.Flow.CountdownBeepEnabled);
        Assert.False(s.Flow.ShutterSoundEnabled);
    }

    [Fact]
    public void OldFileWithoutTheFields_GivesDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "Flow": { "CountdownSeconds": 7 } }""");

        var flow = new JsonSettingsStore(_dir).Current.Flow;

        Assert.Equal(7, flow.CountdownSeconds);
        Assert.True(flow.CountdownBeepEnabled);
        Assert.Equal(CountdownBeepSound.Soft, flow.CountdownBeepSound);
        Assert.Equal(70, flow.CountdownBeepVolumePercent);
        Assert.True(flow.ShutterSoundEnabled);
        Assert.Equal(ShutterSound.Slr, flow.ShutterSound);
        Assert.Equal(70, flow.ShutterVolumePercent);
    }

    [Fact]
    public void FileWithUnknownEnumNamesAndWildVolumes_IsNormalizedNotRejected()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{ "Flow": { "CountdownBeepSound": "Zukunft", "ShutterSound": 17, "CountdownBeepVolumePercent": 999, "ShutterVolumePercent": -5 } }""");

        var store = new JsonSettingsStore(_dir);

        Assert.False(store.LoadFailed);
        Assert.Equal(CountdownBeepSound.Soft, store.Current.Flow.CountdownBeepSound);
        Assert.Equal(ShutterSound.Slr, store.Current.Flow.ShutterSound);
        Assert.Equal(100, store.Current.Flow.CountdownBeepVolumePercent);
        Assert.Equal(0, store.Current.Flow.ShutterVolumePercent);
    }

    [Fact]
    public void JsonRoundtrip_WritesNamesAndKeepsValues()
    {
        var s = new AppSettings();
        s.Flow.CountdownBeepEnabled = false;
        s.Flow.CountdownBeepSound = CountdownBeepSound.Bright;
        s.Flow.CountdownBeepVolumePercent = 35;
        s.Flow.ShutterSoundEnabled = false;
        s.Flow.ShutterSound = ShutterSound.Compact;
        s.Flow.ShutterVolumePercent = 90;

        var json = JsonSerializer.Serialize(s, AppSettings.JsonOptions);
        var back = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!;

        Assert.Contains("\"Bright\"", json);
        Assert.Contains("\"Compact\"", json);
        Assert.False(back.Flow.CountdownBeepEnabled);
        Assert.Equal(CountdownBeepSound.Bright, back.Flow.CountdownBeepSound);
        Assert.Equal(35, back.Flow.CountdownBeepVolumePercent);
        Assert.False(back.Flow.ShutterSoundEnabled);
        Assert.Equal(ShutterSound.Compact, back.Flow.ShutterSound);
        Assert.Equal(90, back.Flow.ShutterVolumePercent);
    }

    [Fact]
    public void StoreSaveAndReload_KeepsValues_AndCloneCopiesThem()
    {
        var store = new JsonSettingsStore(_dir);
        var s = store.Current.Clone();
        s.Flow.CountdownBeepSound = CountdownBeepSound.WoodBlock;
        s.Flow.ShutterSound = ShutterSound.Retro;
        s.Flow.CountdownBeepVolumePercent = 55;
        s.Flow.ShutterVolumePercent = 15;
        store.Save(s);

        var r = new JsonSettingsStore(_dir).Current;

        Assert.Equal(CountdownBeepSound.WoodBlock, r.Flow.CountdownBeepSound);
        Assert.Equal(ShutterSound.Retro, r.Flow.ShutterSound);
        Assert.Equal(55, r.Flow.CountdownBeepVolumePercent);
        Assert.Equal(15, r.Flow.ShutterVolumePercent);
        Assert.Equal(55, r.Clone().Flow.CountdownBeepVolumePercent);
    }

    [Fact]
    public void FlowSection_IsPortable_SoTheNewFieldsAreSharedAndNotDeviceSpecific()
    {
        Assert.Contains(SettingsSections.PortableSections, p => p.Name == nameof(AppSettings.Flow));
        foreach (var name in new[]
                 {
                     "CountdownBeepEnabled", "CountdownBeepSound", "CountdownBeepVolumePercent",
                     "ShutterSoundEnabled", "ShutterSound", "ShutterVolumePercent",
                 })
            Assert.DoesNotContain("Flow." + name, DeviceSpecificSettings.Paths);

        var from = new AppSettings();
        from.Flow.ShutterSound = ShutterSound.Retro;
        from.Flow.CountdownBeepVolumePercent = 20;
        var to = new AppSettings();

        SettingsSections.CopyPortableSections(from, to);

        Assert.Equal(ShutterSound.Retro, to.Flow.ShutterSound);
        Assert.Equal(20, to.Flow.CountdownBeepVolumePercent);
    }
}
