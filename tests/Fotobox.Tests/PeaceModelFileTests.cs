using System.Diagnostics;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Xunit;
using Xunit.Abstractions;

namespace Fotobox.Tests;

/// <summary>
/// Prüft die drei Peace-Modelle (Palm, Handpunkte, YuNet): Datei echt (kein LFS-Zeiger), Session lädt, Ein-/Ausgabeformen wie von
/// den Detektoren angenommen, Inferenz auf einem Nullbild wirft nicht. Gibt die Formen im Test-Output aus.
/// </summary>
public sealed class PeaceModelFileTests(ITestOutputHelper output)
{
    private static string ModelPath(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", "Models", name);

    private static readonly string Palm = ModelPath("palm_detection_full_inf_post_192x192.onnx");
    private static readonly string Landmark = ModelPath("hand_landmark_sparse_Nx3x224x224.onnx");
    private static readonly string YuNet = ModelPath("face_detection_yunet_2026may.onnx");

    public static IEnumerable<object[]> Models =>
    [
        [Palm],
        [Landmark],
        [YuNet],
    ];

    private static InferenceSession Open(string path)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        return new InferenceSession(path, options);
    }

    private static string Describe(InferenceSession session)
    {
        var lines = new List<string>();
        foreach (var (name, meta) in session.InputMetadata)
            lines.Add($"  IN  {name}: {meta.ElementType} [{string.Join(",", meta.Dimensions)}]");
        foreach (var (name, meta) in session.OutputMetadata)
            lines.Add($"  OUT {name}: {meta.ElementType} [{string.Join(",", meta.Dimensions)}]");
        return string.Join(Environment.NewLine, lines);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void ModelFile_ExistsAndIsNoLfsPointer(string path)
    {
        Assert.True(File.Exists(path), path);
        var info = new FileInfo(path);
        Assert.True(info.Length > 100 * 1024, $"{path}: nur {info.Length} Byte (LFS-Zeiger?)");
        var head = new byte[64];
        using (var stream = File.OpenRead(path))
            _ = stream.Read(head, 0, head.Length);
        Assert.DoesNotContain("git-lfs", System.Text.Encoding.ASCII.GetString(head));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void ModelFile_SessionLoads_AndPrintsShapes(string path)
    {
        using var session = Open(path);
        output.WriteLine(Path.GetFileName(path));
        output.WriteLine(Describe(session));
        Assert.NotEmpty(session.InputNames);
        Assert.NotEmpty(session.OutputNames);
    }

    [Fact]
    public void Palm_Input192_OutputHasEightColumns()
    {
        using var session = Open(Palm);
        Assert.Single(session.InputNames);
        var dims = session.InputMetadata[session.InputNames[0]].Dimensions;
        Assert.Equal(4, dims.Length);
        Assert.Equal(3, dims[1]);
        Assert.Equal(192, dims[2]);
        Assert.Equal(192, dims[3]);

        var (shapes, ms) = RunZeros(session, [1, 3, 192, 192]);
        output.WriteLine($"Palm Nullbild: Ausgabe [{string.Join(",", shapes[0])}] in {ms} ms");
        Assert.Equal(2, shapes[0].Length);
        Assert.Equal(8, shapes[0][1]);
    }

    [Fact]
    public void Landmark_Input224_OutputsPointsScoreHandedness()
    {
        using var session = Open(Landmark);
        var inputDims = session.InputMetadata[session.InputNames[0]].Dimensions;
        Assert.Equal(4, inputDims.Length);
        Assert.Equal(3, inputDims[1]);
        Assert.Equal(224, inputDims[2]);
        Assert.Equal(224, inputDims[3]);
        Assert.Equal(3, session.OutputNames.Count);

        var (shapes, ms) = RunZeros(session, [1, 3, 224, 224]);
        for (var i = 0; i < shapes.Length; i++)
            output.WriteLine($"Landmark Nullbild: {session.OutputNames[i]} [{string.Join(",", shapes[i])}] ({ms} ms)");
        Assert.Equal([1L, 63L], shapes[0]);
        Assert.Equal([1L, 1L], shapes[1]);
        Assert.Equal([1L, 1L], shapes[2]);
    }

    [Theory]
    [InlineData(640, 352)]
    [InlineData(320, 320)]
    [InlineData(640, 384)]
    [InlineData(160, 96)]
    public void YuNet_AcceptsDynamicInput_AndGivesTwelveTensors(int width, int height)
    {
        using var session = Open(YuNet);
        Assert.Equal(12, session.OutputNames.Count);
        var (shapes, ms) = RunZeros(session, [1, 3, height, width]);
        output.WriteLine($"YuNet {width}x{height}: {ms} ms");
        for (var i = 0; i < shapes.Length; i++)
            output.WriteLine($"  {session.OutputNames[i]} [{string.Join(",", shapes[i])}]");
        var cells8 = (long)Math.Ceiling(height / 8.0) * (long)Math.Ceiling(width / 8.0);
        Assert.Contains(shapes, s => s.Length >= 2 && s.Contains(cells8));
    }

    [Fact]
    public void YuNet_NeedsMultiplesOf32_UnpaddedHeightFails()
    {
        // 640 × 360: 360 ist kein Vielfaches von 32 (Stride 16/32 runden unterschiedlich) → das Modell scheitert.
        // Deshalb füllt YuNetPreprocessor auf Vielfache von 32 auf.
        using var session = Open(YuNet);
        Assert.Throws<OnnxRuntimeException>(() => RunZeros(session, [1, 3, 360, 640]));
    }

    private static (long[][] Shapes, long Ms) RunZeros(InferenceSession session, long[] dims)
    {
        var data = new float[dims.Aggregate(1L, (a, b) => a * b)];
        var sw = Stopwatch.StartNew();
        using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, data.AsMemory(), dims);
        using var runOptions = new RunOptions();
        using var results = session.Run(runOptions, [session.InputNames[0]], [input], [.. session.OutputNames]);
        sw.Stop();
        var shapes = results.Select(r => r.GetTensorTypeAndShape().Shape).ToArray();
        return (shapes, sw.ElapsedMilliseconds);
    }
}
