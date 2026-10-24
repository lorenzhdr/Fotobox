using System.IO;
using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

public class GuestLayoutsTests
{
    [Theory]
    [InlineData(GuestLayout.Single, LayoutKind.SingleBorderless, StripStyle.DoubleCut)]
    [InlineData(GuestLayout.StripDouble, LayoutKind.Strip, StripStyle.DoubleCut)]
    [InlineData(GuestLayout.StripSingle, LayoutKind.Strip, StripStyle.Single)]
    [InlineData(GuestLayout.Overlay, LayoutKind.Overlay, StripStyle.DoubleCut)]
    public void ApplyTo_ThenFrom_Roundtrips(GuestLayout layout, LayoutKind kind, StripStyle style)
    {
        var settings = new LayoutSettings();

        GuestLayouts.ApplyTo(settings, layout);

        Assert.Equal(kind, settings.Kind);
        Assert.Equal(style, settings.StripStyle);
        Assert.Equal(layout, GuestLayouts.From(settings));
    }

    [Fact]
    public void ApplyTo_NonStrip_KeepsStripStyle()
    {
        var settings = new LayoutSettings { Kind = LayoutKind.Strip, StripStyle = StripStyle.Single };

        GuestLayouts.ApplyTo(settings, GuestLayout.Overlay);

        Assert.Equal(LayoutKind.Overlay, settings.Kind);
        Assert.Equal(StripStyle.Single, settings.StripStyle);
    }

    [Fact]
    public void From_IgnoresStripStyleForNonStrip()
    {
        Assert.Equal(GuestLayout.Single, GuestLayouts.From(new LayoutSettings { Kind = LayoutKind.SingleBorderless, StripStyle = StripStyle.Single }));
        Assert.Equal(GuestLayout.Overlay, GuestLayouts.From(new LayoutSettings { Kind = LayoutKind.Overlay, StripStyle = StripStyle.Single }));
    }

    [Fact]
    public void DisplayName_UsesPhotoCountForStrips()
    {
        Assert.Equal("Ein Foto", GuestLayouts.DisplayName(GuestLayout.Single, 3));
        Assert.Equal("3 Fotos – 2 Streifen", GuestLayouts.DisplayName(GuestLayout.StripDouble, 3));
        Assert.Equal("4 Fotos – 1 Streifen", GuestLayouts.DisplayName(GuestLayout.StripSingle, 4));
        Assert.Equal("Foto mit Rahmen", GuestLayouts.DisplayName(GuestLayout.Overlay, 3));
    }

    [Fact]
    public void Defaults_NoGuestChoices_BothClicksTrigger()
    {
        var s = new AppSettings();

        Assert.Empty(s.Layout.GuestChoices);
        Assert.True(s.Input.TriggerOnLeftClick);
        Assert.True(s.Input.TriggerOnRightClick);
    }
}

public sealed class GuestChoicesNormalizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>Speichert (und normalisiert damit) die Einstellungen und lädt sie neu.</summary>
    private AppSettings SaveAndReload(AppSettings s)
    {
        new JsonSettingsStore(_dir).Save(s);
        return new JsonSettingsStore(_dir).Current;
    }

    [Fact]
    public void RemovesDuplicatesAndUndefinedValues_KeepsListOrder()
    {
        var s = new AppSettings();
        s.Layout.GuestChoices = [GuestLayout.StripSingle, GuestLayout.Single, (GuestLayout)99, GuestLayout.StripSingle, GuestLayout.StripDouble];

        var result = SaveAndReload(s).Layout.GuestChoices;

        Assert.Equal([GuestLayout.StripSingle, GuestLayout.Single, GuestLayout.StripDouble], result);
    }

    [Fact]
    public void CustomOrder_IsKept_DefaultStaysWhereItIs()
    {
        var s = new AppSettings();
        s.Layout.OverlayFileName = "rahmen.png";
        s.Layout.GuestChoices = [GuestLayout.Overlay, GuestLayout.Single, GuestLayout.StripSingle, GuestLayout.StripDouble];

        var result = SaveAndReload(s).Layout.GuestChoices;

        Assert.Equal([GuestLayout.Overlay, GuestLayout.Single, GuestLayout.StripSingle, GuestLayout.StripDouble], result);
    }

    [Fact]
    public void MissingDefault_IsAppendedAtTheEnd_WithoutSorting()
    {
        var s = new AppSettings();   // Standard: Einzelfoto
        s.Layout.OverlayFileName = "rahmen.png";
        s.Layout.GuestChoices = [GuestLayout.Overlay, GuestLayout.StripDouble];

        var result = SaveAndReload(s).Layout.GuestChoices;

        Assert.Equal([GuestLayout.Overlay, GuestLayout.StripDouble, GuestLayout.Single], result);
    }

    [Fact]
    public void AddsDefault_WhenMissing_AndListNotEmpty()
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Strip;
        s.Layout.StripStyle = StripStyle.Single;
        s.Layout.GuestChoices = [GuestLayout.Single];

        var result = SaveAndReload(s).Layout.GuestChoices;

        Assert.Equal([GuestLayout.Single, GuestLayout.StripSingle], result);
    }

    [Fact]
    public void EmptyList_StaysEmpty_NoDefaultAdded()
    {
        var s = new AppSettings();
        s.Layout.GuestChoices = [];

        Assert.Empty(SaveAndReload(s).Layout.GuestChoices);
    }

    [Fact]
    public void OverlayWithoutFile_IsRemoved()
    {
        var s = new AppSettings();
        s.Layout.OverlayFileName = "  ";
        s.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.Overlay];

        Assert.Equal([GuestLayout.Single], SaveAndReload(s).Layout.GuestChoices);
    }

    [Fact]
    public void OverlayOnly_WithoutFile_BecomesEmpty()
    {
        var s = new AppSettings();
        s.Layout.GuestChoices = [GuestLayout.Overlay];

        Assert.Empty(SaveAndReload(s).Layout.GuestChoices);
    }

    [Fact]
    public void OverlayWithFile_IsKept()
    {
        var s = new AppSettings();
        s.Layout.OverlayFileName = "rahmen.png";
        s.Layout.GuestChoices = [GuestLayout.Overlay, GuestLayout.Single];

        Assert.Equal([GuestLayout.Overlay, GuestLayout.Single], SaveAndReload(s).Layout.GuestChoices);
    }

    [Fact]
    public void DefaultOverlayWithoutFile_IsStillAdded()
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Overlay;
        s.Layout.OverlayFileName = null;
        s.Layout.GuestChoices = [GuestLayout.Single];

        Assert.Equal([GuestLayout.Single, GuestLayout.Overlay], SaveAndReload(s).Layout.GuestChoices);
    }

    [Fact]
    public void OldJsonWithoutNewFields_GetsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """
            {
              "Layout": { "Kind": "Strip", "StripPhotoCount": 3 },
              "Input": { "TriggerKeys": [ "Space" ] }
            }
            """);

        var loaded = new JsonSettingsStore(_dir).Current;

        Assert.Equal(LayoutKind.Strip, loaded.Layout.Kind);
        Assert.Empty(loaded.Layout.GuestChoices);
        Assert.True(loaded.Input.TriggerOnLeftClick);
        Assert.True(loaded.Input.TriggerOnRightClick);
        Assert.False(loaded.Input.InvertMouseWheel);
    }

    [Fact]
    public void NewFields_SurviveJsonRoundtrip_AsStrings()
    {
        var s = new AppSettings();
        s.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble];
        s.Input.TriggerOnRightClick = false;
        s.Input.InvertMouseWheel = true;

        var json = JsonSerializer.Serialize(s, AppSettings.JsonOptions);
        var back = SaveAndReload(s);

        Assert.Contains("\"StripDouble\"", json);
        Assert.Equal([GuestLayout.Single, GuestLayout.StripDouble], back.Layout.GuestChoices);
        Assert.True(back.Input.TriggerOnLeftClick);
        Assert.False(back.Input.TriggerOnRightClick);
        Assert.True(back.Input.InvertMouseWheel);
    }
}
