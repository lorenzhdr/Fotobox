using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Fotobox.Core.Tracking;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Aufbereitung der Rohdaten fürs Tracking-Overlay: Ebenen, Handnummern, Hände-Regel, Altern, Warum-Info und Statuszeile.</summary>
public sealed class TrackingOverlayFrameBuilderTests
{
    private static readonly GestureRequestOptions FacesAndHands = new(GestureNeeds.Faces | GestureNeeds.Hands, 10, 0.6, 0.5);
    private static readonly GestureRequestOptions FacesOnly = new(GestureNeeds.Faces, 10, 0.6, 0.5);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly TrackingOverlayFrameBuilder _builder;
    private readonly AppSettings _settings = new();

    public TrackingOverlayFrameBuilderTests() => _builder = new TrackingOverlayFrameBuilder(_time);

    private static TrackingLive Live(
        bool peaceActive = true, bool funActive = false, DetectionStatus? funStatus = null, GestureStatus? peaceStatus = null,
        IReadOnlyList<HandView>? hands = null, IReadOnlyList<MoustacheView>? moustaches = null, HeartView? heart = null,
        bool heartNow = false, bool middleFingerNow = false, IReadOnlyList<FunTarget>? targets = null) =>
        new(peaceActive, peaceStatus ?? new GestureStatus(true, true, null, null, 9.6), funActive, funStatus,
            hands ?? [], moustaches ?? [], heart, heartNow, middleFingerNow, targets ?? []);

    private GestureObservation Gesture(IReadOnlyList<HandLandmarks> hands, GestureRequestOptions? options = null, bool handsIncluded = true,
        bool facesFresh = true, IReadOnlyList<FaceLandmarks>? faces = null, int width = 1280, int height = 720)
    {
        faces ??= [PeaceHandFixtures.Face()];
        var update = new GestureUpdate(faces, hands, width, height, GestureStatus.Starting, handsIncluded, facesFresh);
        var tracked = faces.Select((f, i) => new TrackedFace(i + 1, f)).ToList();
        return new GestureObservation(update, tracked, options ?? FacesAndHands, 16.0 / 9);
    }

    private TrackingOverlayFrame Build(TrackingLive? live = null) => _builder.Build(_settings, live ?? Live());

    [Fact]
    public void FreshBuilder_ProducesNoContent_ButAStatusLine()
    {
        var frame = Build();

        Assert.False(frame.HasContent);
        Assert.Equal(["Personen", "Gesichter", "Hände"], frame.Status.Select(s => s.Name));
        Assert.False(frame.Status[0].Active);   // Zielerfassung nicht aktiv
        Assert.True(frame.Status[1].Active);
        Assert.False(frame.Status[2].Active);   // noch keine Hände angefordert gesehen
    }

    [Fact]
    public void GestureUpdate_ShowsFacesAndHands_WithOwnStableHandNumbers()
    {
        var hand = PeaceHandFixtures.Hand(HandPose.Peace);
        _builder.OnGestureUpdate(Gesture([hand]));
        var first = Build();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _builder.OnGestureUpdate(Gesture([hand]));
        var second = Build();

        Assert.Single(first.Faces);
        Assert.Equal(1, first.Faces[0].Id);
        var h1 = Assert.Single(first.Hands);
        var h2 = Assert.Single(second.Hands);
        Assert.Equal(h1.Number, h2.Number);
        Assert.Equal(21, h1.Points.Count);
        Assert.Equal(1280, first.GestureImageWidth);
        Assert.Empty(h1.Verdicts);   // Warum-Info ist standardmäßig aus
    }

    [Fact]
    public void HandsNotRequested_EmptyHandsWithHandsIncluded_CountAsHandsInactive()
    {
        _builder.OnGestureUpdate(Gesture([PeaceHandFixtures.Hand(HandPose.Peace)]));
        Assert.Single(Build().Hands);

        // Reine Gesichtsanforderung: Der Dienst liefert leere Hände mit HandsIncluded = true.
        _builder.OnGestureUpdate(Gesture([], FacesOnly, handsIncluded: true));
        var frame = Build();

        Assert.Empty(frame.Hands);
        Assert.False(frame.Status[2].Active);
        Assert.Single(frame.Faces);
    }

    [Fact]
    public void FacesOnlyUpdate_WithHandsIncludedFalse_KeepsTheLastHands()
    {
        var hand = PeaceHandFixtures.Hand(HandPose.Peace);
        _builder.OnGestureUpdate(Gesture([hand]));
        _time.Advance(TimeSpan.FromMilliseconds(100));

        _builder.OnGestureUpdate(Gesture([], handsIncluded: false));
        var frame = Build();

        Assert.Single(frame.Hands);
        Assert.True(frame.Status[2].Active);
    }

    [Fact]
    public void StaleFacesUpdate_DoesNotReplaceTheFaces()
    {
        var face = PeaceHandFixtures.Face(0.3);
        _builder.OnGestureUpdate(Gesture([], faces: [face]));

        // FacesFresh = false wiederholt nur frühere Gesichter: Nichts wird übernommen.
        _builder.OnGestureUpdate(Gesture([], facesFresh: false, faces: [PeaceHandFixtures.Face(0.7), PeaceHandFixtures.Face(0.8)]));

        Assert.Single(Build().Faces);
    }

    [Fact]
    public void Data_DisappearsAfterOneAndAHalfSeconds_EachLayerOnItsOwn()
    {
        _builder.OnGestureUpdate(Gesture([PeaceHandFixtures.Hand(HandPose.Peace)]));
        _builder.OnDetections(new DetectionUpdate([new Detection(new NormRect(0.2, 0.2, 0.2, 0.5), 0.9, DetectionKind.Body)], 1280, 720,
            DetectionStatus.Starting(DetectionMethod.Body)));
        var frame = Build(Live(funActive: true));
        Assert.Single(frame.Persons);
        Assert.Single(frame.Hands);
        Assert.Single(frame.Faces);

        _time.Advance(TimeSpan.FromMilliseconds(1400));
        _builder.OnDetections(new DetectionUpdate([new Detection(new NormRect(0.2, 0.2, 0.2, 0.5), 0.9, DetectionKind.Body)], 1280, 720,
            DetectionStatus.Starting(DetectionMethod.Body)));
        frame = Build(Live(funActive: true));
        Assert.Single(frame.Hands);   // genau 1,4 s: noch da
        Assert.Single(frame.Persons);

        _time.Advance(TimeSpan.FromMilliseconds(200));
        frame = Build(Live(funActive: true));
        Assert.Empty(frame.Hands);    // 1,6 s ohne Erneuerung: weg
        Assert.Empty(frame.Faces);
        Assert.Single(frame.Persons); // die Personen wurden vor 0,2 s erneuert

        _time.Advance(TimeSpan.FromMilliseconds(1400));
        Assert.Empty(Build(Live(funActive: true)).Persons);
    }

    [Fact]
    public void Layers_FollowTheOptions()
    {
        _builder.OnGestureUpdate(Gesture([PeaceHandFixtures.Hand(HandPose.Peace)]));
        _builder.OnDetections(new DetectionUpdate([new Detection(new NormRect(0.2, 0.2, 0.2, 0.5), 0.9, DetectionKind.Body)], 1280, 720,
            DetectionStatus.Starting(DetectionMethod.Body)));
        var target = new FunTarget(1, new NormRect(0.2, 0.2, 0.2, 0.5), "TARGET 1", false, _time.GetUtcNow());
        var pose = new MoustachePose(new NormPoint(0.4, 0.3), 120, 0.1);
        var live = Live(funActive: true, targets: [target],
            moustaches: [new MoustacheView(1, default, pose)],
            heart: new HeartView(new NormPoint(0.5, 0.5), 0.2, 0.4, HeartShape.Classic));

        var all = Build(live);
        Assert.Single(all.Persons);
        Assert.Single(all.Targets);
        Assert.Single(all.Faces);
        Assert.Single(all.Hands);
        Assert.Single(all.Moustaches);
        Assert.Single(all.Hearts);

        _settings.TrackingOverlay.ShowPersons = false;
        _settings.TrackingOverlay.ShowFaces = false;
        _settings.TrackingOverlay.ShowHands = false;
        _settings.TrackingOverlay.ShowGestures = false;
        _settings.TrackingOverlay.ShowMoustaches = false;
        var none = Build(live);
        Assert.Empty(none.Persons);
        Assert.Empty(none.Targets);
        Assert.Empty(none.Faces);
        Assert.Empty(none.Hands);
        Assert.Empty(none.Moustaches);
        Assert.Empty(none.Hearts);
        Assert.False(none.HasContent);
    }

    [Fact]
    public void Frame_CarriesACopyOfTheOptions()
    {
        _settings.TrackingOverlay.Style = TrackingOverlayStyle.SciFi;
        var frame = Build();
        _settings.TrackingOverlay.Style = TrackingOverlayStyle.Technik;

        Assert.Equal(TrackingOverlayStyle.SciFi, frame.Options.Style);
    }

    [Fact]
    public void SimulatedTargetsAndVanishingMoustaches_AreSkipped()
    {
        var sim = new FunTarget(FunEngine.SimulatedTargetId, new NormRect(0.4, 0.2, 0.2, 0.6), "TARGET 0", true, _time.GetUtcNow());
        var vanishing = new MoustacheView(2, default, new MoustachePose(new NormPoint(0.5, 0.5), 100, 0), Vanishing: true);

        var frame = Build(Live(funActive: true, targets: [sim], moustaches: [vanishing]));

        Assert.Empty(frame.Targets);
        Assert.Empty(frame.Moustaches);
    }

    [Fact]
    public void TargetsOnlyWhileTheTargetEngineIsActive()
    {
        var target = new FunTarget(1, new NormRect(0.2, 0.2, 0.2, 0.5), "TARGET 1", false, _time.GetUtcNow());

        Assert.Empty(Build(Live(funActive: false, targets: [target])).Targets);
        Assert.Single(Build(Live(funActive: true, targets: [target])).Targets);
    }

    [Fact]
    public void PeaceProgress_ComesFromTheOverlappingEngineHand()
    {
        var hand = PeaceHandFixtures.Hand(HandPose.Peace);
        _builder.OnGestureUpdate(Gesture([hand]));
        var view = new HandView(7, hand.Points, hand.Box, IsPeace: true, Progress: 0.6, FaceId: 1);
        var far = new HandView(8, hand.Points, new NormRect(0.0, 0.0, 0.05, 0.05), IsPeace: false, Progress: 0.1, FaceId: null);

        var frame = Build(Live(hands: [far, view]));

        var item = Assert.Single(frame.Hands);
        Assert.True(item.IsPeace);
        Assert.Equal(0.6, item.Progress, 3);
        Assert.True(frame.PeaceNow);
    }

    [Fact]
    public void Verdicts_AreOnlyComputed_WhenSwitchedOn()
    {
        _builder.OnGestureUpdate(Gesture([PeaceHandFixtures.Hand(HandPose.Peace)]));
        Assert.Empty(Assert.Single(Build().Hands).Verdicts);

        _settings.TrackingOverlay.ShowVerdicts = true;
        var item = Assert.Single(Build().Hands);

        Assert.Equal(3, item.Verdicts.Count);
        Assert.StartsWith("Peace ja", item.Verdicts[0]);
        Assert.StartsWith("Mittelfinger nein", item.Verdicts[1]);
        Assert.StartsWith("Fingerherz", item.Verdicts[2]);
    }

    [Fact]
    public void Verdicts_ForTwoHands_AddAPairLine()
    {
        var a = PeaceHandFixtures.Hand(HandPose.Fist, cx: 0.35);
        var b = PeaceHandFixtures.Hand(HandPose.Fist, cx: 0.65);
        _builder.OnGestureUpdate(Gesture([a, b]));
        _settings.TrackingOverlay.ShowVerdicts = true;

        var frame = Build();

        Assert.Equal(2, frame.Hands.Count);
        var pair = Assert.Single(frame.Pairs);
        Assert.StartsWith("Herz", pair.Lines[0]);

        _settings.TrackingOverlay.ShowVerdicts = false;
        Assert.Empty(Build().Pairs);
    }

    [Fact]
    public void Verdicts_NeedVisibleHands()
    {
        _builder.OnGestureUpdate(Gesture([PeaceHandFixtures.Hand(HandPose.Peace)]));
        _settings.TrackingOverlay.ShowVerdicts = true;
        _settings.TrackingOverlay.ShowHands = false;
        _settings.TrackingOverlay.ShowGestures = false;

        Assert.Empty(Build().Hands);
    }

    [Fact]
    public void Status_ShowsRatesOfActiveServices_AndDropsStaleOnes()
    {
        var funStatus = new DetectionStatus(DetectionMethod.Body, DetectionKind.Body, null, null, 4.8);
        _builder.OnGestureUpdate(Gesture([PeaceHandFixtures.Hand(HandPose.Peace)]));
        _builder.OnDetections(new DetectionUpdate([], 1280, 720, funStatus));

        var frame = Build(Live(funActive: true, funStatus: funStatus));
        Assert.Equal("Personen 4,8/s (Ganzkörper)   Gesichter 9,6/s   Hände 9,6/s", frame.StatusText);

        _time.Advance(TimeSpan.FromSeconds(2));
        frame = Build(Live(funActive: true, funStatus: funStatus));
        Assert.Equal("Personen aktiv (Ganzkörper)   Gesichter aktiv   Hände aktiv", frame.StatusText);

        frame = Build(Live(peaceActive: false));
        Assert.Equal("Personen aus   Gesichter aus   Hände aus", frame.StatusText);
    }

    [Fact]
    public void Status_ShowsUnavailableReasons_AndRespectsTheSwitch()
    {
        var peaceStatus = new GestureStatus(false, true, "Modell fehlt.", null, 0);
        _builder.OnGestureUpdate(Gesture([]));

        var frame = Build(Live(peaceStatus: peaceStatus));
        Assert.Contains("Gesichter aktiv (Modell fehlt.)", frame.StatusText);

        _settings.TrackingOverlay.ShowStatusLine = false;
        Assert.Equal("", Build(Live(peaceStatus: peaceStatus)).StatusText);
    }

    [Fact]
    public void Flags_FollowTheEnginesAndTheGestureSwitch()
    {
        var frame = Build(Live(middleFingerNow: true, heartNow: true));
        Assert.True(frame.MiddleFingerNow);
        Assert.True(frame.HeartNow);
        Assert.True(frame.HasContent);

        _settings.TrackingOverlay.ShowGestures = false;
        frame = Build(Live(middleFingerNow: true, heartNow: true));
        Assert.False(frame.MiddleFingerNow);
        Assert.False(frame.HeartNow);
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        _builder.OnGestureUpdate(Gesture([PeaceHandFixtures.Hand(HandPose.Peace)]));
        _builder.OnDetections(new DetectionUpdate([new Detection(new NormRect(0.2, 0.2, 0.2, 0.5), 0.9, DetectionKind.Body)], 640, 360,
            DetectionStatus.Starting(DetectionMethod.Body)));

        _builder.Reset();
        var frame = Build(Live(funActive: true));

        Assert.False(frame.HasContent);
        Assert.Equal(0, frame.GestureImageWidth);
        Assert.Equal(0, frame.PersonImageWidth);
    }

    [Fact]
    public void ImageSizes_AreKeptPerLayer()
    {
        _builder.OnGestureUpdate(Gesture([], width: 1920, height: 1080));
        _builder.OnDetections(new DetectionUpdate([], 640, 480, DetectionStatus.Starting(DetectionMethod.Body)));

        var frame = Build();

        Assert.Equal((1920, 1080), (frame.GestureImageWidth, frame.GestureImageHeight));
        Assert.Equal((640, 480), (frame.PersonImageWidth, frame.PersonImageHeight));
    }
}
