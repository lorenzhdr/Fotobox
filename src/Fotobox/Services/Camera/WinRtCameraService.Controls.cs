using Fotobox.Core;
using Windows.Media.Capture;

namespace Fotobox.Services.Camera;

// Hardware-Regler der Kamera (VideoDeviceController). Wie alle MediaCapture-Zugriffe laufen sie auf dem Threadpool (MTA) und mit
// Frist (AwaitStepAsync, hängende Aufrufe zählen in _hangingOperations). Das Gate wird nur kurz zum Lesen der Session-Felder
// gehalten, nie während eines Treiberaufrufs: Start/Stopp/Dispose warten so höchstens auf diese Momentaufnahme. Wird die Session
// währenddessen abgebaut, scheitert der Treiberaufruf mit einer Ausnahme (geloggt); er trifft nie eine neue Session, weil er das
// MediaCapture der Momentaufnahme festhält.
//
// Schreibt nur Abweichungen: Je Session führt ein ControlStateCache den zuletzt gelesenen bzw. erfolgreich angewendeten Stand, der
// Admin schickt bei jeder Bewegung alle Regler. Die Frist des Schreibschritts richtet sich nach der Zahl der zu schreibenden Regler
// (ControlsTimeout), weil die erweiterten Regler je Aufruf bis zu 3 s warten. Hängt ein Aufruf trotzdem, wird die Aufgabe je Session
// vorgemerkt (_sessionPending): Die Session wird erst nach deren Abschluss freigegeben (DisposeSessionObjectsAsync), und der
// Live-Arbeiter beginnt keinen neuen Schreibvorgang, solange sie läuft (höchstens ein Schreibvorgang gleichzeitig).
public sealed partial class WinRtCameraService
{
    // Wie lange GetControlsAsync/ein Live-Schreibvorgang auf das Gate wartet (z. B. während eines laufenden Starts).
    private static readonly TimeSpan ControlsGateWait = TimeSpan.FromSeconds(3);

    // Wie lange der Live-Arbeiter nach einem Timeout höchstens auf den hängenden Schreibvorgang wartet, bevor er weitermacht.
    private static readonly TimeSpan LiveHangWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ControlsTimeoutMax = TimeSpan.FromSeconds(30);

    // Gespeicherter Stand für Starts und Wiederverbindungen (nur ConfigureControls ersetzt ihn als Ganzes; StartAsync nie, weil
    // der Admin mit dem Entwurf startet).
    private sealed record ControlState(string? DeviceId, IReadOnlyList<CameraControlValue> Values, CameraPowerline Powerline)
    {
        public static ControlState From(CameraSettings s) => new(
            s.ControlsDeviceId,
            CameraSettings.CloneValues(s.ControlValues).Where(v => v is not null).ToList(),
            s.Powerline);
    }

    private volatile ControlState _controlState = ControlState.From(new CameraSettings());
    private LatestOnlyWorker<LiveControlRequest>? _liveWorker;

    // Stand der laufenden Session (gesetzt vor _capture, geleert mit ihr im Abbau).
    private volatile ControlStateCache? _controlCache;

    // Noch hängende Reglerschreibvorgänge der laufenden (oder gerade aufgebauten) Session; der Abbau wartet darauf.
    private readonly object _pendingLock = new();
    private Task? _sessionPending;
    private readonly CancellationTokenSource _lifetime = new();

    private sealed record LiveControlRequest(IReadOnlyList<CameraControlValue> Values, CameraPowerline Powerline);

    // Momentaufnahme der laufenden Session; Generation zum Erkennen eines Neustarts zwischendurch.
    private sealed record SessionSnapshot(MediaCapture Capture, string? DeviceId, bool Writable, int Generation, ControlStateCache Cache);

    // Haupt-Weg: MediaDeviceControl-Regler; Ersatzweg für Zoom/Belichtung/Weißabgleich/Fokus/Belichtungskorrektur: die erweiterten
    // WinRT-Regler (viele eingebaute Kameras bieten nur diese).
    private static ICameraControlTarget CreateTarget(MediaCapture capture)
    {
        var controller = capture.VideoDeviceController;
        return new FallbackControlTarget(new VideoDeviceControllerTarget(controller), new ExtendedControlTarget(controller));
    }

    public string? ActiveDeviceId => Volatile.Read(ref _activeDeviceId);

    public void ConfigureControls(CameraSettings saved)
    {
        if (saved is null)
            return;
        _controlState = ControlState.From(saved);
    }

    public Task<CameraControlsSnapshot> GetControlsAsync() => Task.Run(GetControlsCoreAsync);

    private async Task<CameraControlsSnapshot> GetControlsCoreAsync()
    {
        SessionSnapshot? session = null;
        try
        {
            session = await SnapshotSessionAsync().ConfigureAwait(false);
            if (session is null)
                return CameraControlsSnapshot.Empty;

            var target = CreateTarget(session.Capture);
            var read = Task.Run(() => CameraControlAccess.Read(target, session.DeviceId, session.Writable));
            AddSessionPending(session.Capture, read); // schon während des Aufrufs: ein Abbau gibt die Session nicht darunter frei
            var snapshot = await AwaitStepAsync(read, "Kamerareglerabfrage").ConfigureAwait(false);
            session.Cache.Seed(snapshot); // Der Treiber ist die Wahrheit: Grundlage für „nur Abweichendes schreiben“.
            return snapshot;
        }
        catch (CameraTimeoutException ex)
        {
            AppLog.Warn("Die Kamera reagiert beim Auslesen der Regler nicht.");
            if (session is not null && ex.Pending is { } pending)
                AddSessionPending(session.Capture, pending);
            return CameraControlsSnapshot.Empty;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kameraregler konnten nicht ausgelesen werden.", ex);
            return CameraControlsSnapshot.Empty;
        }
    }

    public Task ApplyControlsLiveAsync(IReadOnlyList<CameraControlValue> values, CameraPowerline powerline)
    {
        if (values is null || Volatile.Read(ref _disposed) != 0)
            return Task.CompletedTask;
        var worker = _liveWorker;
        if (worker is null)
            return Task.CompletedTask;

        // Kopie: Der Aufrufer darf seine Liste danach weiter ändern.
        var copy = values.Where(v => v is not null).Select(v => v.Clone()).ToList();
        return worker.SubmitAsync(new LiveControlRequest(copy, powerline));
    }

    private void InitializeControls()
    {
        _liveWorker = new LatestOnlyWorker<LiveControlRequest>(
            ApplyLiveAsync,
            ex => AppLog.Warn("Live-Anwendung der Kameraregler ist fehlgeschlagen.", ex));
    }

    // Frist des Schreibschritts: Grundfrist plus je zu schreibendem Regler zwei Aufrufe der erweiterten Regler (je bis CallTimeout),
    // gedeckelt. Ein Regler mit mehr Aufrufen (Weißabgleich: Automatik aus, Preset, Wert) ist selten und deckt die Reserve der Grundfrist.
    internal static TimeSpan ControlsTimeout(int writes)
    {
        var timeout = StepTimeout + TimeSpan.FromTicks(ExtendedControlTarget.CallTimeout.Ticks * 2 * Math.Max(0, writes));
        return timeout > ControlsTimeoutMax ? ControlsTimeoutMax : timeout;
    }

    private async Task ApplyLiveAsync(LiveControlRequest request)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        SessionSnapshot? session = null;
        try
        {
            session = await SnapshotSessionAsync().ConfigureAwait(false);
            if (session is null || !session.Writable)
                return; // Keine Session bzw. nur Livebild (SharedReadOnly): nichts schreiben.

            var cache = session.Cache;
            var writes = cache.CountPending(request.Values, request.Powerline);
            if (writes == 0)
                return; // Die Kamera steht schon so.

            var target = CreateTarget(session.Capture);
            var write = Task.Run(() =>
            {
                // Letzte Prüfung direkt vor dem Schreiben: nur dieselbe, noch laufende Session.
                if (Volatile.Read(ref _disposed) != 0
                    || !ReferenceEquals(Volatile.Read(ref _capture), session.Capture)
                    || session.Generation != Volatile.Read(ref _generation))
                    return;
                CameraControlAccess.Apply(target, request.Values, request.Powerline, live: true, cache);
            });
            AddSessionPending(session.Capture, write); // schon während des Aufrufs, nicht erst nach einem Timeout
            await AwaitStepAsync(write, "Kamerareglerwerte", ControlsTimeout(writes)).ConfigureAwait(false);
        }
        catch (CameraTimeoutException ex)
        {
            AppLog.Warn("Die Kamera reagiert beim Verstellen der Regler nicht.");
            if (session is not null && ex.Pending is { } pending)
            {
                // Die Session erst nach dem hängenden Aufruf freigeben, und bis dahin keinen neuen Schreibvorgang beginnen
                // (der Arbeiter wartet hier; neuere Anforderungen bleiben als „neueste gewinnt“ vorgemerkt).
                AddSessionPending(session.Capture, pending);
                await WaitForHangingWriteAsync(pending).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kameraregler konnten nicht live gesetzt werden.", ex);
        }
    }

    // Wartet (höchstens LiveHangWait, nicht über Dispose hinaus) auf einen hängenden Schreibvorgang. Wirft nie.
    private async Task WaitForHangingWriteAsync(Task pending)
    {
        try
        {
            await pending.WaitAsync(LiveHangWait, _lifetime.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AppLog.Warn("Der hängende Reglerschreibvorgang endet nicht, weitere Schreibvorgänge folgen trotzdem.");
        }
        catch (Exception)
        {
            // Abbruch durch Dispose oder Fehler des Vorgangs (schon geloggt): weiter.
        }
    }

    // ---- Hängende Aufgaben je Session ----

    internal static Task? CombinePending(Task? a, Task? b)
    {
        if (a is null || a.IsCompleted)
            return b is { IsCompleted: false } ? b : null;
        if (b is null || b.IsCompleted)
            return a;
        return Task.WhenAll(a, b);
    }

    // Merkt eine noch laufende Aufgabe für die Session mit diesem MediaCapture vor (nur wenn sie noch die aktuelle ist; eine schon
    // abgebaute Session wurde bereits freigegeben).
    private void AddSessionPending(MediaCapture capture, Task pending)
    {
        lock (_pendingLock)
        {
            if (!ReferenceEquals(Volatile.Read(ref _capture), capture))
                return;
            _sessionPending = CombinePending(_sessionPending, pending);
        }
    }

    // Beim Aufbau (Session noch nicht in _capture eingetragen): Aufgabe für die entstehende Session vormerken.
    private void SetBuildPending(Task pending)
    {
        lock (_pendingLock)
            _sessionPending = CombinePending(_sessionPending, pending);
    }

    // Wie lange der Abbau auf einen gerade laufenden (nicht hängenden) Reglerzugriff wartet, bevor er die Freigabe in den Hintergrund
    // verschiebt: Ein Zugriff, der in Millisekunden endet, soll den Neustart nicht gegen eine noch nicht freigegebene Kamera laufen lassen.
    private static readonly TimeSpan SessionPendingGrace = TimeSpan.FromSeconds(1);

    // Beim Abbau: alles Vorgemerkte übernehmen (der Abbau gibt dann erst nach dessen Abschluss frei). Null = nichts mehr offen.
    private async Task<Task?> TakeSessionPendingAsync()
    {
        Task? pending;
        lock (_pendingLock)
        {
            pending = _sessionPending;
            _sessionPending = null;
        }
        if (pending is not { IsCompleted: false })
            return null;

        try { await pending.WaitAsync(SessionPendingGrace).ConfigureAwait(false); }
        catch { /* Timeout oder Fehler des Zugriffs (geloggt): der Aufrufer prüft IsCompleted */ }
        return pending.IsCompleted ? null : pending;
    }

    // Gate kurz halten, Session-Felder lesen. null = keine Session, Dienst beendet oder Gate nicht rechtzeitig frei.
    private async Task<SessionSnapshot?> SnapshotSessionAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return null;

        if (!await _gate.WaitAsync(ControlsGateWait).ConfigureAwait(false))
        {
            AppLog.Info("Kameraregler: Die Kamera ist gerade beschäftigt, die Anfrage entfällt.");
            return null;
        }

        try
        {
            var capture = _capture;
            var cache = _controlCache;
            if (capture is null || cache is null || Volatile.Read(ref _disposed) != 0)
                return null;
            return new SessionSnapshot(capture, _activeDeviceId, _sharingMode != MediaCaptureSharingMode.SharedReadOnly,
                Volatile.Read(ref _generation), cache);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Beim Sitzungsaufbau (unter _gate, vor reader.StartAsync): gespeicherte Werte (nur wenn sie zu dieser Kamera gehören) und
    // Flimmerschutz (immer). Fehler und Zeitüberschreitung lassen den Start nie scheitern; bei einer Zeitüberschreitung wird die
    // hängende Aufgabe für die entstehende Session vorgemerkt (SetBuildPending), damit sie nicht währenddessen freigegeben wird.
    private async Task ApplySavedControlsAsync(MediaCapture capture, MediaCaptureSharingMode mode, string deviceId, ControlStateCache cache)
    {
        try
        {
            if (mode == MediaCaptureSharingMode.SharedReadOnly)
                return; // Nur Livebild: nie schreiben.

            var state = _controlState;
            var values = CameraControlAccess.ValuesFor(state.DeviceId, state.Values, deviceId);
            if (values.Count == 0 && state.Powerline == CameraPowerline.Unchanged)
                return;

            var target = CreateTarget(capture);
            // Neue Session: der Cache ist leer, alles wird geschrieben.
            var apply = Task.Run(() => CameraControlAccess.Apply(target, values, state.Powerline, live: false, cache));
            SetBuildPending(apply); // schon während des Aufrufs: scheitert der Start dazwischen, wird nicht darunter freigegeben
            await AwaitStepAsync(apply, "Kamerareglerwerte", ControlsTimeout(values.Count + 1)).ConfigureAwait(false);
            AppLog.Info($"Kameraregler angewendet ({values.Count} gespeicherte Werte, Flimmerschutz {state.Powerline}).");
        }
        catch (CameraTimeoutException ex)
        {
            AppLog.Warn("Die Kamera reagiert beim Anwenden der gespeicherten Regler nicht, der Start läuft weiter.");
            if (ex.Pending is { } pending)
                SetBuildPending(pending);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Gespeicherte Kameraregler konnten nicht angewendet werden, der Start läuft weiter.", ex);
        }
    }
}
