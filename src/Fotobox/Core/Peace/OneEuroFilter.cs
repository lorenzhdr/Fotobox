namespace Fotobox.Core.Peace;

/// <summary>
/// One-Euro-Filter (Casiez et al.) für ein verrauschtes Signal bei unregelmäßigem Takt: Im Stillstand starke Glättung
/// (<c>minCutoff</c>), bei schneller Bewegung steigt die Grenzfrequenz mit der geschätzten Geschwindigkeit (<c>beta</c>), die
/// Glättung lässt dann nach und der Nachlauf bleibt klein. <see cref="Velocity"/> ist die gefilterte Ableitung (Einheiten pro Sekunde).
/// UI-frei, nicht threadsicher.
/// </summary>
public sealed class OneEuroFilter
{
    private readonly double _minCutoff;
    private readonly double _beta;
    private readonly double _derivativeCutoff;
    private bool _initialized;
    private double _value;
    private double _lastMeasured;

    /// <param name="minCutoff">Grenzfrequenz (Hz) im Stillstand; kleiner = ruhiger, aber träger.</param>
    /// <param name="beta">Zuwachs der Grenzfrequenz je Geschwindigkeitseinheit (Hz je Einheit/s, siehe <c>speedScale</c> bei <see cref="Filter"/>).</param>
    /// <param name="derivativeCutoff">Grenzfrequenz (Hz) der Geschwindigkeitsschätzung.</param>
    public OneEuroFilter(double minCutoff, double beta, double derivativeCutoff = 1.0)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _derivativeCutoff = derivativeCutoff;
    }

    /// <summary>Gefilterter Wert (nach dem letzten <see cref="Filter"/>).</summary>
    public double Value => _value;

    /// <summary>Gefilterte Geschwindigkeit in Einheiten pro Sekunde.</summary>
    public double Velocity { get; private set; }

    public bool IsInitialized => _initialized;

    /// <summary>Setzt den Zustand auf <paramref name="value"/> (Geschwindigkeit 0).</summary>
    public void Reset(double value)
    {
        _initialized = true;
        _value = value;
        _lastMeasured = value;
        Velocity = 0;
    }

    /// <summary>
    /// Nimmt eine Messung entgegen. <paramref name="dtSeconds"/> = Abstand zur vorigen Messung (≤ 0: Messung wird ignoriert);
    /// <paramref name="speedScale"/> = Größe, in der die Geschwindigkeit für die Grenzfrequenz gemessen wird (z. B. die Gesichtsbreite,
    /// damit „schnell“ unabhängig vom Abstand zur Kamera ist).
    /// </summary>
    public double Filter(double measured, double dtSeconds, double speedScale = 1)
    {
        if (!_initialized)
        {
            Reset(measured);
            return _value;
        }
        if (!(dtSeconds > 0))
            return _value;

        var rawVelocity = (measured - _lastMeasured) / dtSeconds;
        _lastMeasured = measured;
        var ad = Alpha(dtSeconds, _derivativeCutoff);
        Velocity = ad * rawVelocity + (1 - ad) * Velocity;

        var scale = speedScale > 0 ? speedScale : 1;
        var cutoff = _minCutoff + _beta * Math.Abs(Velocity) / scale;
        var a = Alpha(dtSeconds, cutoff);
        _value = a * measured + (1 - a) * _value;
        return _value;
    }

    private static double Alpha(double dt, double cutoff)
    {
        var tau = 1.0 / (2 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }
}
