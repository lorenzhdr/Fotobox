using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Mittelfinger-Einstellungen: Standards, Normalisierung, JSON, Kopie, <see cref="MiddleFingerSettings.Wants"/>.</summary>
public sealed class MiddleFingerSettingsTests
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
        var m = Normalized(_ => { }).MiddleFinger;

        Assert.True(m.Enabled);
        Assert.True(m.ActionCancelPeace);
        Assert.Equal(500, m.HoldMilliseconds);
        Assert.Equal(50, m.SensitivityPercent);
        Assert.True(m.HasAnyAction);
    }

    [Fact]
    public void Version_StaysAtTwo()
    {
        Assert.Equal(2, AppSettings.CurrentVersion);
    }

    [Fact]
    public void Numbers_AboveLimits_AreClamped()
    {
        var m = Normalized(s =>
        {
            s.MiddleFinger.HoldMilliseconds = 99_999;
            s.MiddleFinger.SensitivityPercent = 500;
        }).MiddleFinger;

        Assert.Equal(SettingsLimits.MiddleFingerHoldMillisecondsMax, m.HoldMilliseconds);
        Assert.Equal(SettingsLimits.MiddleFingerSensitivityMax, m.SensitivityPercent);
    }

    [Fact]
    public void Numbers_BelowLimits_AreClamped()
    {
        var m = Normalized(s =>
        {
            s.MiddleFinger.HoldMilliseconds = -5;
            s.MiddleFinger.SensitivityPercent = -5;
        }).MiddleFinger;

        Assert.Equal(SettingsLimits.MiddleFingerHoldMillisecondsMin, m.HoldMilliseconds);
        Assert.Equal(SettingsLimits.MiddleFingerSensitivityMin, m.SensitivityPercent);
    }

    [Theory]
    [InlineData(549, 500)]
    [InlineData(550, 600)]
    [InlineData(1234, 1200)]
    [InlineData(3000, 3000)]
    [InlineData(210, 200)]
    public void HoldMilliseconds_AreRoundedToHundreds(int input, int expected)
    {
        var m = Normalized(s => s.MiddleFinger.HoldMilliseconds = input).MiddleFinger;
        Assert.Equal(expected, m.HoldMilliseconds);
    }

    [Fact]
    public void NoAction_FallsBackToCancelPeace()
    {
        var m = Normalized(s => s.MiddleFinger.ActionCancelPeace = false).MiddleFinger;

        Assert.True(m.ActionCancelPeace);
        Assert.True(m.HasAnyAction);
    }

    [Fact]
    public void MissingSection_YieldsDefaults_EnabledOn()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{ \"Version\": 2 }", AppSettings.JsonOptions)!;

        var m = SettingsNormalizer.Normalize(settings).MiddleFinger;

        Assert.True(m.Enabled);
        Assert.True(m.ActionCancelPeace);
        Assert.Equal(500, m.HoldMilliseconds);
        Assert.Equal(50, m.SensitivityPercent);
    }

    [Fact]
    public void NullSection_YieldsDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{ \"MiddleFinger\": null }", AppSettings.JsonOptions)!;

        var m = SettingsNormalizer.Normalize(settings).MiddleFinger;

        Assert.NotNull(m);
        Assert.True(m.Enabled);
    }

    [Fact]
    public void JsonRoundTrip_KeepsValues_AndSkipsComputedProperties()
    {
        var original = new AppSettings();
        original.MiddleFinger.Enabled = false;
        original.MiddleFinger.HoldMilliseconds = 1500;
        original.MiddleFinger.SensitivityPercent = 80;

        var json = JsonSerializer.Serialize(original, AppSettings.JsonOptions);
        var loaded = SettingsNormalizer.Normalize(JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!).MiddleFinger;

        Assert.DoesNotContain("HasAnyAction", json);
        Assert.DoesNotContain("MinHandScore", json);
        Assert.False(loaded.Enabled);
        Assert.Equal(1500, loaded.HoldMilliseconds);
        Assert.Equal(80, loaded.SensitivityPercent);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var original = new MiddleFingerSettings { Enabled = false, HoldMilliseconds = 900, SensitivityPercent = 70 };

        var clone = original.Clone();
        clone.Enabled = true;
        clone.HoldMilliseconds = 300;

        Assert.False(original.Enabled);
        Assert.Equal(900, original.HoldMilliseconds);
        Assert.Equal(70, clone.SensitivityPercent);
        Assert.NotSame(original, clone);
    }

    [Fact]
    public void AppSettingsClone_CopiesMiddleFingerIndependently()
    {
        var original = new AppSettings();
        original.MiddleFinger.HoldMilliseconds = 2000;

        var clone = original.Clone();
        clone.MiddleFinger.HoldMilliseconds = 400;

        Assert.Equal(2000, original.MiddleFinger.HoldMilliseconds);
        Assert.Equal(400, clone.MiddleFinger.HoldMilliseconds);
    }

    [Theory]
    [InlineData(true, true, MiddleFingerTargets.None, false)]
    [InlineData(true, true, MiddleFingerTargets.Moustache, true)]
    [InlineData(true, true, MiddleFingerTargets.SelfDestruct, true)]
    [InlineData(true, true, MiddleFingerTargets.Moustache | MiddleFingerTargets.SelfDestruct, true)]
    [InlineData(false, true, MiddleFingerTargets.Moustache, false)]
    [InlineData(false, true, MiddleFingerTargets.SelfDestruct, false)]
    [InlineData(true, false, MiddleFingerTargets.Moustache, false)]
    [InlineData(true, false, MiddleFingerTargets.SelfDestruct, false)]
    [InlineData(false, false, MiddleFingerTargets.Moustache | MiddleFingerTargets.SelfDestruct, false)]
    public void Wants_FollowsEnabledActionAndTargets(bool enabled, bool action, MiddleFingerTargets present, bool expected)
    {
        var m = new MiddleFingerSettings { Enabled = enabled, ActionCancelPeace = action };

        Assert.Equal(expected, m.Wants(present));
    }

    [Fact]
    public void MinHandScore_FallsWithSensitivity_WithinBounds()
    {
        var m = new MiddleFingerSettings();
        var previous = double.MaxValue;
        for (var sensitivity = SettingsLimits.MiddleFingerSensitivityMin; sensitivity <= SettingsLimits.MiddleFingerSensitivityMax; sensitivity += 5)
        {
            m.SensitivityPercent = sensitivity;
            var score = m.MinHandScore;
            Assert.True(score <= previous, $"Score bei {sensitivity} % steigt.");
            Assert.InRange(score, 0.55, 0.9);
            previous = score;
        }

        m.SensitivityPercent = SettingsLimits.MiddleFingerSensitivityMin;
        Assert.Equal(0.9, m.MinHandScore, 6);
        m.SensitivityPercent = SettingsLimits.MiddleFingerSensitivityMax;
        Assert.Equal(0.55, m.MinHandScore, 6);
    }
}
