using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Töne des Ablaufs (<see cref="BoothToneSynth"/>): gültige WAV-Daten, Länge, Lautstärke, Transiente, Determinismus.</summary>
public sealed class BoothToneSynthTests
{
    public static TheoryData<BoothSoundCue> AllCues()
    {
        var data = new TheoryData<BoothSoundCue>();
        foreach (var cue in Enum.GetValues<BoothSoundCue>())
            data.Add(cue);
        return data;
    }

    private static short[] Samples(byte[] wav)
    {
        var samples = new short[(wav.Length - 44) / 2];
        Buffer.BlockCopy(wav, 44, samples, 0, samples.Length * 2);
        return samples;
    }

    private static int Peak(IEnumerable<short> samples) => samples.Select(s => Math.Abs((int)s)).DefaultIfEmpty(0).Max();

    private static double Seconds(byte[] wav) => (wav.Length - 44) / 2.0 / BoothToneSynth.SampleRate;

    [Theory]
    [MemberData(nameof(AllCues))]
    public void Create_ReturnsValidWav(BoothSoundCue cue)
    {
        var wav = BoothToneSynth.Create(cue, 70);

        Assert.True(wav.Length > 44);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));   // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));   // mono
        Assert.Equal(BoothToneSynth.SampleRate, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
    }

    [Theory]
    [InlineData(BoothSoundCue.BeepSoft, 0.08, 0.13)]
    [InlineData(BoothSoundCue.BeepSoftFinal, 0.12, 0.22)]
    [InlineData(BoothSoundCue.BeepBright, 0.08, 0.13)]
    [InlineData(BoothSoundCue.BeepBrightFinal, 0.12, 0.22)]
    [InlineData(BoothSoundCue.BeepWoodBlock, 0.08, 0.13)]
    [InlineData(BoothSoundCue.BeepWoodBlockFinal, 0.12, 0.22)]
    [InlineData(BoothSoundCue.ShutterSlr, 0.12, 0.22)]
    [InlineData(BoothSoundCue.ShutterCompact, 0.04, 0.12)]
    [InlineData(BoothSoundCue.ShutterRetro, 0.25, 0.45)]
    public void Create_HasExpectedDuration(BoothSoundCue cue, double min, double max)
    {
        var seconds = Seconds(BoothToneSynth.Create(cue, 70));

        Assert.InRange(seconds, min, max);
    }

    [Theory]
    [MemberData(nameof(AllCues))]
    public void Create_ZeroPercent_IsSilent(BoothSoundCue cue)
    {
        var samples = Samples(BoothToneSynth.Create(cue, 0));

        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.Equal(0, s));
    }

    [Theory]
    [MemberData(nameof(AllCues))]
    public void Create_HigherVolume_GivesHigherPeak_AndNeverClips(BoothSoundCue cue)
    {
        var peaks = new[] { 5, 25, 50, 70, 100 }.Select(v => Peak(Samples(BoothToneSynth.Create(cue, v)))).ToArray();

        for (var i = 1; i < peaks.Length; i++)
            Assert.True(peaks[i] > peaks[i - 1], $"{cue}: Spitze bei Stufe {i} nicht größer.");
        Assert.True(peaks[^1] <= short.MaxValue);
        Assert.True(peaks[3] > 2000, "Standardlautstärke ist hörbar.");
    }

    [Fact]
    public void Create_RoundsVolumeToFivePercentSteps_AndClamps()
    {
        Assert.Same(BoothToneSynth.Create(BoothSoundCue.BeepSoft, 71), BoothToneSynth.Create(BoothSoundCue.BeepSoft, 70));
        Assert.Same(BoothToneSynth.Create(BoothSoundCue.BeepSoft, 300), BoothToneSynth.Create(BoothSoundCue.BeepSoft, 100));
        Assert.Same(BoothToneSynth.Create(BoothSoundCue.BeepSoft, -10), BoothToneSynth.Create(BoothSoundCue.BeepSoft, 0));
        Assert.Equal(75, BoothToneSynth.VolumeStep(74));
        Assert.Equal(70, BoothToneSynth.VolumeStep(72));
    }

    [Theory]
    [InlineData(BoothSoundCue.ShutterSlr)]
    [InlineData(BoothSoundCue.ShutterCompact)]
    [InlineData(BoothSoundCue.ShutterRetro)]
    [InlineData(BoothSoundCue.BeepWoodBlock)]
    [InlineData(BoothSoundCue.BeepWoodBlockFinal)]
    public void Transients_SitAtTheVeryStart(BoothSoundCue cue)
    {
        var samples = Samples(BoothToneSynth.Create(cue, 100));
        var tenMs = BoothToneSynth.SampleRate / 100;

        var early = Peak(samples.Take(tenMs));
        var overall = Peak(samples);

        Assert.True(early > 0.3 * overall, $"{cue}: Die Transiente liegt nicht am Anfang ({early} von {overall}).");
        // Keine Stille vorne: schon im ersten Millisekundenfenster liegt Energie.
        Assert.True(Peak(samples.Take(BoothToneSynth.SampleRate / 1000)) > 0);
    }

    [Theory]
    [MemberData(nameof(AllCues))]
    public void Tail_EndsSoftly(BoothSoundCue cue)
    {
        var samples = Samples(BoothToneSynth.Create(cue, 100));

        Assert.True(Math.Abs((int)samples[^1]) < 0.05 * Peak(samples), $"{cue}: Das Ende knackt.");
    }

    [Theory]
    [InlineData(BoothSoundCue.BeepSoft, BoothSoundCue.BeepSoftFinal)]
    [InlineData(BoothSoundCue.BeepBright, BoothSoundCue.BeepBrightFinal)]
    [InlineData(BoothSoundCue.BeepWoodBlock, BoothSoundCue.BeepWoodBlockFinal)]
    public void FinalBeep_IsHigherAndLonger(BoothSoundCue normal, BoothSoundCue final)
    {
        var a = Samples(BoothToneSynth.Create(normal, 70));
        var b = Samples(BoothToneSynth.Create(final, 70));

        Assert.True(b.Length > a.Length);
        Assert.True(ZeroCrossingRate(b) > ZeroCrossingRate(a), $"{final} klingt nicht höher als {normal}.");
    }

    private static double ZeroCrossingRate(short[] samples)
    {
        var crossings = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] < 0) != (samples[i] < 0))
                crossings++;
        }
        return crossings / (samples.Length / (double)BoothToneSynth.SampleRate);
    }

    [Fact]
    public void Slr_HasTwoSeparateClicks()
    {
        var samples = Samples(BoothToneSynth.Create(BoothSoundCue.ShutterSlr, 100));
        var window = BoothToneSynth.SampleRate / 200; // 5 ms
        var energy = new List<double>();
        for (var i = 0; i + window <= samples.Length; i += window)
            energy.Add(samples.Skip(i).Take(window).Sum(s => (double)s * s));
        var max = energy.Max();

        // Zwei Gipfel (Spiegel hoch/runter) mit einem deutlich leiseren Tal dazwischen.
        var peaks = 0;
        var rising = true;
        var previous = 0.0;
        foreach (var e in energy)
        {
            if (rising && e < previous && previous > 0.2 * max)
            {
                peaks++;
                rising = false;
            }
            else if (!rising && e > previous * 1.5 && e > 0.1 * max)
            {
                rising = true;
            }
            previous = e;
        }
        Assert.True(peaks >= 2, $"Erwartet zwei Klicks, gefunden {peaks}.");
    }

    [Fact]
    public void Output_IsDeterministic()
    {
        // Zwei unabhängige Prozesse liefern gleiche Daten, wenn die Synthese deterministisch ist; hier: Wiederholung nach dem Cache
        // und Vergleich zweier Lautstärken (gleiche Form, nur skaliert).
        var a = Samples(BoothToneSynth.Create(BoothSoundCue.ShutterRetro, 50));
        var b = Samples(BoothToneSynth.Create(BoothSoundCue.ShutterRetro, 100));

        for (var i = 0; i < a.Length; i++)
            Assert.InRange(Math.Abs(b[i] - 2.0 * a[i]), 0, 2.5);
    }

    [Theory]
    [InlineData(CountdownBeepSound.Soft, false, BoothSoundCue.BeepSoft)]
    [InlineData(CountdownBeepSound.Soft, true, BoothSoundCue.BeepSoftFinal)]
    [InlineData(CountdownBeepSound.Bright, false, BoothSoundCue.BeepBright)]
    [InlineData(CountdownBeepSound.Bright, true, BoothSoundCue.BeepBrightFinal)]
    [InlineData(CountdownBeepSound.WoodBlock, false, BoothSoundCue.BeepWoodBlock)]
    [InlineData(CountdownBeepSound.WoodBlock, true, BoothSoundCue.BeepWoodBlockFinal)]
    [InlineData((CountdownBeepSound)99, false, BoothSoundCue.BeepSoft)]
    public void BeepCue_MapsVariants(CountdownBeepSound sound, bool final, BoothSoundCue expected) =>
        Assert.Equal(expected, BoothToneSynth.BeepCue(sound, final));

    [Theory]
    [InlineData(ShutterSound.Slr, BoothSoundCue.ShutterSlr)]
    [InlineData(ShutterSound.Compact, BoothSoundCue.ShutterCompact)]
    [InlineData(ShutterSound.Retro, BoothSoundCue.ShutterRetro)]
    [InlineData((ShutterSound)99, BoothSoundCue.ShutterSlr)]
    public void ShutterCue_MapsVariants(ShutterSound sound, BoothSoundCue expected) =>
        Assert.Equal(expected, BoothToneSynth.ShutterCue(sound));
}
