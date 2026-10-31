using Fotobox.Core;
using Fotobox.Services.Audio;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// <see cref="BoothSoundPlayer"/>: wirft nie. Echte Wiedergabe gibt es hier nicht (die Tests dürfen nichts hörbar abspielen);
/// geprüft werden Vorbereiten aller Töne und das stille Abspielen bei 0 %.
/// </summary>
public sealed class BoothSoundPlayerTests
{
    [Fact]
    public void Prepare_AllCues_DoesNotThrow_AndIsRepeatable()
    {
        var player = new BoothSoundPlayer();

        foreach (var cue in Enum.GetValues<BoothSoundCue>())
        {
            player.Prepare(cue, 70);
            player.Prepare(cue, 70);
            player.Prepare(cue, 0);
        }
    }

    [Fact]
    public void Play_AtZeroPercent_IsSilentAndDoesNotThrow()
    {
        var player = new BoothSoundPlayer();

        foreach (var cue in Enum.GetValues<BoothSoundCue>())
            player.Play(cue, 0);
        player.Play((BoothSoundCue)999, 0);
    }

    [Fact]
    public void Prepare_UnknownCue_DoesNotThrow() => new BoothSoundPlayer().Prepare((BoothSoundCue)999, 50);
}
