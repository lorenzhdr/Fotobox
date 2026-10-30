using System.IO;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.Services.Detection.Acceleration;
using Microsoft.ML.OnnxRuntime;

namespace Fotobox.Services.Detection.Peace;

/// <summary>
/// Gesichtserkennung mit YuNet (ONNX Runtime; CPU, oder GPU/NPU laut Plan des <see cref="IModelPlacement"/> mit Rückfall auf die CPU): Box und fünf Merkmalspunkte je Gesicht. Verkleinert intern auf höchstens
/// 640 px Breite und füllt auf Vielfache von 32 auf. Die Session wird lazy auf dem Threadpool geladen (ein Aufwärmlauf gehört zur
/// Ladefrist) und nie während eines <c>Run</c> freigegeben (<see cref="DeferredReleaseGate"/>): <see cref="DisposeAsync"/> wartet
/// höchstens 1 s, danach gibt der laufende Aufruf nach seinem Ende selbst frei.
/// </summary>
public sealed class YuNetFaceDetector : IFaceLandmarkDetector
{
    /// <summary>Gesamtfrist des Ladens: Plan abwarten + Laden auf dem Gerät + Rückfall auf die CPU (<see cref="AccelerationTimeouts.DetectorInit"/>).</summary>
    public static readonly TimeSpan InitTimeout = AccelerationTimeouts.DetectorInit;
    public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(1);

    private static readonly string[] OutputPrefixes = ["cls_", "obj_", "bbox_", "kps_"];

    private readonly string _modelPath;
    private readonly IModelPlacement? _placement;
    private readonly DeferredReleaseGate _gate;
    private readonly object _initLock = new();
    private readonly object _runLock = new();
    private Task? _initTask;
    private AcceleratedSession? _model;
    private string _inputName = "input";
    private string[] _outputNames = [];
    private int[][] _outputIndex = []; // [Art cls/obj/bbox/kps][Stride 8/16/32] → Ausgabeindex
    private float[]? _inputBuffer;
    private readonly float[][][] _outputArrays = [new float[3][], new float[3][], new float[3][], new float[3][]];
    private int _disposeStarted;

    public YuNetFaceDetector(string? modelPath = null, IModelPlacement? placement = null)
    {
        _modelPath = modelPath ?? DefaultModelPath;
        _placement = placement;
        _gate = new DeferredReleaseGate(ReleaseSession);
    }

    public static string DefaultModelPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "face_detection_yunet_2026may.onnx");

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
            throw new TimeoutException("Das Gesichtsmodell wurde nicht rechtzeitig geladen.");
        }
    }

    private void LoadSession()
    {
        if (!_gate.TryEnter())
            throw new ObjectDisposedException(nameof(YuNetFaceDetector));
        try
        {
            if (!File.Exists(_modelPath))
                throw new FileNotFoundException("Die Modelldatei der Gesichtserkennung fehlt.", _modelPath);

            var model = AcceleratedSession.Load(ModelId.YuNet, _modelPath, _placement, new DeviceBudget(AccelerationTimeouts.DeviceLoad), PrepareSession, out var state);
            _inputName = state.InputName;
            _outputNames = state.OutputNames;
            _outputIndex = state.OutputIndex;
            _inputBuffer = state.Buffer;
            _model = model;
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>Prüft die Ausgaben des Modells und macht den Aufwärmlauf (auf dem Gerät bzw. der CPU, auf der die Session gebaut wurde).</summary>
    private static (string InputName, string[] OutputNames, int[][] OutputIndex, float[] Buffer) PrepareSession(InferenceSession session)
    {
        var names = session.OutputNames.ToArray();
        var index = new int[OutputPrefixes.Length][];
        for (var kind = 0; kind < OutputPrefixes.Length; kind++)
        {
            index[kind] = new int[YuNetDecoder.Strides.Length];
            for (var s = 0; s < YuNetDecoder.Strides.Length; s++)
            {
                var wanted = OutputPrefixes[kind] + YuNetDecoder.Strides[s];
                var found = Array.IndexOf(names, wanted);
                if (found < 0)
                    throw new InvalidDataException($"Das Gesichtsmodell hat keine Ausgabe '{wanted}'.");
                index[kind][s] = found;
            }
        }

        var inputName = session.InputNames[0];
        var warmSize = YuNetPreprocessor.ComputeSize(640, 360);
        var warm = new float[warmSize.RequiredLength];
        using (var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, warm.AsMemory(), [1L, 3L, warmSize.PaddedHeight, warmSize.PaddedWidth]))
        using (var runOptions = new RunOptions())
            session.Run(runOptions, [inputName], [input], names).Dispose();
        return (inputName, names, index, warm);
    }

    public async Task<IReadOnlyList<FaceLandmarks>> DetectAsync(BgraImage image, double minScore, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_gate.IsReleaseRequested, this);
        await InitializeAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => RunCore(image, minScore), CancellationToken.None).ConfigureAwait(false);
    }

    private IReadOnlyList<FaceLandmarks> RunCore(BgraImage image, double minScore)
    {
        if (!_gate.TryEnter())
            throw new ObjectDisposedException(nameof(YuNetFaceDetector));
        try
        {
            var model = _model ?? throw new InvalidOperationException("Die Gesichtserkennung ist nicht initialisiert.");
            lock (_runLock)
            {
                // Der Delegat baut seine Eingabe selbst auf: Fällt die Session zur Laufzeit auf die CPU zurück, wird er wiederholt.
                return model.Run(session =>
                {
                    var size = YuNetPreprocessor.ComputeSize(image.Width, image.Height);
                    var buffer = _inputBuffer;
                    if (buffer is null || buffer.Length != size.RequiredLength)
                        _inputBuffer = buffer = new float[size.RequiredLength];
                    YuNetPreprocessor.Preprocess(image, size, buffer);

                    using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer.AsMemory(), [1L, 3L, size.PaddedHeight, size.PaddedWidth]);
                    using var runOptions = new RunOptions();
                    using var results = session.Run(runOptions, [_inputName], [input], _outputNames);

                    for (var kind = 0; kind < OutputPrefixes.Length; kind++)
                    {
                        for (var s = 0; s < YuNetDecoder.Strides.Length; s++)
                        {
                            var data = results[_outputIndex[kind][s]].GetTensorDataAsSpan<float>();
                            var array = _outputArrays[kind][s];
                            if (array is null || array.Length != data.Length)
                                _outputArrays[kind][s] = array = new float[data.Length];
                            data.CopyTo(array);
                        }
                    }

                    return YuNetDecoder.Decode(_outputArrays[0], _outputArrays[1], _outputArrays[2], _outputArrays[3], size, minScore);
                });
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
        Interlocked.Exchange(ref _model, null)?.Dispose();
        _inputBuffer = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;
        await _gate.RequestReleaseAsync(DisposeWait).ConfigureAwait(false);
    }
}
