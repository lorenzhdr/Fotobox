using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

public class BoothControllerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void TryTrigger_InStartingState_ReturnsFalse()
    {
        var h = new Harness(markReady: false);

        Assert.Equal(BoothState.Starting, h.Controller.State);
        Assert.False(h.Controller.TryTrigger());
        Assert.Equal(0, h.Camera.CaptureCount);
    }

    [Theory]
    [InlineData(CameraStatus.Stopped)]
    [InlineData(CameraStatus.Starting)]
    [InlineData(CameraStatus.NoDevice)]
    [InlineData(CameraStatus.AccessDenied)]
    [InlineData(CameraStatus.Error)]
    public void TryTrigger_CameraNotRunning_ReturnsFalse(CameraStatus status)
    {
        var h = new Harness();
        h.Camera.Status = status;

        Assert.False(h.Controller.TryTrigger());
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void TryTrigger_InIdleWithRunningCamera_StartsCountdown()
    {
        var h = new Harness();

        Assert.True(h.Controller.TryTrigger());

        Assert.Equal(BoothState.Countdown, h.Controller.State);
        Assert.Equal(3, h.Controller.CountdownValue);
        Assert.Equal(1, h.Controller.ShotIndex);
        Assert.Equal(1, h.Controller.ShotCount);
    }

    [Fact]
    public void SecondTrigger_DuringSession_IsIgnored()
    {
        var h = new Harness();
        Assert.True(h.Controller.TryTrigger());

        Assert.False(h.Controller.TryTrigger());
        h.Time.Advance(Second);
        Assert.False(h.Controller.TryTrigger());

        h.RunToIdle();
        Assert.Equal(1, h.Camera.CaptureCount);
        Assert.Single(h.Storage.Saved);
    }

    [Fact]
    public void Countdown_TicksOncePerSecond_ThenCapturesWithFlash()
    {
        var h = new Harness();
        var flashes = 0;
        h.Controller.FlashRequested += (_, _) => flashes++;

        h.Controller.TryTrigger();
        Assert.Equal(3, h.Controller.CountdownValue);

        h.Time.Advance(Second);
        Assert.Equal(2, h.Controller.CountdownValue);
        Assert.Equal(0, h.Camera.CaptureCount);

        h.Time.Advance(Second);
        Assert.Equal(1, h.Controller.CountdownValue);
        Assert.Equal(0, flashes);

        h.Time.Advance(Second);
        Assert.Equal(0, h.Controller.CountdownValue);
        Assert.Equal(1, flashes);
        Assert.Equal(1, h.Camera.CaptureCount);
        Assert.NotEqual(BoothState.Countdown, h.Controller.State);
    }

    [Fact]
    public void Session_ShowsPreviewWithSavedLayout_ThenReturnsToIdle()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);

        Assert.NotNull(h.Controller.PreviewImage);
        Assert.NotNull(h.Controller.LastSaved);

        h.RunToIdle();
        Assert.Null(h.Controller.PreviewImage);
        Assert.Equal(
            [BoothState.Countdown, BoothState.Capturing, BoothState.Preview, BoothState.Idle],
            states);
    }

    [Fact]
    public void Strip_TakesThreePhotos_WithShotInfoAndPause()
    {
        var h = new Harness();
        h.Settings.Current.Layout.Kind = LayoutKind.Strip;
        h.Settings.Current.Layout.StripPhotoCount = 3;
        h.Settings.Current.Flow.PrintingEnabled = false;
        h.Composer.RequiredPhotoCount = 3;
        var flashes = 0;
        var shotIndices = new List<int>();
        h.Controller.FlashRequested += (_, _) => flashes++;
        h.Controller.Changed += (_, _) =>
        {
            if (shotIndices.Count == 0 || shotIndices[^1] != h.Controller.ShotIndex)
                shotIndices.Add(h.Controller.ShotIndex);
        };

        h.Controller.TryTrigger();
        Assert.Equal(3, h.Controller.ShotCount);
        h.RunToIdle();

        Assert.Equal(3, h.Camera.CaptureCount);
        Assert.Equal(3, flashes);
        Assert.Equal(3, h.Composer.LastPhotos!.Count);
        Assert.Single(h.Storage.Saved);
        Assert.Equal(3, h.Storage.Saved[0].Originals.Count);
        Assert.Contains(1, shotIndices);
        Assert.Contains(2, shotIndices);
        Assert.Contains(3, shotIndices);
    }

    [Fact]
    public void PrintingDisabled_NeverCallsPrint()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();

        Assert.Equal(0, h.Printer.PrintCalls);
        Assert.Single(h.Storage.Saved);
    }

    [Fact]
    public void Printing_RunsParallelToPreview_ThenShowsPrintingStateUntilDone()
    {
        var h = new Harness();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        Assert.Equal(1, h.Printer.PrintCalls);   // Druck startet schon während der Vorschau
        Assert.Same(h.Composer.Result, h.Printer.LastPage);

        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);
        Assert.Equal(BoothState.Printing, h.Controller.State);

        h.Printer.Status?.Report("Warte auf den Drucker …");
        h.WaitUntil(() => h.Controller.StatusMessage == "Warte auf den Drucker …");

        h.Printer.Complete(new PrintResult(PrintOutcome.Completed));
        h.WaitUntil(() => h.Controller.State == BoothState.Printed);
        h.RunToIdle();
        Assert.Null(h.Controller.ErrorMessage);
    }

    [Fact]
    public void PrintTakesLongerThanPreview_ProgressRisesThenOverdueThenPrintedThenIdle()
    {
        var h = new Harness();
        h.Settings.Current.Flow.ExpectedPrintSeconds = 120;
        var states = h.RecordStates();
        var changed = 0;
        h.Controller.Changed += (_, _) => changed++;

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        Assert.Equal(120, h.Controller.ExpectedPrintSeconds);
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);
        var start = h.Controller.PrintProgress;
        Assert.InRange(start, 0, 0.1);
        Assert.False(h.Controller.PrintOverdue);

        // Der Takt meldet den Fortschritt laufend, ohne dass sich sonst etwas ändert.
        var before = changed;
        h.AdvanceBy(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250));
        Assert.InRange(changed - before, 100, 400);
        Assert.Equal(start + 0.5, h.Controller.PrintProgress, 2);
        Assert.False(h.Controller.PrintOverdue);
        Assert.Equal(BoothState.Printing, h.Controller.State);

        h.AdvanceBy(TimeSpan.FromSeconds(60), Second);
        Assert.Equal(1, h.Controller.PrintProgress);
        Assert.True(h.Controller.PrintOverdue);
        Assert.Equal(BoothState.Printing, h.Controller.State);

        // Nach Ablauf der Zeit ist der Takt weiter aktiv und der Druck noch nicht fertig.
        h.Printer.Complete(new PrintResult(PrintOutcome.Completed));
        h.WaitUntil(() => h.Controller.State == BoothState.Printed);
        Assert.Equal(1, h.Controller.PrintProgress);
        Assert.False(h.Controller.PrintOverdue);

        // „Fertig“ bleibt 5 Sekunden stehen.
        h.Time.Advance(BoothController.PrintedDisplay - Second);
        Assert.Equal(BoothState.Printed, h.Controller.State);
        h.Time.Advance(Second);
        h.WaitUntil(() => h.Controller.State == BoothState.Idle);

        Assert.Equal(0, h.Controller.PrintProgress);
        Assert.Equal(
            [BoothState.Countdown, BoothState.Capturing, BoothState.Preview, BoothState.Printing, BoothState.Printed, BoothState.Idle],
            states);
        var afterIdle = changed;
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(afterIdle, changed);   // kein Takt mehr im Leerlauf
    }

    [Fact]
    public void PrintFinishedDuringPreview_GoesToPrintedAfterPreview_ThenIdle()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PreviewSeconds = 6;
        var states = h.RecordStates();
        var progressWhenPrinted = -1.0;
        h.Controller.StateChanged += (_, _) =>
        {
            if (h.Controller.State == BoothState.Printed)
                progressWhenPrinted = h.Controller.PrintProgress;
        };

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        h.Printer.Complete(new PrintResult(PrintOutcome.Completed));
        Assert.Equal(BoothState.Preview, h.Controller.State);   // Vorschau läuft zu Ende
        var previewStart = h.Time.GetUtcNow();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printed);
        var printedAt = h.Time.GetUtcNow();
        h.RunToIdle();

        Assert.Equal(
            [BoothState.Countdown, BoothState.Capturing, BoothState.Preview, BoothState.Printed, BoothState.Idle],
            states);
        Assert.Equal(1, progressWhenPrinted);
        Assert.True(printedAt - previewStart >= TimeSpan.FromSeconds(5));   // Rest der Vorschau
        Assert.True(h.Time.GetUtcNow() - printedAt >= BoothController.PrintedDisplay);
    }

    [Fact]
    public void PrintFailure_NeverShowsPrinted()
    {
        var h = new Harness();
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);
        h.Printer.Complete(new PrintResult(PrintOutcome.Failed, "Papier leer"));
        h.RunToIdle();

        Assert.Contains(BoothState.Error, states);
        Assert.DoesNotContain(BoothState.Printed, states);
        Assert.Equal(0, h.Controller.PrintProgress);
    }

    [Fact]
    public void PrintingDisabled_NeverShowsPrinted()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.RunToIdle();

        Assert.DoesNotContain(BoothState.Printed, states);
        Assert.DoesNotContain(BoothState.Printing, states);
    }

    [Fact]
    public void PrintFailure_ShowsErrorMessage_ThenReturnsToIdle()
    {
        var h = new Harness();
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        h.Printer.Complete(new PrintResult(PrintOutcome.Failed, "Papier leer"));
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);

        Assert.Equal(
            "Das Foto wurde gespeichert, aber der Druck ist fehlgeschlagen: Papier leer",
            h.Controller.ErrorMessage);
        Assert.Single(h.Storage.Saved);

        h.RunToIdle();
        Assert.Contains(BoothState.Error, states);
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void PrintThrows_IsReportedAsError_ThenIdle()
    {
        var h = new Harness();
        h.Printer.ThrowOnPrint = true;

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);

        Assert.Contains("fehlgeschlagen", h.Controller.ErrorMessage);
        h.RunToIdle();
    }

    [Fact]
    public void PrintNeverReturns_HardTimeoutEndsInErrorThenIdle()
    {
        var h = new Harness();
        h.Settings.Current.Printer.TimeoutSeconds = 10;

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error, maxSeconds: 200);

        Assert.StartsWith("Das Foto wurde gespeichert, aber der Druck ist fehlgeschlagen", h.Controller.ErrorMessage);
        h.RunToIdle();
    }

    [Fact]
    public void SaveFailure_ShowsErrorWithoutPrinting_ThenIdle()
    {
        var h = new Harness();
        h.Storage.Failure = new IOException("Datenträger voll");
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);

        Assert.False(string.IsNullOrWhiteSpace(h.Controller.ErrorMessage));
        Assert.Equal(0, h.Printer.PrintCalls);

        h.RunToIdle();
        Assert.Equal(BoothState.Idle, h.Controller.State);
        Assert.DoesNotContain(BoothState.Preview, states);
    }

    [Fact]
    public void SaveNeverReturns_TimeoutShowsErrorWithoutPrinting_ThenIdle()
    {
        var h = new Harness();
        h.Storage.Hang = true;
        var states = h.RecordStates();
        var start = h.Time.GetUtcNow();

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);

        Assert.True(h.Time.GetUtcNow() - start >= TimeSpan.FromSeconds(30), "Zeitlimit zu früh ausgelöst.");
        Assert.Equal(
            "Das Foto konnte nicht gespeichert werden. Bitte wende dich an das Personal.",
            h.Controller.ErrorMessage);
        Assert.Null(h.Controller.LastSaved);
        Assert.Equal(0, h.Printer.PrintCalls);

        h.RunToIdle();
        Assert.Equal(BoothState.Idle, h.Controller.State);
        Assert.DoesNotContain(BoothState.Preview, states);
    }

    [Fact]
    public void SaveNeverReturns_TimeoutCancelsTheSave()
    {
        var h = new Harness();
        h.Storage.Hang = true;

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);

        Assert.True(h.Storage.SaveCancelled, "Das Speichern muss bei Zeitüberschreitung über das Token abgebrochen werden.");
        h.RunToIdle();
    }

    [Fact]
    public void SaveFinishesLateAfterTimeout_StillEndsInErrorThenIdle()
    {
        var h = new Harness();
        h.Storage.Hang = true;

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);
        h.Storage.HangSource.TrySetResult(new SavedPhotoSet("spaet/layout.jpg", []));   // wird nur geloggt
        h.Drain();

        Assert.Null(h.Controller.LastSaved);
        Assert.Equal(BoothState.Error, h.Controller.State);
        h.RunToIdle();
        Assert.Equal(0, h.Printer.PrintCalls);
    }

    [Fact]
    public void SaveCompletes_DoesNotCancelTheSaveToken()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();

        Assert.False(h.Storage.SaveCancelled);
    }

    [Fact]
    public void LatePrintStatus_AfterHardTimeout_DoesNotOverwriteNextSessionStatus()
    {
        var h = new Harness();
        h.Settings.Current.Printer.TimeoutSeconds = 10;

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error, maxSeconds: 200);
        var lateStatus = h.Printer.Status!;
        h.RunToIdle();

        // Neuer Durchgang; der verwaiste Druck meldet jetzt noch etwas.
        Assert.True(h.Controller.TryTrigger());
        lateStatus.Report("Verspätete Meldung");
        h.Drain();

        Assert.Null(h.Controller.StatusMessage);
        h.Controller.Cancel();
        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
    }

    [Fact]
    public void PageSpecHangs_TwoSessionsInARow_StartOnlyOneQuery()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;
        h.Printer.PageSpecHang = true;
        try
        {
            h.Controller.TryTrigger();
            h.RunToIdle();
            Assert.Same(PageSpec.Default, h.Composer.LastPage);
            Assert.Equal(1, h.Printer.PageSpecCalls);

            // Die erste Abfrage hängt noch: der nächste Gast wartet auf sie, statt einen weiteren Thread zu blockieren.
            h.Controller.TryTrigger();
            h.RunToIdle();

            Assert.Same(PageSpec.Default, h.Composer.LastPage);
            Assert.Equal(1, h.Printer.PageSpecCalls);
            Assert.Equal(2, h.Storage.Saved.Count);
        }
        finally
        {
            h.Printer.ReleasePageSpec();
        }
    }

    [Fact]
    public void PageSpecPrefetchRunning_SessionWaitsForItInsteadOfStartingAnotherQuery()
    {
        var h = new Harness(markReady: false, prefetch: true);
        h.Settings.Current.Flow.PrintingEnabled = false;
        h.Printer.PageSpecHang = true;
        try
        {
            h.Controller.MarkReady();
            h.WaitUntil(() => h.Printer.IsParked);

            h.Controller.TryTrigger();
            h.RunToIdle();

            Assert.Same(PageSpec.Default, h.Composer.LastPage);   // Wartezeit abgelaufen → Rückfall
            Assert.Equal(1, h.Printer.PageSpecCalls);
        }
        finally
        {
            h.Printer.ReleasePageSpec();
        }
    }

    [Fact]
    public void PageSpecPrefetchRunning_SessionUsesItsResultWhenItFinishesInTime()
    {
        var h = new Harness(markReady: false, prefetch: true);
        h.Settings.Current.Flow.PrintingEnabled = false;
        h.Printer.PageSpecHang = true;
        try
        {
            h.Controller.MarkReady();
            h.WaitUntil(() => h.Printer.IsParked);

            h.Controller.TryTrigger();
            h.AdvanceUntil(() => h.Controller.State == BoothState.Capturing);
            h.Printer.ReleasePageSpec();   // Vorabruf wird fertig, bevor die 5 s abgelaufen sind (Fake-Zeit steht still)
            h.WaitUntil(() => h.Controller.State == BoothState.Preview);
            h.RunToIdle();

            Assert.Same(FakePrinter.Spec, h.Composer.LastPage);
            Assert.Equal(1, h.Printer.PageSpecCalls);
        }
        finally
        {
            h.Printer.ReleasePageSpec();
        }
    }

    [Fact]
    public void PageSpecNeverReturns_FallsBackToDefault_AndSessionReachesIdle()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;
        h.Printer.PageSpecHang = true;
        var states = h.RecordStates();
        var start = h.Time.GetUtcNow();
        try
        {
            h.Controller.TryTrigger();
            h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);

            Assert.True(h.Time.GetUtcNow() - start >= TimeSpan.FromSeconds(5), "Zeitlimit zu früh ausgelöst.");
            Assert.Same(PageSpec.Default, h.Composer.LastPage);

            h.RunToIdle();
            Assert.DoesNotContain(BoothState.Error, states);
            Assert.Single(h.Storage.Saved);
        }
        finally
        {
            h.Printer.ReleasePageSpec();
        }
    }

    [Fact]
    public void PageSpecThrows_FallsBackToDefault_AndSessionReachesIdle()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;
        h.Printer.PageSpecFailure = new InvalidOperationException("Druckserver nicht erreichbar");
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.RunToIdle();

        Assert.Same(PageSpec.Default, h.Composer.LastPage);
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.Single(h.Storage.Saved);
    }

    [Fact]
    public void PageSpec_FromPrinterIsUsedForLayout()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();

        Assert.Same(FakePrinter.Spec, h.Composer.LastPage);
    }

    [Fact]
    public void CaptureFailure_ShowsError_ThenIdle()
    {
        var h = new Harness();
        h.Camera.CaptureFailure = new InvalidOperationException("Kamera getrennt");

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);

        Assert.False(string.IsNullOrWhiteSpace(h.Controller.ErrorMessage));
        Assert.Empty(h.Storage.Saved);

        h.RunToIdle();
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void ComposeFailure_ShowsError_ThenIdle()
    {
        var h = new Harness();
        h.Composer.Failure = new InvalidOperationException("Overlay fehlt");

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Error);
        h.RunToIdle();

        Assert.Empty(h.Storage.Saved);
        Assert.Equal(BoothState.Idle, h.Controller.State);
    }

    [Fact]
    public void AfterSession_NewSessionCanBeStarted()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();
        Assert.Equal(BoothState.Idle, h.Controller.State);

        Assert.True(h.Controller.TryTrigger());
        h.RunToIdle();
        Assert.Equal(2, h.Storage.Saved.Count);
    }

    [Fact]
    public void Cancel_DuringCountdown_ReturnsToIdleWithoutError()
    {
        var h = new Harness();
        h.Controller.TryTrigger();
        h.Time.Advance(Second);

        h.Controller.Cancel();

        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.Equal(0, h.Camera.CaptureCount);
    }

    [Fact]
    public void TryCancelCountdown_DuringCountdown_ReturnsToIdleWithoutPhotoOrError()
    {
        var h = new Harness();
        var states = h.RecordStates();
        h.Controller.TryTrigger();
        h.Time.Advance(Second);

        Assert.True(h.Controller.TryCancelCountdown());

        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.Equal(0, h.Controller.CountdownValue);
        Assert.Equal(0, h.Camera.CaptureCount);
        Assert.Empty(h.Storage.Saved);
        Assert.Equal(0, h.Printer.PrintCalls);
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.True(h.Controller.TryTrigger());   // danach ist ein neuer Durchgang möglich
    }

    [Fact]
    public void TryCancelCountdown_InPauseBetweenStripPhotos_DiscardsPhotos()
    {
        var h = new Harness();
        h.Settings.Current.Layout.Kind = LayoutKind.Strip;
        h.Composer.RequiredPhotoCount = 3;
        h.Settings.Current.Flow.PauseBetweenShotsSeconds = 5;
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Camera.CaptureCount == 1 && h.Controller.State == BoothState.Countdown);
        Assert.Equal(0, h.Controller.CountdownValue);   // Pause zwischen zwei Fotos

        Assert.True(h.Controller.TryCancelCountdown());

        h.WaitUntil(() => h.Controller.State == BoothState.Idle);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.Equal(1, h.Camera.CaptureCount);
        Assert.Empty(h.Storage.Saved);
        Assert.Null(h.Composer.LastPhotos);
    }

    [Fact]
    public void TryCancelCountdown_OutsideCountdown_IsIgnored()
    {
        var h = new Harness();
        Assert.False(h.Controller.TryCancelCountdown());   // Idle

        h.Settings.Current.Flow.PrintingEnabled = false;
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);

        Assert.False(h.Controller.TryCancelCountdown());   // Vorschau
        Assert.Equal(BoothState.Preview, h.Controller.State);
        h.RunToIdle();
        Assert.Single(h.Storage.Saved);
    }

    [Fact]
    public void TryCancelCountdown_WhilePrinting_IsIgnored()
    {
        var h = new Harness();
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);

        Assert.False(h.Controller.TryCancelCountdown());

        Assert.Equal(BoothState.Printing, h.Controller.State);
        h.Printer.Complete(new PrintResult(PrintOutcome.Completed));
        h.RunToIdle();
    }

    [Fact]
    public void PageSpecTimeout_UsesLastGoodValueInsteadOfDefault()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();
        Assert.Same(FakePrinter.Spec, h.Composer.LastPage);   // erster Durchgang: ermittelt und gemerkt

        h.Printer.PageSpecHang = true;
        try
        {
            h.Controller.TryTrigger();
            h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);

            Assert.Same(FakePrinter.Spec, h.Composer.LastPage);
            Assert.NotSame(PageSpec.Default, h.Composer.LastPage);
            h.RunToIdle();
        }
        finally
        {
            h.Printer.ReleasePageSpec();
        }
    }

    [Fact]
    public void PageSpecTimeout_AfterPrinterSettingsChanged_FallsBackToDefault()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;
        h.Controller.TryTrigger();
        h.RunToIdle();

        // Anderer Drucker = anderer Schlüssel: der gemerkte Wert des alten Druckers passt nicht.
        h.Settings.Current.Printer.PrinterName = "Anderer Drucker";
        h.Printer.PageSpecHang = true;
        try
        {
            h.Controller.TryTrigger();
            h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);

            Assert.Same(PageSpec.Default, h.Composer.LastPage);
            h.RunToIdle();
        }
        finally
        {
            h.Printer.ReleasePageSpec();
        }
    }

    [Fact]
    public void MarkReady_PrefetchesPageSpecInBackground()
    {
        var h = new Harness(prefetch: true);

        h.WaitUntil(() => h.Printer.PageSpecCalls >= 1);
    }

    [Fact]
    public void SettingsChanged_PrefetchesPageSpecAgain()
    {
        var h = new Harness(prefetch: true);
        h.WaitUntil(() => h.Printer.PageSpecCalls >= 1);

        // Läuft noch ein Vorabruf, wird kein zweiter gestapelt – daher wiederholt melden.
        h.WaitUntil(() =>
        {
            h.Settings.RaiseSettingsChanged();
            return h.Printer.PageSpecCalls >= 2;
        });
    }

    [Fact]
    public void TryTrigger_PrefetchesPageSpecForCurrentSettingsDuringCountdown()
    {
        var h = new Harness(prefetch: true);
        h.WaitUntil(() => h.Printer.PageSpecCalls >= 1);
        h.Settings.Current.Printer.PrinterName = "Anderer Drucker";   // neuer Schlüssel, nichts läuft dafür

        Assert.True(h.Controller.TryTrigger());

        h.WaitUntil(() => h.Printer.PageSpecCalls >= 2);
        Assert.Equal(BoothState.Countdown, h.Controller.State);   // noch vor der Komposition
        Assert.Contains("Anderer Drucker", h.Printer.PageSpecPrinters);
    }

    [Fact]
    public void Prefetch_Failure_DoesNotBreakSession()
    {
        var h = new Harness(prefetch: true);
        h.Printer.PageSpecFailure = new InvalidOperationException("Druckserver nicht erreichbar");
        h.Settings.Current.Flow.PrintingEnabled = false;
        h.WaitUntil(() => h.Printer.PageSpecCalls >= 1);
        var states = h.RecordStates();

        h.Controller.TryTrigger();
        h.RunToIdle();

        // Je nach Reihenfolge hat der erste Vorabruf noch Erfolg gehabt (dann gilt dessen Wert), sonst Standard.
        Assert.NotNull(h.Composer.LastPage);
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.Single(h.Storage.Saved);
    }

    [Fact]
    public void Default_UsesSettingsLayout_Single()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        Assert.Equal(GuestLayout.Single, GuestLayouts.From(h.Settings.Current.Layout));
        h.Controller.TryTrigger();
        h.RunToIdle();

        Assert.Equal(LayoutKind.SingleBorderless, h.Composer.LastLayout!.Kind);
        Assert.Equal(1, h.Camera.CaptureCount);
    }

    [Fact]
    public void TryTrigger_WithGuestLayoutStripSingle_UsesStripWithoutChangingSettings()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;
        h.Settings.Current.Layout.StripPhotoCount = 3;
        h.Composer.RequiredPhotoCount = 3;
        h.Composer.CountFor = l => l.Kind == LayoutKind.Strip ? l.StripPhotoCount : 1;
        var settingsLayout = h.Settings.Current.Layout;

        Assert.True(h.Controller.TryTrigger(GuestLayout.StripSingle));
        Assert.Equal(3, h.Controller.ShotCount);
        h.RunToIdle();

        Assert.Equal(3, h.Camera.CaptureCount);
        Assert.Equal(LayoutKind.Strip, h.Composer.LastLayout!.Kind);
        Assert.Equal(StripStyle.Single, h.Composer.LastLayout.StripStyle);
        Assert.NotSame(settingsLayout, h.Composer.LastLayout);
        Assert.Same(settingsLayout, h.Settings.Current.Layout);
        Assert.Equal(LayoutKind.SingleBorderless, h.Settings.Current.Layout.Kind);
        Assert.Equal(StripStyle.DoubleCut, h.Settings.Current.Layout.StripStyle);
    }

    [Fact]
    public void TryTrigger_WithGuestLayoutEqualToDefault_UsesSettingsUnchanged()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger(GuestLayout.Single);
        h.RunToIdle();

        Assert.Same(h.Settings.Current.Layout, h.Composer.LastLayout);
    }

    [Fact]
    public void Prefetch_CoversLayoutKindsOfGuestChoices()
    {
        var h = new Harness(markReady: false, prefetch: true);
        h.Settings.Current.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble, GuestLayout.StripSingle];

        h.Controller.MarkReady();

        h.WaitUntil(() => h.Printer.PageSpecKinds.Contains(LayoutKind.Strip)
            && h.Printer.PageSpecKinds.Contains(LayoutKind.SingleBorderless));
        Assert.DoesNotContain(LayoutKind.Overlay, h.Printer.PageSpecKinds);
    }

    [Fact]
    public void MirrorPhoto_FlipsPhotoHorizontally()
    {
        var h = new Harness();
        h.Settings.Current.Camera.MirrorPhoto = true;
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();

        var photo = Assert.Single(h.Composer.LastPhotos!);
        Assert.True(photo.IsFrozen);
        Assert.Equal(h.Camera.Photo.PixelWidth, photo.PixelWidth);
        // Kamerabild: links Rot, rechts Blau → nach dem Spiegeln umgekehrt.
        Assert.Equal(FakeCamera.Blue, ReadPixel(photo, 0));
        Assert.Equal(FakeCamera.Red, ReadPixel(photo, 1));
    }

    [Fact]
    public void MirrorPhotoOff_KeepsPhotoUntouched()
    {
        var h = new Harness();
        h.Settings.Current.Flow.PrintingEnabled = false;

        h.Controller.TryTrigger();
        h.RunToIdle();

        var photo = Assert.Single(h.Composer.LastPhotos!);
        Assert.Same(h.Camera.Photo, photo);
    }

    // ---- Hilfsmittel ----

    private static uint ReadPixel(BitmapSource source, int x)
    {
        var pixels = new uint[1];
        source.CopyPixels(new System.Windows.Int32Rect(x, 0, 1, 1), pixels, 4, 0);
        return pixels[0];
    }

    /// <summary>
    /// Ersatz für den UI-Thread: Fortsetzungen, die von anderen Threads kommen (Task.Run, Progress), landen in
    /// einer Warteschlange und laufen erst, wenn der Test sie auf seinem Thread abarbeitet. Fortsetzungen, die
    /// der Test-Thread selbst auslöst (Fake-Zeit, Complete, Cancel), laufen wie beim Dispatcher direkt inline.
    /// </summary>
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public override SynchronizationContext CreateCopy() => this;

        /// <summary>Führt eine wartende Fortsetzung aus (bei Bedarf bis zum Zeitlimit warten).</summary>
        public bool RunOne(TimeSpan wait)
        {
            if (!_queue.TryTake(out var item, wait))
                return false;
            item.Callback(item.State);
            return true;
        }
    }

    private sealed class Harness
    {
        private readonly PumpContext _pump = new();

        // Nach der letzten Aufnahme holt der Controller die Seitengröße per Task.Run; seine Fortsetzung
        // landet danach im Pump-Kontext. Bis dahin darf die Fake-Zeit nicht weiterlaufen, sonst hinge der
        // Testverlauf vom Thread-Scheduling ab.
        private volatile bool _hopPending;

        public FakeTimeProvider Time { get; } = new();
        public FakeCamera Camera { get; } = new();
        public FakeComposer Composer { get; } = new();
        public FakePrinter Printer { get; } = new();
        public FakeStorage Storage { get; } = new();
        public FakeSettingsStore Settings { get; } = new();
        public BoothController Controller { get; }

        /// <param name="prefetch">
        /// Vorabruf der Seitengröße im Hintergrund. Aus, damit der Task.Run-Sprung genau einmal pro Durchgang
        /// stattfindet (siehe <c>_hopPending</c>); die Tests zum Vorabruf schalten ihn ein.
        /// </param>
        public Harness(bool markReady = true, bool prefetch = false)
        {
            // Eigener Kontext statt dem von xunit: Der Test-Thread ist der "UI-Thread" des Controllers und
            // führt dessen Fortsetzungen deterministisch aus (siehe PumpContext).
            SynchronizationContext.SetSynchronizationContext(_pump);
            Settings.Current.Flow.CountdownSeconds = 3;
            Settings.Current.Flow.PreviewSeconds = 2;
            Settings.Current.Flow.PauseBetweenShotsSeconds = 1;
            Settings.Current.Flow.ErrorDisplaySeconds = 4;
            Controller = new BoothController(Camera, Composer, Printer, Storage, Settings, Time) { PrefetchPageSpec = prefetch };
            // Hängt bereits eine Abfrage (wird wiederverwendet), gibt es keinen Task.Run-Sprung des Durchgangs mehr.
            Camera.Captured = () => _hopPending = Camera.CaptureCount % Composer.RequiredPhotoCount == 0 && !Printer.IsParked;
            Composer.Composing = () => _hopPending = false; // Der Sprung ist beendet, sobald der Controller zusammensetzt.
            Printer.Parked = () => _hopPending = false; // hängende Abfrage: es folgt keine Fortsetzung
            Controller.StateChanged += (_, _) =>
            {
                if (Controller.State is BoothState.Error or BoothState.Idle)
                    _hopPending = false;
            };
            if (markReady)
                Controller.MarkReady();
        }

        public List<BoothState> RecordStates()
        {
            var states = new List<BoothState>();
            Controller.StateChanged += (_, _) => states.Add(Controller.State);
            return states;
        }

        /// <summary>Lässt die Fake-Zeit sekundenweise laufen, bis die Bedingung gilt.</summary>
        public void AdvanceUntil(Func<bool> condition, int maxSeconds = 100)
        {
            for (var i = 0; i < maxSeconds && !condition(); i++)
            {
                Time.Advance(Second);
                Settle();
            }
            Assert.True(condition(), "Bedingung wurde in der Fake-Zeit nicht erreicht.");
        }

        /// <summary>Lässt die Fake-Zeit in kleinen Schritten um <paramref name="total"/> weiterlaufen (Zeitgeber feuern je Schritt einmal).</summary>
        public void AdvanceBy(TimeSpan total, TimeSpan step)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
            {
                Time.Advance(step);
                Settle();
            }
        }

        /// <summary>Wartet in echter Zeit (Fortsetzungen werden dabei ausgeführt), bis die Bedingung gilt.</summary>
        public void WaitUntil(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(30))
                _pump.RunOne(TimeSpan.FromMilliseconds(10));
            Assert.True(condition(), "Bedingung wurde nicht rechtzeitig erfüllt.");
        }

        /// <summary>Arbeitet eingegangene Fortsetzungen ab; wartet, solange der Task.Run-Sprung aussteht.</summary>
        private void Settle()
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (_pump.RunOne(TimeSpan.Zero))
                    continue;
                if (!_hopPending)
                    return;
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "Threadpool-Fortsetzung des Controllers blieb aus.");
                _pump.RunOne(TimeSpan.FromMilliseconds(10));
            }
        }

        /// <summary>Arbeitet alle bereits eingegangenen Fortsetzungen ab (ohne zu warten).</summary>
        public void Drain()
        {
            while (_pump.RunOne(TimeSpan.Zero)) { }
        }

        public void RunToIdle() =>
            AdvanceUntil(() => Controller.State == BoothState.Idle, maxSeconds: 400);
    }

    private sealed class FakeCamera : ICameraService
    {
        public const uint Red = 0xFFFF0000;
        public const uint Blue = 0xFF0000FF;

        public BitmapSource Photo { get; } = CreatePhoto();
        public int CaptureCount { get; private set; }
        public Exception? CaptureFailure { get; set; }
        /// <summary>Wird synchron nach jeder erfolgreichen Aufnahme aufgerufen.</summary>
        public Action? Captured { get; set; }

        public CameraStatus Status { get; set; } = CameraStatus.Running;
        public string? StatusMessage => null;
        public CameraResolution? ActiveResolution => new(2, 1);
        public event EventHandler? StatusChanged { add { } remove { } }
        public event EventHandler<CameraFrame>? FrameArrived { add { } remove { } }

        public Task<IReadOnlyList<CameraDevice>> GetDevicesAsync() =>
            Task.FromResult<IReadOnlyList<CameraDevice>>([]);

        public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) =>
            Task.FromResult<IReadOnlyList<CameraResolution>>([]);

        public Task StartAsync(CameraSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;

        public Task<BitmapSource> CapturePhotoAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CaptureCount++;
            if (CaptureFailure is not null)
                return Task.FromException<BitmapSource>(CaptureFailure);
            Captured?.Invoke();
            return Task.FromResult(Photo);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>2 × 1 Pixel: links Rot, rechts Blau.</summary>
        private static BitmapSource CreatePhoto()
        {
            uint[] pixels = [Red, Blue];
            var bmp = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            bmp.Freeze();
            return bmp;
        }
    }

    private sealed class FakeComposer : ILayoutComposer
    {
        public int RequiredPhotoCount { get; set; } = 1;
        public Exception? Failure { get; set; }
        public IReadOnlyList<BitmapSource>? LastPhotos { get; private set; }
        public PageSpec? LastPage { get; private set; }
        /// <summary>Layout des letzten Compose-Aufrufs (zum Prüfen, womit der Controller gearbeitet hat).</summary>
        public LayoutSettings? LastLayout { get; private set; }
        /// <summary>Optional: Fotozahl abhängig vom Layout (sonst <see cref="RequiredPhotoCount"/>).</summary>
        public Func<LayoutSettings, int>? CountFor { get; set; }
        /// <summary>Wird bei jedem Compose-Aufruf zuerst aufgerufen.</summary>
        public Action? Composing { get; set; }
        public BitmapSource Result { get; } = CreateResult();

        public int GetRequiredPhotoCount(LayoutSettings layout) => CountFor?.Invoke(layout) ?? RequiredPhotoCount;

        public BitmapSource Compose(IReadOnlyList<BitmapSource> photos, LayoutSettings layout, PageSpec page,
            string overlayDirectory, DateTime timestamp)
        {
            Composing?.Invoke();
            if (Failure is not null) throw Failure;
            LastPhotos = photos.ToList();
            LastPage = page;
            LastLayout = layout;
            return Result;
        }

        private static BitmapSource CreateResult()
        {
            var bmp = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            bmp.Freeze();
            return bmp;
        }
    }

    private sealed class FakePrinter : IPrintService
    {
        // Absichtlich ohne RunContinuationsAsynchronously: Complete() setzt die Fortsetzungen des Controllers
        // synchron fort, sodass der Druck beim Rückgabewert von Complete() sicher als beendet gilt.
        private readonly TaskCompletionSource<PrintResult> _result = new();
        private readonly ManualResetEventSlim _pageSpecGate = new(false);

        /// <summary>Von GetPageSpec geliefert; bewusst ungleich <see cref="PageSpec.Default"/>.</summary>
        public static PageSpec Spec { get; } = new(148, 100);

        public bool PageSpecHang { get; set; }
        public Exception? PageSpecFailure { get; set; }
        /// <summary>Wird aufgerufen, sobald GetPageSpec absichtlich hängt.</summary>
        public Action? Parked { get; set; }

        public void ReleasePageSpec() => _pageSpecGate.Set();

        private volatile bool _parked;
        /// <summary>true, solange ein GetPageSpec-Aufruf absichtlich hängt.</summary>
        public bool IsParked => _parked && !_pageSpecGate.IsSet;

        public int PrintCalls { get; private set; }
        public bool ThrowOnPrint { get; set; }
        public BitmapSource? LastPage { get; private set; }
        public IProgress<string>? Status { get; private set; }

        public void Complete(PrintResult result) => _result.TrySetResult(result);

        public IReadOnlyList<PrinterInfo> GetPrinters() => [];
        public IReadOnlyList<PaperSizeInfo> GetPaperSizes(string? printerName) => [];
        private int _pageSpecCalls;
        private readonly ConcurrentQueue<string?> _pageSpecPrinters = new();
        private readonly ConcurrentQueue<LayoutKind> _pageSpecKinds = new();

        /// <summary>Anzahl der GetPageSpec-Aufrufe (auch aus dem Vorabruf, daher thread-sicher).</summary>
        public int PageSpecCalls => Volatile.Read(ref _pageSpecCalls);
        public IReadOnlyCollection<string?> PageSpecPrinters => _pageSpecPrinters;
        public IReadOnlyCollection<LayoutKind> PageSpecKinds => _pageSpecKinds;

        public PageSpec GetPageSpec(PrinterSettings printer, LayoutKind layout)
        {
            _pageSpecPrinters.Enqueue(printer.PrinterName);
            _pageSpecKinds.Enqueue(layout);
            Interlocked.Increment(ref _pageSpecCalls);
            if (PageSpecFailure is not null) throw PageSpecFailure;
            if (PageSpecHang)
            {
                _parked = true;
                Parked?.Invoke();
                _pageSpecGate.Wait(TimeSpan.FromSeconds(60)); // Obergrenze, damit nie ein Thread dauerhaft hängt
            }
            return Spec;
        }

        public Task<PrintResult> PrintAsync(BitmapSource page, PrinterSettings printer,
            IProgress<string>? status = null, CancellationToken ct = default)
        {
            PrintCalls++;
            LastPage = page;
            Status = status;
            if (ThrowOnPrint) throw new InvalidOperationException("Drucker nicht erreichbar");
            return _result.Task;
        }
    }

    private sealed class FakeStorage : IPhotoStorage
    {
        public List<(BitmapSource Layout, IReadOnlyList<BitmapSource> Originals)> Saved { get; } = [];
        public Exception? Failure { get; set; }
        /// <summary>SaveAsync liefert eine Aufgabe, die nie endet (hängende Netzwerkfreigabe).</summary>
        public bool Hang { get; set; }
        /// <summary>true, sobald das an SaveAsync übergebene Token abgebrochen wurde.</summary>
        public bool SaveCancelled => _cancelled;
        private volatile bool _cancelled;
        /// <summary>Ergebnis des hängenden Speicherns; ein Test kann es "spät" fertigstellen.</summary>
        public TaskCompletionSource<SavedPhotoSet> HangSource { get; } = new();

        public string ResolveTargetFolder(StorageSettings storage, DateTime timestamp) => "fake";

        public Task<SavedPhotoSet> SaveAsync(BitmapSource layout, IReadOnlyList<BitmapSource> originals,
            StorageSettings storage, DateTime timestamp, CancellationToken ct = default)
        {
            if (Failure is not null) return Task.FromException<SavedPhotoSet>(Failure);
            ct.Register(() => _cancelled = true);
            if (Hang) return HangSource.Task;
            Saved.Add((layout, originals.ToList()));
            return Task.FromResult(new SavedPhotoSet("fake/layout.jpg", ["fake/orig1.jpg"]));
        }
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public string ConfigDirectory => "fake";
        public string OverlayDirectory => "fake/overlays";
        public AppSettings Current { get; } = new();
        public event EventHandler? SettingsChanged;
        public void Save(AppSettings settings) { }
        public void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
