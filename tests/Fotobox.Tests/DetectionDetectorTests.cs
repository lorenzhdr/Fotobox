using System.IO;
using Fotobox.Core.Fun;
using Fotobox.Services.Detection;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Echtes YOLOX-Modell (CI-tauglich, läuft ohne Hardware), Freigabe-Regel und Windows-Gesichtserkennung.</summary>
public class DetectionDetectorTests
{
    private static DetectionFrame GrayFrame(int w, int h, byte value = 128)
    {
        var pixels = new byte[w * h * 4];
        Array.Fill(pixels, value);
        return new DetectionFrame { Width = w, Height = h, Pixels = pixels, SourceWidth = w, SourceHeight = h };
    }

    // ------------------------------------------------------------------ YOLOX mit echtem Modell

    [Fact]
    public void ModelFile_IsDeployedNextToTheTests()
    {
        Assert.True(File.Exists(YoloxPersonDetector.DefaultModelPath), YoloxPersonDetector.DefaultModelPath);
    }

    [Fact]
    public void Model_Run_ReturnsExpectedOutputShape()
    {
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        using var session = new InferenceSession(YoloxPersonDetector.DefaultModelPath, options);

        var name = Assert.Single(session.InputNames);
        var dims = session.InputMetadata[name].Dimensions;
        Assert.Equal(4, dims.Length);
        Assert.Equal(3, dims[1]);
        Assert.Equal(416, dims[2]);
        Assert.Equal(416, dims[3]);

        var tensor = new float[YoloxPreprocessor.RequiredLength(416)];
        YoloxPreprocessor.Preprocess(GrayFrame(640, 360), tensor);
        using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, tensor.AsMemory(), [1L, 3L, 416L, 416L]);
        using var runOptions = new RunOptions();
        using var results = session.Run(runOptions, [name], [input], [session.OutputNames[0]]);

        Assert.Equal([1L, 3549L, 85L], results[0].GetTensorTypeAndShape().Shape);
    }

    [Fact]
    public async Task YoloxDetector_GrayImage_FindsNoPersons()
    {
        await using var detector = new YoloxPersonDetector();
        await detector.InitializeAsync();

        var result = await detector.DetectAsync(GrayFrame(640, 360), minConfidence: 0.3);

        Assert.Empty(result);
        Assert.Equal(DetectionKind.Body, detector.Kind);
    }

    [Fact]
    public async Task YoloxDetector_InitializeIsIdempotent_AndDetectInitializesLazily()
    {
        await using var detector = new YoloxPersonDetector();

        var result = await detector.DetectAsync(GrayFrame(320, 240), 0.5); // ohne vorheriges Initialize
        await detector.InitializeAsync();
        await detector.InitializeAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task YoloxDetector_MissingModel_Throws()
    {
        await using var detector = new YoloxPersonDetector(Path.Combine(Path.GetTempPath(), "fotobox-gibt-es-nicht.onnx"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => detector.InitializeAsync());
    }

    [Fact]
    public async Task YoloxDetector_AfterDispose_ThrowsObjectDisposed()
    {
        var detector = new YoloxPersonDetector();
        await detector.InitializeAsync();

        await detector.DisposeAsync();
        await detector.DisposeAsync(); // zweimal ist erlaubt

        await Assert.ThrowsAsync<ObjectDisposedException>(() => detector.DetectAsync(GrayFrame(64, 64), 0.5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => detector.InitializeAsync());
    }

    [Fact]
    public async Task YoloxDetector_DisposeRacingWithDetections_NeverCrashes()
    {
        // Viele Analysen laufen, während entsorgt wird: Entweder Ergebnis oder ObjectDisposedException, nie ein Absturz der nativen DLL.
        var detector = new YoloxPersonDetector();
        await detector.InitializeAsync();
        var frame = GrayFrame(640, 360);

        var runs = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            try { await detector.DetectAsync(frame, 0.3); }
            catch (ObjectDisposedException) { }
        })).ToArray();
        await detector.DisposeAsync();
        await Task.WhenAll(runs);
    }

    // ------------------------------------------------------------------ Freigabe-Regel

    [Fact]
    public async Task Gate_ReleasesImmediately_WhenIdle()
    {
        var released = 0;
        var gate = new DeferredReleaseGate(() => Interlocked.Increment(ref released));

        await gate.RequestReleaseAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, released);
        Assert.True(gate.IsReleased);
        Assert.False(gate.TryEnter());
    }

    [Fact]
    public async Task Gate_DoesNotRelease_WhileAccessRuns_AndReleasesAfterExit()
    {
        var released = 0;
        var gate = new DeferredReleaseGate(() => Interlocked.Increment(ref released));
        Assert.True(gate.TryEnter());

        // Frist 100 ms: Der Wartende gibt auf, die Ressource bleibt unberührt.
        await gate.RequestReleaseAsync(TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, released);
        Assert.False(gate.TryEnter()); // neue Zugriffe sind schon abgelehnt

        gate.Exit();

        Assert.Equal(1, released);
        Assert.True(gate.IsReleased);
    }

    [Fact]
    public async Task Gate_WaitingDisposeReturnsAsSoonAsAccessEnds_AndReleasesOnlyOnce()
    {
        var released = 0;
        var gate = new DeferredReleaseGate(() => Interlocked.Increment(ref released));
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());

        var waiting = gate.RequestReleaseAsync(TimeSpan.FromSeconds(10));
        gate.Exit();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(0, released);
        gate.Exit();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await gate.RequestReleaseAsync(TimeSpan.FromMilliseconds(10)); // zweite Anforderung

        Assert.Equal(1, released);
    }

    // ------------------------------------------------------------------ Windows-Gesichtserkennung

    [Fact]
    public void ToGray8_UsesLumaWeights()
    {
        // Ein Pixel je Farbe (B, G, R, A): reines Blau, Grün, Rot, Weiß.
        byte[] bgra = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
        var gray = new byte[4];

        WindowsFaceDetector.ToGray8(bgra, gray, 4, 1, 16);

        Assert.Equal((255 * 29) >> 8, gray[0]);
        Assert.Equal((255 * 150) >> 8, gray[1]);
        Assert.Equal((255 * 77) >> 8, gray[2]);
        Assert.Equal(255, gray[3]);
    }

    [Trait("Category", "Hardware")]
    [Fact]
    public async Task FaceDetector_InitializesAndAnalyzesGrayImage()
    {
        await using var detector = new WindowsFaceDetector();
        await detector.InitializeAsync();

        var result = await detector.DetectAsync(GrayFrame(640, 360), 0.3);

        Assert.Equal(DetectionKind.Face, detector.Kind);
        Assert.Empty(result);
    }
}
