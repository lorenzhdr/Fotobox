using Fotobox.Core;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Die Diagnose (<c>Diagnose</c>, <c>DiagnoseFingerHeart</c>, <c>FindNearestPair</c>) rechnet alle Prüfgrößen, ändert aber nie das Urteil.</summary>
public sealed class HeartDiagnoseTests
{
    private static HeartSettings Settings(int sensitivity = 50)
    {
        return new HeartSettings
        {
            Enabled = true,
            ShapeClassic = true,
            ShapeRoof = true,
            ShapeInverted = true,
            ShapeFingerHeart = true,
            HoldMilliseconds = 1000,
            SensitivityPercent = sensitivity,
        };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ValidHeart_AllChecksPass(int kind)
    {
        var hands = kind switch
        {
            0 => HeartHandFixtures.Classic(),
            1 => HeartHandFixtures.Roof(),
            _ => HeartHandFixtures.Inverted(),
        };
        var diagnosis = HeartGestureClassifier.Diagnose(hands[0], hands[1], Settings());

        Assert.True(diagnosis.AllChecksPassed, string.Join(", ", diagnosis.FailedChecks));
        Assert.True(diagnosis.Verdict.IsHeart);
        Assert.Equal(HeartGestureClassifier.ClassifyPair(hands[0], hands[1], Settings()).Shape, diagnosis.Verdict.Shape);
        Assert.Contains("Form falls alles besteht=" + (kind switch { 0 => "Klassisch", 1 => "Dach", _ => "Umgedreht" }), diagnosis.Info);
        Assert.Contains("Urteil=Herz", diagnosis.ToLogString());
    }

    [Fact]
    public void TipsTooFarApart_OnlyTheTipChecksFail_AndAllOthersAreStillComputed()
    {
        var valid = HeartHandFixtures.Pair(tipGap: 0);
        var apart = HeartHandFixtures.Pair(tipGap: -0.15);
        var reference = HeartGestureClassifier.Diagnose(valid[0], valid[1], Settings());
        var diagnosis = HeartGestureClassifier.Diagnose(apart[0], apart[1], Settings());

        // Derselbe Prüfsatz wie beim gültigen Herz: Es wurde nicht beim ersten Fehlschlag abgebrochen.
        Assert.Equal(reference.Checks.Count, diagnosis.Checks.Count);
        Assert.Equal(reference.Checks.Select(c => c.Name), diagnosis.Checks.Select(c => c.Name));

        Assert.False(diagnosis.Verdict.IsHeart);
        Assert.Equal("Fingerspitzen berühren sich nicht.", diagnosis.Verdict.RejectReason);
        var failed = diagnosis.FailedChecks.ToList();
        Assert.Contains("Zeigefinger-Kontakt/S", failed);
        Assert.Contains("Daumenspitzen/S", failed);
        // Die späteren Prüfungen (Höhe, Neigung, Öffnung) sind trotz des Fehlschlags berechnet und bestanden.
        Assert.Equal(2, failed.Count);
        var tip = diagnosis.Checks.Single(c => c.Name == "Zeigefinger-Kontakt/S");
        Assert.True(tip.Value > tip.Limit);
        Assert.Equal("<=", tip.Op);
        Assert.Contains("FEHL", diagnosis.ToLogString());
    }

    [Fact]
    public void LowHandScore_FailsOnlyTheScoreCheck()
    {
        var hands = HeartHandFixtures.Pair(scoreA: 0.60, scoreB: 0.95);
        var settings = Settings();
        var diagnosis = HeartGestureClassifier.Diagnose(hands[0], hands[1], settings);

        Assert.Equal(["Score A"], diagnosis.FailedChecks);
        Assert.False(diagnosis.Verdict.IsHeart);
        Assert.Equal("Handscore zu niedrig.", diagnosis.Verdict.RejectReason);
        var score = diagnosis.Checks.Single(c => c.Name == "Score A");
        Assert.Equal(settings.MinHandScore, score.Limit, 6);
        Assert.Equal(">=", score.Op);
    }

    [Fact]
    public void PairDiagnosis_AgreesWithClassifyPair_ForPositiveAndNegativePoses()
    {
        var sets = new List<IReadOnlyList<HandLandmarks>>
        {
            HeartHandFixtures.Classic(),
            HeartHandFixtures.Roof(rotationDeg: 20),
            HeartHandFixtures.Inverted(),
            HeartHandFixtures.Classic(rotationDeg: 70),
            HeartHandFixtures.Pair(tipGap: 0.1),
            HeartHandFixtures.Pair(scaleB: 3),
            HeartHandFixtures.TwoFists(),
            HeartHandFixtures.TwoOpenHands(),
            HeartHandFixtures.PrayerHands(),
            HeartHandFixtures.TwoPeaceHands(),
        };
        for (var number = 1; number <= 5; number++)
        {
            sets.Add(HeartHandFixtures.RealHeart(number));
            sets.Add(HeartHandFixtures.RealNonHeart(number));
        }
        foreach (var sensitivity in new[] { 10, 50, 95 })
        {
            foreach (var hands in sets)
            {
                var settings = Settings(sensitivity);
                var verdict = HeartGestureClassifier.ClassifyPair(hands[0], hands[1], settings);
                var diagnosis = HeartGestureClassifier.Diagnose(hands[0], hands[1], settings);
                Assert.Equal(verdict.IsHeart, diagnosis.Verdict.IsHeart);
                Assert.Equal(verdict.RejectReason, diagnosis.Verdict.RejectReason);
                Assert.Equal(verdict.IsHeart, diagnosis.AllChecksPassed);
            }
        }
    }

    [Fact]
    public void InvalidPoints_YieldNoChecksButNoException()
    {
        var hands = HeartHandFixtures.Classic();
        var broken = hands[0] with { Points = hands[0].Points.Take(5).ToList() };
        var diagnosis = HeartGestureClassifier.Diagnose(broken, hands[1], Settings());
        Assert.Empty(diagnosis.Checks);
        Assert.False(diagnosis.Verdict.IsHeart);

        var nan = hands[0].Points.ToList();
        nan[3] = new HandPoint(double.NaN, 0.5, 0);
        var diagnosisNaN = HeartGestureClassifier.Diagnose(hands[0] with { Points = nan }, hands[1], Settings());
        Assert.False(diagnosisNaN.Verdict.IsHeart);
        Assert.Contains("Ungültige Handpunkte", diagnosisNaN.Info);
    }

    [Fact]
    public void FingerHeartDiagnosis_AgreesWithClassifyFingerHeart()
    {
        foreach (var pose in Enum.GetValues<FingerHeartPose>())
        {
            foreach (var sensitivity in new[] { 10, 50, 95 })
            {
                var hand = HeartHandFixtures.FingerHeart(pose);
                var settings = Settings(sensitivity);
                var verdict = HeartGestureClassifier.ClassifyFingerHeart(hand, settings);
                var diagnosis = HeartGestureClassifier.DiagnoseFingerHeart(hand, settings);
                Assert.Equal(verdict.IsHeart, diagnosis.Verdict.IsHeart);
                Assert.Equal(verdict.IsHeart, diagnosis.AllChecksPassed);
                Assert.Equal("Fingerherz", diagnosis.Kind);
                Assert.Contains(diagnosis.Checks, c => c.Name == "Daumen-Zeigefinger-Abstand/S");
            }
        }
    }

    [Fact]
    public void FindNearestPair_PicksThePairWithTheClosestTips()
    {
        var heart = HeartHandFixtures.Classic();
        IReadOnlyList<HandLandmarks> hands = [HeartHandFixtures.TwoOpenHands()[0], heart[0], heart[1]];
        var pair = HeartGestureClassifier.FindNearestPair(hands);
        Assert.NotNull(pair);
        Assert.Equal((1, 2), (pair.Value.A, pair.Value.B));
        Assert.Null(HeartGestureClassifier.FindNearestPair([heart[0]]));
        Assert.Null(HeartGestureClassifier.FindNearestPair([]));
    }

    [Fact]
    public void Engine_WithHandsButNoHeart_LogsWithoutTriggeringOrThrowing()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var engine = new HeartEngine(time);
        engine.Configure(Settings(), 10);
        engine.GesturesEnabled = true;
        var triggers = 0;
        engine.Triggered += (_, _) => triggers++;

        var open = HeartHandFixtures.TwoOpenHands();
        var heart = HeartHandFixtures.Classic();
        for (var i = 0; i < 30; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            engine.OnUpdate(i < 15 ? open : [open[0]], []);
        }
        Assert.Equal(0, triggers);
        Assert.False(engine.DetectedNow);

        // Danach ein Herz mit Lücke: Halten beginnt, wird bei zu langem Aussetzer abgebrochen, kein Auslösen und keine Ausnahme.
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            engine.OnUpdate(heart, []);
        }
        time.Advance(TimeSpan.FromMilliseconds(800));
        engine.OnUpdate(open, []);
        Assert.Equal(0, triggers);
        Assert.False(engine.DetectedNow);

        // Das normale Auslösen funktioniert weiterhin.
        for (var i = 0; i < 12; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            engine.OnUpdate(heart, []);
        }
        Assert.Equal(1, triggers);
    }
}
