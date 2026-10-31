using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Tracking-Overlay (Ansichtsmodell): Sichtbarkeit und Abonnenten, Altern, und vor allem die Passivität – das Overlay darf an den
/// Erkennungen nichts ändern (gleiche Engine-Ergebnisse mit und ohne Overlay, keine zusätzliche Anforderung, ein werfender Beobachter
/// stoppt die Engines nicht).
/// </summary>
public sealed class TrackingOverlayViewModelTests
{
    private static readonly HandLandmarks PeaceHand = PeaceHandFixtures.Hand(HandPose.Peace);
    private static readonly FaceLandmarks Face = PeaceHandFixtures.Face(0.5, 0.3, 0.12);

    public TrackingOverlayViewModelTests() => SynchronizationContext.SetSynchronizationContext(null);

    private sealed class Store : ISettingsStore
    {
        public string ConfigDirectory => "fake";
        public string OverlayDirectory => "fake/overlays";
        public AppSettings Current { get; } = new();
        public event EventHandler? SettingsChanged;
        public void Save(AppSettings settings) { }
        public void RaiseChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Peace- und Zielerfassung samt Attrappen-Diensten und einer eigenen Uhr je Ansichtsmodell-Gruppe.</summary>
    private sealed class Rig : IDisposable
    {
        public FakeGestureService Gesture { get; } = new();
        public FakeDetectionService Detection { get; } = new();
        public TrackingTimeProvider PeaceTime { get; } = new();
        public TrackingTimeProvider OverlayTime { get; } = new();
        public Store Settings { get; } = new();
        public PeaceOverlayViewModel Peace { get; }
        public FunOverlayViewModel Fun { get; }
        public TrackingOverlayViewModel Overlay { get; }
        public List<PeaceTrigger> Actions { get; } = [];

        public Rig()
        {
            SynchronizationContext.SetSynchronizationContext(null);
            Peace = new PeaceOverlayViewModel(Gesture, PeaceTime, new FakeSoundPlayer(), random: n => 0);
            Fun = new FunOverlayViewModel(Detection, new FakeSoundPlayer(), PeaceTime);
            Overlay = new TrackingOverlayViewModel(Settings, OverlayTime, Fun, Peace);
            Peace.ActionRequested += (_, t) => Actions.Add(t);
        }

        public void StartPeace() =>
            Peace.SetMode(PeaceMode.Watching, new PeaceSettings { HoldMilliseconds = 1000, CooldownSeconds = 0 }, mirror: false);

        public void StartFun() => Fun.SetActive(true, new FunSettings(), mirror: false);

        public void Advance(TimeSpan span)
        {
            PeaceTime.Advance(span);
            OverlayTime.Advance(span);
        }

        public void Dispose()
        {
            Overlay.Dispose();
            Peace.Dispose();
            Fun.Dispose();
        }
    }

    private static void Show(Rig rig)
    {
        rig.Overlay.SetStateAllowed(true);
        rig.Overlay.Toggle();
    }

    // ---- Sichtbarkeit

    [Fact]
    public void StartsHidden_WithNoTimerAndNoWork()
    {
        using var rig = new Rig();
        rig.StartPeace();
        rig.Gesture.Last.RaiseUpdated([Face], [PeaceHand]);

        Assert.False(rig.Overlay.IsEnabled);
        Assert.False(rig.Overlay.IsVisible);
        Assert.False(rig.Overlay.ShowStatusLine);
        Assert.Same(Fotobox.Core.Tracking.TrackingOverlayFrame.Empty, rig.Overlay.Frame);
        Assert.Equal(0, rig.OverlayTime.TimersCreated);
    }

    [Fact]
    public void Visible_OnlyWhileEnabledAndInAnAllowedState()
    {
        using var rig = new Rig();

        rig.Overlay.Toggle();                       // eingeschaltet, aber Zustand nicht erlaubt
        Assert.True(rig.Overlay.IsEnabled);
        Assert.False(rig.Overlay.IsVisible);
        Assert.Equal(0, rig.OverlayTime.ActiveTimers);

        rig.Overlay.SetStateAllowed(true);
        Assert.True(rig.Overlay.IsVisible);
        Assert.True(rig.Overlay.ShowStatusLine);
        Assert.Equal(1, rig.OverlayTime.ActiveTimers);

        rig.Overlay.SetStateAllowed(false);
        Assert.False(rig.Overlay.IsVisible);
        Assert.True(rig.Overlay.IsEnabled);          // bleibt eingeschaltet
        Assert.Equal(0, rig.OverlayTime.ActiveTimers);

        rig.Overlay.SetStateAllowed(true);
        Assert.True(rig.Overlay.IsVisible);

        rig.Overlay.Toggle();
        Assert.False(rig.Overlay.IsVisible);
        Assert.Equal(0, rig.OverlayTime.ActiveTimers);
    }

    [Fact]
    public void StatusLineFollowsTheSetting()
    {
        using var rig = new Rig();
        Show(rig);
        Assert.True(rig.Overlay.ShowStatusLine);

        rig.Settings.Current.TrackingOverlay.ShowStatusLine = false;
        rig.Settings.RaiseChanged();

        Assert.False(rig.Overlay.ShowStatusLine);
        Assert.Equal("", rig.Overlay.StatusText);
    }

    [Fact]
    public void Updates_ShowUpAsFrame_WhileVisible_AndAgeOutWithTheTimer()
    {
        using var rig = new Rig();
        rig.StartPeace();
        Show(rig);

        rig.Gesture.Last.RaiseUpdated([Face], [PeaceHand]);
        Assert.Single(rig.Overlay.Frame.Hands);
        Assert.Single(rig.Overlay.Frame.Faces);
        Assert.Contains("Gesichter", rig.Overlay.StatusText);

        rig.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(rig.Overlay.Frame.Hands);

        rig.Advance(TimeSpan.FromSeconds(1));       // 2 s ohne neue Analyse
        Assert.Empty(rig.Overlay.Frame.Hands);
        Assert.Empty(rig.Overlay.Frame.Faces);
    }

    [Fact]
    public void PersonDetections_ShowUp_WithTargetsFromTheEngine()
    {
        using var rig = new Rig();
        rig.StartFun();
        Show(rig);

        rig.Detection.Last.RaiseUpdated([new Detection(new NormRect(0.4, 0.2, 0.2, 0.6), 0.9, DetectionKind.Body)]);

        var person = Assert.Single(rig.Overlay.Frame.Persons);
        Assert.Equal(0.9, person.Confidence, 3);
        Assert.StartsWith("Personen", rig.Overlay.StatusText);
    }

    [Fact]
    public void HidingForgetsTheData_AndShowingStartsFresh()
    {
        using var rig = new Rig();
        rig.StartPeace();
        Show(rig);
        rig.Gesture.Last.RaiseUpdated([Face], [PeaceHand]);
        Assert.NotEmpty(rig.Overlay.Frame.Hands);

        rig.Overlay.SetStateAllowed(false);
        Assert.Same(Fotobox.Core.Tracking.TrackingOverlayFrame.Empty, rig.Overlay.Frame);
        rig.Gesture.Last.RaiseUpdated([Face], [PeaceHand]);   // unsichtbar: nichts wird aufgenommen
        rig.Overlay.SetStateAllowed(true);

        Assert.Empty(rig.Overlay.Frame.Hands);
    }

    [Fact]
    public void Dispose_StopsEverything()
    {
        using var rig = new Rig();
        rig.StartPeace();
        Show(rig);

        rig.Overlay.Dispose();
        rig.Overlay.Toggle();                         // wirkungslos
        rig.Overlay.SetStateAllowed(true);

        Assert.False(rig.Overlay.IsVisible);
        Assert.Equal(0, rig.OverlayTime.ActiveTimers);
    }

    // ---- Passivität

    /// <summary>Spielt dieselbe Folge ab: 3 s Peace-Zeichen mit Gesicht, danach Personen- und Mittelfinger-Updates.</summary>
    private static (List<(PeaceAction Action, int Hand)> Actions, string Snapshot) Play(Rig rig)
    {
        rig.StartPeace();
        rig.StartFun();
        var middle = PeaceHandFixtures.Hand(HandPose.MiddleOnly);
        for (var i = 0; i < 30; i++)
        {
            rig.Advance(TimeSpan.FromMilliseconds(100));
            rig.Gesture.Last.RaiseUpdated([Face], [i < 24 ? PeaceHand : middle]);
            rig.Detection.Last.RaiseUpdated([new Detection(new NormRect(0.4, 0.2, 0.2, 0.6), 0.9, DetectionKind.Body)]);
        }
        var peace = rig.Peace;
        var snapshot = string.Join("|",
            peace.Hands.Select(h => $"{h.TrackId}:{h.IsPeace}:{h.Progress:0.000}:{h.FaceId}"),
            peace.Moustaches.Count, peace.PeaceDetectedNow, peace.MiddleFingerDetectedNow, peace.FaceCount, peace.HeartDetectedNow,
            rig.Fun.Snapshot.Phase, rig.Fun.Snapshot.Count, string.Join(",", rig.Fun.Snapshot.Targets.Select(t => t.Id + t.Label)));
        return (rig.Actions.Select(a => (a.Action, a.HandTrackId)).ToList(), snapshot);
    }

    [Fact]
    public void Passivity_SameUpdates_GiveSameEngineResults_WithAndWithoutVisibleOverlay()
    {
        using var plain = new Rig();
        using var observed = new Rig();
        Show(observed);
        observed.Settings.Current.TrackingOverlay.ShowVerdicts = true;   // möglichst viel Zusatzarbeit

        var expected = Play(plain);
        var actual = Play(observed);

        Assert.NotEmpty(expected.Actions);                     // die Folge löst wirklich etwas aus
        Assert.Equal(expected.Actions, actual.Actions);
        Assert.Equal(expected.Snapshot, actual.Snapshot);
        Assert.True(observed.Overlay.Frame.HasContent);        // und das Overlay hat dabei tatsächlich mitgelesen
    }

    [Fact]
    public void Passivity_TheOverlayNeverRequestsOrChangesTheRequests()
    {
        using var plain = new Rig();
        using var observed = new Rig();
        Show(observed);
        observed.Settings.Current.TrackingOverlay.ShowVerdicts = true;

        Play(plain);
        Play(observed);
        observed.Overlay.Toggle();
        observed.Overlay.Toggle();
        observed.Overlay.SetStateAllowed(false);
        observed.Overlay.SetStateAllowed(true);

        Assert.Equal(plain.Gesture.Requests.Count, observed.Gesture.Requests.Count);
        Assert.Equal(plain.Detection.Requests.Count, observed.Detection.Requests.Count);
        Assert.Equal(plain.Gesture.Last.Updates, observed.Gesture.Last.Updates);
        Assert.Equal(plain.Gesture.Last.Options, observed.Gesture.Last.Options);
        Assert.Equal(plain.Detection.Last.Updates.Count, observed.Detection.Last.Updates.Count);
        Assert.Equal(plain.Gesture.OpenRequests, observed.Gesture.OpenRequests);
        Assert.Equal(plain.Detection.OpenRequests, observed.Detection.OpenRequests);
    }

    [Fact]
    public void Passivity_OnlyShowingTheOverlay_OpensNoRequestAtAll()
    {
        using var rig = new Rig();

        Show(rig);
        rig.Overlay.Toggle();
        Show(rig);

        Assert.Empty(rig.Gesture.Requests);
        Assert.Empty(rig.Detection.Requests);
    }

    [Fact]
    public void Passivity_AThrowingObserver_NeverStopsTheEngines()
    {
        using var plain = new Rig();
        using var broken = new Rig();
        var peaceCalls = 0;
        var funCalls = 0;
        broken.Peace.Observed += (_, _) => { peaceCalls++; throw new InvalidOperationException("Beobachter kaputt"); };
        broken.Fun.Observed += (_, _) => { funCalls++; throw new InvalidOperationException("Beobachter kaputt"); };

        var expected = Play(plain);
        var actual = Play(broken);

        Assert.True(peaceCalls > 0 && funCalls > 0);
        Assert.NotEmpty(actual.Actions);
        Assert.Equal(expected.Actions, actual.Actions);
        Assert.Equal(expected.Snapshot, actual.Snapshot);
    }

    [Fact]
    public void Passivity_WithoutSubscriber_TheTapDoesNothing()
    {
        using var rig = new Rig();
        rig.StartPeace();
        rig.StartFun();

        // Kein Abonnent (Overlay nie eingeschaltet): Updates laufen ganz normal durch, das Overlay bleibt leer.
        rig.Gesture.Last.RaiseUpdated([Face], [PeaceHand]);
        rig.Detection.Last.RaiseUpdated([new Detection(new NormRect(0.4, 0.2, 0.2, 0.6), 0.9, DetectionKind.Body)]);

        Assert.Equal(1, rig.Peace.FaceCount);
        Assert.Same(Fotobox.Core.Tracking.TrackingOverlayFrame.Empty, rig.Overlay.Frame);
    }

    [Fact]
    public void ObserverReceivesTheRawReferences_BeforeTheEnginesRun()
    {
        using var rig = new Rig();
        rig.StartPeace();
        var handsSeenByObserver = -1;
        Fotobox.Core.Tracking.GestureObservation? seen = null;
        rig.Peace.Observed += (_, o) => { seen = o; handsSeenByObserver = rig.Peace.Hands.Count; };

        var update = new GestureUpdate([Face], [PeaceHand], 1280, 720, GestureStatus.Starting);
        rig.Gesture.Last.RaiseUpdated(update);

        Assert.Same(update, seen!.Update);
        Assert.Equal(0, handsSeenByObserver);                 // die Engine hat noch nichts verarbeitet
        Assert.True(rig.Peace.Hands.Count > 0);               // danach schon
        Assert.Equal(rig.Gesture.Last.Options, seen.Options); // Bedarf der laufenden Anforderung (Hände angefordert)
        Assert.True((seen.Options!.Needs & GestureNeeds.Hands) != 0);
    }
}
