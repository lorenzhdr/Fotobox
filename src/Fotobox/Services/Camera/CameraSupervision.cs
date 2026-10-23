namespace Fotobox.Services.Camera;

/// <summary>
/// Entscheidet, ob eine laufende Kamera „stumm“ geworden ist: Kommt nach dem Start nicht innerhalb von
/// <see cref="FirstFrameTimeout"/> ein erstes Bild an (oder später für <see cref="StallTimeout"/> keines mehr),
/// gilt sie als hängend. Hardware-unabhängig und über <see cref="TimeProvider"/> testbar; threadsicher.
/// </summary>
public sealed class FrameWatchdog
{
    /// <summary>Frist bis zum ersten Bild nach dem Start (Autobelichtung/virtuelle Kameras brauchen etwas).</summary>
    public static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Frist ohne neues Bild, nachdem die Kamera schon geliefert hat.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private readonly TimeSpan _firstFrameTimeout;
    private readonly TimeSpan _stallTimeout;
    private long _lastTimestamp;
    private int _hasFrame;

    public FrameWatchdog(TimeProvider time, TimeSpan? firstFrameTimeout = null, TimeSpan? stallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _firstFrameTimeout = firstFrameTimeout ?? FirstFrameTimeout;
        _stallTimeout = stallTimeout ?? StallTimeout;
        _lastTimestamp = time.GetTimestamp();
    }

    public bool HasFrame => Volatile.Read(ref _hasFrame) != 0;

    /// <summary>Wird bei jedem tatsächlich gelieferten Bild aufgerufen (auch aus dem Frame-Handler).</summary>
    public void NotifyFrame()
    {
        Volatile.Write(ref _hasFrame, 1);
        Interlocked.Exchange(ref _lastTimestamp, _time.GetTimestamp());
    }

    /// <summary>Startet die Frist für das erste Bild neu (z. B. beim Übergang zu „Running“); nach dem ersten Bild wirkungslos.</summary>
    public void Arm()
    {
        if (!HasFrame)
            Interlocked.Exchange(ref _lastTimestamp, _time.GetTimestamp());
    }

    /// <summary>Verbleibende Zeit bis zum Alarm; null oder negativ bedeutet: bereits überschritten.</summary>
    public TimeSpan TimeUntilStall
    {
        get
        {
            var last = Interlocked.Read(ref _lastTimestamp);
            var elapsed = _time.GetElapsedTime(last);
            return (HasFrame ? _stallTimeout : _firstFrameTimeout) - elapsed;
        }
    }

    public bool IsStalled => TimeUntilStall <= TimeSpan.Zero;
}

/// <summary>
/// Plan für kurze, stille Wiederholungen eines fehlgeschlagenen Kamerastarts (z. B. direkt nach einem Kamerawechsel,
/// solange Media Foundation die alte Session noch freigibt). Danach greift die normale Fehleranzeige samt Reconnect-Schleife.
/// </summary>
public static class CameraStartRetry
{
    /// <summary>Abstände vor der 1., 2. und 3. Wiederholung.</summary>
    public static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1000),
    ];

    /// <summary>Gesamtbudget (inkl. Dauer der Versuche), nach dem keine stille Wiederholung mehr startet.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <param name="retriesDone">Bereits ausgeführte Wiederholungen (0 nach dem ersten, fehlgeschlagenen Versuch).</param>
    /// <param name="elapsed">Seit dem ersten Versuch vergangene Zeit.</param>
    public static bool TryGetDelay(int retriesDone, TimeSpan elapsed, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (retriesDone < 0 || retriesDone >= Delays.Length)
            return false;
        var next = Delays[retriesDone];
        if (elapsed + next > Budget)
            return false;
        delay = next;
        return true;
    }
}

/// <summary>Ein Kameraschritt (Initialisierung, Formatwahl, Start …) hat seine Frist überschritten.</summary>
public sealed class CameraTimeoutException : TimeoutException
{
    public CameraTimeoutException(string step)
        : base($"Zeitüberschreitung bei der Kamera ({step}).")
    {
    }
}
