using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Druckersperre (<see cref="BoothState.PrinterBlocked"/>) und Ladekreis des <see cref="BoothController"/> (teilt Harness und Fakes mit den Foto-Tests).</summary>
public partial class BoothControllerTests
{
    private static Harness BlockHarness()
    {
        var h = new Harness();
        h.Settings.Current.Flow.ExpectedPrintSeconds = 60;
        h.Settings.Current.Printer.TimeoutSeconds = 300;
        h.Settings.Current.Video.Enabled = true;
        return h;
    }

    private static Harness BlockedHarness(out List<BoothState> states, PrinterAttention reason = PrinterAttention.PaperOut)
    {
        var h = BlockHarness();
        states = h.RecordStates();
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);
        h.Printer.ReportBlock(reason);
        h.Drain();
        return h;
    }

    [Fact]
    public void BlockReport_DuringPrinting_SwitchesToPrinterBlocked_WithReason()
    {
        var h = BlockedHarness(out var states, PrinterAttention.PaperJam);

        Assert.Equal(BoothState.PrinterBlocked, h.Controller.State);
        Assert.Equal(PrinterAttention.PaperJam, h.Controller.BlockedReason);
        Assert.Equal(
            [BoothState.Countdown, BoothState.Capturing, BoothState.Preview, BoothState.Printing, BoothState.PrinterBlocked],
            states);
        Assert.Null(h.Controller.ErrorMessage);
    }

    [Fact]
    public void BlockReport_DuringPreview_EndsPreviewAtOnce()
    {
        var h = BlockHarness();
        h.Settings.Current.Flow.PreviewSeconds = 20;
        var states = h.RecordStates();
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        var previewStart = h.Time.GetUtcNow();

        h.Printer.ReportBlock(PrinterAttention.PaperOut);
        h.Drain();

        // Ohne dass Zeit vergeht: Die Vorschau (20 s) endet sofort, es geht direkt in die Sperre (ohne Printing).
        Assert.Equal(BoothState.PrinterBlocked, h.Controller.State);
        Assert.Equal(previewStart, h.Time.GetUtcNow());
        Assert.Equal([BoothState.Countdown, BoothState.Capturing, BoothState.Preview, BoothState.PrinterBlocked], states);
        Assert.Equal(PrinterAttention.PaperOut, h.Controller.BlockedReason);
    }

    [Fact]
    public void BlockReport_AfterPrintFinished_IsIgnored()
    {
        var h = BlockHarness();
        h.Settings.Current.Flow.PreviewSeconds = 20;
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        h.Printer.Complete(new PrintResult(PrintOutcome.Completed));
        h.Drain();

        h.Printer.ReportBlock(PrinterAttention.PaperOut);   // zu spät: der Druck ist schon fertig
        h.Drain();

        Assert.Equal(BoothState.Preview, h.Controller.State);
    }

    [Fact]
    public void ClearedReport_InBlock_KeepsBlockedState_WithNoReason()
    {
        var h = BlockedHarness(out _);

        h.Printer.ReportBlock(PrinterAttention.None);   // Papier nachgelegt, der Auftrag druckt noch
        h.Drain();

        Assert.Equal(BoothState.PrinterBlocked, h.Controller.State);
        Assert.Equal(PrinterAttention.None, h.Controller.BlockedReason);

        h.Printer.ReportBlock(PrinterAttention.Offline);   // das nächste Problem
        h.Drain();
        Assert.Equal(BoothState.PrinterBlocked, h.Controller.State);
        Assert.Equal(PrinterAttention.Offline, h.Controller.BlockedReason);
    }

    [Fact]
    public void ClearedReport_WithoutBlock_IsIgnored()
    {
        var h = BlockHarness();
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);

        h.Printer.ReportBlock(PrinterAttention.None);
        h.Drain();

        Assert.Equal(BoothState.Printing, h.Controller.State);
    }

    [Fact]
    public void Blocked_NoHardLimit_PrintCompletesLater_ShowsPrinted_ThenIdle()
    {
        var h = BlockedHarness(out var states);

        // Weit über Druck-Timeout (300 s) + Reserve (30 s): Die Sperre wartet ohne eigene Frist.
        h.AdvanceBy(TimeSpan.FromSeconds(400), Second);
        Assert.Equal(BoothState.PrinterBlocked, h.Controller.State);
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.False(h.Printer.LastServiceToken.IsCancellationRequested);

        h.Printer.Complete(new PrintResult(PrintOutcome.Completed));   // Papier nachgelegt, der Auftrag druckt
        h.WaitUntil(() => h.Controller.State == BoothState.Printed);
        h.RunToIdle();

        Assert.Equal(
            [BoothState.Countdown, BoothState.Capturing, BoothState.Preview, BoothState.Printing, BoothState.PrinterBlocked,
                BoothState.Printed, BoothState.Idle],
            states);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.Equal(PrinterAttention.None, h.Controller.BlockedReason);
    }

    [Fact]
    public void HardLimit_WithoutBlock_StillEndsWithTimeoutError()
    {
        var h = BlockHarness();
        var states = h.RecordStates();
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);

        h.AdvanceBy(TimeSpan.FromSeconds(331), Second);
        h.WaitUntil(() => h.Controller.State == BoothState.Error);

        Assert.Contains("Zeitüberschreitung", h.Controller.ErrorMessage);
        Assert.DoesNotContain(BoothState.PrinterBlocked, states);
        h.RunToIdle();
    }

    [Fact]
    public void Blocked_FailedResult_ShowsErrorAsBefore()
    {
        var h = BlockedHarness(out var states);

        h.Printer.Complete(new PrintResult(PrintOutcome.Failed, "Der Druckauftrag wurde abgebrochen."));   // Löschen gemeldet
        h.WaitUntil(() => h.Controller.State == BoothState.Error);

        Assert.Contains("Der Druckauftrag wurde abgebrochen.", h.Controller.ErrorMessage);
        h.RunToIdle();
        Assert.DoesNotContain(BoothState.Printed, states);
    }

    [Fact]
    public void ReleasePrinterBlock_GoesToIdleWithoutError_AndKeepsJob()
    {
        var h = BlockedHarness(out var states);

        Assert.True(h.Controller.ReleasePrinterBlock());
        h.WaitUntil(() => h.Controller.State == BoothState.Idle);

        Assert.Equal(
            [BoothState.Countdown, BoothState.Capturing, BoothState.Preview, BoothState.Printing, BoothState.PrinterBlocked, BoothState.Idle],
            states);
        Assert.Null(h.Controller.ErrorMessage);
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.DoesNotContain(BoothState.Printed, states);
        Assert.DoesNotContain(BoothState.PrintCancelled, states);
        Assert.Equal(0, h.Printer.CancelRequests);   // der Gastenabbruch (Auftrag stornieren) wurde nie ausgelöst
        Assert.True(h.Printer.LastServiceToken.IsCancellationRequested);   // der Dienst wird nur nicht mehr abgewartet
        Assert.Equal(PrinterAttention.None, h.Controller.BlockedReason);
        Assert.True(h.Controller.TryTrigger());   // danach ist ein neuer Durchgang möglich
    }

    [Fact]
    public void ReleasePrinterBlock_OutsideTheBlock_IsIgnored()
    {
        var h = BlockHarness();
        Assert.False(h.Controller.ReleasePrinterBlock());   // Idle

        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);
        Assert.False(h.Controller.ReleasePrinterBlock());   // Printing ohne Sperre
        Assert.Equal(BoothState.Printing, h.Controller.State);
    }

    [Fact]
    public void Blocked_EscapeAndNewSessions_AreIneffective()
    {
        var h = BlockedHarness(out _);
        h.Printer.UserCancelResult = new PrintResult(PrintOutcome.UserCancelled);

        Assert.False(h.Controller.TryCancelPrint());
        Assert.False(h.Controller.TryCancel());
        Assert.False(h.Controller.TryTrigger());
        Assert.False(h.Controller.TryStartVideo());
        Assert.False(h.Controller.TryToggleVideo());
        h.Drain();

        Assert.Equal(BoothState.PrinterBlocked, h.Controller.State);
        Assert.Equal(0, h.Printer.CancelRequests);
        Assert.False(h.Controller.PrintCancelRequested);
    }

    [Fact]
    public void BlockReport_DuringPreview_MakesEscapeIneffectiveAtOnce()
    {
        var h = BlockHarness();
        h.Settings.Current.Flow.PreviewSeconds = 20;
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);

        h.Printer.ReportBlock(PrinterAttention.PaperOut);   // Flag sofort gesetzt, der Zustandswechsel folgt über den Kontext

        Assert.False(h.Controller.TryCancelPrint());
        Assert.Equal(0, h.Printer.CancelRequests);
        h.Drain();
        Assert.Equal(BoothState.PrinterBlocked, h.Controller.State);
    }

    [Fact]
    public void PrintOptions_AlwaysCarryBlockHandler()
    {
        var h = BlockHarness();
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);

        Assert.NotNull(h.Printer.Block);
    }

    // ---- Ladekreis ----

    [Fact]
    public void PrintRing_FillsFromExpectedTimeUntilTimeout_OnlyWhilePrinting()
    {
        var h = BlockHarness();   // erwartet 60 s, Timeout 300 s
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        var t0 = h.Time.GetUtcNow();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);

        Assert.False(h.Controller.ShowPrintRing);
        Assert.Equal(0, h.Controller.PrintRingProgress);

        void AdvanceToElapsed(int seconds) =>
            h.AdvanceUntil(() => (h.Time.GetUtcNow() - t0).TotalSeconds >= seconds, maxSeconds: 400);

        AdvanceToElapsed(59);
        Assert.False(h.Controller.ShowPrintRing);   // der Balken läuft noch
        Assert.Equal(0, h.Controller.PrintRingProgress);

        AdvanceToElapsed(60);
        Assert.True(h.Controller.PrintOverdue);
        Assert.True(h.Controller.ShowPrintRing);
        Assert.Equal(0, h.Controller.PrintRingProgress, 3);

        AdvanceToElapsed(180);   // genau die Mitte zwischen 60 s und 300 s
        Assert.Equal(0.5, h.Controller.PrintRingProgress, 3);
        Assert.True(h.Controller.ShowPrintRing);

        AdvanceToElapsed(300);
        Assert.Equal(1, h.Controller.PrintRingProgress, 3);

        h.Printer.Complete(new PrintResult(PrintOutcome.Completed));
        h.WaitUntil(() => h.Controller.State == BoothState.Printed);
        Assert.False(h.Controller.ShowPrintRing);
        Assert.Equal(0, h.Controller.PrintRingProgress);
        h.RunToIdle();
    }

    [Fact]
    public void PrintRing_IsHiddenInBlock()
    {
        var h = BlockedHarness(out _);
        h.AdvanceBy(TimeSpan.FromSeconds(100), Second);

        Assert.Equal(BoothState.PrinterBlocked, h.Controller.State);
        Assert.False(h.Controller.ShowPrintRing);
    }

    [Fact]
    public void PrintRing_UsesTimeoutOfTheSettings()
    {
        var h = BlockHarness();
        h.Settings.Current.Printer.TimeoutSeconds = 120;   // Ring von 60 s bis 120 s
        h.Controller.TryTrigger();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Preview);
        var t0 = h.Time.GetUtcNow();
        h.AdvanceUntil(() => h.Controller.State == BoothState.Printing);

        h.AdvanceUntil(() => (h.Time.GetUtcNow() - t0).TotalSeconds >= 90);

        Assert.Equal(0.5, h.Controller.PrintRingProgress, 3);
        h.Printer.Complete(new PrintResult(PrintOutcome.Completed));
        h.RunToIdle();
    }
}
