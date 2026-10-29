using Fotobox.Core;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

public sealed class HeartGestureClassifierTests
{
    private static readonly double[] Aspects = [4.0 / 3, 16.0 / 9];
    private static readonly int[] Sensitivities = [10, 20, 30, 40, 50, 60, 70, 80, 90, 95];

    private static HeartSettings Settings(int sensitivity = 50, Action<HeartSettings>? configure = null)
    {
        var s = new HeartSettings
        {
            Enabled = true,
            ShapeClassic = true,
            ShapeRoof = true,
            ShapeInverted = true,
            ShapeFingerHeart = true,
            SensitivityPercent = sensitivity,
        };
        configure?.Invoke(s);
        return s;
    }

    private static IReadOnlyList<HeartMatch> Find(IReadOnlyList<HandLandmarks> hands, int sensitivity = 50, double aspect = 16.0 / 9,
        Action<HeartSettings>? configure = null) =>
        HeartGestureClassifier.FindHearts(hands, Settings(sensitivity, configure), aspect);

    private static HeartVerdict Pair(IReadOnlyList<HandLandmarks> hands, int sensitivity = 50, double aspect = 16.0 / 9) =>
        HeartGestureClassifier.ClassifyPair(hands[0], hands[1], Settings(sensitivity), aspect);

    // ---- Positiv je Form

    public static TheoryData<double, double> RotationsAndAspects()
    {
        var data = new TheoryData<double, double>();
        foreach (var aspect in Aspects)
            foreach (var rotation in new[] { -20.0, 0, 20 })
                data.Add(rotation, aspect);
        return data;
    }

    [Theory]
    [MemberData(nameof(RotationsAndAspects))]
    public void Classic_IsRecognized(double rotation, double aspect)
    {
        var match = Assert.Single(Find(HeartHandFixtures.Classic(rotation, aspect: aspect), aspect: aspect));
        Assert.Equal(HeartShape.Classic, match.Shape);
        Assert.Equal(0, match.HandA);
        Assert.Equal(1, match.HandB);
    }

    [Theory]
    [MemberData(nameof(RotationsAndAspects))]
    public void Roof_IsRecognized(double rotation, double aspect)
    {
        var match = Assert.Single(Find(HeartHandFixtures.Roof(rotation, aspect: aspect), aspect: aspect));
        Assert.Equal(HeartShape.Roof, match.Shape);
    }

    [Theory]
    [MemberData(nameof(RotationsAndAspects))]
    public void Inverted_IsRecognized(double rotation, double aspect)
    {
        var match = Assert.Single(Find(HeartHandFixtures.Inverted(rotation, aspect: aspect), aspect: aspect));
        Assert.Equal(HeartShape.Inverted, match.Shape);
    }

    [Fact]
    public void Match_HasCenterSizeAndBoxOfBothHands()
    {
        var hands = HeartHandFixtures.Classic(cx: 0.4, cy: 0.5, scale: 1);
        var match = Assert.Single(Find(hands));
        Assert.Equal(0.4, match.Center.X, 2);
        Assert.Equal(0.5, match.Center.Y, 2);
        Assert.InRange(match.SizeNorm, 0.1, 0.4);
        Assert.InRange(match.Score, 0, 1);
        Assert.Equal(Math.Min(hands[0].Box.X, hands[1].Box.X), match.Box.X, 6);
        Assert.Equal(Math.Max(hands[0].Box.Right, hands[1].Box.Right), match.Box.Right, 6);
        Assert.Equal(Math.Min(hands[0].Box.Y, hands[1].Box.Y), match.Box.Y, 6);
        Assert.Equal(Math.Max(hands[0].Box.Bottom, hands[1].Box.Bottom), match.Box.Bottom, 6);
    }

    [Theory]
    [InlineData(0.6)]
    [InlineData(1.0)]
    [InlineData(1.6)]
    public void Classic_IsRecognizedAtDifferentSizesAndPositions(double scale)
    {
        Assert.Single(Find(HeartHandFixtures.Classic(scale: scale, cx: 0.3, cy: 0.45)));
        Assert.Single(Find(HeartHandFixtures.Classic(scale: scale, cx: 0.7, cy: 0.55)));
    }

    // ---- Formfilter und Disjunktheit

    [Fact]
    public void ShapeFilter_OnlyEnabledShapesMatch()
    {
        var classic = HeartHandFixtures.Classic();
        var roof = HeartHandFixtures.Roof();
        var inverted = HeartHandFixtures.Inverted();

        Assert.Empty(Find(classic, configure: s => s.ShapeClassic = false));
        Assert.Empty(Find(roof, configure: s => s.ShapeRoof = false));
        Assert.Empty(Find(inverted, configure: s => { s.ShapeInverted = false; }));
        Assert.Single(Find(classic, configure: s => { s.ShapeRoof = false; s.ShapeInverted = false; }));
        Assert.Empty(Find(classic, configure: s => { s.ShapeClassic = s.ShapeRoof = s.ShapeInverted = s.ShapeFingerHeart = false; }));
    }

    [Fact]
    public void ClassicAndRoof_AreDisjoint()
    {
        foreach (var sensitivity in Sensitivities)
        {
            Assert.Equal(HeartShape.Classic, Pair(HeartHandFixtures.Classic(), sensitivity).Shape);
            Assert.Equal(HeartShape.Roof, Pair(HeartHandFixtures.Roof(), sensitivity).Shape);
            Assert.Equal(HeartShape.Classic, Pair(HeartHandFixtures.Pair(OtherFingers.Folded), sensitivity).Shape);
        }
    }

    [Fact]
    public void OnlyOneExtendedFinger_IsClassicNotRoof()
    {
        var verdict = Pair(HeartHandFixtures.Pair(OtherFingers.OneExtended));
        Assert.True(verdict.IsHeart);
        Assert.Equal(HeartShape.Classic, verdict.Shape);
    }

    [Fact]
    public void RoofFingersMustPointUp()
    {
        // Stark geneigt (nur bei lockerer Einstellung noch ein Herz): Die Finger zeigen dann nicht mehr grob nach oben.
        var tilted = HeartHandFixtures.Roof(rotationDeg: 50);
        var verdict = Pair(tilted, 95);
        Assert.True(verdict.IsHeart);
        Assert.Equal(HeartShape.Classic, verdict.Shape);
        Assert.Equal(HeartShape.Roof, Pair(HeartHandFixtures.Roof(), 95).Shape);
    }

    [Fact]
    public void Inverted_NeedsNoFingerCondition()
    {
        foreach (var others in Enum.GetValues<OtherFingers>())
            Assert.Equal(HeartShape.Inverted, Pair(HeartHandFixtures.Pair(others, rotationDeg: 180)).Shape);
    }

    // ---- Grenzen

    [Fact]
    public void TiltLimit_FollowsSensitivity()
    {
        var tilted = HeartHandFixtures.Classic(rotationDeg: 40);
        Assert.False(Pair(tilted, 10).IsHeart);
        Assert.True(Pair(tilted, 95).IsHeart);
        Assert.True(Pair(HeartHandFixtures.Classic(rotationDeg: 25), 10).IsHeart);
        Assert.False(Pair(HeartHandFixtures.Classic(rotationDeg: 90), 95).IsHeart);
        Assert.False(Pair(HeartHandFixtures.Classic(rotationDeg: -90), 95).IsHeart);
    }

    [Fact]
    public void TipsTooFarApart_AreRejected()
    {
        Assert.True(Pair(HeartHandFixtures.Pair(tipGap: 0.03), 10).IsHeart);
        var far = HeartHandFixtures.Pair(tipGap: 0.2);
        foreach (var sensitivity in Sensitivities)
            Assert.False(Pair(far, sensitivity).IsHeart);
        Assert.False(Pair(HeartHandFixtures.Pair(tipGap: 0.10), 10).IsHeart);
        Assert.True(Pair(HeartHandFixtures.Pair(tipGap: 0.10), 95).IsHeart);
    }

    [Fact]
    public void SizeMismatch_IsRejected()
    {
        Assert.True(Pair(HeartHandFixtures.Pair(scaleB: 1.0)).IsHeart);
        var mismatch = HeartHandFixtures.Pair(scaleB: 2.5);
        foreach (var sensitivity in Sensitivities)
            Assert.False(Pair(mismatch, sensitivity).IsHeart);
        // Mäßig größere zweite Hand: nur bei lockerer Einstellung (Spitzen dürfen weiter auseinanderliegen).
        Assert.True(Pair(HeartHandFixtures.Pair(scaleB: 1.4), 95).IsHeart);
    }

    [Fact]
    public void LowHandScore_IsRejected()
    {
        var low = HeartHandFixtures.Pair(scoreA: 0.5, scoreB: 0.95);
        foreach (var sensitivity in new[] { 10, 50 })
            Assert.False(Pair(low, sensitivity).IsHeart);
        Assert.True(Pair(HeartHandFixtures.Pair(scoreA: 0.6, scoreB: 0.95), 95).IsHeart);
        Assert.False(Pair(HeartHandFixtures.Pair(scoreA: 0.6, scoreB: 0.95), 10).IsHeart);
    }

    [Fact]
    public void RejectReason_IsGermanTextOnRejection_NullOnMatch()
    {
        Assert.Null(Pair(HeartHandFixtures.Classic()).RejectReason);
        var verdict = Pair(HeartHandFixtures.TwoOpenHands());
        Assert.False(verdict.IsHeart);
        Assert.False(string.IsNullOrWhiteSpace(verdict.RejectReason));
    }

    // ---- Negativposen

    public static TheoryData<int> AllSensitivities() => new(Sensitivities);

    [Theory]
    [MemberData(nameof(AllSensitivities))]
    public void NegativePoses_AreRejected_AtEverySensitivity(int sensitivity)
    {
        foreach (var aspect in Aspects)
        {
            Assert.Empty(Find(HeartHandFixtures.TwoFists(aspect), sensitivity, aspect));
            Assert.Empty(Find(HeartHandFixtures.TwoOpenHands(aspect), sensitivity, aspect));
            Assert.Empty(Find(HeartHandFixtures.PrayerHands(aspect), sensitivity, aspect));
            Assert.Empty(Find(HeartHandFixtures.TwoPeaceHands(aspect), sensitivity, aspect));
        }
    }

    [Theory]
    [MemberData(nameof(AllSensitivities))]
    public void SameHandTwice_IsRejected(int sensitivity)
    {
        var hand = HeartHandFixtures.Classic()[0];
        Assert.Empty(Find([hand, hand], sensitivity));
    }

    [Fact]
    public void PrayerHands_HaveWristsTooCloseAndAreRejected()
    {
        var verdict = Pair(HeartHandFixtures.PrayerHands(), 95);
        Assert.False(verdict.IsHeart);
        Assert.NotNull(verdict.RejectReason);
    }

    [Fact]
    public void WristOnTheHeartAxis_IsRejectedByTheSideCondition()
    {
        // Ein Handgelenk der Hand B liegt auf der Linie Daumenspitzen → Zeigefingerspitzen (Abstand zur Handlänge unverändert):
        // Spitzen, Höhe und Öffnung bleiben gültig, damit entscheidet allein die Seitenbedingung (Gebetshände-Ausschluss).
        const double aspect = 16.0 / 9;
        var heart = HeartHandFixtures.Classic();
        var a = heart[0];
        var b = heart[1];
        var uX = (a.Points[4].X + b.Points[4].X) / 2 * aspect;
        var b9 = b.Points[9];
        var originalLength = Math.Sqrt(Math.Pow((b.Points[0].X - b9.X) * aspect, 2) + Math.Pow(b.Points[0].Y - b9.Y, 2));
        var dx = uX - b9.X * aspect;
        var wrist = new HandPoint(uX / aspect, b9.Y + Math.Sqrt(originalLength * originalLength - dx * dx), 0);

        var points = b.Points.ToList();
        points[0] = wrist;
        var moved = new HandLandmarks(points, b.Score, b.Box);

        Assert.True(Pair(heart).IsHeart);
        foreach (var sensitivity in new[] { 10, 50, 95 })
        {
            var verdict = Pair([a, moved], sensitivity);
            Assert.False(verdict.IsHeart);
            Assert.Equal("Handgelenke nicht beidseits des Herzens.", verdict.RejectReason);
        }
    }

    // ---- Empfindlichkeit

    [Fact]
    public void Sensitivity_IsMonotone_LooserNeverRejectsWhatStricterAccepts()
    {
        var rotations = Enumerable.Range(0, 15).Select(i => i * 5.0);
        var gaps = new[] { 0, 0.03, 0.06, 0.09, 0.12, 0.15 };
        foreach (var others in new[] { OtherFingers.Curved, OtherFingers.Extended })
        foreach (var rotation in rotations)
        foreach (var gap in gaps)
        {
            var hands = HeartHandFixtures.Pair(others, rotation, tipGap: gap);
            var accepted = false;
            foreach (var sensitivity in Sensitivities)
            {
                var now = Find(hands, sensitivity).Count > 0;
                Assert.True(now || !accepted, $"Nicht monoton bei {others}, Drehung {rotation}, Lücke {gap}, Empfindlichkeit {sensitivity}.");
                accepted |= now;
            }
        }
    }

    [Fact]
    public void Thresholds_LerpFromStrictToLoose_AndClamp()
    {
        var strict = HeartGestureClassifier.Thresholds.For(10);
        var loose = HeartGestureClassifier.Thresholds.For(95);
        Assert.Equal(1.6, strict.MaxSizeRatio, 6);
        Assert.Equal(2.2, loose.MaxSizeRatio, 6);
        Assert.Equal(0.30, strict.TipTouch, 6);
        Assert.Equal(0.55, loose.TipTouch, 6);
        Assert.Equal(30, strict.MaxTiltDeg, 6);
        Assert.Equal(55, loose.MaxTiltDeg, 6);
        Assert.Equal(0.70, strict.MinHeight, 6);
        Assert.Equal(0.45, loose.MinHeight, 6);
        Assert.Equal(strict, HeartGestureClassifier.Thresholds.For(-5));
        Assert.Equal(loose, HeartGestureClassifier.Thresholds.For(200));
    }

    // ---- Paarwahl

    [Fact]
    public void FourHands_FormTwoHearts_RegardlessOfOrder()
    {
        var left = HeartHandFixtures.Classic(cx: 0.25, cy: 0.5, scale: 0.7);
        var right = HeartHandFixtures.Roof(cx: 0.75, cy: 0.5, scale: 0.7);
        var matches = Find([left[0], right[0], left[1], right[1]]);
        Assert.Equal(2, matches.Count);
        var classic = Assert.Single(matches, m => m.Shape == HeartShape.Classic);
        var roof = Assert.Single(matches, m => m.Shape == HeartShape.Roof);
        Assert.Equal([0, 2], new[] { classic.HandA, classic.HandB }.Order().ToArray());
        Assert.Equal([1, 3], new[] { roof.HandA, roof.HandB }.Order().ToArray());
    }

    [Fact]
    public void EveryHand_BelongsToAtMostOneHeart()
    {
        var heart = HeartHandFixtures.Classic();
        // Dritte Hand: Kopie der ersten, minimal verschoben – könnte ebenfalls mit der zweiten ein Herz bilden.
        var shifted = new HandLandmarks(heart[0].Points.Select(p => p with { X = p.X + 0.002 }).ToList(), heart[0].Score, heart[0].Box);
        var matches = Find([heart[0], heart[1], shifted], configure: s => s.ShapeFingerHeart = false);
        var match = Assert.Single(matches);
        var used = new[] { match.HandA, match.HandB };
        Assert.Equal(used.Length, used.Distinct().Count());
    }

    [Fact]
    public void BestScoringPairWins()
    {
        var heart = HeartHandFixtures.Classic();
        var perfect = heart[0];
        var worse = HeartHandFixtures.Pair(tipGap: 0.03)[1];
        var matches = Find([perfect, worse, heart[1]], sensitivity: 95, configure: s => s.ShapeFingerHeart = false);
        var match = Assert.Single(matches);
        Assert.Equal([0, 2], new[] { match.HandA, match.HandB }.Order().ToArray());
    }

    [Fact]
    public void EmptyOrSingleHand_FindsNoPairHeart()
    {
        Assert.Empty(Find([]));
        Assert.Empty(Find([HeartHandFixtures.Classic()[0]], configure: s => s.ShapeFingerHeart = false));
    }

    // ---- Fingerherz

    private static IReadOnlyList<HeartMatch> FindFinger(HandLandmarks hand, int sensitivity, double aspect = 16.0 / 9) =>
        Find([hand], sensitivity, aspect);

    [Fact]
    public void FingerHeart_Crossed_IsRecognizedAtAllSensitivities()
    {
        foreach (var aspect in Aspects)
        foreach (var sensitivity in Sensitivities)
        {
            var hand = HeartHandFixtures.FingerHeart(FingerHeartPose.Crossed, aspect: aspect);
            var match = Assert.Single(FindFinger(hand, sensitivity, aspect));
            Assert.Equal(HeartShape.FingerHeart, match.Shape);
            Assert.Equal(0, match.HandA);
            Assert.Equal(-1, match.HandB);
            Assert.Equal(hand.Box, match.Box);
        }
    }

    [Fact]
    public void FingerHeart_CrossedRotated_IsRecognized()
    {
        foreach (var rotation in new[] { -30.0, 20, 90, 180 })
            Assert.Single(FindFinger(HeartHandFixtures.FingerHeart(FingerHeartPose.Crossed, rotation), 50));
    }

    [Fact]
    public void FingerHeart_Pinch_NeedsLooseSensitivity()
    {
        var pinch = HeartHandFixtures.FingerHeart(FingerHeartPose.Pinch);
        Assert.Empty(FindFinger(pinch, 10));
        Assert.Empty(FindFinger(pinch, 50)); // t < 0,5: Daumen muss den Zeigefinger kreuzen
        Assert.Single(FindFinger(pinch, 80));
        Assert.Single(FindFinger(pinch, 95));
    }

    [Fact]
    public void FingerHeart_OtherFingersFolded_StrictVersusLoose()
    {
        // Ringfinger gestreckt: streng (alle drei gebeugt) abgelehnt, locker (Mittelfinger + kleiner Finger) erlaubt.
        var ringUp = HeartHandFixtures.FingerHeart(FingerHeartPose.Crossed, ringExtended: true);
        Assert.Empty(FindFinger(ringUp, 10));
        Assert.Empty(FindFinger(ringUp, 40));
        Assert.Single(FindFinger(ringUp, 95));
    }

    [Theory]
    [MemberData(nameof(AllSensitivities))]
    public void FingerHeart_OkSignAndOpenPinch_AreRejected(int sensitivity)
    {
        Assert.Empty(FindFinger(HeartHandFixtures.FingerHeart(FingerHeartPose.OkSign), sensitivity));
        Assert.Empty(FindFinger(HeartHandFixtures.FingerHeart(FingerHeartPose.PinchOpen), sensitivity));
    }

    [Fact]
    public void FingerHeart_FistAndOpenHand_AreRejected()
    {
        foreach (var pose in new[] { HandPose.Fist, HandPose.Open, HandPose.Peace })
            Assert.Empty(FindFinger(PeaceHandFixtures.Hand(pose), 95));
    }

    [Fact]
    public void FingerHeart_NeedsShapeEnabled()
    {
        var hand = HeartHandFixtures.FingerHeart(FingerHeartPose.Crossed);
        Assert.Empty(Find([hand], configure: s => s.ShapeFingerHeart = false));
        Assert.Single(Find([hand], configure: s => { s.ShapeClassic = s.ShapeRoof = s.ShapeInverted = false; }));
    }

    [Fact]
    public void FingerHeart_LowScore_IsRejected()
    {
        var weak = HeartHandFixtures.FingerHeart(FingerHeartPose.Crossed, score: 0.5);
        Assert.Empty(FindFinger(weak, 10));
        Assert.Empty(FindFinger(weak, 95));
    }

    [Fact]
    public void FingerHeart_HandOfPairHeart_IsNotCountedTwice()
    {
        // Ein Zwei-Hände-Herz plus eine Fingerherz-Hand: zwei Treffer, die Paar-Hände tauchen nicht zusätzlich als Fingerherz auf.
        var pair = HeartHandFixtures.Classic(cx: 0.3, cy: 0.4, scale: 0.7);
        var finger = HeartHandFixtures.FingerHeart(FingerHeartPose.Crossed, cx: 0.8, cy: 0.7);
        var matches = Find([pair[0], pair[1], finger]);
        Assert.Equal(2, matches.Count);
        Assert.Single(matches, m => m.Shape == HeartShape.Classic);
        var fh = Assert.Single(matches, m => m.Shape == HeartShape.FingerHeart);
        Assert.Equal(2, fh.HandA);
    }

    [Fact]
    public void ClassifyFingerHeart_ReportsReason()
    {
        var verdict = HeartGestureClassifier.ClassifyFingerHeart(PeaceHandFixtures.Hand(HandPose.Open), Settings());
        Assert.False(verdict.IsHeart);
        Assert.False(string.IsNullOrWhiteSpace(verdict.RejectReason));
        var ok = HeartGestureClassifier.ClassifyFingerHeart(HeartHandFixtures.FingerHeart(FingerHeartPose.Crossed), Settings());
        Assert.True(ok.IsHeart);
        Assert.Null(ok.RejectReason);
    }
}
