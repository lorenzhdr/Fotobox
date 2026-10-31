namespace Fotobox.Services.Detection;

/// <summary>
/// Takt der Analysen: Der nächste Start ist nicht „letzter Start + Abstand“ gemessen am tatsächlichen Start, sondern am
/// Sollzeitpunkt. Kommt das Kamerabild später als fällig, geht die Verspätung nicht verloren (sonst läge die Rate bei
/// 10/s und 25 Bildern/s nur bei etwa 8,3/s). Ein Rückstand wird nie in einer Salve aufgeholt: Liegt der nächste Sollzeitpunkt
/// nicht in der Zukunft, gilt jetzt + Abstand. Eine kürzere neue Rate wirkt sofort (gemessen ab dem letzten Start). Nicht threadsicher: wird nur vom
/// Kamera-Handler (seriell) benutzt; alle Zeit läuft über <see cref="TimeProvider"/>.
/// </summary>
internal sealed class AnalysisPacer
{
    private readonly TimeProvider _time;
    private readonly long _origin;
    private TimeSpan _nextDue, _lastStart;
    private bool _hasStarted;

    public AnalysisPacer(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _origin = _time.GetTimestamp();
    }

    /// <summary>Ist eine neue Analyse fällig? Immer, solange noch keine gestartet wurde.</summary>
    /// <param name="interval">Aktueller Sollabstand; ein weiter entfernter Sollzeitpunkt rückt auf letzter Start + Abstand vor (Ratenwechsel).</param>
    public bool IsDue(TimeSpan interval)
    {
        if (!_hasStarted)
            return true;
        var now = _time.GetElapsedTime(_origin);
        ClampDue(interval);
        return now >= _nextDue;
    }

    /// <summary>Hält fest, dass jetzt eine Analyse gestartet wurde, und bestimmt den nächsten Sollzeitpunkt.</summary>
    public void MarkStarted(TimeSpan interval)
    {
        var now = _time.GetElapsedTime(_origin);
        _lastStart = now;
        if (!_hasStarted)
        {
            _hasStarted = true;
            _nextDue = now + interval;
            return;
        }

        ClampDue(interval);
        var next = _nextDue + interval;
        _nextDue = next > now ? next : now + interval;
    }

    // Eine kürzere neue Rate wirkt sofort: Ein weiter entfernter Sollzeitpunkt rückt auf höchstens letzter Start + neuer Abstand.
    private void ClampDue(TimeSpan interval)
    {
        var limit = _lastStart + interval;
        if (_nextDue > limit)
            _nextDue = limit;
    }
}
