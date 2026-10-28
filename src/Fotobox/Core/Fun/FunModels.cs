namespace Fotobox.Core.Fun;

/// <summary>
/// Rechteck in normierten Koordinaten (0..1) relativ zum UNgespiegelten Kamerabild, Ursprung oben links.
/// Werte außerhalb 0..1 sind möglich, bis <see cref="Clamp01"/> sie kappt.
/// </summary>
public readonly record struct NormRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static NormRect FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);

    /// <summary>Auf das Bild (0..1) gekappt; leer (Breite/Höhe 0), wenn es ganz außerhalb liegt.</summary>
    public NormRect Clamp01()
    {
        var l = Math.Clamp(X, 0, 1);
        var t = Math.Clamp(Y, 0, 1);
        var r = Math.Clamp(Right, 0, 1);
        var b = Math.Clamp(Bottom, 0, 1);
        return FromEdges(l, t, Math.Max(l, r), Math.Max(t, b));
    }

    public NormRect Intersect(NormRect other)
    {
        var l = Math.Max(X, other.X);
        var t = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? new NormRect(l, t, 0, 0) : FromEdges(l, t, r, b);
    }

    /// <summary>Intersection over Union (0 … 1).</summary>
    public double IoU(NormRect other)
    {
        var inter = Intersect(other).Area;
        var union = Area + other.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public bool ContainsPoint(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    /// <summary>Das ganze Bild.</summary>
    public static NormRect Full { get; } = new(0, 0, 1, 1);
}

/// <summary>Art einer Erkennung: Gesicht (<c>FaceDetector</c>) oder ganze Person (KI-Modell).</summary>
public enum DetectionKind { Face, Body }

/// <summary>Eine Erkennung im UNgespiegelten Kamerabild. Gesichtserkennung liefert <see cref="Confidence"/> = 1.</summary>
public sealed record Detection(NormRect Box, double Confidence, DetectionKind Kind);

/// <summary>
/// Verkleinertes Analysebild (Bgra32, Stride = Width × 4, UNgespiegelt), das der Erkennungsdienst aus einem Kamera-Frame kopiert.
/// Gehört dem Dienst; ein Detektor darf es nur während <c>DetectAsync</c> lesen.
/// </summary>
public sealed class DetectionFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Pixels { get; init; }
    public int Stride => Width * 4;
    /// <summary>Auflösung des Kamera-Frames, aus dem verkleinert wurde (Seitenverhältnis für das Overlay).</summary>
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }
}

/// <summary>Zustand des Erkennungsdienstes für die Statusanzeige im Admin.</summary>
/// <param name="Requested">Gewünschte Methode.</param>
/// <param name="Active">Tatsächlich laufende Methode; null = noch nicht bereit bzw. keine verfügbar.</param>
/// <param name="FallbackReason">Deutscher Grund, warum statt Ganzkörper die Gesichtserkennung läuft (sonst null).</param>
/// <param name="UnavailableReason">Deutscher Grund, warum gar keine Erkennung läuft (sonst null).</param>
/// <param name="AnalysesPerSecond">Gemessene Analysen pro Sekunde (gleitend).</param>
public sealed record DetectionStatus(
    DetectionMethod Requested,
    DetectionKind? Active,
    string? FallbackReason,
    string? UnavailableReason,
    double AnalysesPerSecond)
{
    public static DetectionStatus Starting(DetectionMethod requested) => new(requested, null, null, null, 0);
}

/// <summary>Ergebnis einer Analyse.</summary>
/// <param name="Detections">Erkennungen (schon nach Mindestkonfidenz gefiltert, nicht nach Größe).</param>
/// <param name="SourceWidth">Breite des analysierten Kamera-Frames.</param>
/// <param name="SourceHeight">Höhe des analysierten Kamera-Frames.</param>
public sealed record DetectionUpdate(
    IReadOnlyList<Detection> Detections,
    int SourceWidth,
    int SourceHeight,
    DetectionStatus Status);
