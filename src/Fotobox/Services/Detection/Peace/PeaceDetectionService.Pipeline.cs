using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Services.Detection.Peace;

// Analysezyklus: YuNet → Hand-Suchbereiche → Palm → Handpunkte. Läuft nur in einer Analyse zugleich (Busy-Flag), daher ohne Sperren.
public sealed partial class PeaceDetectionService
{
    private const int PalmsPerRegion = 2;
    private const double DuplicateRoiDistance = 0.35;
    private const double DuplicateHandIoU = 0.5;
    // Herz (PairedHands): Palm-ROI gilt nur als Duplikat einer verfolgten Hand, wenn ihre Mitte näher als 0,25 × ROI-Größe an deren
    // Handfläche liegt, und jede verfolgte Hand absorbiert je Palm-Lauf höchstens eine Palm-ROI (die nächste). Gemessen (Diagnose-Log,
    // eine Person mit beiden Händen im Herz): echte Doppelerkennungen der verfolgten Hand 6 bis 47 px, die zweite Hand ca. 100 bis 160 px
    // entfernt; die frühere Schwelle 0,5 × ROI-Größe lag bei 140 bis 175 px und verschluckte die zweite Hand fast immer.
    private const double PairedTrackedPalmDistance = 0.25;
    // Herz (PairedHands): zwei Ergebnisse sind Duplikat, wenn der mittlere Landmarkabstand unter 0,3 × |p0−p9| liegt.
    internal const double PairedDuplicateLandmarkDistance = 0.3;
    // Herz-Diagnose: höchstens eine Pipeline-Logzeile pro Sekunde.
    private static readonly TimeSpan PairedDiagnosticsInterval = TimeSpan.FromSeconds(1);
    // Handseite gilt nur als eindeutig, wenn der Wert mehr als 0,3 von 0,5 entfernt ist.
    private const double HandednessMargin = 0.3;

    // Nur innerhalb von RunCycleAsync benutzt (serielle Zyklen).
    private volatile Stage? _inFlight;
    private List<HandLandmarks> _tracked = [];
    private int _faceCursor;
    // Dauer der Teilschritte des laufenden Zyklus (Palm- und Landmark-Läufe summiert).
    private TimeSpan _palmTime, _landmarkTime;
    // Herz-Diagnose (PairedHands): wiederverwendeter Sammler, Drosselung über die Dienstzeit, Roh-Score des letzten Landmark-Laufs.
    private readonly PairedHandDiagnostics _pairedDiagnostics = new();
    private long _pairedDiagnosticsStamp;
    private bool _pairedDiagnosticsLogged;
    private double _lastRawScore = double.NaN;
    private Action<double>? _rawScoreObserver;

    private Stage CurrentStage() => _inFlight ?? _faceStage;

    /// <param name="onFaces">Wird gerufen, sobald die Gesichter feststehen und die Handstufe noch folgt (schneller Gesichtsmodus).</param>
    private async Task<CycleResult> RunCycleAsync(BgraImage image, GestureRequestOptions options, long cycle, Action<IReadOnlyList<FaceLandmarks>>? onFaces = null)
    {
        var needs = options.Needs;
        var divisor = Math.Max(1, options.HandCycleDivisor);
        var faceDetector = _face;
        var handDetector = _hand;
        var useFaces = (needs & (GestureNeeds.Faces | GestureNeeds.Hands)) != 0 && faceDetector is not null && _faceStage.Ready && !_faceStage.Broken;
        var wantHands = (needs & GestureNeeds.Hands) != 0 && handDetector is not null && _handStage.Ready && !_handStage.Broken;
        // Im schnellen Gesichtsmodus läuft die Handstufe nur in jedem n-ten Zyklus.
        var useHands = wantHands && cycle % divisor == 0;

        var start = _time.GetTimestamp();
        _palmTime = _landmarkTime = TimeSpan.Zero;

        IReadOnlyList<FaceLandmarks> faces = [];
        var faceTime = TimeSpan.Zero;
        if (useFaces)
        {
            _inFlight = _faceStage;
            var t0 = _time.GetTimestamp();
            faces = await Guarded(_faceStage, () => faceDetector!.DetectAsync(image, options.MinFaceScore, CancellationToken.None)).ConfigureAwait(false);
            faceTime = _time.GetElapsedTime(t0);
            if (useHands && divisor > 1)
                onFaces?.Invoke(faces);
        }

        IReadOnlyList<HandLandmarks> hands = [];
        if (useHands)
        {
            _inFlight = _handStage;
            hands = await FindHandsAsync(handDetector!, image, faces, useFaces, options.MinHandScore, cycle / divisor, options.PairedHands).ConfigureAwait(false);
        }
        else if (!wantHands)
        {
            _tracked = [];
        }
        var timings = new CycleTimings(faceTime, _palmTime, _landmarkTime, _time.GetElapsedTime(start));
        return new CycleResult(faces, hands, useFaces, useHands, HandsIncluded: useHands || !wantHands, FacesPublished: useFaces && useHands && divisor > 1, timings);
    }

    private static async Task<T> Guarded<T>(Stage stage, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new StageFailure(stage, ex);
        }
    }

    private async Task<IReadOnlyList<HandLandmarks>> FindHandsAsync(IHandLandmarkDetector detector, BgraImage image,
        IReadOnlyList<FaceLandmarks> faces, bool facesAvailable, double minHandScore, long handCycle, bool paired = false)
    {
        int w = image.Width, h = image.Height;
        var palmBudget = MaxPalmRuns;
        var rois = new List<HandRoi>();
        // Nur bei PairedHands: zu jeder ROI die verfolgte Hand, aus der sie stammt (null = Palm-ROI), für die Duplikatprüfung.
        var sources = paired ? new List<HandLandmarks?>() : null;
        // Herz-Diagnose: nur bei PairedHands und höchstens einmal pro Sekunde; sonst null (keine Zusatzarbeit, keine Allokation).
        var diag = paired ? BeginPairedDiagnostics(faces.Count, facesAvailable, minHandScore) : null;
        detector.SetScoreObserver(diag is null ? null : _rawScoreObserver ??= score => _lastRawScore = score);

        // (a) Verfolgte Hände: Ausschnitt aus den letzten Handpunkten, die Handflächen-Suche entfällt.
        foreach (var tracked in _tracked)
        {
            if (rois.Count >= MaxLandmarkRuns)
                break;
            rois.Add(HandRoiMath.FromLandmarks(tracked, w, h));
            sources?.Add(tracked);
        }
        if (diag is not null)
            diag.Tracked = rois.Count;

        // (b) Je Gesicht ohne verfolgte Hand in seinem Suchbereich: Handflächen im Bereich um das Gesicht (reihum, wenn es zu viele sind).
        if (faces.Count > 0)
        {
            var count = faces.Count;
            var start = _faceCursor % count;
            var processed = 0;
            for (var i = 0; i < count && palmBudget > 0; i++)
            {
                var face = faces[(start + i) % count];
                processed++;
                var region = SearchRegion(face, w, h);
                if (region.IsEmpty)
                    continue;
                if (RegionCovered(region, paired))
                {
                    if (diag is not null)
                        diag.RegionsCovered++;
                    continue;
                }
                palmBudget--;
                var tp = _time.GetTimestamp();
                var palms = await Guarded(_handStage, () => detector.DetectPalmsAsync(image, region, PalmsPerRegion, CancellationToken.None)).ConfigureAwait(false);
                _palmTime += _time.GetElapsedTime(tp);
                if (diag is not null)
                {
                    diag.RegionRuns++;
                    diag.PalmHits += palms.Count;
                }
                AddPalmRois(rois, sources, palms, w, h, diag);
            }
            _faceCursor = (start + processed) % count;
        }

        // (c) Alle FullImageInterval Analysen (ohne Gesichtsstufe jede) zusätzlich das ganze Bild.
        if (palmBudget > 0 && (!facesAvailable || handCycle % FullImageInterval == 0))
        {
            palmBudget--;
            var tp = _time.GetTimestamp();
            var palms = await Guarded(_handStage, () => detector.DetectPalmsAsync(image, NormRect.Full, PalmsPerRegion, CancellationToken.None)).ConfigureAwait(false);
            _palmTime += _time.GetElapsedTime(tp);
            if (diag is not null)
            {
                diag.FullImageRun = true;
                diag.PalmHits += palms.Count;
            }
            AddPalmRois(rois, sources, palms, w, h, diag);
        }

        var found = new List<HandLandmarks>();
        for (var i = 0; i < rois.Count && i < MaxLandmarkRuns; i++)
        {
            var roi = rois[i];
            _lastRawScore = double.NaN;
            var tl = _time.GetTimestamp();
            var hand = await Guarded(_handStage, () => detector.DetectLandmarksAsync(image, roi, minHandScore, CancellationToken.None)).ConfigureAwait(false);
            _landmarkTime += _time.GetElapsedTime(tl);
            diag?.AddLandmark(sources?[i] is not null, _lastRawScore, hand is not null);
            if (hand is not null)
                found.Add(hand);
        }

        // Duplikate (dieselbe Hand aus Verfolgung und Palm) zusammenfassen: der bessere Score gewinnt.
        found.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var result = new List<HandLandmarks>(found.Count);
        foreach (var hand in found)
        {
            bool duplicate;
            if (diag is not null)
            {
                // Wie Any(): bei der ersten Übereinstimmung Schluss, jede ausgeführte Prüfung kommt in die Diagnose.
                duplicate = false;
                foreach (var kept in result)
                {
                    var same = IsPairedDuplicate(kept, hand, w, h);
                    diag.AddPairCheck(kept, hand, w, h, same);
                    if (same)
                    {
                        duplicate = true;
                        break;
                    }
                }
            }
            else
            {
                duplicate = paired
                    ? result.Any(kept => IsPairedDuplicate(kept, hand, w, h))
                    : result.Any(kept => kept.Box.IoU(hand.Box) > DuplicateHandIoU);
            }
            if (!duplicate)
                result.Add(hand);
        }
        _tracked = result;
        if (diag is not null)
        {
            diag.HandsBefore = found.Count;
            diag.HandsAfter = result.Count;
            FinishPairedDiagnostics(diag);
        }
        return result;
    }

    // Herz-Diagnose: Gibt den (wiederverwendeten) Sammler zurück, wenn die letzte Pipeline-Logzeile mindestens eine Sekunde her ist.
    private PairedHandDiagnostics? BeginPairedDiagnostics(int faces, bool facesAvailable, double minHandScore)
    {
        if (_pairedDiagnosticsLogged && _time.GetElapsedTime(_pairedDiagnosticsStamp) < PairedDiagnosticsInterval)
            return null;
        return _pairedDiagnostics.Reset(faces, facesAvailable, minHandScore);
    }

    private void FinishPairedDiagnostics(PairedHandDiagnostics diag)
    {
        // Ohne Gesicht, Hand und Palm-Ergebnis (leeres Bild) keine Zeile; die Drosselfrist läuft dann nicht an.
        if (!diag.HasContent)
            return;
        _pairedDiagnosticsLogged = true;
        _pairedDiagnosticsStamp = _time.GetTimestamp();
        AppLog.Info(diag.Format());
    }

    /// <summary>
    /// Ob der Suchbereich als abgedeckt gilt und keine Handflächen-Suche braucht: bisher schon bei einer verfolgten Hand darin;
    /// mit <paramref name="paired"/> erst bei <see cref="PalmsPerRegion"/> verfolgten Händen (die zweite Hand eines Herzens wird gesucht).
    /// </summary>
    private bool RegionCovered(NormRect region, bool paired)
    {
        if (!paired)
            return _tracked.Any(t => region.ContainsPoint(t.Box.CenterX, t.Box.CenterY));
        return _tracked.Count(t => region.ContainsPoint(t.Box.CenterX, t.Box.CenterY)) >= PalmsPerRegion;
    }

    internal static void AddPalmRois(List<HandRoi> rois, List<HandLandmarks?>? sources, IReadOnlyList<PalmCandidate> palms, int w, int h,
        PairedHandDiagnostics? diag = null)
    {
        var count = palms.Count;
        var palmRois = new HandRoi[count];
        for (var p = 0; p < count; p++)
            palmRois[p] = HandRoiMath.FromPalm(palms[p], w, h);

        // Nur PairedHands: Jede verfolgte Hand absorbiert höchstens EINE Palm-ROI dieses Palm-Laufs (die nächste innerhalb der Schwelle).
        // Weitere Kandidaten werden nie wegen derselben verfolgten Hand verworfen; echte Doppelerkennungen fängt danach die
        // Palm-gegen-Palm-Prüfung bzw. IsPairedDuplicate nach dem Landmark-Lauf ab.
        var absorbed = new double[count]; // Abstand in Pixeln; NaN = nicht absorbiert
        Array.Fill(absorbed, double.NaN);
        if (sources is not null)
        {
            for (var i = 0; i < sources.Count; i++)
            {
                if (sources[i] is not { } tracked)
                    continue;
                var best = -1;
                var bestDistance = double.MaxValue;
                for (var p = 0; p < count; p++)
                {
                    if (!double.IsNaN(absorbed[p]))
                        continue;
                    var distance = NearPalmDistance(palmRois[p], tracked, w, h);
                    if (distance < PairedTrackedPalmDistance * palmRois[p].SizePx && distance < bestDistance)
                    {
                        best = p;
                        bestDistance = distance;
                    }
                }
                if (best >= 0)
                    absorbed[best] = bestDistance;
            }
        }

        for (var p = 0; p < count; p++)
        {
            var roi = palmRois[p];
            if (!double.IsNaN(absorbed[p]))
            {
                diag?.AddNearPalmDrop(absorbed[p], PairedTrackedPalmDistance * roi.SizePx);
                continue;
            }
            if (rois.Count >= MaxLandmarkRuns)
            {
                if (diag is not null)
                    diag.RoiCapDropped++;
                continue;
            }
            var duplicate = false;
            for (var i = 0; i < rois.Count && !duplicate; i++)
            {
                // Mit PairedHands zählen nur Palm-ROIs (keine aus verfolgten Händen abgeleiteten, 2,0 × Handausdehnung großen ROIs).
                if (sources?[i] is not null)
                    continue;
                duplicate = IsDuplicate(rois[i], roi, w, h);
                if (duplicate && diag is not null)
                    diag.RoiDuplicateDropped++;
            }
            if (!duplicate)
            {
                rois.Add(roi);
                sources?.Add(null);
                if (diag is not null)
                    diag.PalmRoisKept++;
            }
        }
    }

    // Abstand (Pixel) der Palm-ROI-Mitte vom Handflächenschwerpunkt einer verfolgten Hand.
    private static double NearPalmDistance(HandRoi palmRoi, HandLandmarks tracked, int w, int h)
    {
        var (cx, cy) = PalmCentroid(tracked);
        var dx = (palmRoi.Center.X - cx) * w;
        var dy = (palmRoi.Center.Y - cy) * h;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Handflächenschwerpunkt: Mittel der Punkte 0 (Handgelenk) und 5, 9, 13, 17 (Fingerwurzeln), normiert.</summary>
    private static (double X, double Y) PalmCentroid(HandLandmarks hand)
    {
        var p = hand.Points;
        return ((p[0].X + p[5].X + p[9].X + p[13].X + p[17].X) / 5, (p[0].Y + p[5].Y + p[9].Y + p[13].Y + p[17].Y) / 5);
    }

    /// <summary>
    /// Duplikat zweier Handergebnisse (nur PairedHands): mittlerer 2D-Landmarkabstand (in Pixeln, isotrop) unter 0,3 × |p0−p9|,
    /// und die Handseiten sind nicht eindeutig verschieden. Fehlt die Handseite, zählt nur der Abstand.
    /// </summary>
    internal static bool IsPairedDuplicate(HandLandmarks a, HandLandmarks b, int w, int h)
    {
        if (a.Handedness is { } ha && b.Handedness is { } hb
            && Math.Abs(ha - 0.5) > HandednessMargin && Math.Abs(hb - 0.5) > HandednessMargin && (ha > 0.5) != (hb > 0.5))
            return false;
        var count = Math.Min(a.Points.Count, b.Points.Count);
        if (count == 0)
            return false;
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            var dx = (a.Points[i].X - b.Points[i].X) * w;
            var dy = (a.Points[i].Y - b.Points[i].Y) * h;
            sum += Math.Sqrt(dx * dx + dy * dy);
        }
        // Maßstab: die kleinere der beiden Handlängen (Handgelenk → Mittelfingerwurzel), damit eher getrennt als zusammengefasst wird.
        var scale = Math.Min(WristToMiddle(a, w, h), WristToMiddle(b, w, h));
        return sum / count < PairedDuplicateLandmarkDistance * scale;
    }

    private static double WristToMiddle(HandLandmarks hand, int w, int h)
    {
        var dx = (hand.Points[0].X - hand.Points[9].X) * w;
        var dy = (hand.Points[0].Y - hand.Points[9].Y) * h;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsDuplicate(HandRoi a, HandRoi b, int w, int h)
    {
        var dx = (a.Center.X - b.Center.X) * w;
        var dy = (a.Center.Y - b.Center.Y) * h;
        return Math.Sqrt(dx * dx + dy * dy) < DuplicateRoiDistance * Math.Max(a.SizePx, b.SizePx);
    }

    /// <summary>
    /// Suchbereich für Hände um ein Gesicht (Breite fw): x ∈ Mitte ± 3,5 fw, y ∈ [Mitte − 2 fw, Mitte + 3 fw], quadratisch (7 fw),
    /// aufs Bild begrenzt; leer, wenn er ganz außerhalb liegt. Ergebnis normiert.
    /// </summary>
    internal static NormRect SearchRegion(FaceLandmarks face, int imageW, int imageH)
    {
        var fw = face.Box.Width * imageW;
        if (!(fw > 0))
            return default;
        var cx = face.Box.CenterX * imageW;
        var cy = face.Box.CenterY * imageH + 0.5 * fw; // Mitte des y-Bereichs
        var half = 3.5 * fw;
        return NormRect.FromEdges((cx - half) / imageW, (cy - half) / imageH, (cx + half) / imageW, (cy + half) / imageH).Clamp01();
    }
}
