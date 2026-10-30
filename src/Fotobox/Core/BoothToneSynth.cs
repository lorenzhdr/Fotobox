using System.Collections.Concurrent;
using System.IO;

namespace Fotobox.Core;

/// <summary>
/// Töne des Ablaufs (nur hinten erweitern): Countdown-Piepen je Variante in „normal“ und „letzter Piep“ (Ziffer 1, höher und etwas
/// länger) sowie die drei Kamera-Sounds beim Auslösen.
/// </summary>
public enum BoothSoundCue
{
    BeepSoft, BeepSoftFinal,
    BeepBright, BeepBrightFinal,
    BeepWoodBlock, BeepWoodBlockFinal,
    ShutterSlr, ShutterCompact, ShutterRetro,
}

/// <summary>
/// Erzeugt die Töne des Countdown-Piepens und des Kamera-Sounds selbst (RIFF/WAVE, PCM 16 Bit mono, 44,1 kHz, 44-Byte-Kopf) im Speicher,
/// ohne Audiodateien. Getrennt von <see cref="Fun.ToneSynth"/> (Töne der Fun-Funktionen), dessen Daten unverändert bleiben.
/// Alles ist deterministisch (fester Zufalls-Startwert) und je Signal und 5-%-Lautstärkestufe gecacht; die Daten dürfen nicht verändert
/// werden. Die Auslöser beginnen sofort mit der Klick-Transiente (keine Stille am Anfang), die Piepe blenden kurz ein und aus.
/// </summary>
public static class BoothToneSynth
{
    public const int SampleRate = Fun.ToneSynth.SampleRate;

    // Spitzenpegel bei 100 % Lautstärke (Anteil der Vollaussteuerung). Piepe sind Dauertöne und wirken bei gleichem Spitzenwert viel
    // lauter als kurze Klicks; die Pegel gleichen das an.
    private const double BeepLevel = 0.55;
    private const double WoodLevel = 0.80;
    private const double ShutterLevel = 0.90;

    private static readonly ConcurrentDictionary<BoothSoundCue, double[]> Shapes = new();
    private static readonly ConcurrentDictionary<(BoothSoundCue Cue, int Step), byte[]> Cache = new();

    /// <summary>Lautstärke auf die 5-%-Stufen der Ton-Puffer runden und auf den Einstellungsbereich klemmen.</summary>
    public static int VolumeStep(int percent)
    {
        var step = SettingsLimits.SoundVolumeStep;
        var clamped = Math.Clamp(percent, SettingsLimits.SoundVolumeMin, SettingsLimits.SoundVolumeMax);
        return (clamped + step / 2) / step * step;
    }

    /// <summary>Ton mit der Lautstärke <paramref name="volumePercent"/> (5-%-Stufen; 0 = still, alle Abtastwerte 0).</summary>
    public static byte[] Create(BoothSoundCue cue, int volumePercent) =>
        Cache.GetOrAdd((cue, VolumeStep(volumePercent)), key => ToWav(Scale(key.Cue, Shape(key.Cue), key.Step)));

    /// <summary>Piepvariante → Signal (normal bzw. letzter Piep).</summary>
    public static BoothSoundCue BeepCue(CountdownBeepSound sound, bool final) => (sound, final) switch
    {
        (CountdownBeepSound.Bright, false) => BoothSoundCue.BeepBright,
        (CountdownBeepSound.Bright, true) => BoothSoundCue.BeepBrightFinal,
        (CountdownBeepSound.WoodBlock, false) => BoothSoundCue.BeepWoodBlock,
        (CountdownBeepSound.WoodBlock, true) => BoothSoundCue.BeepWoodBlockFinal,
        (_, true) => BoothSoundCue.BeepSoftFinal,
        _ => BoothSoundCue.BeepSoft,
    };

    /// <summary>Kamera-Sound-Variante → Signal.</summary>
    public static BoothSoundCue ShutterCue(ShutterSound sound) => sound switch
    {
        ShutterSound.Compact => BoothSoundCue.ShutterCompact,
        ShutterSound.Retro => BoothSoundCue.ShutterRetro,
        _ => BoothSoundCue.ShutterSlr,
    };

    private static double Level(BoothSoundCue cue) => cue switch
    {
        BoothSoundCue.BeepWoodBlock or BoothSoundCue.BeepWoodBlockFinal => WoodLevel,
        BoothSoundCue.ShutterSlr or BoothSoundCue.ShutterCompact or BoothSoundCue.ShutterRetro => ShutterLevel,
        _ => BeepLevel,
    };

    private static short[] Scale(BoothSoundCue cue, double[] shape, int step)
    {
        var gain = Level(cue) * step / 100.0 * short.MaxValue;
        var data = new short[shape.Length];
        for (var i = 0; i < shape.Length; i++)
            data[i] = (short)Math.Clamp((int)Math.Round(shape[i] * gain), -short.MaxValue, short.MaxValue);
        return data;
    }

    private static double[] Shape(BoothSoundCue cue) => Shapes.GetOrAdd(cue, Build);

    private static double[] Build(BoothSoundCue cue)
    {
        var raw = cue switch
        {
            BoothSoundCue.BeepSoft => SoftBeep(660, 0.100),
            BoothSoundCue.BeepSoftFinal => SoftBeep(990, 0.160),           // Quinte höher, länger
            BoothSoundCue.BeepBright => BrightBeep(1320, 0.090),
            BoothSoundCue.BeepBrightFinal => BrightBeep(1980, 0.150),      // Quinte höher, länger
            BoothSoundCue.BeepWoodBlock => WoodBlock(1100, 0.090, 0.022),
            BoothSoundCue.BeepWoodBlockFinal => WoodBlock(1650, 0.140, 0.034),
            BoothSoundCue.ShutterSlr => SlrShutter(),
            BoothSoundCue.ShutterCompact => CompactShutter(),
            BoothSoundCue.ShutterRetro => RetroShutter(),
            _ => new double[(int)(0.01 * SampleRate)],
        };
        return Normalize(raw);
    }

    // ------------------------------------------------------------ Piepe

    /// <summary>Weicher Sinuston mit einem Hauch Oberwelle, sanftem Ein- (10 ms) und Ausblenden (30 ms).</summary>
    private static double[] SoftBeep(double hz, double seconds)
    {
        var count = Count(seconds);
        var data = new double[count];
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var tone = Math.Sin(2 * Math.PI * hz * t) + 0.12 * Math.Sin(2 * Math.PI * 2 * hz * t);
            data[i] = tone * Fade(i, count, 0.010, 0.030);
        }
        return data;
    }

    /// <summary>Heller Ton mit Oberwellen (näher am Rechteck), kurzem Ein- (4 ms) und Ausblenden (20 ms).</summary>
    private static double[] BrightBeep(double hz, double seconds)
    {
        var count = Count(seconds);
        var data = new double[count];
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var w = 2 * Math.PI * hz * t;
            var tone = Math.Sin(w) + 0.33 * Math.Sin(3 * w) + 0.2 * Math.Sin(5 * w);
            data[i] = tone * Fade(i, count, 0.004, 0.020);
        }
        return data;
    }

    /// <summary>
    /// Holzblock: gedämpfter Resonanz-Tick aus drei nicht ganzzahlig verwandten Teiltönen mit schnellem Abklingen
    /// (Zeitkonstante <paramref name="tau"/>) und einem kurzen Rauschanteil am Anfang.
    /// </summary>
    private static double[] WoodBlock(double hz, double seconds, double tau)
    {
        var count = Count(seconds);
        var random = new Random(7);
        var data = new double[count];
        var noiseState = 0.0;
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var body = Math.Sin(2 * Math.PI * hz * t)
                + 0.45 * Math.Sin(2 * Math.PI * hz * 2.31 * t) * Math.Exp(-t / (tau * 0.6))
                + 0.25 * Math.Sin(2 * Math.PI * hz * 3.77 * t) * Math.Exp(-t / (tau * 0.35));
            noiseState += 0.5 * ((random.NextDouble() * 2 - 1) - noiseState);
            var knock = noiseState * Math.Exp(-t / 0.0015) * 0.7;
            data[i] = (body * Math.Exp(-t / tau) + knock) * Fade(i, count, 0, 0.012);
        }
        return data;
    }

    // ------------------------------------------------------------ Auslöser

    /// <summary>
    /// Spiegelreflex: zwei Klicks (Spiegel hoch, Spiegel runter, 78 ms Abstand) aus bandgefiltertem Rauschen, jeweils mit kurzer tiefer
    /// Resonanz („Schlag“ des Spiegelkastens). Der erste Klick sitzt ganz am Anfang.
    /// </summary>
    private static double[] SlrShutter()
    {
        var buffer = new double[Count(0.170)];
        var random = new Random(11);
        AddNoiseBurst(buffer, 0.000, 0.040, 700, 5200, 0.0065, 1.00, random);
        AddThump(buffer, 0.000, 150, 0.020, 0.75);
        AddNoiseBurst(buffer, 0.078, 0.040, 900, 4200, 0.0055, 0.80, random);
        AddThump(buffer, 0.078, 120, 0.016, 0.50);
        FadeOutTail(buffer, 0.008);
        return buffer;
    }

    /// <summary>Kompaktkamera: ein heller, kurzer Klick mit einem winzigen hohen Nachklang.</summary>
    private static double[] CompactShutter()
    {
        var buffer = new double[Count(0.075)];
        var random = new Random(23);
        AddNoiseBurst(buffer, 0.000, 0.030, 2600, 9500, 0.0024, 1.00, random);
        AddThump(buffer, 0.000, 3100, 0.0040, 0.35);
        AddThump(buffer, 0.000, 420, 0.0080, 0.30);
        FadeOutTail(buffer, 0.008);
        return buffer;
    }

    /// <summary>Retro: ein dumpfer Klick, danach das Ratschen des Filmtransports (neun gepulste Rauschbursts im Abstand von 30 ms, der letzte kräftiger).</summary>
    private static double[] RetroShutter()
    {
        var buffer = new double[Count(0.380)];
        var random = new Random(37);
        AddNoiseBurst(buffer, 0.000, 0.040, 500, 4200, 0.0050, 1.00, random);
        AddThump(buffer, 0.000, 105, 0.022, 0.80);
        const int pulses = 9;
        for (var n = 0; n < pulses; n++)
        {
            var start = 0.075 + n * 0.030;
            var last = n == pulses - 1;
            var amplitude = last ? 0.65 : 0.30 + 0.05 * Math.Sin(n * 1.7);
            AddNoiseBurst(buffer, start, 0.012, 400, 2800, 0.0022, amplitude, random);
            if (last)
                AddThump(buffer, start, 90, 0.016, 0.40);
        }
        FadeOutTail(buffer, 0.012);
        return buffer;
    }

    /// <summary>
    /// Addiert einen Rauschburst: Rauschen, durch Hoch- und Tiefpass auf das Band [<paramref name="lowHz"/>, <paramref name="highHz"/>]
    /// begrenzt, mit sofortigem Einsatz und exponentiellem Abklingen (<paramref name="tau"/>).
    /// </summary>
    private static void AddNoiseBurst(double[] buffer, double startSeconds, double seconds, double lowHz, double highHz,
        double tau, double amplitude, Random random)
    {
        var start = (int)Math.Round(startSeconds * SampleRate);
        var count = Math.Min(Count(seconds), buffer.Length - start);
        var lowCoefficient = 1 - Math.Exp(-2 * Math.PI * lowHz / SampleRate);
        var highCoefficient = 1 - Math.Exp(-2 * Math.PI * highHz / SampleRate);
        double slow = 0, fast = 0;
        for (var i = 0; i < count; i++)
        {
            var noise = random.NextDouble() * 2 - 1;
            fast += highCoefficient * (noise - fast);   // Tiefpass bei highHz
            slow += lowCoefficient * (noise - slow);    // Tiefpass bei lowHz; die Differenz ist ein Bandpass
            var t = (double)i / SampleRate;
            buffer[start + i] += (fast - slow) * Math.Exp(-t / tau) * amplitude * 2.5;
        }
    }

    /// <summary>Addiert eine abklingende Sinusresonanz (Schlag bzw. Tick).</summary>
    private static void AddThump(double[] buffer, double startSeconds, double hz, double tau, double amplitude)
    {
        var start = (int)Math.Round(startSeconds * SampleRate);
        var count = Math.Min(Count(tau * 6), buffer.Length - start);
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            buffer[start + i] += Math.Sin(2 * Math.PI * hz * t) * Math.Exp(-t / tau) * amplitude;
        }
    }

    // ------------------------------------------------------------ Hilfen

    private static int Count(double seconds) => Math.Max(1, (int)Math.Round(seconds * SampleRate));

    /// <summary>Ein-/Ausblendfaktor (0..1) gegen Knacken; Dauer 0 = kein Ein- bzw. Ausblenden, sinusförmiger Verlauf.</summary>
    private static double Fade(int index, int count, double fadeInSeconds, double fadeOutSeconds)
    {
        var fadeIn = (int)Math.Round(fadeInSeconds * SampleRate);
        var fadeOut = (int)Math.Round(fadeOutSeconds * SampleRate);
        var gain = 1.0;
        if (fadeIn > 0 && index < fadeIn)
            gain *= Math.Sin(0.5 * Math.PI * (index + 1) / fadeIn);
        var remaining = count - index;
        if (fadeOut > 0 && remaining <= fadeOut)
            gain *= Math.Sin(0.5 * Math.PI * remaining / fadeOut);
        return gain;
    }

    /// <summary>Blendet das Ende kurz aus (der Anfang bleibt unberührt: Die Transiente liegt ganz vorn).</summary>
    private static void FadeOutTail(double[] buffer, double seconds)
    {
        var fade = Math.Min(buffer.Length, (int)Math.Round(seconds * SampleRate));
        for (var i = 0; i < fade; i++)
            buffer[buffer.Length - 1 - i] *= Math.Sin(0.5 * Math.PI * (i + 1) / (fade + 1));
    }

    /// <summary>Auf Spitze 1 normieren (die Lautstärke kommt erst danach).</summary>
    private static double[] Normalize(double[] raw)
    {
        var peak = 0.0;
        foreach (var value in raw)
            peak = Math.Max(peak, Math.Abs(value));
        if (peak > 0)
        {
            for (var i = 0; i < raw.Length; i++)
                raw[i] /= peak;
        }
        return raw;
    }

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
