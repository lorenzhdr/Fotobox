using System.Diagnostics;
using Fotobox.Core;
using Microsoft.ML.OnnxRuntime;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>
/// Gemeinsame Frist für das Laden aller Modelle eines Detektors auf dem Gerät (<see cref="AccelerationTimeouts.DeviceLoad"/>): Was
/// davon verbraucht ist, steht für das nächste Modell nicht mehr zur Verfügung.
/// </summary>
internal sealed class DeviceBudget(TimeSpan total)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public TimeSpan Remaining
    {
        get
        {
            var left = total - _watch.Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }
}

/// <summary>
/// Eine ONNX-Session eines Modells auf dem geplanten Gerät, mit Rückfall auf die CPU: Scheitert das Laden (Ausnahme oder Frist) auf
/// dem Gerät, wird einmal auf der CPU nachgeladen; scheitert ein <c>Run</c> auf dem Gerät mit einer <see cref="OnnxRuntimeException"/>
/// (z. B. Grafiktreiber zurückgesetzt), wird die Session einmal durch eine CPU-Session ersetzt und der Aufruf wiederholt. Beides wird
/// dem <see cref="IModelPlacement"/> gemeldet. Danach bleibt das Modell auf der CPU. <see cref="Run{T}"/> gehört in die Sperre des
/// Detektors (<c>_runLock</c>): Dort läuft kein anderer <c>Run</c>, die alte Session darf also freigegeben werden. Die Freigabe
/// der ganzen Instanz geht nur über das <see cref="DeferredReleaseGate"/> des Detektors.
/// </summary>
internal sealed class AcceleratedSession : IDisposable
{
    private readonly ModelId _model;
    private readonly string _modelPath;
    private readonly IModelPlacement? _placement;
    private InferenceSession _session;
    private AcceleratorDevice? _device;

    private AcceleratedSession(ModelId model, string modelPath, IModelPlacement? placement, InferenceSession session, AcceleratorDevice? device)
    {
        _model = model;
        _modelPath = modelPath;
        _placement = placement;
        _session = session;
        _device = device;
    }

    public InferenceSession Session => _session;

    /// <summary>Gerät der Session; null = CPU.</summary>
    public AcceleratorDevice? Device => _device;

    /// <summary>
    /// Lädt das Modell auf dem geplanten Gerät (<paramref name="prepare"/> = Namen auslesen, prüfen und Aufwärmlauf; läuft mit auf dem
    /// Gerät) und fällt bei Fehler oder Fristende auf die CPU zurück. Blockiert (nur auf dem Threadpool aufrufen). Wirft nur, wenn auch
    /// der CPU-Weg scheitert.
    /// </summary>
    public static AcceleratedSession Load<TState>(ModelId model, string modelPath, IModelPlacement? placement, DeviceBudget budget,
        Func<InferenceSession, TState> prepare, out TState state)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        AcceleratorDevice? device = null;
        try
        {
            device = placement?.GetDevice(model);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Beschleunigung: Der Plan für {model} war nicht abrufbar, es läuft die CPU.", ex);
        }

        if (device is { IsCpu: false } && budget.Remaining <= TimeSpan.Zero)
        {
            const string reason = "Die Ladefrist auf dem Gerät war schon aufgebraucht";
            AppLog.Warn($"Beschleunigung: {model} wird nicht auf „{device.Name}“ geladen ({reason}) – Rückfall auf die CPU.");
            Report(placement, model, device, reason, atRuntime: false);
        }
        else if (device is { IsCpu: false })
        {
            var attempt = Task.Run(() =>
            {
                var session = OnnxSessionFactory.Create(modelPath, device);
                try
                {
                    return (Session: session, State: prepare(session));
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            });

            string reason;
            try
            {
                if (attempt.Wait(budget.Remaining))
                {
                    state = attempt.Result.State;
                    return new AcceleratedSession(model, modelPath, placement, attempt.Result.Session, device);
                }

                reason = $"Zeitüberschreitung beim Laden auf dem Gerät (Frist {AccelerationTimeouts.DeviceLoad.TotalSeconds:0} s)";
                // Die Session der abgebrochenen Ladung gibt sich nach ihrem Ende selbst frei (nie während eines Laufs).
                attempt.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        t.Result.Session.Dispose();
                    else
                        _ = t.Exception;
                }, TaskScheduler.Default);
            }
            catch (AggregateException ex)
            {
                reason = (ex.InnerException ?? ex).Message;
            }

            AppLog.Warn($"Beschleunigung: {model} konnte nicht auf „{device.Name}“ geladen werden ({reason}) – Rückfall auf die CPU.");
            Report(placement, model, device, reason, atRuntime: false);
        }

        var cpu = OnnxSessionFactory.Create(modelPath, null);
        try
        {
            state = prepare(cpu);
        }
        catch
        {
            cpu.Dispose();
            throw;
        }
        return new AcceleratedSession(model, modelPath, placement, cpu, null);
    }

    /// <summary>
    /// Führt <paramref name="run"/> mit der aktuellen Session aus. Scheitert er auf dem Gerät mit einer <see cref="OnnxRuntimeException"/>,
    /// wird auf eine CPU-Session gewechselt und <paramref name="run"/> einmal wiederholt (der Delegat muss deshalb seine Eingabe selbst
    /// aufbauen). Andere Ausnahmen (Dekodierung, Abbruch) laufen unverändert durch.
    /// </summary>
    public T Run<T>(Func<InferenceSession, T> run)
    {
        try
        {
            return run(_session);
        }
        catch (OnnxRuntimeException ex) when (_device is not null)
        {
            SwitchToCpu(ex);
            return run(_session);
        }
    }

    private void SwitchToCpu(Exception cause)
    {
        var device = _device!;
        var old = _session;
        // Erst die CPU-Session bauen: scheitert das, bleibt der Fehler beim Aufrufer (zählt dann als normaler Fehlschlag).
        var cpu = OnnxSessionFactory.Create(_modelPath, null);
        _session = cpu;
        _device = null;
        var reason = "Laufzeitfehler auf dem Gerät: " + cause.Message;
        AppLog.Warn($"Beschleunigung: {_model} auf „{device.Name}“ fehlgeschlagen ({cause.Message}) – Rückfall auf die CPU.", cause);
        Report(_placement, _model, device, reason, atRuntime: true);
        try
        {
            old.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die fehlerhafte Geräte-Session konnte nicht freigegeben werden.", ex);
        }
    }

    private static void Report(IModelPlacement? placement, ModelId model, AcceleratorDevice device, string reason, bool atRuntime)
    {
        try
        {
            placement?.ReportFallback(model, device, reason, atRuntime);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Der Rückfall konnte nicht gemeldet werden.", ex);
        }
    }

    public void Dispose() => _session.Dispose();
}
