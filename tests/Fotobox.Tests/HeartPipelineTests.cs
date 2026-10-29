using System.Collections.Concurrent;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Detection.Peace;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Hand-Pipeline mit <c>PairedHands</c> (Herzerkennung): eng anliegende Hände werden weder unterdrückt noch zusammengefasst.
/// Ohne <c>PairedHands</c> bleibt das bisherige Verhalten (Regressionsschutz für Peace/Mittelfinger).
/// </summary>
public class HeartPipelineTests
{
    private const int W = 1280, H = 720;
    private static readonly TimeSpan Real = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Interval5 = TimeSpan.FromMilliseconds(200);
    private static readonly CameraFrame Frame = new() { Width = W, Height = H, Stride = W * 4, Pixels = new byte[W * H * 4] };

    private readonly DetectionFakeCamera _camera = new();
    private readonly FakeTimeProvider _time = new();
    private readonly PeaceFakeFaceDetector _face = new();
    private readonly PeaceFakeHandDetector _hand = new();

    private static GestureRequestOptions Options(bool paired) => new(GestureNeeds.Faces | GestureNeeds.Hands, 5, 0.6, 0.5, PairedHands: paired);

    private PeaceDetectionService NewService() => new(_camera, () => _face, () => _hand, _time);

    private static HandLandmarks WithSide(HandLandmarks hand, double? side) => hand with { Handedness = side };

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Real;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Nicht eingetreten: " + what);
            await Task.Delay(10);
        }
    }

    private async Task<(IGestureRequest Request, ConcurrentQueue<GestureUpdate> Updates)> Start(PeaceDetectionService service, bool paired)
    {
        var request = service.Request(Options(paired));
        var updates = new ConcurrentQueue<GestureUpdate>();
        request.Updated += (_, u) => updates.Enqueue(u);
        await WaitFor(() => request.Status.FacesReady && request.Status.HandsReady, "Stufen bereit");
        return (request, updates);
    }

    private async Task Cycle(ConcurrentQueue<GestureUpdate> updates, int expectedUpdates)
    {
        var deadline = DateTime.UtcNow + Real;
        while (true)
        {
            _time.Advance(Interval5);
            _camera.Raise(Frame);
            var retryAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
            while (updates.Count < expectedUpdates && DateTime.UtcNow < retryAt)
                await Task.Delay(10);
            if (updates.Count >= expectedUpdates)
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Nicht eingetreten: Update {expectedUpdates}");
        }
    }

    // Palm mit Schlüsselpunkten senkrecht: Winkel 0, Palm-ROI-Mitte liegt 0,5 Palm-Größen über der Palm-Mitte (die Handmitte).
    private static PalmCandidate PalmForHandAt(double cx, double cy, double size = 0.05) =>
        PeaceFakes.Palm(cx, cy + 0.5 * size * W / H, size: size);

    // ------------------------------------------------------------ zweite Hand im selben Gesichtsbereich

    [Fact]
    public async Task Paired_SecondHandInSameRegion_IsFound_WhileFirstIsTracked()
    {
        var a = PeaceFakes.Hand(0.45, 0.6);
        var b = PeaceFakes.Hand(0.55, 0.6);
        await using var service = NewService();
        // Zyklus 1 findet nur A; ab Zyklus 2 liefert die Palm-Suche A (Duplikat der verfolgten Hand) und B.
        _hand.OnPalms = (_, _) => _hand.PalmCalls <= 1 ? [PalmForHandAt(0.45, 0.6)] : [PalmForHandAt(0.45, 0.6), PalmForHandAt(0.55, 0.6)];
        _hand.OnLandmarks = roi => Math.Abs(roi.Center.X - 0.55) < 0.03 ? b : a;
        var (request, updates) = await Start(service, paired: true);
        using var _ = request;

        await Cycle(updates, 1);
        Assert.Single(updates.Last().Hands);
        await Cycle(updates, 2);

        Assert.Equal(2, _hand.PalmCalls); // trotz verfolgter Hand im Bereich wird weiter nach einer zweiten gesucht
        Assert.Equal(2, updates.Last().Hands.Count);
        // Verfolgte Hand und zweite Hand bleiben ab jetzt beide verfolgt (zwei Hände im Bereich → keine Palm-Suche mehr).
        await Cycle(updates, 3);
        Assert.Equal(2, _hand.PalmCalls);
        Assert.Equal(2, updates.Last().Hands.Count);
    }

    [Fact]
    public async Task NotPaired_TrackedHandInRegion_SuppressesPalmSearch_AsBefore()
    {
        var a = PeaceFakes.Hand(0.45, 0.6);
        var b = PeaceFakes.Hand(0.55, 0.6);
        await using var service = NewService();
        _hand.OnPalms = (_, _) => _hand.PalmCalls <= 1 ? [PalmForHandAt(0.45, 0.6)] : [PalmForHandAt(0.45, 0.6), PalmForHandAt(0.55, 0.6)];
        _hand.OnLandmarks = roi => Math.Abs(roi.Center.X - 0.55) < 0.03 ? b : a;
        var (request, updates) = await Start(service, paired: false);
        using var _ = request;

        await Cycle(updates, 1);
        await Cycle(updates, 2);

        Assert.Equal(1, _hand.PalmCalls);
        Assert.Single(updates.Last().Hands);
    }

    // ------------------------------------------------------------ Schluss-Dedupe

    /// <summary>Zwei Ergebnisse aus zwei weit auseinander liegenden Palm-ROIs; die Landmarks kommen der Reihe nach aus <paramref name="hands"/>.</summary>
    private async Task<IReadOnlyList<HandLandmarks>> FindWithTwoPalms(bool paired, HandLandmarks first, HandLandmarks second)
    {
        await using var service = NewService();
        _hand.OnPalms = (_, _) => [PalmForHandAt(0.3, 0.6), PalmForHandAt(0.7, 0.6)];
        var calls = 0;
        _hand.OnLandmarks = _ => Interlocked.Increment(ref calls) == 1 ? first : second;
        var (request, updates) = await Start(service, paired);
        using var _ = request;
        await Cycle(updates, 1);
        return updates.First().Hands;
    }

    [Fact]
    public async Task Paired_NeighbouringHands_WithOverlappingBoxes_AreNotMerged()
    {
        var a = PeaceFakes.Hand(0.45, 0.6);
        var b = PeaceFakes.Hand(0.465, 0.6, score: 0.8); // Boxen überlappen zu gut der Hälfte, Landmarks 19 px verschoben
        Assert.True(a.Box.IoU(b.Box) > 0.5);

        var hands = await FindWithTwoPalms(paired: true, a, b);

        Assert.Equal(2, hands.Count);
    }

    [Fact]
    public async Task NotPaired_NeighbouringHands_WithOverlappingBoxes_AreMerged_AsBefore()
    {
        var a = PeaceFakes.Hand(0.45, 0.6);
        var b = PeaceFakes.Hand(0.465, 0.6, score: 0.8);

        var hands = await FindWithTwoPalms(paired: false, a, b);

        Assert.Single(hands);
        Assert.Equal(a.Score, hands[0].Score);
    }

    [Fact]
    public async Task Paired_DifferentSides_AreNeverMerged_EvenWithSameLandmarks()
    {
        var left = WithSide(PeaceFakes.Hand(0.45, 0.6), 0.05);
        var right = WithSide(PeaceFakes.Hand(0.45, 0.6, score: 0.8), 0.95);

        var hands = await FindWithTwoPalms(paired: true, left, right);

        Assert.Equal(2, hands.Count);
    }

    [Fact]
    public async Task Paired_TrueDuplicates_AreStillMerged_BestScoreWins()
    {
        var a = WithSide(PeaceFakes.Hand(0.45, 0.6, score: 0.7), 0.9);
        var b = WithSide(PeaceFakes.Hand(0.4505, 0.6, score: 0.95), 0.92); // < 1 px Versatz

        var hands = await FindWithTwoPalms(paired: true, a, b);

        Assert.Single(hands);
        Assert.Equal(0.95, hands[0].Score);
    }

    // ------------------------------------------------------------ IsPairedDuplicate (Einheit)

    [Fact]
    public void IsPairedDuplicate_DistanceCriterion_WithoutHandedness()
    {
        var a = PeaceFakes.Hand(0.45, 0.6);
        Assert.True(PeaceDetectionService.IsPairedDuplicate(a, PeaceFakes.Hand(0.45, 0.6), W, H));
        Assert.True(PeaceDetectionService.IsPairedDuplicate(a, PeaceFakes.Hand(0.4505, 0.6), W, H)); // ~0,6 px
        Assert.False(PeaceDetectionService.IsPairedDuplicate(a, PeaceFakes.Hand(0.47, 0.6), W, H)); // ~26 px
        Assert.False(PeaceDetectionService.IsPairedDuplicate(a, PeaceFakes.Hand(0.55, 0.6), W, H));
    }

    [Theory]
    [InlineData(0.05, 0.95, false)] // eindeutig verschiedene Seiten
    [InlineData(0.95, 0.05, false)]
    [InlineData(0.05, 0.1, true)]   // gleiche Seite
    [InlineData(0.95, 0.9, true)]
    [InlineData(0.4, 0.6, true)]    // uneindeutig (innerhalb ±0,3 um 0,5) → nur Abstand
    [InlineData(0.05, 0.6, true)]
    [InlineData(0.05, null, true)]  // fehlende Handseite → nur Abstand
    [InlineData(null, null, true)]
    public void IsPairedDuplicate_HandednessRule_WithIdenticalLandmarks(double? sideA, double? sideB, bool expected)
    {
        var a = WithSide(PeaceFakes.Hand(0.45, 0.6), sideA);
        var b = WithSide(PeaceFakes.Hand(0.45, 0.6), sideB);
        Assert.Equal(expected, PeaceDetectionService.IsPairedDuplicate(a, b, W, H));
    }

    // ------------------------------------------------------------ Palm-gegen-verfolgte-Hand (Einheit)

    [Fact]
    public void AddPalmRois_Paired_PalmNextToTrackedHand_IsKept_WhereTheOldRuleWouldDropIt()
    {
        // Große verfolgte Hand: ihre Landmark-ROI (2,0 × Ausdehnung) ist so groß, dass die alte Schwelle 0,35 × ROI-Seite die Palm unterdrückt.
        var tracked = PeaceFakes.Hand(0.4, 0.6, size: 0.2);
        var palm = PalmForHandAt(0.4 + 0.09, 0.6); // Palm-ROI-Mitte ~115 px neben der Handfläche der verfolgten Hand

        var roisOld = new List<HandRoi> { HandRoiMath.FromLandmarks(tracked, W, H) };
        PeaceDetectionService.AddPalmRois(roisOld, null, [palm], W, H);
        Assert.Single(roisOld);

        var roisPaired = new List<HandRoi> { HandRoiMath.FromLandmarks(tracked, W, H) };
        PeaceDetectionService.AddPalmRois(roisPaired, [tracked], [palm], W, H);
        Assert.Equal(2, roisPaired.Count);
    }

    [Fact]
    public void AddPalmRois_Paired_PalmOnTrackedHand_IsStillDuplicate()
    {
        var tracked = PeaceFakes.Hand(0.4, 0.6);
        var rois = new List<HandRoi> { HandRoiMath.FromLandmarks(tracked, W, H) };

        PeaceDetectionService.AddPalmRois(rois, [tracked], [PalmForHandAt(0.4, 0.6)], W, H);

        Assert.Single(rois);
    }

    [Fact]
    public void AddPalmRois_Paired_PalmAgainstPalm_UsesTheOldRule()
    {
        var rois = new List<HandRoi>();
        var sources = new List<HandLandmarks?>();
        PeaceDetectionService.AddPalmRois(rois, sources, [PalmForHandAt(0.4, 0.6), PalmForHandAt(0.41, 0.6), PalmForHandAt(0.7, 0.6)], W, H);

        Assert.Equal(2, rois.Count); // die zweite liegt innerhalb von 0,35 × ROI-Seite der ersten
        Assert.Equal(2, sources.Count);
    }
}
