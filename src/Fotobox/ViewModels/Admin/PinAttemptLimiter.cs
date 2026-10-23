namespace Fotobox.ViewModels.Admin;

/// <summary>
/// Begrenzt PIN-Fehlversuche: nach <see cref="MaxAttempts"/> Fehlversuchen ist die Eingabe für
/// <see cref="LockoutDuration"/> gesperrt. Zeit kommt über <see cref="TimeProvider"/> (testbar).
/// </summary>
public sealed class PinAttemptLimiter
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private int _failures;
    private DateTimeOffset? _lockedUntil;

    public PinAttemptLimiter(TimeProvider time, int maxAttempts = 5, TimeSpan? lockoutDuration = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _time = time;
        MaxAttempts = maxAttempts;
        LockoutDuration = lockoutDuration ?? TimeSpan.FromSeconds(30);
    }

    public int MaxAttempts { get; }

    public TimeSpan LockoutDuration { get; }

    public bool IsLockedOut => RemainingLockout > TimeSpan.Zero;

    /// <summary>Verbleibende Sperrzeit (<see cref="TimeSpan.Zero"/>, wenn nicht gesperrt).</summary>
    public TimeSpan RemainingLockout
    {
        get
        {
            lock (_gate)
            {
                if (_lockedUntil is not { } until)
                    return TimeSpan.Zero;

                var remaining = until - _time.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                    return remaining;

                // Sperre abgelaufen → neue Versuche erlaubt.
                _lockedUntil = null;
                _failures = 0;
                return TimeSpan.Zero;
            }
        }
    }

    /// <summary>Verbleibende Versuche bis zur nächsten Sperre.</summary>
    public int RemainingAttempts
    {
        get
        {
            if (IsLockedOut)
                return 0;
            lock (_gate)
                return Math.Max(0, MaxAttempts - _failures);
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
                _lockedUntil = _time.GetUtcNow() + LockoutDuration;
        }
    }

    public void RegisterSuccess()
    {
        lock (_gate)
        {
            _failures = 0;
            _lockedUntil = null;
        }
    }
}
