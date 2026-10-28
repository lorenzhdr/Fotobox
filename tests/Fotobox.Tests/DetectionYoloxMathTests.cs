using System.IO;
using Fotobox.Core.Fun;
using Fotobox.Services.Detection;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Vorverarbeitung (Letterbox, BGR, Padding) und Dekodierung der YOLOX-Ausgabe, rein rechnerisch.</summary>
public class DetectionYoloxMathTests
{
    private const int S = YoloxPreprocessor.DefaultInputSize;

    private static DetectionFrame SolidFrame(int w, int h, byte b, byte g, byte r)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
        }
        return new DetectionFrame { Width = w, Height = h, Pixels = pixels, SourceWidth = w, SourceHeight = h };
    }

    private static float At(float[] t, int channel, int x, int y) => t[channel * S * S + y * S + x];

    // ------------------------------------------------------------------ Preprocessor

    [Fact]
    public void Preprocess_Landscape_PlacesImageTopLeft_AndPadsWith114()
    {
        var frame = SolidFrame(200, 100, b: 10, g: 20, r: 30);
        var tensor = new float[YoloxPreprocessor.RequiredLength(S)];

        var ratio = YoloxPreprocessor.Preprocess(frame, tensor);

        Assert.Equal(2.08f, ratio, 4); // min(416/100, 416/200)
        // Bildbereich: 416 × 208 oben links, BGR-Reihenfolge der Kanäle.
        foreach (var (x, y) in new[] { (0, 0), (415, 0), (0, 207), (415, 207), (200, 100) })
        {
            Assert.Equal(10f, At(tensor, 0, x, y), 3);
            Assert.Equal(20f, At(tensor, 1, x, y), 3);
            Assert.Equal(30f, At(tensor, 2, x, y), 3);
        }
        // Darunter nur Padding.
        foreach (var (x, y) in new[] { (0, 208), (415, 415), (200, 300) })
            for (int c = 0; c < 3; c++)
                Assert.Equal(114f, At(tensor, c, x, y));
    }

    [Fact]
    public void Preprocess_Portrait_PadsRightSide()
    {
        var frame = SolidFrame(100, 200, b: 1, g: 2, r: 3);
        var tensor = new float[YoloxPreprocessor.RequiredLength(S)];

        var ratio = YoloxPreprocessor.Preprocess(frame, tensor);

        Assert.Equal(2.08f, ratio, 4); // min(416/200, 416/100)
        Assert.Equal(1f, At(tensor, 0, 207, 415), 3);
        Assert.Equal(114f, At(tensor, 0, 208, 0));
        Assert.Equal(114f, At(tensor, 2, 415, 415));
    }

    [Fact]
    public void Preprocess_KeepsValuesUnnormalized_AndChannelsDistinct()
    {
        // Linke Hälfte rot, rechte Hälfte blau: prüft Kanalreihenfolge (Bgra32 → Kanal 0 = Blau) und Bereich 0–255.
        const int w = 416, h = 416;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (x < w / 2) { pixels[i + 2] = 255; } else { pixels[i] = 200; }
                pixels[i + 3] = 255;
            }
        var frame = new DetectionFrame { Width = w, Height = h, Pixels = pixels, SourceWidth = w, SourceHeight = h };
        var tensor = new float[YoloxPreprocessor.RequiredLength(S)];

        var ratio = YoloxPreprocessor.Preprocess(frame, tensor);

        Assert.Equal(1f, ratio);
        Assert.Equal(0f, At(tensor, 0, 10, 10));     // Blau links 0
        Assert.Equal(255f, At(tensor, 2, 10, 10));   // Rot links 255
        Assert.Equal(200f, At(tensor, 0, 400, 10));  // Blau rechts 200
        Assert.Equal(0f, At(tensor, 2, 400, 10));
    }

    [Fact]
    public void Preprocess_SwapRedBlue_ExchangesChannels()
    {
        var frame = SolidFrame(64, 64, b: 10, g: 20, r: 30);
        var tensor = new float[YoloxPreprocessor.RequiredLength(S)];

        YoloxPreprocessor.Preprocess(frame, tensor, swapRedBlue: true);

        Assert.Equal(30f, At(tensor, 0, 5, 5), 3);
        Assert.Equal(20f, At(tensor, 1, 5, 5), 3);
        Assert.Equal(10f, At(tensor, 2, 5, 5), 3);
    }

    [Fact]
    public void Preprocess_Downscale_BilinearAveragesNeighbours()
    {
        // 832 × 416 → r = 0,5: Jeder Ausgabepixel mittelt 2 × 2 Quellpixel (Spalten abwechselnd 0 und 100).
        const int w = 832, h = 416;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[(y * w + x) * 4 + 1] = (byte)(x % 2 == 0 ? 0 : 100);
        var frame = new DetectionFrame { Width = w, Height = h, Pixels = pixels, SourceWidth = w, SourceHeight = h };
        var tensor = new float[YoloxPreprocessor.RequiredLength(S)];

        var ratio = YoloxPreprocessor.Preprocess(frame, tensor);

        Assert.Equal(0.5f, ratio);
        Assert.Equal(50f, At(tensor, 1, 100, 50), 2);
        Assert.Equal(114f, At(tensor, 1, 100, 300)); // Höhe 208 → darunter Padding
    }

    [Fact]
    public void Preprocess_RejectsBadInput()
    {
        var frame = SolidFrame(10, 10, 0, 0, 0);
        Assert.Throws<ArgumentException>(() => YoloxPreprocessor.Preprocess(frame, new float[10]));
        var broken = new DetectionFrame { Width = 100, Height = 100, Pixels = new byte[10], SourceWidth = 100, SourceHeight = 100 };
        Assert.Throws<ArgumentException>(() => YoloxPreprocessor.Preprocess(broken, new float[YoloxPreprocessor.RequiredLength(S)]));
    }

    // ------------------------------------------------------------------ Decoder

    private const int Anchors = 3549;
    private const int Columns = 85;

    private static float[] EmptyOutput() => new float[Anchors * Columns];

    private static void SetAnchor(float[] output, int row, float rawX, float rawY, float w, float h, float obj, float cls0, int otherClass = -1, float otherValue = 0)
    {
        var o = row * Columns;
        output[o] = rawX;
        output[o + 1] = rawY;
        output[o + 2] = MathF.Log(w); // roh = ln(Breite / Stride)
        output[o + 3] = MathF.Log(h);
        output[o + 4] = obj;
        output[o + 5] = cls0;
        if (otherClass > 0)
            output[o + 5 + otherClass] = otherValue;
    }

    private static readonly long[] Shape = [1, Anchors, Columns];

    [Fact]
    public void Decode_Stride8Anchor_ProducesExpectedNormalizedBox()
    {
        var output = EmptyOutput();
        // Stride 8, Zeile 5, Spalte 10 → Index 5·52 + 10. cx = (0,5+10)·8 = 84, cy = (0,5+5)·8 = 44, w = 4·8 = 32, h = 8·8 = 64.
        SetAnchor(output, 5 * 52 + 10, 0.5f, 0.5f, 4f, 8f, obj: 0.9f, cls0: 0.8f);

        // Analysebild 208 × 104 → r = min(416/104, 416/208) = 2.
        var result = YoloxDecoder.Decode(output, Shape, ratio: 2f, imageWidth: 208, imageHeight: 104, minConfidence: 0.3);

        var d = Assert.Single(result);
        Assert.Equal(DetectionKind.Body, d.Kind);
        Assert.Equal(0.72, d.Confidence, 4);
        // xyxy im Modell: 68..100 × 12..76 → / r → 34..50 × 6..38 → normiert.
        Assert.Equal(34.0 / 208, d.Box.X, 4);
        Assert.Equal(16.0 / 208, d.Box.Width, 4);
        Assert.Equal(6.0 / 104, d.Box.Y, 4);
        Assert.Equal(32.0 / 104, d.Box.Height, 4);
    }

    [Fact]
    public void Decode_Stride16And32Anchors_UseTheirOwnGridOffsets()
    {
        var output = EmptyOutput();
        // Stride 16 beginnt bei Index 2704 (52²), Gitter 26 breit: Zeile 3, Spalte 4. cx = 4·16 = 64 (roh 0), cy = 3·16 = 48.
        SetAnchor(output, 2704 + 3 * 26 + 4, 0f, 0f, 2f, 2f, obj: 1f, cls0: 0.9f); // 32 × 32 → Box 48..80 × 32..64
        // Stride 32 beginnt bei 2704 + 676 = 3380, Gitter 13 breit: Zeile 10, Spalte 11. cx = 11·32 = 352, cy = 10·32 = 320.
        SetAnchor(output, 3380 + 10 * 13 + 11, 0f, 0f, 1f, 1f, obj: 1f, cls0: 0.7f); // 32 × 32 → Box 336..368 × 304..336

        var result = YoloxDecoder.Decode(output, Shape, ratio: 1f, imageWidth: 416, imageHeight: 416, minConfidence: 0.3);

        Assert.Equal(2, result.Count);
        Assert.Equal(0.9, result[0].Confidence, 4); // nach Konfidenz absteigend
        Assert.Equal(48.0 / 416, result[0].Box.X, 4);
        Assert.Equal(32.0 / 416, result[0].Box.Y, 4);
        Assert.Equal(32.0 / 416, result[0].Box.Width, 4);
        Assert.Equal(336.0 / 416, result[1].Box.X, 4);
        Assert.Equal(304.0 / 416, result[1].Box.Y, 4);
    }

    [Fact]
    public void Decode_BelowThreshold_IsDropped()
    {
        var output = EmptyOutput();
        SetAnchor(output, 100, 0.5f, 0.5f, 4f, 4f, obj: 0.9f, cls0: 0.8f); // 0,72

        Assert.Empty(YoloxDecoder.Decode(output, Shape, 1f, 416, 416, minConfidence: 0.8));
        Assert.Single(YoloxDecoder.Decode(output, Shape, 1f, 416, 416, minConfidence: 0.7));
    }

    [Fact]
    public void Decode_OtherClassesAreIgnored()
    {
        var output = EmptyOutput();
        // Starke „Tasche“ (Klasse 26), aber kaum Person.
        SetAnchor(output, 100, 0.5f, 0.5f, 4f, 4f, obj: 0.95f, cls0: 0.02f, otherClass: 26, otherValue: 0.99f);

        Assert.Empty(YoloxDecoder.Decode(output, Shape, 1f, 416, 416, minConfidence: 0.05));
    }

    [Fact]
    public void Decode_Nms_SuppressesOverlappingDuplicates_AndKeepsSeparateBoxes()
    {
        var output = EmptyOutput();
        // Zwei Nachbarzellen mit nahezu gleicher Box (IoU hoch) und eine ferne Zelle.
        SetAnchor(output, 10 * 52 + 10, 0.5f, 0.5f, 6f, 10f, obj: 0.9f, cls0: 0.9f);  // 0,81
        SetAnchor(output, 10 * 52 + 11, 0.4f, 0.5f, 6f, 10f, obj: 0.8f, cls0: 0.9f);  // 0,72, Duplikat
        SetAnchor(output, 40 * 52 + 40, 0.5f, 0.5f, 6f, 10f, obj: 0.7f, cls0: 0.9f);  // 0,63, getrennt

        var result = YoloxDecoder.Decode(output, Shape, 1f, 416, 416, minConfidence: 0.3);

        Assert.Equal(2, result.Count);
        Assert.Equal(0.81, result[0].Confidence, 4);
        Assert.Equal(0.63, result[1].Confidence, 4);
    }

    [Fact]
    public void Decode_ClampsBoxToImage_AndDropsBoxesOutside()
    {
        var output = EmptyOutput();
        // Zelle 0, roh 0 → Mitte (0, 0), w = h = 64: Box −32..32 ragt über die obere linke Ecke und wird auf 0..32 gekappt.
        SetAnchor(output, 0, 0f, 0f, 8f, 8f, obj: 1f, cls0: 0.9f);

        var d = Assert.Single(YoloxDecoder.Decode(output, Shape, 1f, 416, 416, minConfidence: 0.3));
        Assert.Equal(0, d.Box.X);
        Assert.Equal(0, d.Box.Y);
        Assert.Equal(32.0 / 416, d.Box.Width, 4);
    }

    [Fact]
    public void Decode_UnexpectedShape_Throws()
    {
        Assert.Throws<InvalidDataException>(() => YoloxDecoder.Decode(new float[3000 * 85], [1, 3000, 85], 1f, 416, 416, 0.3));
        Assert.Throws<InvalidDataException>(() => YoloxDecoder.Decode(new float[10], [1, 1, 10, 1], 1f, 416, 416, 0.3));
        Assert.Throws<InvalidDataException>(() => YoloxDecoder.Decode(new float[3549 * 4], [1, 3549, 4], 1f, 416, 416, 0.3));
        Assert.Throws<InvalidDataException>(() => YoloxDecoder.Decode(new float[100], Shape, 1f, 416, 416, 0.3)); // Daten kürzer als Form
    }

    [Theory]
    [InlineData(21, 32)]
    [InlineData(3549, 416)]
    [InlineData(8400, 640)]
    public void InputSizeForAnchors_DerivesSizeFromShape(int anchors, int expected) =>
        Assert.Equal(expected, YoloxDecoder.InputSizeForAnchors(anchors));

    [Fact]
    public void Decode_OtherAnchorCount_UsesDerivedGrid()
    {
        // 640 px: 80² + 40² + 20² = 8400 Anker. Erste Zelle von Stride 16 beginnt bei 6400.
        var output = new float[8400 * 85];
        SetAnchor(output, 6400 + 5 * 40 + 6, 0f, 0f, 2f, 2f, obj: 1f, cls0: 0.9f); // cx = 6·16 = 96, cy = 5·16 = 80, 32 × 32

        var d = Assert.Single(YoloxDecoder.Decode(output, [1, 8400, 85], 1f, 640, 640, 0.3));
        Assert.Equal(80.0 / 640, d.Box.X, 4);
        Assert.Equal(64.0 / 640, d.Box.Y, 4);
    }
}
