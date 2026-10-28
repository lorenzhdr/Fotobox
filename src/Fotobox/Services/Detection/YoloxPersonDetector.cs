using System.IO;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Microsoft.ML.OnnxRuntime;

namespace Fotobox.Services.Detection;

// Der Typname Detection kollidiert innerhalb dieses Namespaces mit dem Namespace selbst.
using Detection = Fotobox.Core.Fun.Detection;

/// <summary>
/// Ganzkörper-Personenerkennung mit YOLOX-Nano (ONNX Runtime, CPU). Die <see cref="InferenceSession"/> wird lazy auf dem Threadpool
/// geladen. Sie wird nie während eines laufenden <c>Run</c> freigegeben (<see cref="DeferredReleaseGate"/>): Ein
/// <see cref="DisposeAsync"/> wartet bis zu 3 s; läuft der Aufruf dann noch, gibt er die Session nach seinem Ende selbst frei.
/// </summary>
public sealed class YoloxPersonDetector : IPersonDetector
{
    public static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(3);

    private readonly string _modelPath;
    private readonly DeferredReleaseGate _gate;
    private readonly object _initLock = new();
    private readonly object _runLock = new();
    private Task? _initTask;
    private InferenceSession? _session;
    private string _inputName = "images";
    private int _inputSize = YoloxPreprocessor.DefaultInputSize;
    private float[]? _inputBuffer;
    private int _disposeStarted;

    public YoloxPersonDetector(string? modelPath = null)
    {
        _modelPath = modelPath ?? DefaultModelPath;
        _gate = new DeferredReleaseGate(ReleaseSession);
    }

    public static string DefaultModelPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "yolox_nano.onnx");

    public DetectionKind Kind => DetectionKind.Body;

    /// <summary>Lädt das Modell (einmal, idempotent). Hat ein Timeout; das Laden selbst läuft auf dem Threadpool weiter und gibt sich selbst frei, falls inzwischen entsorgt wurde.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_gate.IsReleaseRequested, this);
        Task init;
        lock (_initLock)
            init = _initTask ??= Task.Run(LoadSession);
        try
        {
            await init.WaitAsync(InitTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Das KI-Modell wurde nicht rechtzeitig geladen.");
        }
    }

    private void LoadSession()
    {
        if (!_gate.TryEnter())
            throw new ObjectDisposedException(nameof(YoloxPersonDetector));
        try
        {
            if (!File.Exists(_modelPath))
                throw new FileNotFoundException("Die Modelldatei der Personenerkennung fehlt.", _modelPath);

            using var options = new SessionOptions
            {
                IntraOpNumThreads = 2,
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            // Die ORT-Threads schlafen zwischen den Analysen (im Leerlauf 5/s), statt aktiv zu warten: spart CPU, Ergebnisse bleiben gleich.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            var session = new InferenceSession(_modelPath, options);
            try
            {
                var inputName = session.InputNames[0];
                var size = YoloxPreprocessor.DefaultInputSize;
                if (session.InputMetadata.TryGetValue(inputName, out var meta) && meta.Dimensions.Length == 4
                    && meta.Dimensions[2] > 0 && meta.Dimensions[2] == meta.Dimensions[3])
                    size = meta.Dimensions[2];

                // Aufwärmlauf: Der erste Run ist deutlich langsamer (Speicher-Arena, Kernel-Initialisierung). Er soll in der großzügigen
                // Ladefrist (InitTimeout) laufen statt in der 2-s-Frist der ersten Analyse, denn schon ein Analyse-Timeout schaltet
                // dauerhaft auf die Gesichtserkennung um. Ein Fehler hier gilt als Ladefehler; der Puffer wird von RunCore weiterverwendet.
                var warmup = new float[YoloxPreprocessor.RequiredLength(size)];
                using (var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, warmup.AsMemory(), [1L, 3L, size, size]))
                using (var runOptions = new RunOptions())
                    session.Run(runOptions, [inputName], [input], [session.OutputNames[0]]).Dispose();

                _inputName = inputName;
                _inputSize = size;
                _inputBuffer = warmup;
                _session = session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
        finally
        {
            _gate.Exit();
        }
    }

    public async Task<IReadOnlyList<Detection>> DetectAsync(DetectionFrame frame, double minConfidence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_gate.IsReleaseRequested, this);
        await InitializeAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => RunCore(frame, minConfidence), CancellationToken.None).ConfigureAwait(false);
    }

    private IReadOnlyList<Detection> RunCore(DetectionFrame frame, double minConfidence)
    {
        if (!_gate.TryEnter())
            throw new ObjectDisposedException(nameof(YoloxPersonDetector));
        try
        {
            var session = _session ?? throw new InvalidOperationException("Die Personenerkennung ist nicht initialisiert.");
            lock (_runLock)
            {
                var size = _inputSize;
                var buffer = _inputBuffer;
                if (buffer is null || buffer.Length != YoloxPreprocessor.RequiredLength(size))
                    _inputBuffer = buffer = new float[YoloxPreprocessor.RequiredLength(size)];

                var ratio = YoloxPreprocessor.Preprocess(frame, buffer, size);
                using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer.AsMemory(), [1L, 3L, size, size]);
                using var runOptions = new RunOptions();
                using var results = session.Run(runOptions, [_inputName], [input], [session.OutputNames[0]]);
                var output = results[0];
                var shape = output.GetTensorTypeAndShape().Shape;
                return YoloxDecoder.Decode(output.GetTensorDataAsSpan<float>(), shape, ratio, frame.Width, frame.Height, minConfidence);
            }
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>Nur über das <see cref="DeferredReleaseGate"/>: erst wenn kein Aufruf mehr läuft.</summary>
    private void ReleaseSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        session?.Dispose();
        _inputBuffer = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;
        await _gate.RequestReleaseAsync(DisposeWait).ConfigureAwait(false);
    }
}
