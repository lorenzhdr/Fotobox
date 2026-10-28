using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Fotobox.Views.Converters;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Einstellungen der Mausradzeile und der neun Hinweis-Stile: Standardwerte, Normalisierung, Speichern, Hilfsfunktionen.</summary>
public sealed class WheelHintSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private static AppSettings Normalized(Action<AppSettings> configure)
    {
        var s = new AppSettings();
        configure(s);
        return SettingsNormalizer.Normalize(s);
    }

    // ---- Standardwerte und Enum

    [Fact]
    public void Defaults_WheelHintIsShown_Automatic_AndMatching()
    {
        var g = new AppSettings().GuestScreen;

        Assert.True(g.ShowWheelHint);
        Assert.Null(g.WheelHintText);
        Assert.Equal(WheelHintStyle.Matching, g.WheelHintStyle);
        Assert.Equal(2, new AppSettings().Version);   // keine Migration nötig
    }

    [Fact]
    public void HintStyle_NewStylesAreAppendedAtTheEnd_InTheAgreedOrder()
    {
        Assert.Equal(
            [HintStyle.Pill, HintStyle.Plain, HintStyle.Banner, HintStyle.Neon, HintStyle.Bubble,
             HintStyle.Ticker, HintStyle.Glass, HintStyle.Note, HintStyle.Cinema],
            Enum.GetValues<HintStyle>());
        Assert.Equal(
            [WheelHintStyle.Matching, WheelHintStyle.SmallPill, WheelHintStyle.Plain, WheelHintStyle.Chip, WheelHintStyle.Arrows],
            Enum.GetValues<WheelHintStyle>());
    }

    // ---- Normalisierung

    [Fact]
    public void Normalizer_UnknownWheelStyle_BecomesMatching()
    {
        var g = Normalized(s => s.GuestScreen.WheelHintStyle = (WheelHintStyle)77).GuestScreen;

        Assert.Equal(WheelHintStyle.Matching, g.WheelHintStyle);
    }

    [Fact]
    public void Normalizer_UnknownHintStyle_StillFallsBackToPill_AndKnownNewStylesStay()
    {
        Assert.Equal(HintStyle.Pill, Normalized(s => s.GuestScreen.HintStyle = (HintStyle)77).GuestScreen.HintStyle);
        Assert.Equal(HintStyle.Cinema, Normalized(s => s.GuestScreen.VideoHintStyle = HintStyle.Cinema).GuestScreen.VideoHintStyle);
        Assert.Equal(HintStyle.Neon, Normalized(s => s.GuestScreen.HintStyle = HintStyle.Neon).GuestScreen.HintStyle);
    }

    [Fact]
    public void Normalizer_WheelHintText_IsCleaned_LimitedAndBlankBecomesNull()
    {
        Assert.Equal("Rad  drehen", Normalized(s => s.GuestScreen.WheelHintText = "  Rad\r\ndrehen  ").GuestScreen.WheelHintText);
        Assert.Null(Normalized(s => s.GuestScreen.WheelHintText = "   ").GuestScreen.WheelHintText);
        Assert.Null(Normalized(s => s.GuestScreen.WheelHintText = "").GuestScreen.WheelHintText);
        Assert.Equal(SettingsLimits.HintTextMaxLength,
            Normalized(s => s.GuestScreen.WheelHintText = new string('r', SettingsLimits.HintTextMaxLength + 30)).GuestScreen.WheelHintText!.Length);
        // Platzhalter bleiben wörtlich stehen (keine Sonderbehandlung).
        Assert.Equal("{Layout} {Klick}", Normalized(s => s.GuestScreen.WheelHintText = "{Layout} {Klick}").GuestScreen.WheelHintText);
    }

    // ---- Speichern und Laden

    [Fact]
    public void Store_Roundtrip_KeepsWheelHintSettings_AndWritesStyleAsName()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.GuestScreen.ShowWheelHint = false;
        s.GuestScreen.WheelHintText = "Rad drehen";
        s.GuestScreen.WheelHintStyle = WheelHintStyle.Arrows;
        s.GuestScreen.HintStyle = HintStyle.Ticker;
        s.GuestScreen.VideoHintStyle = HintStyle.Cinema;
        store.Save(s);

        var reloaded = new JsonSettingsStore(_dir).Current.GuestScreen;

        Assert.False(reloaded.ShowWheelHint);
        Assert.Equal("Rad drehen", reloaded.WheelHintText);
        Assert.Equal(WheelHintStyle.Arrows, reloaded.WheelHintStyle);
        Assert.Equal(HintStyle.Ticker, reloaded.HintStyle);
        Assert.Equal(HintStyle.Cinema, reloaded.VideoHintStyle);
        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"WheelHintStyle\": \"Arrows\"", json);
        Assert.Contains("\"HintStyle\": \"Ticker\"", json);
    }

    [Fact]
    public void Store_OldFileWithoutWheelSettings_UsesDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"Version\": 2, \"GuestScreen\": { \"HintText\": \"Hallo\" } }");

        var store = new JsonSettingsStore(_dir);

        Assert.False(store.LoadFailed);
        Assert.Equal("Hallo", store.Current.GuestScreen.HintText);
        Assert.True(store.Current.GuestScreen.ShowWheelHint);
        Assert.Null(store.Current.GuestScreen.WheelHintText);
        Assert.Equal(WheelHintStyle.Matching, store.Current.GuestScreen.WheelHintStyle);
    }

    [Fact]
    public void Store_UnknownWheelStyleName_DoesNotInvalidateTheFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath,
            "{ \"Flow\": { \"CountdownSeconds\": 3 }, \"GuestScreen\": { \"WheelHintStyle\": \"Hologramm\", \"WheelHintText\": \"  Rad  \" } }");

        var store = new JsonSettingsStore(_dir);

        Assert.False(store.LoadFailed);
        Assert.Equal(WheelHintStyle.Matching, store.Current.GuestScreen.WheelHintStyle);
        Assert.Equal("Rad", store.Current.GuestScreen.WheelHintText);
        Assert.Equal(3, store.Current.Flow.CountdownSeconds);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void Clone_CopiesWheelHintSettings()
    {
        var s = new AppSettings();
        s.GuestScreen.ShowWheelHint = false;
        s.GuestScreen.WheelHintText = "Rad";
        s.GuestScreen.WheelHintStyle = WheelHintStyle.Chip;

        var clone = s.Clone();

        Assert.False(clone.GuestScreen.ShowWheelHint);
        Assert.Equal("Rad", clone.GuestScreen.WheelHintText);
        Assert.Equal(WheelHintStyle.Chip, clone.GuestScreen.WheelHintStyle);
    }

    // ---- Text der Mausradzeile

    [Fact]
    public void WheelHintText_EmptyOrNull_UsesAutomaticText()
    {
        var screen = new GuestScreenSettings();

        Assert.Equal("Mausrad: Layout wechseln · Ein Foto", GuestLayouts.WheelHintText(screen, GuestLayout.Single, 3));
        Assert.Equal("Mausrad: Layout wechseln · 3 Fotos – 2 Streifen", GuestLayouts.WheelHintText(screen, GuestLayout.StripDouble, 3));
        screen.WheelHintText = "   ";
        Assert.Equal(GuestLayouts.WheelHint(GuestLayout.Overlay, 3), GuestLayouts.WheelHintText(screen, GuestLayout.Overlay, 3));
        Assert.Equal(GuestLayouts.WheelHint(GuestLayout.Single, 3), GuestLayouts.WheelHintText(null, GuestLayout.Single, 3));
    }

    [Fact]
    public void WheelHintText_CustomText_IsReturnedVerbatim_WithoutPlaceholdersOrLayoutName()
    {
        var screen = new GuestScreenSettings { WheelHintText = "Drehen {Layout} {Fototaste_1} {Klick}" };

        Assert.Equal("Drehen {Layout} {Fototaste_1} {Klick}", GuestLayouts.WheelHintText(screen, GuestLayout.StripSingle, 4));
    }

    // ---- EnumMatchConverter

    [Fact]
    public void EnumMatchConverter_Convert_ComparesValueWithParameter()
    {
        var c = new EnumMatchConverter();
        var culture = CultureInfo.InvariantCulture;

        Assert.Equal(true, c.Convert(HintStyle.Neon, typeof(bool), HintStyle.Neon, culture));
        Assert.Equal(false, c.Convert(HintStyle.Neon, typeof(bool), HintStyle.Pill, culture));
        Assert.Equal(false, c.Convert(null, typeof(bool), HintStyle.Pill, culture));
        Assert.Equal(false, c.Convert(HintStyle.Pill, typeof(bool), null, culture));
        // Verschiedene Enum-Typen mit gleichem Zahlenwert sind nicht gleich.
        Assert.Equal(false, c.Convert(WheelHintStyle.Matching, typeof(bool), HintStyle.Pill, culture));
    }

    [Fact]
    public void EnumMatchConverter_ConvertBack_ReturnsParameterWhenChecked_OtherwiseDoNothing()
    {
        var c = new EnumMatchConverter();
        var culture = CultureInfo.InvariantCulture;

        Assert.Equal(HintStyle.Glass, c.ConvertBack(true, typeof(HintStyle), HintStyle.Glass, culture));
        Assert.Same(Binding.DoNothing, c.ConvertBack(false, typeof(HintStyle), HintStyle.Glass, culture));
        Assert.Same(Binding.DoNothing, c.ConvertBack(null, typeof(HintStyle), HintStyle.Glass, culture));
        Assert.Same(Binding.DoNothing, c.ConvertBack(true, typeof(HintStyle), null, culture));
    }

    [Fact]
    public void EnumMatchVisibilityConverter_ShowsOnlyTheMatchingValue()
    {
        var c = new EnumMatchVisibilityConverter();
        var culture = CultureInfo.InvariantCulture;

        Assert.Equal(Visibility.Visible, c.Convert(WheelHintStyle.Chip, typeof(Visibility), WheelHintStyle.Chip, culture));
        Assert.Equal(Visibility.Collapsed, c.Convert(WheelHintStyle.Chip, typeof(Visibility), WheelHintStyle.Arrows, culture));
        Assert.Same(Binding.DoNothing, c.ConvertBack(Visibility.Visible, typeof(WheelHintStyle), WheelHintStyle.Chip, culture));
    }
}
