using System.IO;
using Fotobox.Core.Peace;
using Fotobox.Services.Detection.Peace;
using Xunit;

namespace Fotobox.Tests;

public class YuNetDecoderTests
{
    /// <summary>Leere Ausgaben (alles 0) für eine Eingabegröße.</summary>
    private sealed class Outputs(YuNetInputSize size)
    {
        public float[][] Cls { get; } = Make(size, 1);
        public float[][] Obj { get; } = Make(size, 1);
        public float[][] Bbox { get; } = Make(size, 4);
        public float[][] Kps { get; } = Make(size, 10);

        private static float[][] Make(YuNetInputSize size, int width) =>
            [.. YuNetDecoder.Strides.Select(s => new float[size.PaddedWidth / s * (size.PaddedHeight / s) * width])];

        /// <summary>Setzt eine Zelle (Stride-Index, Zeile, Spalte).</summary>
        public void Set(int s, int row, int col, float cls, float obj, float dx, float dy, float dw, float dh, params float[] kps)
        {
            var cols = size.PaddedWidth / YuNetDecoder.Strides[s];
            var i = row * cols + col;
            Cls[s][i] = cls;
            Obj[s][i] = obj;
            Bbox[s][i * 4] = dx;
            Bbox[s][i * 4 + 1] = dy;
            Bbox[s][i * 4 + 2] = dw;
            Bbox[s][i * 4 + 3] = dh;
            for (var k = 0; k < kps.Length; k++)
                Kps[s][i * 10 + k] = kps[k];
        }

        public IReadOnlyList<FaceLandmarks> Decode(YuNetInputSize size, double min = 0.5) => YuNetDecoder.Decode(Cls, Obj, Bbox, Kps, size, min);
    }

    private static readonly YuNetInputSize Size64 = new(64, 64, 64, 64);

    [Fact]
    public void Decode_SingleCell_GivesBoxPointsAndGeometricMeanScore()
    {
        var o = new Outputs(Size64);
        // Stride 8, Zeile 3, Spalte 4: Mitte (4 + 0,5)·8 = 36, (3 + 0,25)·8 = 26; Größe exp(ln 2)·8 = 16.
        o.Set(0, 3, 4, 0.81f, 1f, 0.5f, 0.25f, MathF.Log(2), MathF.Log(2),
            -1f, -1f, 1f, -1f, 0f, 0f, -1f, 1f, 1f, 1f);

        var faces = o.Decode(Size64);

        var f = Assert.Single(faces);
        Assert.Equal(0.9, f.Score, 6); // sqrt(0,81 · 1)
        Assert.Equal((36.0 - 8) / 64, f.Box.X, 6);
        Assert.Equal((26.0 - 8) / 64, f.Box.Y, 6);
        Assert.Equal(16.0 / 64, f.Box.Width, 6);
        Assert.Equal(16.0 / 64, f.Box.Height, 6);
        // Punkt = (Spalte + k)·Stride: rechtes Auge (3·8, 2·8), linkes Auge (5·8, 2·8), Nase (4·8, 3·8), Mundwinkel unten.
        Assert.Equal(new NormPoint(24.0 / 64, 16.0 / 64), f.RightEye);
        Assert.Equal(new NormPoint(40.0 / 64, 16.0 / 64), f.LeftEye);
        Assert.Equal(new NormPoint(32.0 / 64, 24.0 / 64), f.Nose);
        Assert.Equal(new NormPoint(24.0 / 64, 32.0 / 64), f.MouthRight);
        Assert.Equal(new NormPoint(40.0 / 64, 32.0 / 64), f.MouthLeft);
        // „Rechts“ ist im ungespiegelten Bild links.
        Assert.True(f.RightEye.X < f.LeftEye.X);
    }

    [Fact]
    public void Decode_ScoresAreClamped_AndBelowMinIsDropped()
    {
        var o = new Outputs(Size64);
        o.Set(0, 0, 0, 5f, 5f, 0, 0, 0, 0); // > 1 wird auf 1 gekappt → Score 1
        o.Set(0, 4, 4, 0.2f, 0.2f, 0, 0, 0, 0); // Score 0,2 < 0,5
        o.Set(0, 6, 6, -1f, 1f, 0, 0, 0, 0); // negativ → 0

        var faces = o.Decode(Size64);

        var f = Assert.Single(faces);
        Assert.Equal(1.0, f.Score, 6);
    }

    [Fact]
    public void Decode_UsesAllThreeStrides()
    {
        var o = new Outputs(Size64);
        o.Set(0, 0, 0, 1f, 1f, 0f, 0f, 0f, 0f);          // Stride 8: Mitte (0, 0)
        o.Set(1, 2, 2, 1f, 1f, 0f, 0f, 0f, 0f);          // Stride 16: Mitte (32, 32), Größe 16
        o.Set(2, 1, 1, 1f, 1f, 0f, 0f, MathF.Log(2), MathF.Log(2)); // Stride 32: Mitte (32, 32), Größe 64

        var faces = o.Decode(Size64);

        // Stride 16 und 32 überlappen stark (IoU 16²/64² < 0,3: bleibt beides), Stride 8 liegt in der Ecke.
        Assert.Equal(3, faces.Count);
        Assert.Contains(faces, f => Math.Abs(f.Box.Width - 8.0 / 64) < 1e-6);
        Assert.Contains(faces, f => Math.Abs(f.Box.Width - 16.0 / 64) < 1e-6);
        Assert.Contains(faces, f => Math.Abs(f.Box.Width - 1.0) < 1e-6);
    }

    [Fact]
    public void Decode_Nms_KeepsBetterOfOverlappingBoxes_AndSortsByScore()
    {
        var o = new Outputs(Size64);
        o.Set(0, 3, 3, 0.64f, 1f, 0f, 0f, MathF.Log(2), MathF.Log(2)); // Score 0,8, Box 16 × 16 um (24, 24)
        o.Set(0, 3, 4, 0.49f, 1f, 0f, 0f, MathF.Log(2), MathF.Log(2)); // Score 0,7, um 8 px verschoben → IoU 1/3 > 0,3
        o.Set(0, 7, 7, 0.9f, 1f, 0f, 0f, MathF.Log(2), MathF.Log(2));  // weit weg, Score ~0,95

        var faces = o.Decode(Size64);

        Assert.Equal(2, faces.Count);
        Assert.True(faces[0].Score > faces[1].Score);
        Assert.Equal(Math.Sqrt(0.9), faces[0].Score, 6);
        Assert.Equal(0.8, faces[1].Score, 6);
    }

    [Fact]
    public void Decode_NormalizesToContent_NotToPaddedInput()
    {
        var size = YuNetPreprocessor.ComputeSize(100, 60, 100); // Inhalt 100 × 60, Eingabe 128 × 64
        Assert.Equal(new YuNetInputSize(100, 60, 128, 64), size);
        var o = new Outputs(size);
        // Stride 8, Zeile 3, Spalte 8: Mitte (8 + 0)·8 = 64, 3·8 = 24, Größe 8.
        o.Set(0, 3, 8, 1f, 1f, 0f, 0f, 0f, 0f);

        var f = Assert.Single(o.Decode(size));

        Assert.Equal((64.0 - 4) / 100, f.Box.X, 6);
        Assert.Equal((24.0 - 4) / 60, f.Box.Y, 6);
        Assert.Equal(8.0 / 100, f.Box.Width, 6);
        Assert.Equal(8.0 / 60, f.Box.Height, 6);
    }

    [Fact]
    public void Decode_LimitsFaceCount()
    {
        var o = new Outputs(Size64);
        for (var i = 0; i < 8; i++)
            o.Set(0, i, 0, 1f, 1f, 0f, 0f, 0f, 0f);

        var faces = YuNetDecoder.Decode(o.Cls, o.Obj, o.Bbox, o.Kps, Size64, 0.5, maxFaces: 3);

        Assert.Equal(3, faces.Count);
    }

    [Fact]
    public void Decode_WrongOutputLength_Throws()
    {
        var o = new Outputs(Size64);
        var other = new Outputs(new YuNetInputSize(32, 32, 32, 32));

        Assert.Throws<InvalidDataException>(() => YuNetDecoder.Decode(other.Cls, other.Obj, other.Bbox, other.Kps, Size64, 0.5));
    }

    // ---------------------------------------------------------------- Vorverarbeitung

    [Theory]
    [InlineData(1920, 1080, 640, 360, 640, 384)]
    [InlineData(1280, 720, 640, 360, 640, 384)]
    [InlineData(640, 352, 640, 352, 640, 352)]
    [InlineData(320, 240, 320, 240, 320, 256)]
    [InlineData(10, 10, 10, 10, 32, 32)]
    public void ComputeSize_ShrinksToMax640_AndPadsToMultiplesOf32(int w, int h, int cw, int ch, int pw, int ph)
    {
        Assert.Equal(new YuNetInputSize(cw, ch, pw, ph), YuNetPreprocessor.ComputeSize(w, h));
    }

    [Fact]
    public void Preprocess_WritesBgr0To255_AndZeroPadding()
    {
        // 2 × 2 Pixel, B=10 G=20 R=30 überall; Eingabe 2 × 2 (Inhalt) in Ebene 32 × 32.
        var pixels = new byte[2 * 2 * 4];
        for (var i = 0; i < 4; i++)
        {
            pixels[i * 4] = 10;
            pixels[i * 4 + 1] = 20;
            pixels[i * 4 + 2] = 30;
            pixels[i * 4 + 3] = 255;
        }
        var image = new BgraImage { Width = 2, Height = 2, Stride = 8, Pixels = pixels };
        var size = YuNetPreprocessor.ComputeSize(2, 2);
        var dest = new float[size.RequiredLength];

        YuNetPreprocessor.Preprocess(image, size, dest);

        var plane = size.PaddedWidth * size.PaddedHeight;
        Assert.Equal(10f, dest[0], 3);          // Kanal 0 = Blau
        Assert.Equal(20f, dest[plane], 3);      // Kanal 1 = Grün
        Assert.Equal(30f, dest[2 * plane], 3);  // Kanal 2 = Rot
        Assert.Equal(0f, dest[5]);              // rechts neben dem Inhalt: Auffüllung
        Assert.Equal(0f, dest[2 * 32]);         // unter dem Inhalt: Auffüllung
    }
}
