using Fotobox.Core;
using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Entscheidungsregeln des Ablauf-Tons (<see cref="BoothSoundCues"/>).</summary>
public sealed class BoothSoundCuesTests
{
    [Theory]
    [InlineData(BoothState.Countdown, CountdownStyle.Normal, 5, BoothSoundCue.BeepSoft)]
    [InlineData(BoothState.Countdown, CountdownStyle.Normal, 2, BoothSoundCue.BeepSoft)]
    [InlineData(BoothState.Countdown, CountdownStyle.Normal, 1, BoothSoundCue.BeepSoftFinal)]
    [InlineData(BoothState.Countdown, CountdownStyle.Peace, 3, BoothSoundCue.BeepSoft)]
    [InlineData(BoothState.Countdown, CountdownStyle.Peace, 1, BoothSoundCue.BeepSoftFinal)]
    [InlineData(BoothState.VideoCountdown, CountdownStyle.Normal, 3, BoothSoundCue.BeepSoft)]
    [InlineData(BoothState.VideoCountdown, CountdownStyle.Normal, 1, BoothSoundCue.BeepSoftFinal)]
    public void Beep_PlaysForNormalAndPeaceAndVideo(BoothState state, CountdownStyle style, int value, BoothSoundCue expected)
    {
        var beep = BoothSoundCues.Beep(state, style, value, new FlowSettings());

        Assert.NotNull(beep);
        Assert.Equal(expected, beep.Value.Cue);
        Assert.Equal(70, beep.Value.VolumePercent);
    }

    [Theory]
    [InlineData(CountdownStyle.SelfDestruct)]
    [InlineData(CountdownStyle.Kisscam)]
    public void Beep_IsSilentForSelfDestructAndKisscam(CountdownStyle style)
    {
        Assert.Null(BoothSoundCues.Beep(BoothState.Countdown, style, 3, new FlowSettings()));
        Assert.Null(BoothSoundCues.Beep(BoothState.Countdown, style, 1, new FlowSettings()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Beep_IsSilentForValueBelowOne(int value) =>
        Assert.Null(BoothSoundCues.Beep(BoothState.Countdown, CountdownStyle.Normal, value, new FlowSettings()));

    [Theory]
    [InlineData(BoothState.Idle)]
    [InlineData(BoothState.Capturing)]
    [InlineData(BoothState.Recording)]
    [InlineData(BoothState.Preview)]
    public void Beep_IsSilentOutsideCountdowns(BoothState state) =>
        Assert.Null(BoothSoundCues.Beep(state, CountdownStyle.Normal, 3, new FlowSettings()));

    [Fact]
    public void Beep_SwitchedOff_IsSilent_AndNullSettingsAreSafe()
    {
        Assert.Null(BoothSoundCues.Beep(BoothState.Countdown, CountdownStyle.Normal, 3, new FlowSettings { CountdownBeepEnabled = false }));
        Assert.Null(BoothSoundCues.Beep(BoothState.Countdown, CountdownStyle.Normal, 3, null));
    }

    [Fact]
    public void Beep_UsesChosenVariantAndVolume()
    {
        var flow = new FlowSettings { CountdownBeepSound = CountdownBeepSound.WoodBlock, CountdownBeepVolumePercent = 35 };

        var beep = BoothSoundCues.Beep(BoothState.Countdown, CountdownStyle.Normal, 1, flow);

        Assert.Equal(new BoothSound(BoothSoundCue.BeepWoodBlockFinal, 35), beep);
    }

    [Theory]
    [InlineData(CountdownStyle.Normal)]
    [InlineData(CountdownStyle.Peace)]
    [InlineData(CountdownStyle.Kisscam)]
    public void Shutter_PlaysForNormalPeaceAndKisscam(CountdownStyle style)
    {
        var shutter = BoothSoundCues.Shutter(style, new FlowSettings());

        Assert.Equal(new BoothSound(BoothSoundCue.ShutterSlr, 70), shutter);
    }

    [Fact]
    public void Shutter_IsSilentForSelfDestruct_WhenSwitchedOff_AndForNullSettings()
    {
        Assert.Null(BoothSoundCues.Shutter(CountdownStyle.SelfDestruct, new FlowSettings()));
        Assert.Null(BoothSoundCues.Shutter(CountdownStyle.Normal, new FlowSettings { ShutterSoundEnabled = false }));
        Assert.Null(BoothSoundCues.Shutter(CountdownStyle.Normal, null));
    }

    [Theory]
    [InlineData(ShutterSound.Compact, BoothSoundCue.ShutterCompact)]
    [InlineData(ShutterSound.Retro, BoothSoundCue.ShutterRetro)]
    public void Shutter_UsesChosenVariantAndVolume(ShutterSound sound, BoothSoundCue expected)
    {
        var shutter = BoothSoundCues.Shutter(CountdownStyle.Normal, new FlowSettings { ShutterSound = sound, ShutterVolumePercent = 90 });

        Assert.Equal(new BoothSound(expected, 90), shutter);
    }
}
