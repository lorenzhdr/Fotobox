namespace Fotobox.Services.Detection;

/// <summary>
/// Schützt eine native Ressource (z. B. eine ONNX-<c>InferenceSession</c>) davor, während eines laufenden Aufrufs freigegeben zu
/// werden (sonst Access Violation in der nativen DLL). Aufrufer melden jeden Zugriff mit <see cref="TryEnter"/>/<see cref="Exit"/>
/// an. <see cref="RequestReleaseAsync"/> sperrt neue Zugriffe und führt <c>release</c> genau einmal aus: sofort, wenn kein Zugriff
/// läuft, sonst durch den letzten <see cref="Exit"/>. Der Wartende gibt nach der Frist auf; die Freigabe erfolgt dann trotzdem
/// später durch den laufenden Zugriff selbst.
/// </summary>
internal sealed class DeferredReleaseGate
{
    private readonly object _lock = new();
    private readonly Action _release;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _releaseRequested;
    private bool _releaseStarted;

    public DeferredReleaseGate(Action release) => _release = release;

    /// <summary>Freigabe wurde angefordert (neue Zugriffe sind abgelehnt).</summary>
    public bool IsReleaseRequested
    {
        get { lock (_lock) return _releaseRequested; }
    }

    /// <summary>Die Freigabe ist ausgeführt.</summary>
    public bool IsReleased => _released.Task.IsCompleted;

    /// <summary>Beginnt einen Zugriff. false = Freigabe bereits angefordert, die Ressource darf nicht mehr benutzt werden.</summary>
    public bool TryEnter()
    {
        lock (_lock)
        {
            if (_releaseRequested)
                return false;
            _active++;
            return true;
        }
    }

    /// <summary>Beendet einen mit <see cref="TryEnter"/> begonnenen Zugriff (immer im <c>finally</c>).</summary>
    public void Exit()
    {
        bool runRelease;
        lock (_lock)
        {
            _active--;
            runRelease = ShouldStartReleaseLocked();
        }
        if (runRelease)
            RunRelease();
    }

    /// <summary>Fordert die Freigabe an und wartet höchstens <paramref name="wait"/> darauf. Wirft nie.</summary>
    public async Task RequestReleaseAsync(TimeSpan wait)
    {
        bool runRelease;
        lock (_lock)
        {
            _releaseRequested = true;
            runRelease = ShouldStartReleaseLocked();
        }
        if (runRelease)
            RunRelease();

        try
        {
            await _released.Task.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Der laufende Zugriff gibt nach seinem Ende selbst frei.
        }
        catch
        {
            // wirft nie
        }
    }

    private bool ShouldStartReleaseLocked()
    {
        if (!_releaseRequested || _active > 0 || _releaseStarted)
            return false;
        _releaseStarted = true;
        return true;
    }

    private void RunRelease()
    {
        try
        {
            _release();
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("Freigabe des Erkennungsverfahrens fehlgeschlagen.", ex);
        }
        finally
        {
            _released.TrySetResult();
        }
    }
}
