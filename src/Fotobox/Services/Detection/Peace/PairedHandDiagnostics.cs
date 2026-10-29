using System.Globalization;
using System.Text;
using Fotobox.Core.Peace;

namespace Fotobox.Services.Detection.Peace;

/// <summary>
/// Sammelt für einen Analysezyklus der Hand-Pipeline im Modus <c>PairedHands</c> (Herzerkennung) die Zwischenergebnisse für die
/// Logzeile „Herz-Diagnose: Pipeline …“. Wird vom <see cref="PeaceDetectionService"/> höchstens einmal pro Sekunde angelegt
/// (wiederverwendet, daher keine Allokation in Zyklen ohne Logzeile) und beeinflusst die Erkennung nicht. Nicht threadsicher; die
/// Zyklen laufen seriell.
/// </summary>
internal sealed class PairedHandDiagnostics
{
    private readonly StringBuilder _nearPalm = new();
    private readonly StringBuilder _scores = new();
    private readonly StringBuilder _pairs = new();

    public int Faces { get; private set; }
    public bool FacesAvailable { get; private set; }
    public double MinHandScore { get; private set; }
    public int Tracked { get; set; }
    public int RegionRuns { get; set; }
    public int RegionsCovered { get; set; }
    public bool FullImageRun { get; set; }
    public int PalmHits { get; set; }
    public int PalmRoisKept { get; set; }
    public int NearPalmDropped { get; private set; }
    public int RoiDuplicateDropped { get; set; }
    public int RoiCapDropped { get; set; }
    public int HandsBefore { get; set; }
    public int HandsAfter { get; set; }

    public PairedHandDiagnostics Reset(int faces, bool facesAvailable, double minHandScore)
    {
        Faces = faces;
        FacesAvailable = facesAvailable;
        MinHandScore = minHandScore;
        Tracked = RegionRuns = RegionsCovered = PalmHits = PalmRoisKept = NearPalmDropped = RoiDuplicateDropped = RoiCapDropped = 0;
        HandsBefore = HandsAfter = 0;
        FullImageRun = false;
        _nearPalm.Clear();
        _scores.Clear();
        _pairs.Clear();
        return this;
    }

    /// <summary>Lohnt sich eine Logzeile? Nicht, wenn weder Gesicht noch verfolgte Hand, Palm- oder Landmark-Ergebnis da war.</summary>
    public bool HasContent => Faces > 0 || Tracked > 0 || PalmHits > 0 || _scores.Length > 0;

    /// <summary>Eine Palm-ROI wurde als Duplikat einer verfolgten Hand verworfen (Abstand und Schwelle in Pixeln).</summary>
    public void AddNearPalmDrop(double distancePx, double thresholdPx)
    {
        NearPalmDropped++;
        if (_nearPalm.Length > 0)
            _nearPalm.Append(", ");
        _nearPalm.Append("Abstand ").Append(F1(distancePx)).Append(" px < Schwelle ").Append(F1(thresholdPx)).Append(" px");
    }

    /// <summary>Ergebnis eines Landmark-Laufs (<paramref name="rawScore"/> NaN = nicht beobachtet).</summary>
    public void AddLandmark(bool fromTracked, double rawScore, bool accepted)
    {
        if (_scores.Length > 0)
            _scores.Append("; ");
        _scores.Append(fromTracked ? "verfolgt " : "Palm ")
            .Append(double.IsNaN(rawScore) ? "?" : rawScore.ToString("0.000", CultureInfo.InvariantCulture))
            .Append(accepted ? " ok" : " verworfen");
    }

    /// <summary>Eine Duplikatprüfung zweier Handergebnisse (nur bei PairedHands).</summary>
    public void AddPairCheck(HandLandmarks kept, HandLandmarks candidate, int w, int h, bool duplicate)
    {
        var (mean, threshold) = PairedMetrics(kept, candidate, w, h);
        if (_pairs.Length > 0)
            _pairs.Append("; ");
        _pairs.Append("Scores ").Append(F3(kept.Score)).Append('/').Append(F3(candidate.Score))
            .Append(": mittlerer Abstand ").Append(F1(mean)).Append(" px, Schwelle ").Append(F1(threshold)).Append(" px")
            .Append(", Handseite ").Append(Side(kept.Handedness)).Append('/').Append(Side(candidate.Handedness))
            .Append(" -> ").Append(duplicate ? "Duplikat (zusammengefasst)" : "getrennt");
    }

    public string Format()
    {
        var sb = new StringBuilder(512);
        sb.Append("Herz-Diagnose: Pipeline: Gesichter=").Append(Faces.ToString(CultureInfo.InvariantCulture))
            .Append(FacesAvailable ? "" : " (keine Gesichtsstufe)")
            .Append(", verfolgte Hände=").Append(Tracked.ToString(CultureInfo.InvariantCulture))
            .Append(", Palmsuchen: Gesichtsbereiche=").Append(RegionRuns.ToString(CultureInfo.InvariantCulture))
            .Append(" (abgedeckt übersprungen ").Append(RegionsCovered.ToString(CultureInfo.InvariantCulture)).Append(')')
            .Append(", Vollbild=").Append(FullImageRun ? "ja" : "nein")
            .Append(", Palm-Treffer=").Append(PalmHits.ToString(CultureInfo.InvariantCulture))
            .Append(", Palm-ROIs übernommen=").Append(PalmRoisKept.ToString(CultureInfo.InvariantCulture))
            .Append(" (verworfen: nahe verfolgter Hand ").Append(NearPalmDropped.ToString(CultureInfo.InvariantCulture));
        if (_nearPalm.Length > 0)
            sb.Append(" [").Append(_nearPalm).Append(']');
        sb.Append(", sonstige Duplikate ").Append(RoiDuplicateDropped.ToString(CultureInfo.InvariantCulture))
            .Append(", ROI-Obergrenze ").Append(RoiCapDropped.ToString(CultureInfo.InvariantCulture)).Append(')')
            .Append(", Mindestscore=").Append(F3(MinHandScore))
            .Append(", Landmark-Läufe=[").Append(_scores).Append(']')
            .Append(", Hände vor Paar-Duplikatprüfung=").Append(HandsBefore.ToString(CultureInfo.InvariantCulture))
            .Append(", danach=").Append(HandsAfter.ToString(CultureInfo.InvariantCulture));
        if (_pairs.Length > 0)
            sb.Append(", Paarprüfungen=[").Append(_pairs).Append(']');
        sb.Append('.');
        return sb.ToString();
    }

    // Mittlerer 2D-Landmarkabstand (Pixel) und Schwelle (0,3 × kleinere Handlänge), wie in PeaceDetectionService.IsPairedDuplicate.
    private static (double Mean, double Threshold) PairedMetrics(HandLandmarks a, HandLandmarks b, int w, int h)
    {
        var count = Math.Min(a.Points.Count, b.Points.Count);
        if (count == 0)
            return (0, 0);
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            var dx = (a.Points[i].X - b.Points[i].X) * w;
            var dy = (a.Points[i].Y - b.Points[i].Y) * h;
            sum += Math.Sqrt(dx * dx + dy * dy);
        }
        var scale = Math.Min(WristToMiddle(a, w, h), WristToMiddle(b, w, h));
        return (sum / count, PeaceDetectionService.PairedDuplicateLandmarkDistance * scale);
    }

    private static double WristToMiddle(HandLandmarks hand, int w, int h)
    {
        if (hand.Points.Count < 10)
            return 0;
        var dx = (hand.Points[0].X - hand.Points[9].X) * w;
        var dy = (hand.Points[0].Y - hand.Points[9].Y) * h;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string Side(double? handedness) => handedness is { } v ? F3(v) : "-";

    private static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static string F3(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
}
