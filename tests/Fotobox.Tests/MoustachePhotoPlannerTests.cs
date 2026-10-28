using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

/// <summary><see cref="MoustachePhotoPlanner"/>: Zuordnung Fotogesichter zu Live-Spuren, Ziele, Rückfälle.</summary>
public sealed class MoustachePhotoPlannerTests
{
    private const int W = 1000, H = 500;

    private static FaceLandmarks Face(double cx, double cy, double w = 0.1)
    {
        var h = w * 1.3 * W / H;   // grob quadratisch in Pixeln
        return new FaceLandmarks(
            new NormRect(cx - w / 2, cy - h / 2, w, h), 0.9,
            RightEye: new NormPoint(cx - w * 0.2, cy - h * 0.15),
            LeftEye: new NormPoint(cx + w * 0.2, cy - h * 0.15),
            Nose: new NormPoint(cx, cy),
            MouthRight: new NormPoint(cx - w * 0.15, cy + h * 0.2),
            MouthLeft: new NormPoint(cx + w * 0.15, cy + h * 0.2));
    }

    private static MoustacheTrack Track(int id, MoustacheStyle style, FaceLandmarks face, TimeSpan? age = null) =>
        new(id, style, face.Box, new MoustachePose(new NormPoint(face.Nose.X, face.Nose.Y + 0.01), 77, 0.25), age ?? TimeSpan.Zero);

    private static MoustachePlanInput Input(MoustacheTargets targets, int? owner, params MoustacheTrack[] tracks) =>
        new(targets, owner, tracks);

    private static int Never(int n) => throw new InvalidOperationException("random darf nicht gefragt werden");

    [Fact]
    public void NullPhotoFaces_Everyone_UsesAllLivePoses()
    {
        var a = Track(1, MoustacheStyle.Twirl, Face(0.3, 0.5));
        var b = Track(2, MoustacheStyle.Brush, Face(0.7, 0.5));

        var plan = MoustachePhotoPlanner.Plan(Input(MoustacheTargets.Everyone, null, a, b), null, W, H, Never);

        Assert.Equal([(a.Pose, a.Style), (b.Pose, b.Style)], plan);
    }

    [Fact]
    public void NullPhotoFaces_PeaceShower_UsesOnlyOwnerPose()
    {
        var a = Track(1, MoustacheStyle.Twirl, Face(0.3, 0.5));
        var b = Track(2, MoustacheStyle.Brush, Face(0.7, 0.5));

        var plan = MoustachePhotoPlanner.Plan(Input(MoustacheTargets.PeaceShower, 2, a, b), null, W, H, Never);

        Assert.Equal([(b.Pose, b.Style)], plan);
    }

    [Fact]
    public void NullPhotoFaces_PeaceShowerWithoutOwner_IsEmpty()
    {
        var a = Track(1, MoustacheStyle.Twirl, Face(0.3, 0.5));

        Assert.Empty(MoustachePhotoPlanner.Plan(Input(MoustacheTargets.PeaceShower, null, a), null, W, H, Never));
    }

    [Fact]
    public void PhotoFace_MatchedByIoU_UsesPhotoPoseAndTrackStyle()
    {
        var live = Face(0.30, 0.50);
        var photo = Face(0.31, 0.51);   // leicht verschoben
        var track = Track(1, MoustacheStyle.Walrus, live);

        var plan = MoustachePhotoPlanner.Plan(Input(MoustacheTargets.Everyone, null, track), [photo], W, H, Never);

        var (pose, style) = Assert.Single(plan);
        Assert.Equal(MoustacheStyle.Walrus, style);
        Assert.Equal(MoustacheGeometry.FromFace(photo, W, H), pose);
        Assert.NotEqual(track.Pose, pose);
    }

    [Fact]
    public void PhotoFace_MatchedByCenterDistance_WhenBoxesDoNotOverlapEnough()
    {
        var live = Face(0.30, 0.50, 0.10);
        // Nur die Mitte ist nahe (< 0,5 Gesichtsbreiten), Box viel kleiner: IoU < 0,3
        var photo = Face(0.32, 0.50, 0.05);
        Assert.True(photo.Box.IoU(live.Box) < MoustachePhotoPlanner.MinIoU);
        var track = Track(1, MoustacheStyle.Chevron, live);

        var plan = MoustachePhotoPlanner.Plan(Input(MoustacheTargets.Everyone, null, track), [photo], W, H, Never);

        var (_, style) = Assert.Single(plan);
        Assert.Equal(MoustacheStyle.Chevron, style);
    }

    [Fact]
    public void TwoFaces_AreAssignedToTheirOwnTracks_EvenIfListedInOtherOrder()
    {
        var left = Face(0.25, 0.5);
        var right = Face(0.75, 0.5);
        var trackLeft = Track(1, MoustacheStyle.Twirl, left);
        var trackRight = Track(2, MoustacheStyle.Brush, right);

        var plan = MoustachePhotoPlanner.Plan(
            Input(MoustacheTargets.Everyone, null, trackLeft, trackRight), [right, left], W, H, Never);

        Assert.Equal(2, plan.Count);
        Assert.Equal((MoustacheGeometry.FromFace(right, W, H), MoustacheStyle.Brush), plan[0]);
        Assert.Equal((MoustacheGeometry.FromFace(left, W, H), MoustacheStyle.Twirl), plan[1]);
    }

    [Fact]
    public void GreedyAssignment_BestMatchWins_TrackUsedOnlyOnce()
    {
        var live = Face(0.50, 0.50);
        var close = Face(0.50, 0.50);     // IoU 1
        var farther = Face(0.52, 0.50);   // IoU hoch, aber schlechter
        var track = Track(1, MoustacheStyle.Brush, live);
        var calls = new List<int>();

        var plan = MoustachePhotoPlanner.Plan(Input(MoustacheTargets.Everyone, null, track), [farther, close], W, H,
            n => { calls.Add(n); return 0; });

        Assert.Equal(2, plan.Count);
        Assert.Equal((MoustacheGeometry.FromFace(farther, W, H), MoustacheStyle.Twirl), plan[0]);   // ohne Spur: Zufall (0 = Twirl)
        Assert.Equal((MoustacheGeometry.FromFace(close, W, H), MoustacheStyle.Brush), plan[1]);
        Assert.Equal([4], calls);
    }

    [Theory]
    [InlineData(0, MoustacheStyle.Twirl)]
    [InlineData(1, MoustacheStyle.Walrus)]
    [InlineData(2, MoustacheStyle.Chevron)]
    [InlineData(3, MoustacheStyle.Brush)]
    public void Everyone_UnmatchedPhotoFace_GetsRandomStyle(int value, MoustacheStyle expected)
    {
        var tracked = Face(0.25, 0.5);
        var stranger = Face(0.75, 0.5);
        var track = Track(1, MoustacheStyle.Twirl, tracked);

        var plan = MoustachePhotoPlanner.Plan(Input(MoustacheTargets.Everyone, null, track), [tracked, stranger], W, H, n =>
        {
            Assert.Equal(4, n);
            return value;
        });

        Assert.Equal(2, plan.Count);
        Assert.Equal(expected, plan[1].Style);
        Assert.Equal(MoustacheGeometry.FromFace(stranger, W, H), plan[1].Pose);
    }

    [Fact]
    public void Everyone_UnfoundTracks_AgeRuleDecidesLivePose()
    {
        var fresh = Track(1, MoustacheStyle.Twirl, Face(0.2, 0.5), TimeSpan.FromMilliseconds(300));
        var stale = Track(2, MoustacheStyle.Brush, Face(0.5, 0.5), TimeSpan.FromMilliseconds(800));
        var atLimit = Track(3, MoustacheStyle.Walrus, Face(0.8, 0.5), TimeSpan.FromMilliseconds(500));

        var plan = MoustachePhotoPlanner.Plan(Input(MoustacheTargets.Everyone, null, fresh, stale, atLimit), [], W, H, Never);

        Assert.Equal([(fresh.Pose, fresh.Style)], plan);   // „Alter < 500 ms“: genau 500 ms zählt nicht mehr
    }

    [Fact]
    public void PeaceShower_OnlyOwnersPhotoFaceGetsMoustache()
    {
        var ownerFace = Face(0.3, 0.5);
        var otherFace = Face(0.7, 0.5);
        var owner = Track(1, MoustacheStyle.Brush, ownerFace);
        var other = Track(2, MoustacheStyle.Twirl, otherFace);

        var plan = MoustachePhotoPlanner.Plan(Input(MoustacheTargets.PeaceShower, 1, owner, other), [otherFace, ownerFace], W, H, Never);

        Assert.Equal([(MoustacheGeometry.FromFace(ownerFace, W, H), MoustacheStyle.Brush)], plan);
    }

    [Fact]
    public void PeaceShower_UnknownPhotoFace_NeverGetsMoustache()
    {
        var owner = Track(1, MoustacheStyle.Brush, Face(0.3, 0.5), TimeSpan.FromSeconds(2));

        var plan = MoustachePhotoPlanner.Plan(Input(MoustacheTargets.PeaceShower, 1, owner), [Face(0.8, 0.5)], W, H, Never);

        Assert.Empty(plan);
    }

    [Fact]
    public void PeaceShower_OwnerNotInPhoto_FreshOwnerUsesLivePose_StaleOwnerNothing()
    {
        var fresh = Track(1, MoustacheStyle.Brush, Face(0.3, 0.5), TimeSpan.FromMilliseconds(100));
        var stale = Track(1, MoustacheStyle.Brush, Face(0.3, 0.5), TimeSpan.FromMilliseconds(900));

        Assert.Equal([(fresh.Pose, fresh.Style)],
            MoustachePhotoPlanner.Plan(Input(MoustacheTargets.PeaceShower, 1, fresh), [], W, H, Never));
        Assert.Empty(MoustachePhotoPlanner.Plan(Input(MoustacheTargets.PeaceShower, 1, stale), [], W, H, Never));
    }

    [Fact]
    public void NoTracksAndNoFaces_IsEmpty()
    {
        Assert.Empty(MoustachePhotoPlanner.Plan(Input(MoustacheTargets.Everyone, null), [], W, H, Never));
    }
}
