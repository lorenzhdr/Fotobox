using Fotobox.Core;
using Windows.Media.Capture;

namespace Fotobox.Services.Camera;

// Hardware-Regler der Kamera (VideoDeviceController). Wie alle MediaCapture-Zugriffe laufen sie auf dem Threadpool (MTA) und mit
// Frist (AwaitStepAsync, hängende Aufrufe zählen in _hangingOperations). Das Gate wird nur kurz zum Lesen der Session-Felder
// gehalten, nie während eines Treiberaufrufs: Start/Stopp/Dispose warten so höchstens auf diese Momentaufnahme. Wird die Session
// währenddessen abgebaut, scheitert der Treiberaufruf mit einer Ausnahme (geloggt); er trifft nie eine neue Session, weil er das
// MediaCapture der Momentaufnahme festhält.
public sealed partial class WinRtCameraService
{
    // Wie lange GetControlsAsync/ein Live-Schreibvorgang auf das Gate wartet (z. B. während eines laufenden Starts).
    private static readonly TimeSpan ControlsGateWait = TimeSpan.FromSeconds(3);

    // Gespeicherter Stand für Starts und Wiederverbindungen (StartAsync und ConfigureControls ersetzen ihn als Ganzes).
    private sealed record ControlState(string? DeviceId, IReadOnlyList<CameraControlValue> Values, CameraPowerline Powerline)
    {
        public static ControlState From(CameraSettings s) => new(
            s.ControlsDeviceId,
            CameraSettings.CloneValues(s.ControlValues).Where(v => v is not null).ToList(),
            s.Powerline);
    }

    private volatile ControlState _controlState = ControlState.From(new CameraSettings());
    private LatestOnlyWorker<LiveControlRequest>? _liveWorker;

    private sealed record LiveControlRequest(IReadOnlyList<CameraControlValue> Values, CameraPowerline Powerline);

    // Momentaufnahme der laufenden Session; Generation zum Erkennen eines Neustarts zwischendurch.
    private sealed record SessionSnapshot(MediaCapture Capture, string? DeviceId, bool Writable, int Generation);

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
        try
        {
            var session = await SnapshotSessionAsync().ConfigureAwait(false);
            if (session is null)
                return CameraControlsSnapshot.Empty;

            var target = CreateTarget(session.Capture);
            return await AwaitStepAsync(
                Task.Run(() => CameraControlAccess.Read(target, session.DeviceId, session.Writable)),
                "Kamerareglerabfrage").ConfigureAwait(false);
        }
        catch (CameraTimeoutException)
        {
            AppLog.Warn("Die Kamera reagiert beim Auslesen der Regler nicht.");
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

    private async Task ApplyLiveAsync(LiveControlRequest request)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            var session = await SnapshotSessionAsync().ConfigureAwait(false);
            if (session is null || !session.Writable)
                return; // Keine Session bzw. nur Livebild (SharedReadOnly): nichts schreiben.

            var target = CreateTarget(session.Capture);
            await AwaitStepAsync(Task.Run(() =>
            {
                // Letzte Prüfung direkt vor dem Schreiben: nur dieselbe, noch laufende Session.
                if (Volatile.Read(ref _disposed) != 0
                    || !ReferenceEquals(Volatile.Read(ref _capture), session.Capture)
                    || session.Generation != Volatile.Read(ref _generation))
                    return;
                CameraControlAccess.Apply(target, request.Values, request.Powerline, live: true);
            }), "Kamerareglerwerte").ConfigureAwait(false);
        }
        catch (CameraTimeoutException)
        {
            AppLog.Warn("Die Kamera reagiert beim Verstellen der Regler nicht.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kameraregler konnten nicht live gesetzt werden.", ex);
        }
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
            if (capture is null || Volatile.Read(ref _disposed) != 0)
                return null;
            return new SessionSnapshot(capture, _activeDeviceId, _sharingMode != MediaCaptureSharingMode.SharedReadOnly,
                Volatile.Read(ref _generation));
        }
        finally
        {
            _gate.Release();
        }
    }

    // Beim Sitzungsaufbau (unter _gate, vor reader.StartAsync): gespeicherte Werte (nur wenn sie zu dieser Kamera gehören) und
    // Flimmerschutz (immer). Fehler und Zeitüberschreitung lassen den Start nie scheitern.
    private async Task ApplySavedControlsAsync(MediaCapture capture, MediaCaptureSharingMode mode, string deviceId)
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
            await AwaitStepAsync(
                Task.Run(() => CameraControlAccess.Apply(target, values, state.Powerline, live: false)),
                "Kamerareglerwerte").ConfigureAwait(false);
            AppLog.Info($"Kameraregler angewendet ({values.Count} gespeicherte Werte, Flimmerschutz {state.Powerline}).");
        }
        catch (CameraTimeoutException)
        {
            AppLog.Warn("Die Kamera reagiert beim Anwenden der gespeicherten Regler nicht, der Start läuft weiter.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Gespeicherte Kameraregler konnten nicht angewendet werden, der Start läuft weiter.", ex);
        }
    }
}
