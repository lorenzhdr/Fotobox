using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Herz-/Kisscam-Einstellungen: Standards, Normalisierung, JSON, Kopie.</summary>
public sealed class HeartSettingsTests
{
    private static AppSettings Normalized(Action<AppSettings> configure)
    {
        var s = new AppSettings();
        configure(s);
        return SettingsNormalizer.Normalize(s);
    }

    [Fact]
    public void Defaults_AreAsSpecified_AndUnchangedByNormalizer()
    {
        var h = Normalized(_ => { }).Heart;

        Assert.True(h.Enabled);
        Assert.True(h.ShapeClassic);
        Assert.True(h.ShapeRoof);
        Assert.False(h.ShapeInverted);
        Assert.False(h.ShapeFingerHeart);
        Assert.Equal(1800, h.HoldMilliseconds);
        Assert.Equal(50, h.SensitivityPercent);
        Assert.True(h.ShowHoldFeedback);
        Assert.Equal(HeartAction.Kisscam, h.Action);
        Assert.Equal(10, h.KisscamSeconds);
        Assert.Null(h.KisscamText);
        Assert.Null(h.KisscamBannerText);
        Assert.Equal(KisscamColor.PinkRed, h.KisscamColor);
        Assert.False(h.KisscamZoomEnabled);
        Assert.Equal(140, h.KisscamZoomPercent);
        Assert.Equal(HeartSettings.DefaultKisscamFilePrefix, h.KisscamFilePrefix);
        Assert.Null(h.KisscamMusicPath);
        Assert.Equal(80, h.KisscamMusicVolumePercent);
        Assert.True(h.HasAnyShape);
    }

    [Fact]
    public void Version_StaysAtTwo()
    {
        Assert.Equal(2, AppSettings.CurrentVersion);
    }

    [Fact]
    public void Numbers_AboveLimits_AreClamped()
    {
        var h = Normalized(s =>
        {
            s.Heart.HoldMilliseconds = 99_999;
            s.Heart.SensitivityPercent = 500;
            s.Heart.KisscamSeconds = 9999;
            s.Heart.KisscamZoomPercent = 9999;
            s.Heart.KisscamMusicVolumePercent = 9999;
        }).Heart;

        Assert.Equal(SettingsLimits.HeartHoldMillisecondsMax, h.HoldMilliseconds);
        Assert.Equal(SettingsLimits.HeartSensitivityMax, h.SensitivityPercent);
        Assert.Equal(SettingsLimits.KisscamSecondsMax, h.KisscamSeconds);
        Assert.Equal(SettingsLimits.KisscamZoomPercentMax, h.KisscamZoomPercent);
        Assert.Equal(SettingsLimits.KisscamMusicVolumeMax, h.KisscamMusicVolumePercent);
    }

    [Fact]
    public void Numbers_BelowLimits_AreClamped()
    {
        var h = Normalized(s =>
        {
            s.Heart.HoldMilliseconds = -5;
            s.Heart.SensitivityPercent = -5;
            s.Heart.KisscamSeconds = -5;
            s.Heart.KisscamZoomPercent = -5;
            s.Heart.KisscamMusicVolumePercent = -5;
        }).Heart;

        Assert.Equal(SettingsLimits.HeartHoldMillisecondsMin, h.HoldMilliseconds);
        Assert.Equal(SettingsLimits.HeartSensitivityMin, h.SensitivityPercent);
        Assert.Equal(SettingsLimits.KisscamSecondsMin, h.KisscamSeconds);
        Assert.Equal(SettingsLimits.KisscamZoomPercentMin, h.KisscamZoomPercent);
        Assert.Equal(SettingsLimits.KisscamMusicVolumeMin, h.KisscamMusicVolumePercent);
    }

    [Theory]
    [InlineData(1049, 1000)]
    [InlineData(1050, 1100)]
    [InlineData(1234, 1200)]
    [InlineData(3000, 3000)]
    [InlineData(310, 300)]
    public void HoldMilliseconds_AreRoundedToHundreds(int input, int expected)
    {
        var h = Normalized(s => s.Heart.HoldMilliseconds = input).Heart;
        Assert.Equal(expected, h.HoldMilliseconds);
    }

    [Theory]
    [InlineData(114, 110)]
    [InlineData(115, 120)]
    [InlineData(143, 140)]
    [InlineData(200, 200)]
    [InlineData(197, 200)]
    public void KisscamZoomPercent_IsRoundedToTens(int input, int expected)
    {
        var h = Normalized(s => s.Heart.KisscamZoomPercent = input).Heart;
        Assert.Equal(expected, h.KisscamZoomPercent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 5)]
    [InlineData(82, 80)]
    [InlineData(83, 85)]
    [InlineData(100, 100)]
    public void KisscamMusicVolumePercent_IsRoundedToFives(int input, int expected)
    {
        var h = Normalized(s => s.Heart.KisscamMusicVolumePercent = input).Heart;
        Assert.Equal(expected, h.KisscamMusicVolumePercent);
    }

    [Fact]
    public void NoShape_FallsBackToClassic()
    {
        var h = Normalized(s =>
        {
            s.Heart.ShapeClassic = false;
            s.Heart.ShapeRoof = false;
            s.Heart.ShapeInverted = false;
            s.Heart.ShapeFingerHeart = false;
        }).Heart;

        Assert.True(h.ShapeClassic);
        Assert.False(h.ShapeRoof);
        Assert.True(h.HasAnyShape);
    }

    [Fact]
    public void SingleOtherShape_IsKept()
    {
        var h = Normalized(s =>
        {
            s.Heart.ShapeClassic = false;
            s.Heart.ShapeRoof = false;
            s.Heart.ShapeFingerHeart = true;
        }).Heart;

        Assert.False(h.ShapeClassic);
        Assert.True(h.ShapeFingerHeart);
    }

    [Fact]
    public void UndefinedEnums_FallBackToDefaults()
    {
        var h = Normalized(s =>
        {
            s.Heart.Action = (HeartAction)99;
            s.Heart.KisscamColor = (KisscamColor)99;
        }).Heart;

        Assert.Equal(HeartAction.Kisscam, h.Action);
        Assert.Equal(KisscamColor.PinkRed, h.KisscamColor);
    }

    [Fact]
    public void KisscamColor_AllDefinedValuesAreKept()
    {
        foreach (var color in Enum.GetValues<KisscamColor>())
        {
            var h = Normalized(s => s.Heart.KisscamColor = color).Heart;
            Assert.Equal(color, h.KisscamColor);
        }
    }

    [Fact]
    public void KisscamText_ControlCharactersBecomeSpaces_AndIsShortened()
    {
        var h = Normalized(s => s.Heart.KisscamText = "Hallo\r\nWelt\t!").Heart;
        Assert.Equal("Hallo  Welt !", h.KisscamText);

        h = Normalized(s => s.Heart.KisscamText = new string('x', SettingsLimits.HintTextMaxLength + 50)).Heart;
        Assert.Equal(SettingsLimits.HintTextMaxLength, h.KisscamText!.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    [InlineData(null)]
    public void KisscamText_EmptyOrWhitespace_BecomesNull(string? input)
    {
        var h = Normalized(s => s.Heart.KisscamText = input).Heart;
        Assert.Null(h.KisscamText);
    }

    [Fact]
    public void KisscamBannerText_IsCleaned_AndShortenedTo30()
    {
        var h = Normalized(s => s.Heart.KisscamBannerText = "  Küss\r\ndich!  ").Heart;
        Assert.Equal("Küss  dich!", h.KisscamBannerText);

        h = Normalized(s => s.Heart.KisscamBannerText = new string('B', 80)).Heart;
        Assert.Equal(SettingsLimits.KisscamBannerMaxLength, h.KisscamBannerText!.Length);
        Assert.Equal(30, h.KisscamBannerText.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    [InlineData(null)]
    public void KisscamBannerText_EmptyOrWhitespace_BecomesNull(string? input)
    {
        var h = Normalized(s => s.Heart.KisscamBannerText = input).Heart;
        Assert.Null(h.KisscamBannerText);
    }

    [Theory]
    [InlineData("Kuss/Cam:*?", "KussCam")]
    [InlineData("  Party-Kuss.  ", "Party-Kuss")]
    [InlineData("Kuss\tCam", "KussCam")]
    public void KisscamFilePrefix_IsSanitized(string input, string expected)
    {
        var h = Normalized(s => s.Heart.KisscamFilePrefix = input).Heart;
        Assert.Equal(expected, h.KisscamFilePrefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/:*?")]
    [InlineData(null)]
    public void KisscamFilePrefix_EmptyOrOnlyInvalid_BecomesDefault(string? input)
    {
        var h = Normalized(s => s.Heart.KisscamFilePrefix = input!).Heart;
        Assert.Equal(HeartSettings.DefaultKisscamFilePrefix, h.KisscamFilePrefix);
    }

    [Fact]
    public void KisscamFilePrefix_TooLong_IsShortened()
    {
        var h = Normalized(s => s.Heart.KisscamFilePrefix = new string('p', SettingsLimits.FilePrefixMaxLength + 40)).Heart;
        Assert.Equal(SettingsLimits.FilePrefixMaxLength, h.KisscamFilePrefix.Length);
    }

    [Fact]
    public void KisscamMusicPath_IsTrimmed_AndNotCheckedForExistence()
    {
        const string path = @"Z:\gibt\es\nicht\Lied.mp3";
        var h = Normalized(s => s.Heart.KisscamMusicPath = "  " + path + "  ").Heart;
        Assert.Equal(path, h.KisscamMusicPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("C:\\Musik\\Lied\n.mp3")]
    [InlineData("C:\\Musik\\Lied\0.mp3")]
    [InlineData("C:\\Musik\\\tLied.mp3")]
    public void KisscamMusicPath_EmptyOrWithControlCharacters_BecomesNull(string? input)
    {
        var h = Normalized(s => s.Heart.KisscamMusicPath = input).Heart;
        Assert.Null(h.KisscamMusicPath);
    }

    [Fact]
    public void KisscamMusicPath_TooLong_BecomesNull_AtLimitIsKept()
    {
        var atLimit = @"C:\" + new string('m', SettingsLimits.KisscamMusicPathMaxLength - 3);
        Assert.Equal(SettingsLimits.KisscamMusicPathMaxLength, atLimit.Length);

        var kept = Normalized(s => s.Heart.KisscamMusicPath = atLimit).Heart;
        Assert.Equal(atLimit, kept.KisscamMusicPath);

        var dropped = Normalized(s => s.Heart.KisscamMusicPath = atLimit + "x").Heart;
        Assert.Null(dropped.KisscamMusicPath);
    }

    [Fact]
    public void MissingSection_YieldsDefaults_EnabledOn()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{ \"Version\": 2 }", AppSettings.JsonOptions)!;

        var h = SettingsNormalizer.Normalize(settings).Heart;

        Assert.True(h.Enabled);
        Assert.True(h.ShapeClassic);
        Assert.True(h.ShapeRoof);
        Assert.Equal(1800, h.HoldMilliseconds);
        Assert.Equal(10, h.KisscamSeconds);
        Assert.Equal(HeartSettings.DefaultKisscamFilePrefix, h.KisscamFilePrefix);
    }

    [Fact]
    public void NullSection_YieldsDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{ \"Heart\": null }", AppSettings.JsonOptions)!;

        var h = SettingsNormalizer.Normalize(settings).Heart;

        Assert.NotNull(h);
        Assert.True(h.Enabled);
        Assert.Equal(HeartAction.Kisscam, h.Action);
    }

    [Fact]
    public void JsonRoundTrip_KeepsValues_AndSkipsComputedProperties()
    {
        var original = new AppSettings();
        original.Heart.Enabled = false;
        original.Heart.ShapeRoof = false;
        original.Heart.ShapeInverted = true;
        original.Heart.ShapeFingerHeart = true;
        original.Heart.HoldMilliseconds = 1500;
        original.Heart.SensitivityPercent = 80;
        original.Heart.ShowHoldFeedback = false;
        original.Heart.KisscamSeconds = 25;
        original.Heart.KisscamText = "Küss {Sekunden}";
        original.Heart.KisscamBannerText = "LOVE";
        original.Heart.KisscamColor = KisscamColor.Purple;
        original.Heart.KisscamZoomEnabled = true;
        original.Heart.KisscamZoomPercent = 170;
        original.Heart.KisscamFilePrefix = "Kuss";
        original.Heart.KisscamMusicPath = @"D:\Musik\Lied.mp3";
        original.Heart.KisscamMusicVolumePercent = 45;

        var json = JsonSerializer.Serialize(original, AppSettings.JsonOptions);
        var loaded = SettingsNormalizer.Normalize(JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!).Heart;

        Assert.DoesNotContain("HasAnyShape", json);
        Assert.DoesNotContain("MinHandScore", json);
        Assert.False(loaded.Enabled);
        Assert.False(loaded.ShapeRoof);
        Assert.True(loaded.ShapeInverted);
        Assert.True(loaded.ShapeFingerHeart);
        Assert.Equal(1500, loaded.HoldMilliseconds);
        Assert.Equal(80, loaded.SensitivityPercent);
        Assert.False(loaded.ShowHoldFeedback);
        Assert.Equal(25, loaded.KisscamSeconds);
        Assert.Equal("Küss {Sekunden}", loaded.KisscamText);
        Assert.Equal("LOVE", loaded.KisscamBannerText);
        Assert.Equal(KisscamColor.Purple, loaded.KisscamColor);
        Assert.True(loaded.KisscamZoomEnabled);
        Assert.Equal(170, loaded.KisscamZoomPercent);
        Assert.Equal("Kuss", loaded.KisscamFilePrefix);
        Assert.Equal(@"D:\Musik\Lied.mp3", loaded.KisscamMusicPath);
        Assert.Equal(45, loaded.KisscamMusicVolumePercent);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var original = new HeartSettings
        {
            Enabled = false,
            HoldMilliseconds = 900,
            SensitivityPercent = 70,
            KisscamText = "Original",
            KisscamMusicPath = @"D:\a.mp3",
        };

        var clone = original.Clone();
        clone.Enabled = true;
        clone.HoldMilliseconds = 300;
        clone.KisscamText = "Kopie";
        clone.KisscamMusicPath = null;

        Assert.False(original.Enabled);
        Assert.Equal(900, original.HoldMilliseconds);
        Assert.Equal("Original", original.KisscamText);
        Assert.Equal(@"D:\a.mp3", original.KisscamMusicPath);
        Assert.Equal(70, clone.SensitivityPercent);
        Assert.NotSame(original, clone);
    }

    [Fact]
    public void AppSettingsClone_CopiesHeartIndependently()
    {
        var original = new AppSettings();
        original.Heart.HoldMilliseconds = 2000;
        original.Heart.KisscamSeconds = 30;

        var clone = original.Clone();
        clone.Heart.HoldMilliseconds = 400;
        clone.Heart.KisscamSeconds = 5;

        Assert.NotSame(original.Heart, clone.Heart);
        Assert.Equal(2000, original.Heart.HoldMilliseconds);
        Assert.Equal(30, original.Heart.KisscamSeconds);
        Assert.Equal(400, clone.Heart.HoldMilliseconds);
        Assert.Equal(5, clone.Heart.KisscamSeconds);
    }

    [Fact]
    public void IsShapeEnabled_FollowsFlags()
    {
        var h = new HeartSettings { ShapeClassic = true, ShapeRoof = false, ShapeInverted = true, ShapeFingerHeart = false };

        Assert.True(h.IsShapeEnabled(HeartShape.Classic));
        Assert.False(h.IsShapeEnabled(HeartShape.Roof));
        Assert.True(h.IsShapeEnabled(HeartShape.Inverted));
        Assert.False(h.IsShapeEnabled(HeartShape.FingerHeart));
        Assert.False(h.IsShapeEnabled((HeartShape)99));
    }

    [Fact]
    public void MinHandScore_UsesTheSharedSensitivityMapping()
    {
        var h = new HeartSettings { SensitivityPercent = SettingsLimits.HeartSensitivityMin };
        Assert.Equal(MiddleFingerSettings.MinHandScoreFor(SettingsLimits.HeartSensitivityMin), h.MinHandScore, 9);

        h.SensitivityPercent = SettingsLimits.HeartSensitivityMax;
        Assert.Equal(MiddleFingerSettings.MinHandScoreFor(SettingsLimits.HeartSensitivityMax), h.MinHandScore, 9);
    }
}
