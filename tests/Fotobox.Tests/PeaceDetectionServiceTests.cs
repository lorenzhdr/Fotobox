using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Detection.Peace;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Gesichtsdetektor-Attrappe mit steuerbarem Laden und Analysieren; zählt Aufrufe je Bildbreite.</summary>
internal sealed class PeaceFakeFaceDetector : IFaceLandmarkDetector
{
    private int _detectCalls, _disposed;
    public Func<Task>? OnInitialize { get; set; }
    public Func<BgraImage, Task<IReadOnlyList<FaceLandmarks>>>? OnDetect { get; set; }
    public Func<Task>? OnDispose { get; set; }
    public ConcurrentQueue<(int Width, int Height)> Sizes { get; } = new();
    public IReadOnlyList<FaceLandmarks> Faces { get; set; } = [PeaceFakes.Face(0.5, 0.4, 0.1)];

    public int DetectCalls => Volatile.Read(ref _detectCalls);
    public int CallsWithWidth(int width) => Sizes.Count(s => s.Width == width);
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Task InitializeAsync(CancellationToken ct = default) => OnInitialize?.Invoke() ?? Task.CompletedTask;

    public async Task<IReadOnlyList<FaceLandmarks>> DetectAsync(BgraImage image, double minScore, CancellationToken ct = default)
    {
        Sizes.Enqueue((image.Width, image.Height));
        Interlocked.Increment(ref _detectCalls);
        if (OnDetect is { } detect)
            return await detect(image).ConfigureAwait(false);
        return Faces;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return OnDispose is { } dispose ? new ValueTask(dispose()) : ValueTask.CompletedTask;
    }
}

/// <summary>Handdetektor-Attrappe: merkt sich Suchbereiche und Ausschnitte.</summary>
internal sealed class PeaceFakeHandDetector : IHandLandmarkDetector
{
    private int _palmCalls, _landmarkCalls, _disposed;
    public Func<Task>? OnInitialize { get; set; }
    public Func<NormRect, int, IReadOnlyList<PalmCandidate>> OnPalms { get; set; } = static (_, _) => [];
    public Func<HandRoi, HandLandmarks?> OnLandmarks { get; set; } = static _ => null;
    public ConcurrentQueue<NormRect> Regions { get; } = new();
    public ConcurrentQueue<HandRoi> Rois { get; } = new();

    public int PalmCalls => Volatile.Read(ref _palmCalls);
    public int LandmarkCalls => Volatile.Read(ref _landmarkCalls);
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Task InitializeAsync(CancellationToken ct = default) => OnInitialize?.Invoke() ?? Task.CompletedTask;

    public Task<IReadOnlyList<PalmCandidate>> DetectPalmsAsync(BgraImage image, NormRect region, int max, CancellationToken ct = default)
    {
        Regions.Enqueue(region);
        Interlocked.Increment(ref _palmCalls);
        return Task.FromResult(OnPalms(region, max));
    }

    public Task<HandLandmarks?> DetectLandmarksAsync(BgraImage image, HandRoi roi, double minScore, CancellationToken ct = default)
    {
        Rois.Enqueue(roi);
        Interlocked.Increment(ref _landmarkCalls);
        return Task.FromResult(OnLandmarks(roi));
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}

internal static class PeaceFakes
{
    public static FaceLandmarks Face(double cx, double cy, double width, double score = 0.9)
    {
        var height = width * 1.3;
        var box = new NormRect(cx - width / 2, cy - height / 2, width, height);
        return new FaceLandmarks(box, score,
            new NormPoint(cx - width * 0.2, cy - height * 0.1), new NormPoint(cx + width * 0.2, cy - height * 0.1),
            new NormPoint(cx, cy + height * 0.1), new NormPoint(cx - width * 0.15, cy + height * 0.25), new NormPoint(cx + width * 0.15, cy + height * 0.25));
    }

    /// <summary>Hand mit 21 Punkten um (<paramref name="cx"/>, <paramref name="cy"/>), Handgelenk unten, Finger nach oben.</summary>
    public static HandLandmarks Hand(double cx, double cy, double score = 0.9, double size = 0.05)
    {
        var points = Enumerable.Range(0, 21)
            .Select(i => new HandPoint(cx + (i % 5 - 2) * size / 5, cy + size / 2 - i * size / 21, 0))
            .ToArray();
        return new HandLandmarks(points, score, new NormRect(cx - size / 2, cy - size / 2, size, size));
    }

    public static PalmCandidate Palm(double cx, double cy, double score = 0.9, double size = 0.05) =>
        new(score, new NormPoint(cx, cy), size, new NormPoint(cx, cy + size), new NormPoint(cx, cy - size));
}

public class PeaceDetectionServiceTests
{
    private static readonly TimeSpan Real = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Interval5 = TimeSpan.FromMilliseconds(200);

    private readonly DetectionFakeCamera _camera = new();
    private readonly FakeTimeProvider _time = new();
    private readonly PeaceFakeFaceDetector _face = new();
    private readonly PeaceFakeHandDetector _hand = new();
    private int _faceCreated, _handCreated;

    private static readonly byte[] FramePixels = new byte[1280 * 720 * 4];
    private static readonly CameraFrame Frame = new() { Width = 1280, Height = 720, Stride = 1280 * 4, Pixels = FramePixels };

    private static GestureRequestOptions Options(GestureNeeds needs = GestureNeeds.Faces | GestureNeeds.Hands, int perSecond = 5) =>
        new(needs, perSecond, 0.6, 0.5);

    private PeaceDetectionService NewService(TimeProvider? time = null) => new(_camera,
        () => { Interlocked.Increment(ref _faceCreated); return _face; },
        () => { Interlocked.Increment(ref _handCreated); return _hand; },
        time ?? _time);

    private static async Task WaitFor(Func<bool> condition, string what = "Bedingung")
    {
        var deadline = DateTime.UtcNow + Real;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Nicht eingetreten: " + what);
            await Task.Delay(10);
        }
    }

    private static async Task WaitReady(IGestureRequest request, bool hands)
    {
        await WaitFor(() => request.Status.FacesReady, "Gesichtsstufe bereit");
        if (hands)
            await WaitFor(() => request.Status.HandsReady, "Handstufe bereit");
    }

    private static ConcurrentQueue<GestureUpdate> Collect(IGestureRequest request)
    {
        var updates = new ConcurrentQueue<GestureUpdate>();
        request.Updated += (_, u) => updates.Enqueue(u);
        return updates;
    }

    /// <summary>
    /// Lässt die Rate verstreichen, löst einen Frame aus und wartet, bis die Analyse beendet ist (Update eingegangen). Kommt
    /// keins, war der vorige Lauf beim Frame evtl. noch als belegt markiert (der Frame wurde verworfen): dann erneut auslösen.
    /// </summary>
    private async Task Cycle(ConcurrentQueue<GestureUpdate> updates, int expectedUpdates, TimeSpan? step = null)
    {
        var deadline = DateTime.UtcNow + Real;
        while (true)
        {
            _time.Advance(step ?? Interval5);
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

    /// <summary>Löst Frames aus, bis der Detektor <paramref name="calls"/> Aufrufe hatte (ein vorheriger Lauf kann noch Busy sein).</summary>
    private async Task RaiseUntilCalls(int calls)
    {
        var deadline = DateTime.UtcNow + Real;
        while (_face.DetectCalls < calls)
        {
            Assert.True(DateTime.UtcNow < deadline, "Aufruf " + calls);
            _time.Advance(Interval5);
            _camera.Raise(Frame);
            await Task.Delay(10);
        }
        await Task.Delay(30); // Ergebnis bzw. Fehler verbuchen lassen
    }

    // ------------------------------------------------------------------ Abo

    [Fact]
    public async Task Subscription_OnlyWhileRequestsAreOpen_AndNoDetectorWithoutRequest()
    {
        await using var service = NewService();
        Assert.Equal(0, _camera.Subscribers);
        _camera.Raise(Frame);
        Assert.Equal(0, _faceCreated);

        var a = service.Request(Options());
        Assert.Equal(1, _camera.Subscribers);
        var b = service.Request(Options());
        Assert.Equal(1, _camera.Subscribers);

        a.Dispose();
        Assert.Equal(1, _camera.Subscribers);
        b.Dispose();
        b.Dispose();
        Assert.Equal(0, _camera.Subscribers);

        var c = service.Request(Options());
        Assert.Equal(1, _camera.Subscribers);
        c.Dispose();
        Assert.Equal(0, _camera.Subscribers);
    }

    [Fact]
    public async Task Detectors_AreCreatedLazily_AndOnlyOnce()
    {
        await using var service = NewService();
        Assert.Equal(0, _faceCreated);
        Assert.Equal(0, _handCreated);

        using var request = service.Request(Options());
        await WaitReady(request, hands: true);
        using var second = service.Request(Options());

        Assert.Equal(1, _faceCreated);
        Assert.Equal(1, _handCreated);
    }

    // ------------------------------------------------------------------ Stapel

    [Fact]
    public async Task OnlyTheTopmostRequest_GetsResults_AndTheLowerOneAgainAfterDispose()
    {
        await using var service = NewService();
        using var lower = service.Request(Options(GestureNeeds.Faces));
        var top = service.Request(Options(GestureNeeds.Faces));
        var lowerUpdates = Collect(lower);
        var topUpdates = Collect(top);
        await WaitReady(top, hands: false);

        await Cycle(topUpdates, 1);
        Assert.Empty(lowerUpdates);

        top.Dispose();
        await Cycle(lowerUpdates, 1);
        Assert.Single(topUpdates);
        Assert.Equal(1, _camera.Subscribers);
    }

    [Fact]
    public async Task DisposedRequest_GetsNoFurtherEvents()
    {
        await using var service = NewService();
        var request = service.Request(Options(GestureNeeds.Faces));
        var updates = Collect(request);
        await WaitReady(request, hands: false);
        await Cycle(updates, 1);

        request.Dispose();
        _time.Advance(Interval5);
        _camera.Raise(Frame);
        await Task.Delay(100);

        Assert.Single(updates);
        Assert.Equal(0, _camera.Subscribers);
    }

    // ------------------------------------------------------------------ Rate und Update

    [Fact]
    public async Task Update_ChangesRateWithoutRebuild()
    {
        await using var service = NewService();
        using var request = service.Request(Options(GestureNeeds.Faces, perSecond: 1));
        var updates = Collect(request);
        await WaitReady(request, hands: false);

        _camera.Raise(Frame);
        await WaitFor(() => updates.Count == 1);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        _camera.Raise(Frame); // 1/s: zu früh
        await Task.Delay(100);
        Assert.Equal(1, _face.DetectCalls);

        request.Update(Options(GestureNeeds.Faces, perSecond: 10));
        _camera.Raise(Frame); // 300 ms ≥ 100 ms
        await WaitFor(() => updates.Count == 2);

        Assert.Equal(2, _face.DetectCalls);
        Assert.Equal(1, _faceCreated);
    }

    [Fact]
    public async Task MeasuredRate_IsReportedInStatus()
    {
        await using var service = NewService();
        using var request = service.Request(Options(GestureNeeds.Faces));
        var updates = Collect(request);
        await WaitReady(request, hands: false);

        _camera.Raise(Frame);
        await WaitFor(() => updates.Count == 1);
        for (var i = 2; i <= 6; i++)
            await Cycle(updates, i);

        Assert.InRange(request.Status.AnalysesPerSecond, 4.0, 6.0);
    }

    [Fact]
    public async Task CycleDurations_AreMeasured_AndReportedAsMeanInStatus()
    {
        _face.OnDetect = _ =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(40)); // YuNet "dauert" 40 ms
            return Task.FromResult(_face.Faces);
        };
        await using var service = NewService();
        using var request = service.Request(Options(GestureNeeds.Faces));
        var updates = Collect(request);
        await WaitReady(request, hands: false);

        _camera.Raise(Frame);
        await WaitFor(() => updates.Count == 1);
        await Cycle(updates, 2);

        Assert.InRange(request.Status.MeanCycleMs, 39.9, 40.1);
    }

    [Fact]
    public async Task Updates_CarryTheFrameArrivalTime()
    {
        await using var service = NewService();
        using var request = service.Request(Options(GestureNeeds.Faces));
        var updates = Collect(request);
        await WaitReady(request, hands: false);

        _time.Advance(TimeSpan.FromSeconds(3));
        var expected = _time.GetUtcNow();
        _camera.Raise(Frame);
        await WaitFor(() => updates.Count == 1);

        Assert.Equal(expected, updates.Single().FrameTime);
        Assert.True(updates.Single().HandsIncluded);
        Assert.True(updates.Single().FacesFresh);
    }

    // ------------------------------------------------------------------ Schneller Gesichtsmodus

    private static GestureRequestOptions Fast(int perSecond = 10, int divisor = 2) =>
        new(GestureNeeds.Faces | GestureNeeds.Hands, perSecond, 0.6, 0.5, divisor);

    [Fact]
    public async Task FastFaces_RunHandStageOnlyInEverySecondCycle_AndFacesComeFirst()
    {
        _hand.OnPalms = (_, _) => [PeaceFakes.Palm(0.5, 0.6)];
        _hand.OnLandmarks = _ => PeaceFakes.Hand(0.5, 0.6);
        await using var service = NewService();
        using var request = service.Request(Fast());
        var updates = Collect(request);
        await WaitReady(request, hands: true);
        var step = TimeSpan.FromMilliseconds(100);

        // Zyklus 1: nur Gesichter (ein Update, Hände nicht beteiligt).
        await Cycle(updates, 1, step);
        var first = updates.ToArray()[0];
        Assert.False(first.HandsIncluded);
        Assert.True(first.FacesFresh);
        Assert.Equal(0, _hand.LandmarkCalls);

        // Zyklus 2: erst ein reines Gesichts-Update, dann das Update mit den Händen (Gesichter wiederholt, nicht neu).
        await Cycle(updates, 3, step);
        var all = updates.ToArray();
        Assert.False(all[1].HandsIncluded);
        Assert.True(all[1].FacesFresh);
        Assert.Empty(all[1].Hands);
        Assert.True(all[2].HandsIncluded);
        Assert.False(all[2].FacesFresh);
        Assert.Single(all[2].Hands);
        Assert.Single(all[2].Faces);
    }

    [Fact]
    public async Task FastFaces_HandsKeepBeingTracked_AcrossSkippedCycles()
    {
        _hand.OnPalms = (_, _) => [PeaceFakes.Palm(0.5, 0.6)];
        _hand.OnLandmarks = _ => PeaceFakes.Hand(0.5, 0.6);
        await using var service = NewService();
        using var request = service.Request(Fast());
        var updates = Collect(request);
        await WaitReady(request, hands: true);
        var step = TimeSpan.FromMilliseconds(100);

        await Cycle(updates, 1, step); // Zyklus 1 (ohne Hände)
        await Cycle(updates, 3, step); // Zyklus 2: Palm-Suche
        var palmsAfterFirstHandCycle = _hand.PalmCalls;
        await Cycle(updates, 4, step); // Zyklus 3: ohne Hände
        await Cycle(updates, 6, step); // Zyklus 4: Hände verfolgt, keine neue Palm-Suche um das Gesicht

        Assert.Equal(palmsAfterFirstHandCycle, _hand.PalmCalls);
        Assert.Single(updates.Last().Hands);
    }

    [Fact]
    public async Task DefaultMode_StillDeliversOneUpdatePerCycle_WithHandsIncluded()
    {
        await using var service = NewService();
        using var request = service.Request(Options());
        var updates = Collect(request);
        await WaitReady(request, hands: true);

        await Cycle(updates, 1);
        await Cycle(updates, 2);

        Assert.Equal(2, updates.Count);
        Assert.All(updates, u => { Assert.True(u.HandsIncluded); Assert.True(u.FacesFresh); });
    }

    // ------------------------------------------------------------------ Stufen

    [Fact]
    public async Task FacesOnly_NeverCallsTheHandStage()
    {
        await using var service = NewService();
        using var request = service.Request(Options(GestureNeeds.Faces));
        var updates = Collect(request);
        await WaitReady(request, hands: false);

        await Cycle(updates, 1);
        await Cycle(updates, 2);

        Assert.Equal(0, _handCreated);
        Assert.Equal(0, _hand.PalmCalls);
        Assert.Equal(0, _hand.LandmarkCalls);
        Assert.Empty(updates.First().Hands);
        Assert.Single(updates.First().Faces);
    }

    [Fact]
    public async Task UpdateToFacesOnly_StopsTheHandStage()
    {
        await using var service = NewService();
        _hand.OnPalms = (r, _) => [PeaceFakes.Palm(r.CenterX, r.CenterY)];
        using var request = service.Request(Options());
        var updates = Collect(request);
        await WaitReady(request, hands: true);
        await Cycle(updates, 1);
        var palms = _hand.PalmCalls;
        Assert.True(palms > 0);

        request.Update(Options(GestureNeeds.Faces));
        await Cycle(updates, 2);
        await Cycle(updates, 3);

        Assert.Equal(palms, _hand.PalmCalls);
        Assert.Empty(updates.Last().Hands);
    }

    [Fact]
    public async Task Hands_AreSearchedAroundFace_ThenTracked()
    {
        await using var service = NewService();
        _hand.OnPalms = (r, _) => [PeaceFakes.Palm(0.5, 0.55)];
        _hand.OnLandmarks = _ => PeaceFakes.Hand(0.5, 0.55);
        using var request = service.Request(Options());
        var updates = Collect(request);
        await WaitReady(request, hands: true);

        await Cycle(updates, 1);

        var first = updates.First();
        Assert.Single(first.Hands);
        Assert.Equal(1280, first.SourceWidth);
        Assert.Equal(720, first.SourceHeight);
        Assert.True(first.Status.HandsReady);
        Assert.Equal(1, _hand.PalmCalls);
        Assert.True(_hand.Regions.TryPeek(out var region));
        Assert.Equal(PeaceDetectionServiceAccess.SearchRegion(first.Faces[0], 1280, 720), region);

        // Zweiter Zyklus: Die Hand ist bekannt (Mitte im Suchbereich) → keine Palm-Suche, Ausschnitt aus den Handpunkten.
        await Cycle(updates, 2);

        Assert.Equal(1, _hand.PalmCalls);
        Assert.Equal(2, _hand.LandmarkCalls);
        Assert.Single(updates.Last().Hands);
    }

    [Fact]
    public async Task FullImagePalmSearch_RunsEveryFourthCycle()
    {
        await using var service = NewService();
        using var request = service.Request(Options());
        var updates = Collect(request);
        await WaitReady(request, hands: true);

        for (var i = 1; i <= 3; i++)
            await Cycle(updates, i);
        Assert.DoesNotContain(_hand.Regions, r => r == NormRect.Full);

        await Cycle(updates, 4);

        Assert.Contains(_hand.Regions, r => r == NormRect.Full);
        Assert.Equal(4 + 1, _hand.PalmCalls); // 4 × Gesichtsbereich + 1 × ganzes Bild
    }

    [Fact]
    public async Task Budgets_LimitPalmAndLandmarkRuns_AndFacesAreVisitedRoundRobin()
    {
        await using var service = NewService();
        _face.Faces = [.. Enumerable.Range(0, 6).Select(i => PeaceFakes.Face(0.1 + i * 0.15, 0.4, 0.04))];
        _hand.OnPalms = (r, max) => [PeaceFakes.Palm(r.CenterX, r.CenterY, size: 0.03), PeaceFakes.Palm(r.CenterX + 0.1, r.CenterY, size: 0.03)];
        using var request = service.Request(Options());
        var updates = Collect(request);
        await WaitReady(request, hands: true);

        await Cycle(updates, 1);

        Assert.Equal(PeaceDetectionService.MaxPalmRuns, _hand.PalmCalls);
        Assert.Equal(PeaceDetectionService.MaxLandmarkRuns, _hand.LandmarkCalls);
        var firstRound = _hand.Regions.ToArray();

        await Cycle(updates, 2);

        var secondRound = _hand.Regions.Skip(firstRound.Length).Take(PeaceDetectionService.MaxPalmRuns).ToArray();
        Assert.Equal(2 * PeaceDetectionService.MaxPalmRuns, _hand.PalmCalls);
        Assert.Empty(secondRound.Intersect(firstRound)); // andere Gesichter (Cursor)
        Assert.True(_hand.LandmarkCalls <= 2 * PeaceDetectionService.MaxLandmarkRuns);
    }

    [Fact]
    public async Task DuplicateHands_FromTrackingAndPalm_AreMerged()
    {
        await using var service = NewService();
        _hand.OnPalms = (r, _) => [PeaceFakes.Palm(0.5, 0.55)];
        _hand.OnLandmarks = _ => PeaceFakes.Hand(0.5, 0.55);
        using var request = service.Request(Options());
        var updates = Collect(request);
        await WaitReady(request, hands: true);

        for (var i = 1; i <= 4; i++) // im 4. Zyklus kommt zur Verfolgung die Suche im ganzen Bild dazu
            await Cycle(updates, i);

        Assert.Single(updates.Last().Hands);
    }

    [Fact]
    public async Task FaceStageMissing_HandsStillWork_WithFullImageSearchEveryCycle()
    {
        await using var service = NewService();
        _face.OnInitialize = () => throw new FileNotFoundException("fehlt");
        _hand.OnPalms = (r, _) => [PeaceFakes.Palm(0.5, 0.5)];
        _hand.OnLandmarks = _ => PeaceFakes.Hand(0.5, 0.5);
        using var request = service.Request(Options());
        var updates = Collect(request);
        await WaitFor(() => request.Status.FaceUnavailableReason is not null && request.Status.HandsReady, "Gesicht fehlt, Hand bereit");

        await Cycle(updates, 1);

        Assert.Equal(PeaceDetectionService.FaceMissingReason, request.Status.FaceUnavailableReason);
        Assert.Equal(0, _face.DetectCalls);
        Assert.Equal(NormRect.Full, Assert.Single(_hand.Regions));
        Assert.Single(updates.First().Hands);
        Assert.Empty(updates.First().Faces);
    }

    // ------------------------------------------------------------------ Fehler

    [Fact]
    public async Task ThreeExceptionsInARow_MakeTheStageUnavailable_WithReason()
    {
        await using var service = NewService();
        _face.OnDetect = _ => throw new InvalidOperationException("kaputt");
        using var request = service.Request(Options(GestureNeeds.Faces));
        var statuses = new ConcurrentQueue<GestureStatus>();
        request.StatusChanged += (_, s) => statuses.Enqueue(s);
        await WaitReady(request, hands: false);

        for (var i = 1; i <= 3; i++)
            await RaiseUntilCalls(i);

        await WaitFor(() => request.Status.FaceUnavailableReason is not null, "Grund");
        Assert.Contains("ausgefallen", request.Status.FaceUnavailableReason);
        Assert.Contains("Schnurrbart", request.Status.FaceUnavailableReason);
        Assert.False(request.Status.FacesReady);
        await WaitFor(() => statuses.Any(s => s.FaceUnavailableReason is not null), "StatusChanged");

        _time.Advance(Interval5);
        _camera.Raise(Frame);
        await Task.Delay(100);
        Assert.Equal(3, _face.DetectCalls);
        await WaitFor(() => _face.IsDisposed, "Detektor freigegeben");
    }

    [Fact]
    public async Task TwoExceptionsThenSuccess_DoNotBreakTheStage()
    {
        await using var service = NewService();
        var calls = 0;
        _face.OnDetect = _ => ++calls <= 2 ? throw new InvalidOperationException("kaputt") : Task.FromResult<IReadOnlyList<FaceLandmarks>>([PeaceFakes.Face(0.5, 0.5, 0.1)]);
        using var request = service.Request(Options(GestureNeeds.Faces));
        var updates = Collect(request);
        await WaitReady(request, hands: false);

        for (var i = 1; i <= 5; i++)
            await RaiseUntilCalls(i);

        await WaitFor(() => updates.Count >= 3, "Updates nach Fehlern");
        Assert.Null(request.Status.FaceUnavailableReason);
        Assert.True(request.Status.FacesReady);
    }

    [Fact]
    public async Task ThreeTimeoutsInARow_MakeTheStageUnavailable_AndBusyStaysUntilTheHangingRunEnds()
    {
        await using var service = NewService();
        _face.OnDetect = async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _time);
            return [];
        };
        using var request = service.Request(Options(GestureNeeds.Faces));
        await WaitReady(request, hands: false);

        for (var i = 1; i <= 3; i++)
        {
            // Frame auslösen, bis der Aufruf des Detektors stattfindet (vorher kann der Busy-Zustand noch gesetzt sein).
            var deadline = DateTime.UtcNow + Real;
            while (_face.DetectCalls < i)
            {
                Assert.True(DateTime.UtcNow < deadline, "Aufruf " + i);
                _time.Advance(Interval5);
                _camera.Raise(Frame);
                await Task.Delay(10);
            }
            _time.Advance(TimeSpan.FromSeconds(2)); // Analysefrist (2 s) überschritten, der Detektor hängt noch (3 s)
            await Task.Delay(100);
            if (i < 3)
            {
                // Busy wird erst gelöst, wenn der hängende Lauf wirklich endet: weitere Frames starten nichts.
                _camera.Raise(Frame);
                await Task.Delay(50);
                Assert.Equal(i, _face.DetectCalls);
                _time.Advance(TimeSpan.FromSeconds(2));
            }
        }

        await WaitFor(() => request.Status.FaceUnavailableReason is not null, "Grund");
        Assert.Contains("Zeitüberschreitung", request.Status.FaceUnavailableReason);
    }

    [Fact]
    public async Task MissingFaceModel_SetsReason_AndHandsDoNotDependOnIt()
    {
        await using var service = NewService();
        _face.OnInitialize = () => throw new FileNotFoundException("fehlt");
        using var request = service.Request(Options(GestureNeeds.Faces));

        await WaitFor(() => request.Status.FaceUnavailableReason is not null, "Grund");

        Assert.Equal("Das Gesichtsmodell fehlt – der Schnurrbart ist nicht verfügbar.", request.Status.FaceUnavailableReason);
        Assert.False(request.Status.FacesReady);
        Assert.Null(request.Status.HandUnavailableReason);
    }

    [Fact]
    public async Task MissingHandModel_SetsReason_AndFacesStillAreDelivered()
    {
        await using var service = NewService();
        _hand.OnInitialize = () => throw new FileNotFoundException("fehlt");
        using var request = service.Request(Options());
        var updates = Collect(request);

        await WaitFor(() => request.Status.HandUnavailableReason is not null && request.Status.FacesReady, "Grund");
        await Cycle(updates, 1);

        Assert.Equal("Das Handmodell fehlt – die Peaceerkennung ist nicht verfügbar.", request.Status.HandUnavailableReason);
        Assert.False(request.Status.HandsReady);
        Assert.Single(updates.First().Faces);
        Assert.Empty(updates.First().Hands);
    }

    [Fact]
    public async Task OtherInitError_GivesShortCause()
    {
        await using var service = NewService();
        _face.OnInitialize = () => throw new InvalidDataException("kaputtes Modell");
        using var request = service.Request(Options(GestureNeeds.Faces));

        await WaitFor(() => request.Status.FaceUnavailableReason is not null, "Grund");

        Assert.Contains("konnte nicht geladen werden", request.Status.FaceUnavailableReason);
        Assert.Contains("InvalidDataException", request.Status.FaceUnavailableReason);
    }

    [Fact]
    public async Task FactoryThatThrows_MarksTheStageUnavailable()
    {
        await using var service = new PeaceDetectionService(_camera, () => throw new InvalidOperationException("Fabrik"), () => _hand, _time);
        using var request = service.Request(Options(GestureNeeds.Faces));

        Assert.NotNull(request.Status.FaceUnavailableReason);
        Assert.False(request.Status.FacesReady);
    }

    // ------------------------------------------------------------------ Kopie des Bilds

    [Fact]
    public async Task CameraBuffer_IsCopiedSynchronously_AnalysisSeesOldPixels()
    {
        await using var service = NewService();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new TaskCompletionSource<(byte[] Bytes, byte[] Array)>();
        _face.OnDetect = async image =>
        {
            await gate.Task;
            seen.TrySetResult((new byte[] { image.Pixels[0], image.Pixels[1], image.Pixels[2] }, image.Pixels));
            return [];
        };
        var pixels = new byte[640 * 480 * 4];
        pixels[0] = 11; pixels[1] = 22; pixels[2] = 33;
        var frame = new CameraFrame { Width = 640, Height = 480, Stride = 640 * 4, Pixels = pixels };
        using var request = service.Request(Options(GestureNeeds.Faces));
        await WaitReady(request, hands: false);

        _camera.Raise(frame);
        await WaitFor(() => _face.DetectCalls == 1);
        pixels[0] = 99; pixels[1] = 99; pixels[2] = 99; // Kamera überschreibt ihren Puffer
        gate.SetResult();

        var (bytes, array) = await seen.Task.WaitAsync(Real);
        Assert.Equal(new byte[] { 11, 22, 33 }, bytes);
        Assert.NotSame(pixels, array);
    }

    [Fact]
    public async Task LargeFrames_AreDownscaledTo1920_AndSourceSizeIsReported_SmallOnesNot()
    {
        await using var service = NewService();
        using var request = service.Request(Options(GestureNeeds.Faces));
        var updates = Collect(request);
        await WaitReady(request, hands: false);

        _time.Advance(Interval5);
        _camera.Raise(new CameraFrame { Width = 2560, Height = 1440, Stride = 2560 * 4, Pixels = new byte[2560 * 1440 * 4] });
        await WaitFor(() => updates.Count == 1);
        _time.Advance(Interval5);
        _camera.Raise(new CameraFrame { Width = 320, Height = 240, Stride = 320 * 4, Pixels = new byte[320 * 240 * 4] });
        await WaitFor(() => updates.Count == 2);

        Assert.Equal(new[] { (1920, 1080), (320, 240) }, _face.Sizes.ToArray());
        Assert.Equal(2560, updates.First().SourceWidth);
        Assert.Equal(1440, updates.First().SourceHeight);
    }

    [Fact]
    public async Task InvalidFrame_IsIgnored()
    {
        await using var service = NewService();
        using var request = service.Request(Options(GestureNeeds.Faces));
        await WaitReady(request, hands: false);

        _camera.Raise(new CameraFrame { Width = 100, Height = 100, Stride = 400, Pixels = new byte[10] });
        await Task.Delay(50);

        Assert.Equal(0, _face.DetectCalls);
    }

    // ------------------------------------------------------------------ Foto-Erkennung

    private static BgraImage Photo() => new() { Width = 64, Height = 48, Stride = 64 * 4, Pixels = new byte[64 * 48 * 4] };

    /// <summary>Foto-Aufrufe (Breite 64) warten auf <paramref name="photoGate"/>; Live-Aufrufe sind sofort fertig.</summary>
    private void BlockPhotoCalls(TaskCompletionSource photoGate) =>
        _face.OnDetect = async image =>
        {
            if (image.Width == 64)
                await photoGate.Task.ConfigureAwait(false);
            return [PeaceFakes.Face(0.5, 0.5, 0.2)];
        };

    [Fact]
    public async Task PhotoDetection_ReturnsFaces_WithoutRequest()
    {
        await using var service = NewService();

        var faces = await service.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(5));

        Assert.NotNull(faces);
        Assert.Single(faces);
        Assert.Equal(1, _faceCreated);
        Assert.Equal(0, _handCreated);
    }

    [Fact]
    public async Task PhotoDetection_PausesLiveAnalysis_AndResumesAfterwards()
    {
        await using var service = NewService();
        var photoGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockPhotoCalls(photoGate);
        using var request = service.Request(Options(GestureNeeds.Faces));
        var updates = Collect(request);
        await WaitReady(request, hands: false);

        var photo = service.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(5));
        await WaitFor(() => _face.CallsWithWidth(64) == 1, "Foto-Analyse läuft");
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(Interval5);
            _camera.Raise(Frame);
        }
        await Task.Delay(100);
        Assert.Equal(0, _face.CallsWithWidth(1280)); // pausiert

        photoGate.SetResult();
        Assert.NotNull(await photo.WaitAsync(Real));
        await Cycle(updates, 1);

        Assert.Equal(1, _face.CallsWithWidth(1280));
    }

    [Fact]
    public async Task PhotoDetection_Timeout_GivesNull_AndLiveResumesOnlyAfterTheHangingRunEnds()
    {
        await using var service = NewService();
        var photoGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockPhotoCalls(photoGate);
        using var request = service.Request(Options(GestureNeeds.Faces));
        var updates = Collect(request);
        await WaitReady(request, hands: false);

        var photo = service.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(2));
        await WaitFor(() => _face.CallsWithWidth(64) == 1);
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Null(await photo.WaitAsync(Real));

        // Das Foto-Ergebnis ist verworfen, der Lauf hängt aber noch: Das Gate bleibt zu.
        _time.Advance(Interval5);
        _camera.Raise(Frame);
        await Task.Delay(100);
        Assert.Equal(0, _face.CallsWithWidth(1280));

        photoGate.SetResult();
        await WaitFor(() =>
        {
            _time.Advance(Interval5);
            _camera.Raise(Frame);
            return !updates.IsEmpty;
        }, "Live läuft wieder");
    }

    [Fact]
    public async Task PhotoDetection_WaitsForRunningLiveAnalysis_AndCountsWaitTowardsTimeout()
    {
        await using var service = NewService();
        var liveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _face.OnDetect = async image =>
        {
            if (image.Width == 1280)
                await liveGate.Task.ConfigureAwait(false);
            return [PeaceFakes.Face(0.5, 0.5, 0.2)];
        };
        using var request = service.Request(Options(GestureNeeds.Faces));
        await WaitReady(request, hands: false);
        _camera.Raise(Frame);
        await WaitFor(() => _face.CallsWithWidth(1280) == 1);

        var waits = service.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(waits.IsCompleted);
        liveGate.SetResult();
        Assert.NotNull(await waits.WaitAsync(Real));

        // Zweiter Durchgang: Die laufende Live-Analyse hängt länger als die Frist → null; danach geht es wieder.
        var liveGate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _face.OnDetect = async image =>
        {
            if (image.Width == 1280)
                await liveGate2.Task.ConfigureAwait(false);
            return [PeaceFakes.Face(0.5, 0.5, 0.2)];
        };
        _time.Advance(Interval5);
        _camera.Raise(Frame);
        await WaitFor(() => _face.CallsWithWidth(1280) == 2);
        var late = service.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await late.WaitAsync(Real));
        liveGate2.SetResult();
        await Task.Delay(50);

        Assert.NotNull(await service.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(5)).WaitAsync(Real));
    }

    [Fact]
    public async Task PhotoDetection_NeverThrows_ReturnsNullOnProblems()
    {
        // Modell fehlt
        await using (var service = NewService())
        {
            _face.OnInitialize = () => throw new FileNotFoundException("fehlt");
            Assert.Null(await service.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(5)));
            Assert.Null(await service.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(5)));
        }

        // Ausnahme in der Erkennung
        var throwing = new PeaceFakeFaceDetector { OnDetect = _ => throw new InvalidOperationException("kaputt") };
        await using (var service = new PeaceDetectionService(_camera, () => throwing, () => _hand, _time))
        {
            Assert.Null(await service.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(5)));
            // Abbruch und ungültige Eingaben
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Null(await service.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(5), cts.Token));
            Assert.Null(await service.DetectFacesInPhotoAsync(Photo(), TimeSpan.Zero));
            Assert.Null(await service.DetectFacesInPhotoAsync(null!, TimeSpan.FromSeconds(5)));
        }

        // nach Dispose
        var disposed = NewService();
        await disposed.DisposeAsync();
        Assert.Null(await disposed.DetectFacesInPhotoAsync(Photo(), TimeSpan.FromSeconds(5)));
    }

    // ------------------------------------------------------------------ Freigabe

    [Fact]
    public async Task Dispose_UnsubscribesClosesRequests_AndDisposesDetectors()
    {
        var service = NewService();
        var request = service.Request(Options());
        var updates = Collect(request);
        await WaitReady(request, hands: true);

        await service.DisposeAsync();
        _time.Advance(Interval5);
        _camera.Raise(Frame);
        await Task.Delay(50);

        Assert.Equal(0, _camera.Subscribers);
        Assert.True(_face.IsDisposed);
        Assert.True(_hand.IsDisposed);
        Assert.Empty(updates);
        var late = service.Request(Options());
        Assert.Equal(0, _camera.Subscribers);
        late.Dispose();
        await service.DisposeAsync(); // mehrfach erlaubt
    }

    [Fact]
    public async Task Dispose_TakesAtMost1500ms_EvenIfDetectorsHang()
    {
        var service = NewService(TimeProvider.System);
        _face.OnDispose = () => Task.Delay(Timeout.Infinite);
        using var request = service.Request(Options());
        await WaitReady(request, hands: true);

        var watch = Stopwatch.StartNew();
        await service.DisposeAsync();
        watch.Stop();

        Assert.InRange(watch.ElapsedMilliseconds, 1000, 2500);
        Assert.Equal(0, _camera.Subscribers);
    }

    [Fact]
    public void SearchRegion_IsSquareAroundFace_ClampedToImage()
    {
        // Gesicht 100 px breit in 1000 × 1000 bei (500, 500): Bereich x 150..850, y (550 ± 350) = 200..900.
        var face = PeaceFakes.Face(0.5, 0.5, 0.1);

        var region = PeaceDetectionServiceAccess.SearchRegion(face, 1000, 1000);

        Assert.Equal(0.15, region.X, 6);
        Assert.Equal(0.7, region.Width, 6);
        Assert.Equal(0.7, region.Height, 6);
        Assert.Equal(0.2, region.Y, 6);

        var corner = PeaceDetectionServiceAccess.SearchRegion(PeaceFakes.Face(0.95, 0.05, 0.2), 1000, 1000);
        Assert.InRange(corner.X, 0, 1);
        Assert.True(corner.Right <= 1.0000001);
        Assert.True(corner.Y >= 0);
        Assert.False(corner.IsEmpty);
    }
}

/// <summary>Zugriff auf interne Hilfen des Dienstes.</summary>
internal static class PeaceDetectionServiceAccess
{
    public static NormRect SearchRegion(FaceLandmarks face, int w, int h) => PeaceDetectionService.SearchRegion(face, w, h);
}
