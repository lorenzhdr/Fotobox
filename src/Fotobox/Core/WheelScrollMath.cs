namespace Fotobox.Core;

/// <summary>
/// Rechnet Mausrad-Deltas in Scrollstrecken um. Windows meldet pro Rastung 120; Präzisions-Trackpads senden viele
/// Ereignisse mit kleinem Delta. WPF wertet nur das Vorzeichen aus, hier zählt der Betrag, damit das Scrollen
/// proportional zur Fingerbewegung bleibt.
/// </summary>
public static class WheelScrollMath
{
    /// <summary>Delta einer vollen Mausrad-Rastung (<c>WHEEL_DELTA</c>).</summary>
    public const int NotchDelta = 120;

    /// <summary>Höhe einer Zeile in WPF-Pixeln (wie <c>ScrollViewer.LineUp</c>).</summary>
    public const double DefaultLineHeight = 16;

    /// <summary>
    /// Scrollstrecke in Pixeln für ein Delta. Positiv = Inhalt nach oben bewegen (Delta &gt; 0, Rad nach vorn).
    /// Bei <paramref name="wheelScrollLines"/> &gt; 0 gilt Delta/120 × Zeilen × Zeilenhöhe; bei 0 oder negativ
    /// (Windows-Einstellung „eine Seite pro Rastung“, Wert -1) gilt Delta/120 × <paramref name="viewportHeight"/>.
    /// </summary>
    public static double PixelsFor(int delta, int wheelScrollLines, double lineHeight = DefaultLineHeight, double viewportHeight = 0)
    {
        double notches = delta / (double)NotchDelta;
        if (wheelScrollLines <= 0)
            return notches * Math.Max(0, viewportHeight);
        return notches * wheelScrollLines * lineHeight;
    }
}

/// <summary>
/// Sammelt Mausrad-Deltas für Ansichten, die zeilenweise scrollen (z. B. virtualisierte Listen), und liefert ganze Rastungen.
/// Der Rest bleibt erhalten; ein Richtungswechsel verwirft ihn.
/// </summary>
public sealed class WheelDeltaAccumulator
{
    private int _rest;

    /// <summary>Noch nicht ausgegebener Rest (|Rest| &lt; 120).</summary>
    public int Rest => _rest;

    /// <summary>Addiert ein Delta und liefert die Zahl ganzer Rastungen (positiv = nach oben, negativ = nach unten).</summary>
    public int Add(int delta)
    {
        if (delta == 0)
            return 0;
        if (_rest != 0 && Math.Sign(_rest) != Math.Sign(delta))
            _rest = 0;
        _rest += delta;
        int notches = _rest / WheelScrollMath.NotchDelta;
        _rest -= notches * WheelScrollMath.NotchDelta;
        return notches;
    }

    /// <summary>Verwirft den Rest.</summary>
    public void Reset() => _rest = 0;
}
