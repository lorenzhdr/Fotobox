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

    // Nur innerhalb von RunCycleAsync benutzt (serielle Zyklen).
    private volatile Stage? _inFlight;
    private List<HandLandmarks> _tracked = [];
    private int _faceCursor;
    // Dauer der Teilschritte des laufenden Zyklus (Palm- und Landmark-Läufe summiert).
    private TimeSpan _palmTime, _landmarkTime;

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
            hands = await FindHandsAsync(handDetector!, image, faces, useFaces, options.MinHandScore, cycle / divisor).ConfigureAwait(false);
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
        IReadOnlyList<FaceLandmarks> faces, bool facesAvailable, double minHandScore, long handCycle)
    {
        int w = image.Width, h = image.Height;
        var palmBudget = MaxPalmRuns;
        var rois = new List<HandRoi>();

        // (a) Verfolgte Hände: Ausschnitt aus den letzten Handpunkten, die Handflächen-Suche entfällt.
        foreach (var tracked in _tracked)
        {
            if (rois.Count >= MaxLandmarkRuns)
                break;
            rois.Add(HandRoiMath.FromLandmarks(tracked, w, h));
        }

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
                if (region.IsEmpty || _tracked.Any(t => region.ContainsPoint(t.Box.CenterX, t.Box.CenterY)))
                    continue;
                palmBudget--;
                var tp = _time.GetTimestamp();
                var palms = await Guarded(_handStage, () => detector.DetectPalmsAsync(image, region, PalmsPerRegion, CancellationToken.None)).ConfigureAwait(false);
                _palmTime += _time.GetElapsedTime(tp);
                AddPalmRois(rois, palms, w, h);
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
            AddPalmRois(rois, palms, w, h);
        }

        var found = new List<HandLandmarks>();
        for (var i = 0; i < rois.Count && i < MaxLandmarkRuns; i++)
        {
            var roi = rois[i];
            var tl = _time.GetTimestamp();
            var hand = await Guarded(_handStage, () => detector.DetectLandmarksAsync(image, roi, minHandScore, CancellationToken.None)).ConfigureAwait(false);
            _landmarkTime += _time.GetElapsedTime(tl);
            if (hand is not null)
                found.Add(hand);
        }

        // Duplikate (dieselbe Hand aus Verfolgung und Palm) zusammenfassen: der bessere Score gewinnt.
        found.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var result = new List<HandLandmarks>(found.Count);
        foreach (var hand in found)
        {
            if (!result.Any(kept => kept.Box.IoU(hand.Box) > DuplicateHandIoU))
                result.Add(hand);
        }
        _tracked = result;
        return result;
    }

    private static void AddPalmRois(List<HandRoi> rois, IReadOnlyList<PalmCandidate> palms, int w, int h)
    {
        foreach (var palm in palms)
        {
            if (rois.Count >= MaxLandmarkRuns)
                return;
            var roi = HandRoiMath.FromPalm(palm, w, h);
            if (!rois.Any(existing => IsDuplicate(existing, roi, w, h)))
                rois.Add(roi);
        }
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
