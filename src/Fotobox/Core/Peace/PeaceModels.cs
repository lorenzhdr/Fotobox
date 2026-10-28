using Fotobox.Core.Fun;

namespace Fotobox.Core.Peace;

/// <summary>Punkt in normierten Koordinaten (0..1) relativ zum UNgespiegelten Kamerabild, Ursprung oben links.</summary>
public readonly record struct NormPoint(double X, double Y);

/// <summary>Handpunkt: X/Y normiert und ungespiegelt, Z relativ zum Handgelenk (Tiefe, gleiche Größenordnung wie X/Y).</summary>
public readonly record struct HandPoint(double X, double Y, double Z);

/// <summary>
/// Gesicht mit fünf Merkmalspunkten (YuNet). „Rechts“ ist rechts von der Person aus gesehen, also im ungespiegelten Kamerabild links.
/// </summary>
public sealed record FaceLandmarks(NormRect Box, double Score, NormPoint RightEye, NormPoint LeftEye, NormPoint Nose, NormPoint MouthRight, NormPoint MouthLeft);

/// <summary>Hand mit genau 21 Punkten in der Reihenfolge von MediaPipe (0 = Handgelenk).</summary>
public sealed record HandLandmarks(IReadOnlyList<HandPoint> Points, double Score, NormRect Box);

/// <summary>Was eine Anforderung an Erkennung braucht.</summary>
[Flags]
public enum GestureNeeds { None = 0, Faces = 1, Hands = 2 }

/// <summary>
/// Bedarf einer Anforderung: Umfang, Rate (Analysen pro Sekunde) und Mindestscores. <paramref name="HandCycleDivisor"/> &gt; 1 =
/// schneller Gesichtsmodus (Bart sichtbar): Gesichter laufen in jedem Zyklus (<paramref name="AnalysesPerSecond"/>), die Handstufe
/// nur in jedem n-ten (effektive Handrate = Rate / n), und Gesichter werden vor der Handstufe ausgeliefert.
/// </summary>
public sealed record GestureRequestOptions(GestureNeeds Needs, int AnalysesPerSecond, double MinFaceScore, double MinHandScore, int HandCycleDivisor = 1);

/// <summary>
/// Zustand der Erkennungsstufen; Gründe sind kurze deutsche Texte für Admin und Log. <paramref name="MeanCycleMs"/> = mittlere Dauer
/// eines Analysezyklus in Millisekunden (0 = unbekannt).
/// </summary>
public sealed record GestureStatus(bool FacesReady, bool HandsReady, string? FaceUnavailableReason, string? HandUnavailableReason, double AnalysesPerSecond, double MeanCycleMs = 0)
{
    public static GestureStatus Starting { get; } = new(false, false, null, null, 0);
}

/// <summary>
/// Ergebnis einer Analyse; <see cref="SourceWidth"/>/<see cref="SourceHeight"/> = Auflösung des Kamerabilds.
/// <paramref name="HandsIncluded"/> = die Handstufe hat in diesem Zyklus gelaufen bzw. liefert hier ihr Ergebnis; bei false sind
/// <see cref="Hands"/> leer, bedeuten aber NICHT „keine Hände“ (reines Gesichts-Update). <paramref name="FacesFresh"/> = die Gesichter
/// sind neu; bei false wiederholt das Update die Gesichter eines früheren Updates desselben Zyklus (nur zur Zuordnung der Hände).
/// <paramref name="FrameTime"/> = Zeitpunkt (<see cref="TimeProvider"/>), zu dem das analysierte Bild eintraf (Mindestwert = unbekannt).
/// </summary>
public sealed record GestureUpdate(IReadOnlyList<FaceLandmarks> Faces, IReadOnlyList<HandLandmarks> Hands, int SourceWidth, int SourceHeight,
    GestureStatus Status, bool HandsIncluded = true, bool FacesFresh = true, DateTimeOffset FrameTime = default);

/// <summary>Rohes Bild als BGRA (8 Bit je Kanal, <see cref="Stride"/> Bytes je Zeile).</summary>
public sealed class BgraImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required byte[] Pixels { get; init; }
}

/// <summary>Handfläche aus der Palm-Erkennung: Score, Mittelpunkt, Größe (normiert) und die Schlüsselpunkte 0 (Handgelenk) und 2 (Mittelfingerwurzel).</summary>
public sealed record PalmCandidate(double Score, NormPoint Center, double Size, NormPoint Kp0, NormPoint Kp2);

/// <summary>Gedrehter Ausschnitt für die Handpunkte: Mitte (normiert), Kantenlänge in Bildpixeln und Drehwinkel (Bogenmaß).</summary>
public readonly record struct HandRoi(NormPoint Center, double SizePx, double AngleRad);
