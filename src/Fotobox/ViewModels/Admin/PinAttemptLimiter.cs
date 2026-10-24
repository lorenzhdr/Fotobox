namespace Fotobox.ViewModels.Admin;

/// <summary>
/// Begrenzt PIN-Fehlversuche: nach <see cref="MaxAttempts"/> Fehlversuchen ist die Eingabe für
/// <see cref="LockoutDuration"/> gesperrt. Jeder weitere Fehlversuch (der Zähler wird erst durch eine richtige
/// PIN zurückgesetzt, nicht durch Ablauf der Sperre) verlängert die Sperre stufenweise (30 s, 60 s, 2 min,
/// 5 min, höchstens 15 min). Zeit kommt über <see cref="TimeProvider"/> (testbar) und ist monoton (Uhrsprünge ändern die Sperre nicht).
/// </summary>
public sealed class PinAttemptLimiter
{
    // Faktoren auf die Grunddauer: 30 s, 60 s, 2 min, 5 min, 15 min (letzte Stufe gilt dauerhaft).
    private static readonly int[] LockoutFactors = [1, 2, 4, 10, 30];

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private int _failures;
    // Monotone Zeit (Stopwatch-Ticks): ein Sprung der Systemuhr verlängert oder verkürzt die Sperre nicht.
    private long? _lockStart;
    private TimeSpan _lockLength;

    public PinAttemptLimiter(TimeProvider time, int maxAttempts = 5, TimeSpan? lockoutDuration = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _time = time;
        MaxAttempts = maxAttempts;
        LockoutDuration = lockoutDuration ?? TimeSpan.FromSeconds(30);
    }

    public int MaxAttempts { get; }

    /// <summary>Grunddauer der ersten Sperre.</summary>
    public TimeSpan LockoutDuration { get; }

    /// <summary>Längste Sperrdauer (letzte Eskalationsstufe).</summary>
    public TimeSpan MaxLockoutDuration => LockoutDuration * LockoutFactors[^1];

    public bool IsLockedOut => RemainingLockout > TimeSpan.Zero;

    /// <summary>Verbleibende Sperrzeit (<see cref="TimeSpan.Zero"/>, wenn nicht gesperrt).</summary>
    public TimeSpan RemainingLockout
    {
        get
        {
            lock (_gate)
            {
                if (_lockStart is not { } start)
                    return TimeSpan.Zero;

                var remaining = _lockLength - _time.GetElapsedTime(start);
                if (remaining > TimeSpan.Zero)
                    return remaining;

                // Sperre abgelaufen → neuer Versuch erlaubt. Der Fehlerzähler bleibt bewusst stehen:
                // Ein weiterer Fehlversuch löst sofort die nächsthöhere Sperrstufe aus.
                _lockStart = null;
                return TimeSpan.Zero;
            }
        }
    }

    /// <summary>Verbleibende Versuche bis zur nächsten Sperre (nach einer ersten Sperre immer 1).</summary>
    public int RemainingAttempts
    {
        get
        {
            if (IsLockedOut)
                return 0;
            lock (_gate)
                return Math.Max(1, MaxAttempts - _failures);
        }
    }

    /// <summary>Registriert einen Fehlversuch. Während einer Sperre wird er ignoriert.</summary>
    public void RegisterFailure()
    {
        if (IsLockedOut)
            return;

        lock (_gate)
        {
            _failures++;
            if (_failures >= MaxAttempts)
            {
                var level = Math.Min(_failures - MaxAttempts, LockoutFactors.Length - 1);
                _lockStart = _time.GetTimestamp();
                _lockLength = LockoutDuration * LockoutFactors[level];
            }
        }
    }

    public void RegisterSuccess()
    {
        lock (_gate)
        {
            _failures = 0;
            _lockStart = null;
        }
    }
}
