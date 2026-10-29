using Fotobox.Core.Peace;
using Fotobox.ViewModels.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Statuszeile der Peace-Vorschau im Admin (reine Funktion).</summary>
public sealed class PeacePreviewStatusFormatterTests
{
    private static GestureStatus Ready(double rate = 4.8, string? faceReason = null, string? handReason = null,
        bool faces = true, bool hands = true) =>
        new(faces, hands, faceReason, handReason, rate);

    [Fact]
    public void Ready_ShowsHandsFacesAndRate()
    {
        var lines = PeacePreviewStatusFormatter.Format(Ready(), hands: 1, faces: 2, peaceNow: false, cameraRunning: true);

        Assert.Equal("Hände: 1 · Gesichter: 2 · 4,8 Analysen/s", lines.Text);
        Assert.Null(lines.Warning);
    }

    [Fact]
    public void MiddleFingerNow_AppendsItsText()
    {
        var lines = PeacePreviewStatusFormatter.Format(Ready(), 1, 2, peaceNow: false, cameraRunning: true, middleFingerNow: true);

        Assert.Equal("Hände: 1 · Gesichter: 2 · 4,8 Analysen/s · Mittelfinger erkannt", lines.Text);
    }

    [Fact]
    public void PeaceNow_AppendsTheBadgeText()
    {
        var lines = PeacePreviewStatusFormatter.Format(Ready(), 1, 2, peaceNow: true, cameraRunning: true);

        Assert.Equal("Hände: 1 · Gesichter: 2 · 4,8 Analysen/s · Peace erkannt ✌", lines.Text);
    }

    [Fact]
    public void UnknownFaceCount_OmitsTheFaces()
    {
        var lines = PeacePreviewStatusFormatter.Format(Ready(), 0, -1, false, true);

        Assert.Equal("Hände: 0 · 4,8 Analysen/s", lines.Text);
    }

    [Fact]
    public void StarterStatus_ShowsLoading()
    {
        var lines = PeacePreviewStatusFormatter.Format(GestureStatus.Starting, 0, 0, false, true);

        Assert.Equal(PeacePreviewStatusFormatter.Loading, lines.Text);
        Assert.Null(lines.Warning);
    }

    [Fact]
    public void NoStatus_ReportsNotAvailable()
    {
        Assert.Equal(PeacePreviewStatusFormatter.NotAvailable,
            PeacePreviewStatusFormatter.Format(null, 0, 0, false, true).Text);
    }

    [Fact]
    public void UnavailableStage_IsReportedAsWarning_WhileTheOtherStageStillWorks()
    {
        var lines = PeacePreviewStatusFormatter.Format(
            Ready(faces: false, faceReason: "Das Gesichtsmodell fehlt."), 1, -1, false, true);

        Assert.Equal("Hände: 1 · 4,8 Analysen/s", lines.Text);
        Assert.Equal("Das Gesichtsmodell fehlt.", lines.Warning);
    }

    [Fact]
    public void BothStagesUnavailable_ShowsNotAvailableWithBothReasons()
    {
        var lines = PeacePreviewStatusFormatter.Format(
            Ready(faces: false, hands: false, faceReason: "Gesicht weg.", handReason: "Hand weg."), 0, 0, false, true);

        Assert.Equal(PeacePreviewStatusFormatter.NotAvailable, lines.Text);
        Assert.Equal("Hand weg.\nGesicht weg.", lines.Warning);
    }

    [Fact]
    public void IdenticalReasons_AreShownOnlyOnce()
    {
        // Der „inerte“ Dienst (keine Erkennung verfügbar) meldet für Hand- und Gesichtsstufe denselben Grund.
        var lines = PeacePreviewStatusFormatter.Format(
            Ready(faces: false, hands: false, faceReason: "Nicht verfügbar.", handReason: "Nicht verfügbar."), 0, 0, false, true);

        Assert.Equal(PeacePreviewStatusFormatter.NotAvailable, lines.Text);
        Assert.Equal("Nicht verfügbar.", lines.Warning);
    }

    [Fact]
    public void NoCamera_AddsAWarning()
    {
        var lines = PeacePreviewStatusFormatter.Format(Ready(), 0, 0, false, cameraRunning: false);

        Assert.Equal(PeacePreviewStatusFormatter.NoCamera, lines.Warning);
        Assert.Equal("Keine Kamera – Vorschau nicht verfügbar.", lines.Warning);
    }

    [Fact]
    public void NegativeValues_AreClamped()
    {
        var lines = PeacePreviewStatusFormatter.Format(Ready(rate: -1), -3, 0, false, true);

        Assert.Equal("Hände: 0 · Gesichter: 0 · 0,0 Analysen/s", lines.Text);
    }

    [Fact]
    public void MeanCycleDuration_IsShownRoundedAfterTheRate()
    {
        var status = Ready() with { MeanCycleMs = 63.4 };
        var lines = PeacePreviewStatusFormatter.Format(status, 1, 2, peaceNow: false, cameraRunning: true);

        Assert.Equal("Hände: 1 · Gesichter: 2 · 4,8 Analysen/s · Analyse ⌀ 63 ms", lines.Text);
    }

    [Fact]
    public void MeanCycleDuration_PrecedesThePeaceBadge()
    {
        var status = Ready() with { MeanCycleMs = 120 };
        var lines = PeacePreviewStatusFormatter.Format(status, 1, 2, peaceNow: true, cameraRunning: true);

        Assert.Equal("Hände: 1 · Gesichter: 2 · 4,8 Analysen/s · Analyse ⌀ 120 ms · Peace erkannt ✌", lines.Text);
    }

    [Fact]
    public void UnknownMeanCycleDuration_IsOmitted()
    {
        var lines = PeacePreviewStatusFormatter.Format(Ready() with { MeanCycleMs = 0 }, 1, 2, false, true);

        Assert.DoesNotContain("⌀", lines.Text);
    }
}
