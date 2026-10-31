using Fotobox.Core.Fun;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;

namespace Fotobox.Core.Tracking;

/// <summary>
/// Rohdaten einer Analyse des Gestendienstes, wie das <c>PeaceOverlayViewModel</c> sie dem Tracking-Overlay meldet (nur Verweise, nichts
/// wird kopiert oder berechnet). <paramref name="Faces"/> sind die verfolgten Gesichter dieses Updates (nur bei
/// <see cref="GestureUpdate.FacesFresh"/> neu), <paramref name="Options"/> der aktuell angeforderte Bedarf (daraus folgt, ob Hände
/// angefordert sind), <paramref name="Aspect"/> Breite/Höhe des Kamerabilds.
/// </summary>
public sealed record GestureObservation(GestureUpdate Update, IReadOnlyList<TrackedFace> Faces, GestureRequestOptions? Options, double Aspect);

/// <summary>
/// Passiv gelesener Stand von Zielerfassung und Gestenerkennung zum Zeitpunkt des Aufbaus (Engine-Ergebnisse, Statuswerte); der Aufrufer
/// liest ihn auf dem UI-Thread aus den Ansichtsmodellen.
/// </summary>
public sealed record TrackingLive(
    bool PeaceActive, GestureStatus? PeaceStatus, bool FunActive, DetectionStatus? FunStatus,
    IReadOnlyList<HandView> PeaceHands, IReadOnlyList<MoustacheView> Moustaches, HeartView? Heart, bool HeartNow, bool MiddleFingerNow,
    IReadOnlyList<FunTarget> Targets);

/// <summary>
/// Baut aus den Rohdaten der Erkennungen (<see cref="OnGestureUpdate"/>, <see cref="OnDetections"/>) und dem passiv gelesenen Stand der
/// Engines (<see cref="TrackingLive"/>) das unveränderliche <see cref="TrackingOverlayFrame"/>. Hält nur Verweise auf die Rohlisten und
/// einen eigenen <see cref="HandTracker"/> für die Handnummern; berührt weder Engines noch Dienste und ruft nie <c>Request</c> auf.
/// Daten, die länger als <see cref="MaxAge"/> nicht erneuert wurden, erscheinen nicht mehr. Die Warum-Info (nur bei
/// <see cref="TrackingOverlaySettings.ShowVerdicts"/>) ruft ausschließlich die zustandslosen Klassifikatoren auf. Zeit nur über den
/// <see cref="TimeProvider"/>; nicht threadsicher (UI-Thread).
/// </summary>
public sealed class TrackingOverlayFrameBuilder
{
    /// <summary>Älter als diese Frist ohne neue Daten verschwindet eine Ebene.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(1.5);

    /// <summary>Ab dieser Überdeckung gehört eine Hand der Anzeige zu einer Hand der Peaceerkennung.</summary>
    private const double MatchIoU = 0.3;

    private static readonly DateTimeOffset Never = DateTimeOffset.MinValue;

    private readonly TimeProvider _time;
    private readonly HandTracker _handTracker;

    private IReadOnlyList<TrackedFace> _faces = [];
    private DateTimeOffset _facesAt = Never;
    private IReadOnlyList<TrackedHand> _hands = [];
    private DateTimeOffset _handsAt = Never;
    private bool _handsRequested;
    private GestureRequestOptions? _options;
    private IReadOnlyList<Detection> _detections = [];
    private DateTimeOffset _detectionsAt = Never;
    private DateTimeOffset _peaceSeenAt = Never;
    private DateTimeOffset _funSeenAt = Never;
    private double _peaceW, _peaceH, _funW, _funH;
    private double _aspect = OverlayMapper.DefaultAspect;

    public TrackingOverlayFrameBuilder(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _handTracker = new HandTracker(time);
    }

    /// <summary>Vergisst alle Rohdaten und Handnummern (beim Einblenden).</summary>
    public void Reset()
    {
        _handTracker.LostAfter = HandTracker.LostTimeout;
        _faces = [];
        _facesAt = Never;
        _hands = [];
        _handsAt = Never;
        _handsRequested = false;
        _options = null;
        _detections = [];
        _detectionsAt = Never;
        _peaceSeenAt = Never;
        _funSeenAt = Never;
        _peaceW = _peaceH = _funW = _funH = 0;
        _aspect = OverlayMapper.DefaultAspect;
    }

    /// <summary>
    /// Nimmt eine Analyse des Gestendienstes auf. Ein reines Gesichts-Update (<see cref="GestureUpdate.HandsIncluded"/> false) sagt nichts
    /// über die Hände: dann bleiben die letzten. Sind gar keine Hände angefordert (<see cref="GestureRequestOptions.Needs"/>), liefert der
    /// Dienst leere Listen mit <c>HandsIncluded</c> true; das gilt als „Hände nicht aktiv“.
    /// </summary>
    public void OnGestureUpdate(GestureObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var update = observation.Update;
        var now = _time.GetUtcNow();
        _peaceSeenAt = now;
        _options = observation.Options;
        if (observation.Aspect > 0 && double.IsFinite(observation.Aspect))
            _aspect = observation.Aspect;
        if (update.SourceWidth > 0 && update.SourceHeight > 0)
        {
            _peaceW = update.SourceWidth;
            _peaceH = update.SourceHeight;
        }

        if (update.FacesFresh)
        {
            _faces = observation.Faces;
            _facesAt = now;
        }

        _handsRequested = observation.Options is { } options && (options.Needs & GestureNeeds.Hands) != 0;
        if (!_handsRequested)
        {
            _hands = [];
            _handsAt = Never;
        }
        else if (update.HandsIncluded)
        {
            if (observation.Options is { } o)
                _handTracker.LostAfter = TimeSpan.FromSeconds(PeaceEngine.AnalysisIntervalFactor / Math.Max(1, o.AnalysesPerSecond));
            _hands = _handTracker.Update(update.Hands, _aspect);
            _handsAt = now;
        }
    }

    /// <summary>Nimmt eine Analyse der Personenerkennung auf.</summary>
    public void OnDetections(DetectionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var now = _time.GetUtcNow();
        _funSeenAt = now;
        _detections = update.Detections;
        _detectionsAt = now;
        if (update.SourceWidth > 0 && update.SourceHeight > 0)
        {
            _funW = update.SourceWidth;
            _funH = update.SourceHeight;
        }
    }

    /// <summary>Baut den Stand zum jetzigen Zeitpunkt.</summary>
    public TrackingOverlayFrame Build(AppSettings settings, TrackingLive live)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(live);
        var options = (settings.TrackingOverlay ?? new TrackingOverlaySettings()).Clone();
        var now = _time.GetUtcNow();

        var personsFresh = IsFresh(_detectionsAt, now);
        var facesFresh = IsFresh(_facesAt, now);
        var handsFresh = _handsRequested && IsFresh(_handsAt, now);

        var persons = options.ShowPersons && personsFresh
            ? _detections.Select(d => new TrackingPersonItem(d.Box, d.Confidence, d.Kind)).ToList()
            : [];
        var targets = options.ShowPersons && live.FunActive
            ? live.Targets.Where(t => t.Id != FunEngine.SimulatedTargetId)
                .Select(t => new TrackingTargetItem(t.Id, t.Box, t.Label, t.IsLockingOn)).ToList()
            : [];
        var faces = options.ShowFaces && facesFresh
            ? _faces.Select(f => new TrackingFaceItem(f.Id, f.Face)).ToList()
            : [];

        var rawHands = handsFresh ? _hands.Select(h => h.Hand).ToList() : [];
        var showHands = (options.ShowHands || options.ShowGestures) && handsFresh;
        var verdicts = options.ShowVerdicts && showHands;
        var hands = new List<TrackingHandItem>();
        if (showHands)
        {
            foreach (var tracked in _hands)
            {
                var hand = tracked.Hand;
                var view = BestView(live.PeaceHands, hand.Box);
                hands.Add(new TrackingHandItem(tracked.Id, hand.Points.ToArray(), hand.Box, hand.Score, hand.Handedness,
                    view?.IsPeace ?? false, view?.Progress ?? 0,
                    verdicts ? HandVerdicts(hand, settings) : []));
            }
        }

        var pairs = verdicts && rawHands.Count >= 2 ? PairVerdicts(rawHands, settings) : [];

        var moustaches = options.ShowMoustaches
            ? live.Moustaches.Where(m => !m.Vanishing).Select(m => new TrackingMoustacheItem(m.FaceId, m.Pose)).ToList()
            : [];
        var hearts = options.ShowGestures && live.Heart is { } heart
            ? new List<TrackingHeartItem> { new(heart.Center, heart.SizeNorm, heart.Progress, heart.Shape) }
            : [];

        return new TrackingOverlayFrame
        {
            Options = options,
            PersonImageWidth = _funW,
            PersonImageHeight = _funH,
            GestureImageWidth = _peaceW,
            GestureImageHeight = _peaceH,
            Persons = persons,
            Targets = targets,
            Faces = faces,
            Hands = hands,
            Moustaches = moustaches,
            Hearts = hearts,
            Pairs = pairs,
            PeaceNow = options.ShowGestures && live.PeaceHands.Any(h => h.IsPeace),
            MiddleFingerNow = options.ShowGestures && live.MiddleFingerNow,
            HeartNow = options.ShowGestures && live.HeartNow,
            Status = BuildStatus(live, now),
        };
    }

    private static bool IsFresh(DateTimeOffset at, DateTimeOffset now) => at != Never && now - at <= MaxAge;

    private static HandView? BestView(IReadOnlyList<HandView> views, NormRect box)
    {
        HandView? best = null;
        var bestIoU = MatchIoU;
        foreach (var view in views)
        {
            var iou = view.Box.IoU(box);
            if (iou >= bestIoU)
            {
                bestIoU = iou;
                best = view;
            }
        }
        return best;
    }

    // ---------------------------------------------------------------- Statuszeile

    private IReadOnlyList<TrackingStatusEntry> BuildStatus(TrackingLive live, DateTimeOffset now)
    {
        var entries = new List<TrackingStatusEntry>(3);

        // Personen (Zielerfassung): Die Rate gilt nur, solange Analysen eintreffen (der Dienst behält sonst den letzten Wert).
        var funRate = live.FunActive && IsFresh(_funSeenAt, now) && live.FunStatus is { AnalysesPerSecond: > 0 } fs ? fs.AnalysesPerSecond : (double?)null;
        string? funDetail = null;
        if (live.FunActive && live.FunStatus is { } fun)
        {
            funDetail = fun.UnavailableReason ?? fun.FallbackReason
                ?? (fun.Active switch { DetectionKind.Body => "Ganzkörper", DetectionKind.Face => "Gesicht", _ => "lädt" });
        }
        entries.Add(new TrackingStatusEntry("Personen", live.FunActive, funRate, funDetail));

        // Gesichter und Hände (Gestendienst): Hände nur, wenn die laufende Anforderung sie enthält.
        var peaceFresh = IsFresh(_peaceSeenAt, now);
        var status = live.PeaceStatus;
        var rate = live.PeaceActive && peaceFresh && status is { AnalysesPerSecond: > 0 } ps ? ps.AnalysesPerSecond : (double?)null;
        string? faceDetail = null, handDetail = null;
        if (live.PeaceActive && status is not null)
        {
            faceDetail = status.FaceUnavailableReason ?? (status.FacesReady ? null : "lädt");
            handDetail = status.HandUnavailableReason ?? (status.HandsReady ? null : "lädt");
        }
        entries.Add(new TrackingStatusEntry("Gesichter", live.PeaceActive, rate, faceDetail));

        var handsActive = live.PeaceActive && _handsRequested;
        var divisor = Math.Max(1, _options?.HandCycleDivisor ?? 1);
        entries.Add(new TrackingStatusEntry("Hände", handsActive, handsActive && rate is { } r ? r / divisor : null, handsActive ? handDetail : null));
        return entries;
    }

    // ---------------------------------------------------------------- Warum-Info (nur Klassifikatoren, kein Engine-Zustand)

    private IReadOnlyList<string> HandVerdicts(HandLandmarks hand, AppSettings settings)
    {
        var peace = PeaceGestureClassifier.Classify(hand, settings.Peace.SensitivityPercent, _aspect);
        var middle = MiddleFingerGestureClassifier.Classify(hand, settings.MiddleFinger.SensitivityPercent, _aspect);
        var finger = HeartGestureClassifier.ClassifyFingerHeart(hand, settings.Heart, _aspect);
        return
        [
            peace.IsPeace ? $"Peace ja ({Num(peace.Score)})" : $"Peace nein: {peace.RejectReason}",
            middle.IsMiddleFinger ? $"Mittelfinger ja ({Num(middle.Score)})" : $"Mittelfinger nein: {middle.RejectReason}",
            finger.IsHeart ? $"Fingerherz ja ({Num(finger.Score)})" : $"Fingerherz nein: {finger.RejectReason}",
        ];
    }

    private IReadOnlyList<TrackingPairItem> PairVerdicts(IReadOnlyList<HandLandmarks> hands, AppSettings settings)
    {
        var matches = HeartGestureClassifier.FindHearts(hands, settings.Heart, _aspect);
        if (matches.Count > 0)
        {
            var best = matches[0];
            return [new TrackingPairItem(best.Center, [$"Herz {best.Shape} ja ({Num(best.Score)})"])];
        }

        if (HeartGestureClassifier.FindNearestPair(hands, _aspect) is not { } pair)
            return [];
        var a = hands[pair.A];
        var b = hands[pair.B];
        var diagnosis = HeartGestureClassifier.Diagnose(a, b, settings.Heart, _aspect);
        var lines = new List<string> { $"Herz nein: {diagnosis.Verdict.RejectReason ?? "abgelehnt"}" };
        var failed = diagnosis.FailedChecks.Take(3).ToList();
        if (failed.Count > 0)
            lines.Add("Nicht bestanden: " + string.Join(", ", failed));
        var at = new NormPoint((a.Box.CenterX + b.Box.CenterX) / 2, (a.Box.CenterY + b.Box.CenterY) / 2);
        return [new TrackingPairItem(at, lines)];
    }

    private static string Num(double value) => value.ToString("0.00", TrackingOverlayFrame.Culture);
}
