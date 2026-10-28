using System.Text.Json;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Peace-Einstellungen: Standards, Normalisierung, JSON und <see cref="OverlayMapper.MapPoint"/>.</summary>
public sealed class PeaceSettingsTests
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
        var p = Normalized(_ => { }).Peace;

        Assert.True(p.Enabled);
        Assert.True(p.ActionSelfDestruct);
        Assert.False(p.ActionPhoto);
        Assert.False(p.ActionMoustache);
        Assert.Equal(1000, p.HoldMilliseconds);
        Assert.False(p.ShowHoldProgress);
        Assert.Equal(0, p.CooldownSeconds);
        Assert.Equal(50, p.SensitivityPercent);
        Assert.Equal(5, p.AnalysesPerSecond);
        Assert.Equal(10, p.SelfDestructSeconds);
        Assert.False(p.SelfDestructSoundEnabled);
        Assert.Equal(3, p.PhotoCountdownSeconds);
        Assert.Equal(MoustacheTargets.Everyone, p.MoustacheTargets);
        Assert.Equal(MoustacheStyle.Random, p.MoustacheStyle);
        Assert.Equal(MoustacheVisibility.UntilPhotoOrGone, p.MoustacheVisibility);
        Assert.Equal(15, p.MoustacheDurationSeconds);
        Assert.False(p.MoustacheAutoPhoto);
        Assert.Equal(3, p.MoustacheAutoPhotoCountdownSeconds);
        Assert.True(p.HasAnyAction);
    }

    [Fact]
    public void Numbers_AboveLimits_AreClamped()
    {
        var p = Normalized(s =>
        {
            s.Peace.HoldMilliseconds = 99_999;
            s.Peace.CooldownSeconds = 99_999;
            s.Peace.SensitivityPercent = 500;
            s.Peace.AnalysesPerSecond = 500;
            s.Peace.SelfDestructSeconds = 500;
            s.Peace.PhotoCountdownSeconds = 500;
            s.Peace.MoustacheDurationSeconds = 5000;
            s.Peace.MoustacheAutoPhotoCountdownSeconds = 500;
        }).Peace;

        Assert.Equal(SettingsLimits.PeaceHoldMillisecondsMax, p.HoldMilliseconds);
        Assert.Equal(SettingsLimits.PeaceCooldownSecondsMax, p.CooldownSeconds);
        Assert.Equal(SettingsLimits.PeaceSensitivityMax, p.SensitivityPercent);
        Assert.Equal(SettingsLimits.PeaceAnalysesPerSecondMax, p.AnalysesPerSecond);
        Assert.Equal(SettingsLimits.PeaceSelfDestructSecondsMax, p.SelfDestructSeconds);
        Assert.Equal(SettingsLimits.PeacePhotoCountdownMax, p.PhotoCountdownSeconds);
        Assert.Equal(SettingsLimits.PeaceMoustacheDurationMax, p.MoustacheDurationSeconds);
        Assert.Equal(SettingsLimits.PeaceMoustacheAutoPhotoCountdownMax, p.MoustacheAutoPhotoCountdownSeconds);
    }

    [Fact]
    public void Numbers_BelowLimits_AreClamped()
    {
        var p = Normalized(s =>
        {
            s.Peace.HoldMilliseconds = -5;
            s.Peace.CooldownSeconds = -5;
            s.Peace.SensitivityPercent = -5;
            s.Peace.AnalysesPerSecond = -5;
            s.Peace.SelfDestructSeconds = -5;
            s.Peace.PhotoCountdownSeconds = -5;
            s.Peace.MoustacheDurationSeconds = -5;
            s.Peace.MoustacheAutoPhotoCountdownSeconds = -5;
        }).Peace;

        Assert.Equal(SettingsLimits.PeaceHoldMillisecondsMin, p.HoldMilliseconds);
        Assert.Equal(SettingsLimits.PeaceCooldownSecondsMin, p.CooldownSeconds);
        Assert.Equal(SettingsLimits.PeaceSensitivityMin, p.SensitivityPercent);
        Assert.Equal(SettingsLimits.PeaceAnalysesPerSecondMin, p.AnalysesPerSecond);
        Assert.Equal(SettingsLimits.PeaceSelfDestructSecondsMin, p.SelfDestructSeconds);
        Assert.Equal(SettingsLimits.PeacePhotoCountdownMin, p.PhotoCountdownSeconds);
        Assert.Equal(SettingsLimits.PeaceMoustacheDurationMin, p.MoustacheDurationSeconds);
        Assert.Equal(SettingsLimits.PeaceMoustacheAutoPhotoCountdownMin, p.MoustacheAutoPhotoCountdownSeconds);
    }

    [Theory]
    [InlineData(1049, 1000)]
    [InlineData(1050, 1100)]
    [InlineData(1234, 1200)]
    [InlineData(3000, 3000)]
    [InlineData(310, 300)]
    public void HoldMilliseconds_AreRoundedToHundreds(int input, int expected)
    {
        var p = Normalized(s => s.Peace.HoldMilliseconds = input).Peace;
        Assert.Equal(expected, p.HoldMilliseconds);
    }

    [Fact]
    public void NoAction_FallsBackToSelfDestruct()
    {
        var p = Normalized(s =>
        {
            s.Peace.ActionSelfDestruct = false;
            s.Peace.ActionPhoto = false;
            s.Peace.ActionMoustache = false;
        }).Peace;

        Assert.True(p.ActionSelfDestruct);
        Assert.False(p.ActionPhoto);
        Assert.False(p.ActionMoustache);
    }

    [Fact]
    public void OtherActionsSelected_AreKept()
    {
        var p = Normalized(s =>
        {
            s.Peace.ActionSelfDestruct = false;
            s.Peace.ActionPhoto = true;
        }).Peace;

        Assert.False(p.ActionSelfDestruct);
        Assert.True(p.ActionPhoto);
    }

    [Fact]
    public void UndefinedEnums_FallBackToDefaults()
    {
        var p = Normalized(s =>
        {
            s.Peace.MoustacheTargets = (MoustacheTargets)99;
            s.Peace.MoustacheStyle = (MoustacheStyle)99;
            s.Peace.MoustacheVisibility = (MoustacheVisibility)99;
        }).Peace;

        Assert.Equal(MoustacheTargets.Everyone, p.MoustacheTargets);
        Assert.Equal(MoustacheStyle.Random, p.MoustacheStyle);
        Assert.Equal(MoustacheVisibility.UntilPhotoOrGone, p.MoustacheVisibility);
    }

    [Fact]
    public void UnknownEnumNamesInJson_FallBackToDefaults()
    {
        var json = "{ \"Peace\": { \"MoustacheStyle\": \"Bogus\", \"MoustacheTargets\": \"Nobody\", \"MoustacheVisibility\": \"Forever\", \"HoldMilliseconds\": 1500 } }";
        var settings = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!;

        var p = SettingsNormalizer.Normalize(settings).Peace;

        Assert.Equal(MoustacheStyle.Random, p.MoustacheStyle);
        Assert.Equal(MoustacheTargets.Everyone, p.MoustacheTargets);
        Assert.Equal(MoustacheVisibility.UntilPhotoOrGone, p.MoustacheVisibility);
        Assert.Equal(1500, p.HoldMilliseconds);
    }

    [Fact]
    public void MissingPeaceSection_YieldsDefaults_EnabledOn()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{ \"Version\": 2 }", AppSettings.JsonOptions)!;

        var p = SettingsNormalizer.Normalize(settings).Peace;

        Assert.True(p.Enabled);
        Assert.True(p.ActionSelfDestruct);
    }

    [Fact]
    public void NullPeaceSection_YieldsDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{ \"Peace\": null }", AppSettings.JsonOptions)!;

        var p = SettingsNormalizer.Normalize(settings).Peace;

        Assert.NotNull(p);
        Assert.True(p.Enabled);
    }

    [Fact]
    public void AppSettingsClone_CopiesPeaceIndependently()
    {
        var original = new AppSettings();
        original.Peace.HoldMilliseconds = 2000;
        original.Peace.MoustacheStyle = MoustacheStyle.Walrus;

        var clone = original.Clone();
        clone.Peace.HoldMilliseconds = 500;

        Assert.Equal(2000, original.Peace.HoldMilliseconds);
        Assert.Equal(MoustacheStyle.Walrus, clone.Peace.MoustacheStyle);
        Assert.Equal(500, clone.Peace.HoldMilliseconds);
    }

    [Fact]
    public void PeaceSettingsClone_IsIndependent()
    {
        var original = new PeaceSettings { CooldownSeconds = 30 };
        var clone = original.Clone();
        clone.CooldownSeconds = 60;

        Assert.Equal(30, original.CooldownSeconds);
        Assert.Equal(60, clone.CooldownSeconds);
    }

    [Fact]
    public void PeaceSettings_RoundTripsThroughJson_WithoutDerivedProperties()
    {
        var json = JsonSerializer.Serialize(new AppSettings(), AppSettings.JsonOptions);

        Assert.DoesNotContain("HasAnyAction", json);
        Assert.DoesNotContain("MinHandScore", json);
        Assert.Contains("\"Peace\"", json);
    }

    [Theory]
    [InlineData(10, 0.9)]
    [InlineData(95, 0.55)]
    [InlineData(0, 0.9)]
    [InlineData(500, 0.55)]
    public void MinHandScore_HitsLimits_AndIsClamped(int sensitivity, double expected)
    {
        var p = new PeaceSettings { SensitivityPercent = sensitivity };
        Assert.Equal(expected, p.MinHandScore, 6);
    }

    [Fact]
    public void MinHandScore_FallsMonotonicallyWithSensitivity()
    {
        var previous = double.MaxValue;
        for (var s = SettingsLimits.PeaceSensitivityMin; s <= SettingsLimits.PeaceSensitivityMax; s += 5)
        {
            var score = new PeaceSettings { SensitivityPercent = s }.MinHandScore;
            Assert.True(score < previous);
            previous = score;
        }
    }

    // ---- OverlayMapper.MapPoint

    [Theory]
    [InlineData(OverlayFit.Uniform, false)]
    [InlineData(OverlayFit.Uniform, true)]
    [InlineData(OverlayFit.UniformToFill, false)]
    [InlineData(OverlayFit.UniformToFill, true)]
    public void MapPoint_IsConsistentWithMap(OverlayFit fit, bool mirror)
    {
        var box = new NormRect(0.2, 0.3, 0.3, 0.2);
        var mappedBox = OverlayMapper.Map(box, 640, 480, 1600, 900, fit, mirror);
        var mappedCenter = OverlayMapper.MapPoint(new NormPoint(box.CenterX, box.CenterY), 640, 480, 1600, 900, fit, mirror);

        Assert.Equal(mappedBox.X + mappedBox.Width / 2, mappedCenter.X, 6);
        Assert.Equal(mappedBox.Y + mappedBox.Height / 2, mappedCenter.Y, 6);
    }

    [Fact]
    public void MapPoint_MirrorFlipsX_AroundViewCenterForCenteredImage()
    {
        var plain = OverlayMapper.MapPoint(new NormPoint(0.25, 0.5), 1920, 1080, 1920, 1080, OverlayFit.Uniform, mirror: false);
        var mirrored = OverlayMapper.MapPoint(new NormPoint(0.25, 0.5), 1920, 1080, 1920, 1080, OverlayFit.Uniform, mirror: true);

        Assert.Equal(480, plain.X, 6);
        Assert.Equal(1440, mirrored.X, 6);
        Assert.Equal(plain.Y, mirrored.Y, 6);
    }

    [Fact]
    public void MapPoint_InvalidView_ReturnsDefault()
    {
        Assert.Equal(default, OverlayMapper.MapPoint(new NormPoint(0.5, 0.5), 640, 480, 0, 100, OverlayFit.Uniform, mirror: false));
    }
}
