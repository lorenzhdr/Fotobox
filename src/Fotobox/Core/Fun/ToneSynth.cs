using System.Collections.Concurrent;
using System.IO;

namespace Fotobox.Core.Fun;

/// <summary>Klang-Signale der Zielerfassung und der Peaceerkennung (nur hinten erweitern).</summary>
public enum FunSoundCue { Acquire, Lock, Text, SelfDestructTick, SelfDestructTickFast, Explosion }

/// <summary>
/// Erzeugt die kurzen Signaltöne selbst (RIFF/WAVE, PCM 16 Bit mono 44,1 kHz) im Speicher, ohne Audiodateien.
/// Die Daten sind gecacht und dürfen nicht verändert werden.
/// </summary>
public static class ToneSynth
{
    public const int SampleRate = 44_100;
    private const double Volume = 0.35;
    private const double FadeSeconds = 0.006; // kurzes Ein-/Ausblenden gegen Knacken

    private static readonly ConcurrentDictionary<FunSoundCue, byte[]> Cache = new();

    public static byte[] Create(FunSoundCue cue) => Cache.GetOrAdd(cue, Build);

    private static byte[] Build(FunSoundCue cue)
    {
        var samples = cue switch
        {
            FunSoundCue.Acquire => Tone(0.080, 1800, 1800),
            FunSoundCue.Lock => Concat(Tone(0.070, 2400, 2400), Silence(0.050), Tone(0.070, 2400, 2400)),
            FunSoundCue.Text => Tone(0.150, 1000, 1400),
            FunSoundCue.SelfDestructTick => Concat(Siren(0.55, 600, 1200), Silence(0.05), Tone(0.12, 1500, 1500)),
            FunSoundCue.SelfDestructTickFast => Concat(Tone(0.09, 1900, 1900), Silence(0.05), Tone(0.09, 2300, 2300)),
            FunSoundCue.Explosion => ExplosionSamples(),
            _ => Silence(0.01),
        };
        return ToWav(samples);
    }

    /// <summary>Sinuston mit gleitender Frequenz (Phase wird aufsummiert, damit der Verlauf stetig bleibt).</summary>
    private static short[] Tone(double seconds, double startHz, double endHz)
    {
        var count = (int)Math.Round(seconds * SampleRate);
        var fade = Math.Max(1, (int)Math.Round(FadeSeconds * SampleRate));
        var data = new short[count];
        var phase = 0.0;
        for (var i = 0; i < count; i++)
        {
            var t = count <= 1 ? 0 : (double)i / (count - 1);
            var hz = startHz + (endHz - startHz) * t;
            phase += 2 * Math.PI * hz / SampleRate;
            var envelope = Math.Min(1.0, Math.Min((double)(i + 1) / fade, (double)(count - i) / fade));
            data[i] = (short)Math.Round(Math.Sin(phase) * envelope * Volume * short.MaxValue);
        }
        return data;
    }

    /// <summary>Sirenen-Sweep: Sinus mit etwas Oberwelle für den schärferen Klang, auf die Standard-Lautstärke normiert.</summary>
    private static short[] Siren(double seconds, double startHz, double endHz)
    {
        var count = (int)Math.Round(seconds * SampleRate);
        var raw = new double[count];
        var phase = 0.0;
        for (var i = 0; i < count; i++)
        {
            var t = count <= 1 ? 0 : (double)i / (count - 1);
            phase += 2 * Math.PI * (startHz + (endHz - startHz) * t) / SampleRate;
            raw[i] = Math.Sin(phase) + 0.35 * Math.Sin(2 * phase) + 0.12 * Math.Sin(3 * phase);
        }
        return Finish(raw);
    }

    /// <summary>
    /// Explosion (0,8 s): Rauschen, das beim Abklingen dumpfer wird (Tiefpass mit sinkender Grenze), dazu ein tiefes Grollen bei 60 Hz.
    /// Deterministisch durch den festen Startwert.
    /// </summary>
    private static short[] ExplosionSamples()
    {
        const double seconds = 0.8;
        var count = (int)Math.Round(seconds * SampleRate);
        var random = new Random(42);
        var raw = new double[count];
        var lowpass = 0.0;
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var u = (double)i / (count - 1);
            var coefficient = 0.55 - 0.50 * Math.Sqrt(u); // hell am Anfang, dumpf am Ende
            lowpass += coefficient * ((random.NextDouble() * 2 - 1) - lowpass);
            var noise = lowpass * Math.Exp(-t / 0.20);
            var rumble = Math.Sin(2 * Math.PI * 60 * t + 2.0 * Math.Sin(2 * Math.PI * 7 * t)) * Math.Exp(-t / 0.38);
            var attack = Math.Min(1.0, t / 0.004);
            raw[i] = (1.6 * noise + 0.55 * rumble) * attack;
        }
        return Finish(raw);
    }

    /// <summary>Auf die Standard-Lautstärke normieren (Spitze = <see cref="Volume"/>) und Anfang/Ende kurz ausblenden.</summary>
    private static short[] Finish(double[] raw)
    {
        var fade = Math.Max(1, (int)Math.Round(FadeSeconds * SampleRate));
        var peak = 0.0;
        for (var i = 0; i < raw.Length; i++)
        {
            var envelope = Math.Min(1.0, Math.Min((double)(i + 1) / fade, (double)(raw.Length - i) / fade));
            raw[i] *= envelope;
            peak = Math.Max(peak, Math.Abs(raw[i]));
        }

        var data = new short[raw.Length];
        var gain = peak > 0 ? Volume * short.MaxValue / peak : 0;
        for (var i = 0; i < raw.Length; i++)
            data[i] = (short)Math.Round(raw[i] * gain);
        return data;
    }

    private static short[] Silence(double seconds) => new short[(int)Math.Round(seconds * SampleRate)];

    private static short[] Concat(params short[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] ToWav(short[] samples)
    {
        var dataBytes = samples.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                    // Größe des fmt-Blocks
        w.Write((short)1);              // PCM
        w.Write((short)1);              // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);        // Bytes pro Sekunde
        w.Write((short)2);              // Blockgröße
        w.Write((short)16);             // Bit pro Sample
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples)
            w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
