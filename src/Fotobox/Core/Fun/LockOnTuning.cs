namespace Fotobox.Core.Fun;

/// <summary>
/// Kennlinien der Einrast-Animation aus der Intensität (<see cref="FunSettings.LockOnIntensityPercent"/>, 0–100). 50 % ergibt
/// exakt das ursprüngliche Aussehen (Startskala 1,8, Puls 1,0, Blinken 180 ms zwischen 1 und 0,3, Tonfaktor 1,0); 0 % ist dezent,
/// aber noch sichtbar bzw. hörbar, 100 % deutlich wilder. UI-frei und ohne Zustand.
/// </summary>
public static class LockOnTuning
{
    /// <summary>Die Intensität, die dem ursprünglichen Aussehen entspricht.</summary>
    public const int NeutralPercent = 50;

    /// <summary>Dauer, auf die die festen Animationszeiten (Sekunden) ursprünglich abgestimmt waren.</summary>
    public const double ReferenceSeconds = 1.2;

    /// <summary>Größte Pulsamplitude (bei 100 %); die Zeichnung begrenzt den Puls darauf.</summary>
    public const double MaxPulse = 2.0;

    /// <summary>Intensität auf den gültigen Bereich klemmen.</summary>
    public static int Clamp(int percent) =>
        Math.Clamp(percent, SettingsLimits.FunLockOnIntensityMin, SettingsLimits.FunLockOnIntensityMax);

    /// <summary>Dauer auf den gültigen Bereich klemmen (Millisekunden).</summary>
    public static int ClampMilliseconds(int milliseconds) =>
        Math.Clamp(milliseconds, SettingsLimits.FunLockOnMillisecondsMin, SettingsLimits.FunLockOnMillisecondsMax);

    /// <summary>Zeitmaßstab der festen Animationszeiten: Dauer / 1,2 s (bei 1200 ms exakt 1).</summary>
    public static double TimeScale(int milliseconds) => ClampMilliseconds(milliseconds) / (ReferenceSeconds * 1000);

    /// <summary>Startskala des Zusammenziehens (ShrinkPulse): 1,2 bei 0 %, 1,8 bei 50 %, 2,4 bei 100 % (linear).</summary>
    public static double ShrinkStartScale(int percent) => Round(1.2 + 1.2 * Fraction(percent));

    /// <summary>
    /// Höhe des Pulses (Strichstärke, Leuchten und Aufblähen steigen mit ihm): 0,4 bei 0 %, 1,0 bei 50 %, 1,6 bei 100 % (linear).
    /// Ein Wert 0 wäre unsichtbar, deshalb nicht proportional zur Intensität.
    /// </summary>
    public static double PulseAmplitude(int percent) => Round(0.4 + 1.2 * Fraction(percent));

    /// <summary>Faktor für Strichstärke und Leuchten der Scan-Linie: 0,6 bei 0 %, 1,0 bei 50 %, 1,4 bei 100 %.</summary>
    public static double LineFactor(int percent) => Round(0.6 + 0.8 * Fraction(percent));

    /// <summary>Dauer einer Blinkhälfte von „LOCKING…“ in Millisekunden: 300 bei 0 %, 180 bei 50 %, 120 bei 100 % (stückweise linear).</summary>
    public static double BlinkPeriodMilliseconds(int percent)
    {
        var k = Fraction(percent);
        return Round(k <= 0.5 ? 300 - 240 * k : 180 - 120 * (k - 0.5));
    }

    /// <summary>Geringste Deckkraft beim Blinken: 0,6 bei 0 %, 0,3 bei 50 %, 0,1 bei 100 % (stückweise linear).</summary>
    public static double BlinkMinOpacity(int percent)
    {
        var k = Fraction(percent);
        return Round(k <= 0.5 ? 0.6 - 0.6 * k : 0.3 - 0.4 * (k - 0.5));
    }

    /// <summary>
    /// Lautstärkefaktor der Töne gegenüber der Standardlautstärke: 0,35 bei 0 %, 1,0 bei 50 %, 1,6 bei 100 % (stückweise linear).
    /// Die Standardspitze ist 0,35 der Vollaussteuerung, der Höchstwert liegt also bei 0,56 und übersteuert nicht.
    /// </summary>
    public static double SoundGain(int percent)
    {
        var k = Fraction(percent);
        return Round(k <= 0.5 ? 0.35 + 1.3 * k : 1.0 + 1.2 * (k - 0.5));
    }

    /// <summary>Intensität auf die 5-%-Stufen der Ton-Puffer runden (Zwischenwerte der Einstellung gibt es nur in 5er-Schritten).</summary>
    public static int SoundStep(int percent)
    {
        var step = SettingsLimits.FunLockOnIntensityStep;
        return Math.Clamp((int)Math.Round(Clamp(percent) / (double)step, MidpointRounding.AwayFromZero) * step,
            SettingsLimits.FunLockOnIntensityMin, SettingsLimits.FunLockOnIntensityMax);
    }

    private static double Fraction(int percent) => Clamp(percent) / 100.0;

    // Rundung gegen Gleitkomma-Rauschen, damit 50 % exakt die alten Werte liefert.
    private static double Round(double value) => Math.Round(value, 6);
}
