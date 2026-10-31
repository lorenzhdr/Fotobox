using System.IO;
using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Tracking-Overlay: Standardwerte, Normalisierung des Abschnitts und Vorrang der Taste (nach der Leistungsanzeige gegen alle anderen).</summary>
public sealed class TrackingOverlaySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Defaults_AreAgreed()
    {
        var t = new TrackingOverlaySettings();

        Assert.True(t.ShowPersons && t.ShowFaces && t.ShowHands && t.ShowGestures && t.ShowMoustaches && t.ShowLabels && t.ShowStatusLine);
        Assert.False(t.ShowVerdicts);
        Assert.Equal(TrackingOverlayStyle.Technik, t.Style);
        Assert.Equal(100, t.ScalePercent);
        Assert.Equal((50, 200), (SettingsLimits.TrackingOverlayScaleMin, SettingsLimits.TrackingOverlayScaleMax));
        Assert.Equal(100, SettingsLimits.TrackingOverlayScaleDefault);
        Assert.Equal("Ctrl+Alt+X", InputSettings.DefaultTrackingOverlayKey);
        Assert.Equal([InputSettings.DefaultTrackingOverlayKey], new InputSettings().TrackingOverlayKeys);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var a = new TrackingOverlaySettings { ShowVerdicts = true, Style = TrackingOverlayStyle.SciFi, ScalePercent = 120 };
        var b = a.Clone();
        b.ShowVerdicts = false;
        b.ScalePercent = 70;

        Assert.True(a.ShowVerdicts);
        Assert.Equal(120, a.ScalePercent);
        Assert.Equal(TrackingOverlayStyle.SciFi, b.Style);
    }

    [Fact]
    public void OldFileWithoutTheSection_GetsDefaultSectionAndDefaultKey()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"Flow\": { \"CountdownSeconds\": 3 }, \"Input\": { \"TriggerKeys\": [\"F9\"] } }");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal(["Ctrl+Alt+X"], s.Input.TrackingOverlayKeys);
        Assert.Equal(["F9"], s.Input.TriggerKeys);
        Assert.Equal(JsonSerializer.Serialize(new TrackingOverlaySettings(), AppSettings.JsonOptions),
            JsonSerializer.Serialize(s.TrackingOverlay, AppSettings.JsonOptions));
    }

    [Fact]
    public void NullSectionInFile_BecomesDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"TrackingOverlay\": null }");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.NotNull(s.TrackingOverlay);
        Assert.Equal(100, s.TrackingOverlay.ScalePercent);
    }

    [Fact]
    public void Settings_AreClamped_UnknownStyleFallsBack_AndNormalizingIsIdempotent()
    {
        var s = new AppSettings();
        s.TrackingOverlay.ScalePercent = 10;
        s.TrackingOverlay.Style = (TrackingOverlayStyle)99;

        SettingsNormalizer.Normalize(s);

        Assert.Equal(SettingsLimits.TrackingOverlayScaleMin, s.TrackingOverlay.ScalePercent);
        Assert.Equal(TrackingOverlayStyle.Technik, s.TrackingOverlay.Style);

        s.TrackingOverlay.ScalePercent = 4000;
        SettingsNormalizer.Normalize(s);
        Assert.Equal(SettingsLimits.TrackingOverlayScaleMax, s.TrackingOverlay.ScalePercent);

        s.TrackingOverlay.ScalePercent = 133;   // rundet auf das 10er-Raster
        SettingsNormalizer.Normalize(s);
        Assert.Equal(130, s.TrackingOverlay.ScalePercent);

        var once = JsonSerializer.Serialize(s, AppSettings.JsonOptions);
        SettingsNormalizer.Normalize(s);
        Assert.Equal(once, JsonSerializer.Serialize(s, AppSettings.JsonOptions));
    }

    [Fact]
    public void UnknownStyleNameInFile_FallsBackToTechnik_OtherFieldsStay()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"TrackingOverlay\": { \"Style\": \"Quatsch\", \"ShowFaces\": false, \"ScalePercent\": 140 } }");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal(TrackingOverlayStyle.Technik, s.TrackingOverlay.Style);
        Assert.False(s.TrackingOverlay.ShowFaces);
        Assert.Equal(140, s.TrackingOverlay.ScalePercent);
    }

    [Fact]
    public void Keys_KeyOfAnyOtherAction_Yields_AndExistingKeysStay()
    {
        var input = new InputSettings
        {
            AdminKeys = ["Ctrl+Alt+A"],
            TriggerKeys = ["Space"],
            CancelCountdownKeys = ["Escape"],
            VideoStartKeys = ["F5"],
            VideoStopKeys = ["F6"],
            PerformanceOverlayKeys = ["Ctrl+Alt+Y"],
            TrackingOverlayKeys = ["Ctrl+Alt+A", "Escape", "F5", "F6", "Space", "Ctrl+Alt+Y", "Ctrl+Alt+X"],
        };

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["Ctrl+Alt+X"], input.TrackingOverlayKeys);
        Assert.Equal(["Ctrl+Alt+Y"], input.PerformanceOverlayKeys);   // die Leistungsanzeige behält ihre Taste
        Assert.Equal(["Ctrl+Alt+A"], input.AdminKeys);
        Assert.Equal(["Space"], input.TriggerKeys);
        Assert.Equal(["Escape"], input.CancelCountdownKeys);
        Assert.Equal(["F5"], input.VideoStartKeys);
        Assert.Equal(["F6"], input.VideoStopKeys);
    }

    [Fact]
    public void Keys_DefaultKeyHeldByAnotherAction_IsDroppedWithoutReplacement_NoMigration()
    {
        var input = new InputSettings { VideoStopKeys = ["Ctrl+Alt+X"] };

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Empty(input.TrackingOverlayKeys);
        Assert.Equal(["Ctrl+Alt+X"], input.VideoStopKeys);
    }

    [Fact]
    public void Keys_PerformanceOverlayHasPrecedence_OverTracking()
    {
        var input = new InputSettings { PerformanceOverlayKeys = ["F8"], TrackingOverlayKeys = ["F8", "F9"] };

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["F8"], input.PerformanceOverlayKeys);
        Assert.Equal(["F9"], input.TrackingOverlayKeys);
    }

    [Fact]
    public void Keys_EmptyOrNullStaysEmpty()
    {
        var input = new InputSettings { TrackingOverlayKeys = [] };

        SettingsNormalizer.NormalizeKeys(input);
        Assert.Empty(input.TrackingOverlayKeys);

        input.TrackingOverlayKeys = null!;
        SettingsNormalizer.NormalizeKeys(input);
        Assert.Empty(input.TrackingOverlayKeys);
    }

    [Fact]
    public void Keys_InvalidDuplicatesAndTooMany_AreCleaned_AndNormalizingIsIdempotent()
    {
        var input = new InputSettings
        {
            TrackingOverlayKeys = ["Quatsch", "F1", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9"],
        };

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(SettingsLimits.KeysPerActionMax, input.TrackingOverlayKeys.Count);
        Assert.Equal("F1", input.TrackingOverlayKeys[0]);
        Assert.DoesNotContain("Quatsch", input.TrackingOverlayKeys);

        var once = JsonSerializer.Serialize(input, AppSettings.JsonOptions);
        SettingsNormalizer.NormalizeKeys(input);
        Assert.Equal(once, JsonSerializer.Serialize(input, AppSettings.JsonOptions));
    }
}
