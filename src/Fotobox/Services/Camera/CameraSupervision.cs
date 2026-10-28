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
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        Interlocked.Exchange(ref _lastTimestamp, _time.GetTimestamp());
        if (Interlocked.Exchange(ref _hasFrame, 1) == 0)
            _firstFrame.TrySetResult();
    }

    /// <summary>
    /// Wartet bis zu <c>FirstFrameTimeout</c> auf das erste Bild. true = Bild ist da, false = Frist ohne Bild abgelaufen.
    /// Wirft <see cref="OperationCanceledException"/>, wenn <paramref name="ct"/> abgebrochen wird.
    /// </summary>
    public async Task<bool> WaitForFirstFrameAsync(CancellationToken ct = default)
    {
        try
        {
            await _firstFrame.Task.WaitAsync(_firstFrameTimeout, _time, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
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

/// <summary>
/// Wartezeit der Reconnect-Schleife: Liefert die Kamera bei Wiederverbindungsversuchen wiederholt kein Bild,
/// wird das Intervall gedämpft, damit nicht dauernd neue MediaCapture-Objekte entstehen.
/// Hängt zusätzlich noch eine frühere Kamera-Operation im Treiber, wird nur selten neu versucht (<see cref="ShouldAttempt"/>).
/// </summary>
public static class CameraReconnectBackoff
{
    public static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(15);

    /// <summary>Abstand der Versuche, solange noch eine frühere Kamera-Operation im Treiber hängt.</summary>
    public static readonly TimeSpan HangingRetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>Anzahl aufeinanderfolgender Versuche ohne Bild, ab der das lange Intervall gilt.</summary>
    public const int SlowAfterNoImageAttempts = 3;

    public static TimeSpan GetInterval(int consecutiveNoImageAttempts) =>
        consecutiveNoImageAttempts >= SlowAfterNoImageAttempts ? SlowInterval : BaseInterval;

    /// <summary>
    /// Ob die Wiederverbindung jetzt einen Versuch starten soll. Solange noch eine frühere Kamera-Operation im Treiber hängt,
    /// würde jeder neue Versuch nur ein weiteres MediaCapture mit hängender Operation hinterlassen; dann wird nur alle
    /// <see cref="HangingRetryInterval"/> trotzdem versucht. Endet die hängende Operation (z. B. beim Abziehen/Einstecken),
    /// geht es sofort normal weiter.
    /// </summary>
    public static bool ShouldAttempt(int hangingOperations, TimeSpan sinceLastAttempt) =>
        hangingOperations <= 0 || sinceLastAttempt >= HangingRetryInterval;
}

/// <summary>Ein Kameraschritt (Initialisierung, Formatwahl, Start …) hat seine Frist überschritten.</summary>
public sealed class CameraTimeoutException : TimeoutException
{
    public CameraTimeoutException(string step, Task? pending = null)
        : base($"Zeitüberschreitung bei der Kamera ({step}).")
    {
        Pending = pending;
    }

    /// <summary>
    /// Die noch laufende WinRT-Operation des Schritts (falls bekannt). Beteiligte Objekte dürfen erst nach deren
    /// Abschluss freigegeben werden, nie währenddessen (Zugriffsverletzung/Hänger im Treiber möglich).
    /// </summary>
    public Task? Pending { get; }
}

/// <summary>
/// Drosselt Log-Meldungen für Fehler, die bei jedem Kamerabild erneut auftreten (bis zu ca. 30 Meldungen je Sekunde):
/// Gemeldet wird der erste Fehler einer Serie und danach nur noch jeder <see cref="RepeatEvery"/>-te.
/// Ein erfolgreich verarbeitetes Bild beendet die Serie. Threadsicher.
/// </summary>
public sealed class FrameErrorThrottle
{
    /// <summary>Abstand (in Fehlern) der Folgemeldungen einer Serie, bei 30 Bildern/s etwa alle 10 s.</summary>
    public const int RepeatEvery = 300;

    private int _count;

    /// <summary>Zählt einen Fehler. true = jetzt loggen; <paramref name="count"/> ist die Länge der Serie bis hierhin.</summary>
    public bool OnError(out int count)
    {
        count = Interlocked.Increment(ref _count);
        return count == 1 || count % RepeatEvery == 0;
    }

    /// <summary>Beendet die Serie; liefert die Anzahl der Fehler seit dem letzten Erfolg (0 = es gab keine).</summary>
    public int OnSuccess() => Interlocked.Exchange(ref _count, 0);
}
