using System.IO;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Microsoft.ML.OnnxRuntime;

namespace Fotobox.Services.Detection.Peace;

/// <summary>
/// Handerkennung mit zwei MediaPipe-Modellen (PINTO0309-Exporte, ONNX Runtime, CPU): Palm Detection (192 × 192, Vorverarbeitung
/// RGB 0–1, Fenster quadratisch mit schwarzem Rand) und Hand Landmark (224 × 224, gedrehter Ausschnitt, 21 Punkte in Pixeln des
/// Ausschnitts). Beide Sessions liegen in einem <see cref="DeferredReleaseGate"/>: Sie werden nie während eines <c>Run</c> freigegeben;
/// <see cref="DisposeAsync"/> wartet höchstens 1 s, danach gibt der laufende Aufruf nach seinem Ende selbst frei.
/// </summary>
public sealed class MediaPipeHandDetector : IHandLandmarkDetector
{
    public const int PalmInputSize = 192;
    public const int LandmarkInputSize = 224;
    public const double MinPalmScore = 0.5;
    public static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(1);

    private readonly string _palmPath;
    private readonly string _landmarkPath;
    private readonly DeferredReleaseGate _gate;
    private readonly object _initLock = new();
    private readonly object _runLock = new();
    private Task? _initTask;
    private InferenceSession? _palm;
    private InferenceSession? _landmark;
    private string _palmInput = "input";
    private string _landmarkInput = "input";
    private string[] _palmOutputs = [];
    private string[] _landmarkOutputs = [];
    private int _pointsIndex, _scoreIndex = 1, _handednessIndex = -1;
    private float[]? _palmBuffer;
    private float[]? _landmarkBuffer;
    private int _disposeStarted;
    // Diagnose (Herz-Log): bekommt den Roh-Score jedes Landmark-Laufs, auch bei verworfenen Händen.
    private volatile Action<double>? _scoreObserver;

    public MediaPipeHandDetector(string? palmModelPath = null, string? landmarkModelPath = null)
    {
        _palmPath = palmModelPath ?? DefaultPalmModelPath;
        _landmarkPath = landmarkModelPath ?? DefaultLandmarkModelPath;
        _gate = new DeferredReleaseGate(ReleaseSessions);
    }

    public static string DefaultPalmModelPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "palm_detection_full_inf_post_192x192.onnx");
    public static string DefaultLandmarkModelPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "hand_landmark_sparse_Nx3x224x224.onnx");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_gate.IsReleaseRequested, this);
        Task init;
        lock (_initLock)
            init = _initTask ??= Task.Run(LoadSessions);
        try
        {
            await init.WaitAsync(InitTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Das Handmodell wurde nicht rechtzeitig geladen.");
        }
    }

    private static SessionOptions CreateOptions()
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        return options;
    }

    private void LoadSessions()
    {
        if (!_gate.TryEnter())
            throw new ObjectDisposedException(nameof(MediaPipeHandDetector));
        InferenceSession? palm = null, landmark = null;
        try
        {
            if (!File.Exists(_palmPath))
                throw new FileNotFoundException("Die Modelldatei der Handflächenerkennung fehlt.", _palmPath);
            if (!File.Exists(_landmarkPath))
                throw new FileNotFoundException("Die Modelldatei der Handpunkte fehlt.", _landmarkPath);

            using var options = CreateOptions();
            palm = new InferenceSession(_palmPath, options);
            landmark = new InferenceSession(_landmarkPath, options);

            var palmInput = palm.InputNames[0];
            var palmOutputs = palm.OutputNames.ToArray();
            var landmarkInput = landmark.InputNames[0];
            var landmarkOutputs = landmark.OutputNames.ToArray();
            if (landmarkOutputs.Length < 2)
                throw new InvalidDataException("Das Handpunkt-Modell hat zu wenige Ausgaben.");
            var pointsIndex = Array.IndexOf(landmarkOutputs, "xyz_x21");
            var scoreIndex = Array.IndexOf(landmarkOutputs, "hand_score");
            if (pointsIndex < 0) pointsIndex = 0;
            if (scoreIndex < 0) scoreIndex = 1;
            // Händigkeit (0 = links, 1 = rechts) ist optional: fehlt die Ausgabe, bleibt Handedness null.
            var handednessIndex = Array.IndexOf(landmarkOutputs, "lefthand_0_or_righthand_1");

            // Aufwärmläufe innerhalb der Ladefrist (der erste Run ist deutlich langsamer).
            var palmBuffer = new float[3 * PalmInputSize * PalmInputSize];
            using (var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, palmBuffer.AsMemory(), [1L, 3L, PalmInputSize, PalmInputSize]))
            using (var runOptions = new RunOptions())
                palm.Run(runOptions, [palmInput], [input], palmOutputs).Dispose();
            var landmarkBuffer = new float[3 * LandmarkInputSize * LandmarkInputSize];
            using (var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, landmarkBuffer.AsMemory(), [1L, 3L, LandmarkInputSize, LandmarkInputSize]))
            using (var runOptions = new RunOptions())
                landmark.Run(runOptions, [landmarkInput], [input], landmarkOutputs).Dispose();

            _palmInput = palmInput;
            _palmOutputs = palmOutputs;
            _landmarkInput = landmarkInput;
            _landmarkOutputs = landmarkOutputs;
            _pointsIndex = pointsIndex;
            _scoreIndex = scoreIndex;
            _handednessIndex = handednessIndex;
            _palmBuffer = palmBuffer;
            _landmarkBuffer = landmarkBuffer;
            _palm = palm;
            _landmark = landmark;
            palm = null;
            landmark = null;
        }
        finally
        {
            palm?.Dispose();
            landmark?.Dispose();
            _gate.Exit();
        }
    }

    public async Task<IReadOnlyList<PalmCandidate>> DetectPalmsAsync(BgraImage image, NormRect region, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_gate.IsReleaseRequested, this);
        await InitializeAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => RunPalm(image, region, max), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<HandLandmarks?> DetectLandmarksAsync(BgraImage image, HandRoi roi, double minScore, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_gate.IsReleaseRequested, this);
        await InitializeAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => RunLandmarks(image, roi, minScore), CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void SetScoreObserver(Action<double>? observer) => _scoreObserver = observer;

    private IReadOnlyList<PalmCandidate> RunPalm(BgraImage image, NormRect region, int max)
    {
        if (!_gate.TryEnter())
            throw new ObjectDisposedException(nameof(MediaPipeHandDetector));
        try
        {
            var session = _palm ?? throw new InvalidOperationException("Die Handerkennung ist nicht initialisiert.");
            lock (_runLock)
            {
                var buffer = _palmBuffer ??= new float[3 * PalmInputSize * PalmInputSize];
                var window = PalmWindow.For(region, image.Width, image.Height);
                AffineCrop.Sample(image, window.X0 + window.Side / 2, window.Y0 + window.Side / 2, 0, window.Side, window.Side,
                    PalmInputSize, PalmInputSize, buffer, PalmInputSize, PalmInputSize, bgr: false, scale: 1f / 255f);

                using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer.AsMemory(), [1L, 3L, PalmInputSize, PalmInputSize]);
                using var runOptions = new RunOptions();
                using var results = session.Run(runOptions, [_palmInput], [input], _palmOutputs);
                var output = results[0];
                var shape = output.GetTensorTypeAndShape().Shape;
                if (shape.Length != 2 || shape[1] != PalmDecoder.Columns)
                    throw new InvalidDataException("Unerwartete Ausgabeform der Handflächenerkennung.");
                return PalmDecoder.Decode(output.GetTensorDataAsSpan<float>(), (int)shape[0], window, image.Width, image.Height, MinPalmScore, max);
            }
        }
        finally
        {
            _gate.Exit();
        }
    }

    private HandLandmarks? RunLandmarks(BgraImage image, HandRoi roi, double minScore)
    {
        if (!_gate.TryEnter())
            throw new ObjectDisposedException(nameof(MediaPipeHandDetector));
        try
        {
            var session = _landmark ?? throw new InvalidOperationException("Die Handerkennung ist nicht initialisiert.");
            lock (_runLock)
            {
                var buffer = _landmarkBuffer ??= new float[3 * LandmarkInputSize * LandmarkInputSize];
                AffineCrop.SampleRoi(image, roi, LandmarkInputSize, buffer);

                using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer.AsMemory(), [1L, 3L, LandmarkInputSize, LandmarkInputSize]);
                using var runOptions = new RunOptions();
                using var results = session.Run(runOptions, [_landmarkInput], [input], _landmarkOutputs);
                var data = results[_pointsIndex].GetTensorDataAsSpan<float>();
                var scores = results[_scoreIndex].GetTensorDataAsSpan<float>();
                if (data.Length < 63 || scores.Length < 1)
                    throw new InvalidDataException("Unerwartete Ausgabeform der Handpunkte.");
                var score = scores[0];
                _scoreObserver?.Invoke(score); // Diagnose: Roh-Score auch für verworfene Hände
                if (!(score >= minScore))
                    return null;

                var points = new HandPoint[21];
                var wristZ = data[2];
                var zScale = roi.SizePx / image.Width / LandmarkInputSize;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                for (var i = 0; i < 21; i++)
                {
                    var p = AffineCrop.RoiPointToNorm(roi, image.Width, image.Height, LandmarkInputSize, data[3 * i], data[3 * i + 1]);
                    points[i] = new HandPoint(p.X, p.Y, (data[3 * i + 2] - wristZ) * zScale);
                    minX = Math.Min(minX, p.X);
                    maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxY = Math.Max(maxY, p.Y);
                }
                // Alle Ausgaben werden ohnehin berechnet (Run mit der vollen Ausgabeliste), das Auslesen kostet nichts.
                double? handedness = null;
                if (_handednessIndex >= 0 && _handednessIndex < results.Count)
                {
                    var sides = results[_handednessIndex].GetTensorDataAsSpan<float>();
                    if (sides.Length >= 1 && float.IsFinite(sides[0]))
                        handedness = Math.Clamp((double)sides[0], 0, 1);
                }
                return new HandLandmarks(points, score, NormRect.FromEdges(minX, minY, maxX, maxY), handedness);
            }
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>Nur über das <see cref="DeferredReleaseGate"/>: erst wenn kein Aufruf mehr läuft.</summary>
    private void ReleaseSessions()
    {
        Interlocked.Exchange(ref _palm, null)?.Dispose();
        Interlocked.Exchange(ref _landmark, null)?.Dispose();
        _palmBuffer = null;
        _landmarkBuffer = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;
        await _gate.RequestReleaseAsync(DisposeWait).ConfigureAwait(false);
    }
}
