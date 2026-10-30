using Fotobox.Core;
using Fotobox.Services.Printing;
using Xunit;

namespace Fotobox.Tests;

public class PrintJobStateEvaluatorTests
{
    private static readonly PrintQueueFlags Healthy = default;
    private static readonly TimeSpan Zero = TimeSpan.Zero;

    private static PrintJobDecision Eval(PrintJobFlags? job, PrintQueueFlags? queue = null, bool seen = true,
        double sinceSubmitted = 1, double errorFor = 0) =>
        PrintJobStateEvaluator.Evaluate(job, queue ?? Healthy, seen,
            TimeSpan.FromSeconds(sinceSubmitted), TimeSpan.FromSeconds(errorFor));

    [Fact]
    public void RunningJob_IsInProgress_WithPrintingMessage()
    {
        var d = Eval(new PrintJobFlags(IsPrinting: true));

        Assert.Equal(PrintJobPhase.InProgress, d.Phase);
        Assert.Equal("Wird gedruckt…", d.Message);
        Assert.False(d.GenericError);
    }

    [Fact]
    public void PrintedFlag_MeansCompleted()
    {
        Assert.Equal(PrintJobPhase.Completed, Eval(new PrintJobFlags(IsPrinted: true)).Phase);
    }

    [Fact]
    public void CompletedWithoutPrinted_StaysInProgress_BecausePrinterMayStillBePrinting()
    {
        var d = Eval(new PrintJobFlags(IsCompleted: true));

        Assert.Equal(PrintJobPhase.InProgress, d.Phase);
        Assert.Equal("Wird gedruckt…", d.Message);
    }

    [Fact]
    public void CompletedWithoutPrinted_StillReportsPaperOutAndOffline()
    {
        var paper = Eval(new PrintJobFlags(IsCompleted: true, IsPaperOut: true));
        var offline = Eval(new PrintJobFlags(IsCompleted: true), new PrintQueueFlags(IsOffline: true));

        Assert.Equal(PrintJobPhase.InProgress, paper.Phase);
        Assert.Equal("Papier leer – bitte nachlegen", paper.Message);
        Assert.Equal(PrintJobPhase.InProgress, offline.Phase);
        Assert.Equal("Drucker offline", offline.Message);
    }

    [Fact]
    public void CompletedAndRetained_MeansCompleted()
    {
        Assert.Equal(PrintJobPhase.Completed, Eval(new PrintJobFlags(IsCompleted: true, IsRetained: true)).Phase);
    }

    [Fact]
    public void RetainedWithoutCompleted_IsNotCompleted()
    {
        Assert.Equal(PrintJobPhase.InProgress, Eval(new PrintJobFlags(IsRetained: true, IsPrinting: true)).Phase);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void PrintedJob_IsCompleted_EvenWhenAlsoDeletingOrDeleted(bool deleting, bool deleted, bool completed)
    {
        var d = Eval(new PrintJobFlags(IsPrinted: true, IsDeleting: deleting, IsDeleted: deleted, IsCompleted: completed));

        Assert.Equal(PrintJobPhase.Completed, d.Phase);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CompletedJob_BeingDeleted_IsCompleted_NotCancelled(bool deleting, bool deleted)
    {
        var d = Eval(new PrintJobFlags(IsCompleted: true, IsDeleting: deleting, IsDeleted: deleted));

        Assert.Equal(PrintJobPhase.Completed, d.Phase);
    }

    [Fact]
    public void JobVanished_AfterBeingSeen_MeansCompleted()
    {
        Assert.Equal(PrintJobPhase.Completed, Eval(null, seen: true, sinceSubmitted: 2).Phase);
    }

    [Fact]
    public void JobNeverSeen_WaitsUntilTenSeconds_ThenCompleted()
    {
        Assert.Equal(PrintJobPhase.InProgress, Eval(null, seen: false, sinceSubmitted: 9.9).Phase);
        Assert.Equal(PrintJobPhase.Completed, Eval(null, seen: false, sinceSubmitted: 10).Phase);
    }

    [Fact]
    public void DeletedJob_Fails()
    {
        var d = Eval(new PrintJobFlags(IsDeleted: true));

        Assert.Equal(PrintJobPhase.Failed, d.Phase);
        Assert.Equal(PrintJobStateEvaluator.FailedDeleted, d.Message);
    }

    [Fact]
    public void DeletingJob_Fails()
    {
        Assert.Equal(PrintJobPhase.Failed, Eval(new PrintJobFlags(IsDeleting: true)).Phase);
    }

    [Fact]
    public void PaperOut_OnJob_ReportsPaperOutAndKeepsWaiting_EvenWithErrorFlag()
    {
        var d = Eval(new PrintJobFlags(IsInError: true, IsPaperOut: true), errorFor: 60);

        Assert.Equal(PrintJobPhase.InProgress, d.Phase);
        Assert.Equal("Papier leer – bitte nachlegen", d.Message);
    }

    [Fact]
    public void PaperOut_OnQueue_ReportsPaperOut()
    {
        var d = Eval(new PrintJobFlags(IsPrinting: true), new PrintQueueFlags(IsPaperOut: true));

        Assert.Equal("Papier leer – bitte nachlegen", d.Message);
    }

    [Fact]
    public void PaperJam_HasPriorityOverPaperOut()
    {
        var d = Eval(new PrintJobFlags(), new PrintQueueFlags(IsPaperJammed: true, IsPaperOut: true));

        Assert.Equal("Papierstau", d.Message);
        Assert.Equal(PrintJobPhase.InProgress, d.Phase);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Offline_IsReported(bool queueOffline, bool jobOffline)
    {
        var d = Eval(new PrintJobFlags(IsOffline: jobOffline), new PrintQueueFlags(IsOffline: queueOffline));

        Assert.Equal("Drucker offline", d.Message);
        Assert.Equal(PrintJobPhase.InProgress, d.Phase);
    }

    [Fact]
    public void NotAvailableQueue_CountsAsOffline()
    {
        Assert.Equal("Drucker offline", Eval(new PrintJobFlags(), new PrintQueueFlags(IsNotAvailable: true)).Message);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void NeedsAttention_IsReported(bool jobIntervention, bool queueIntervention, bool jobBlocked)
    {
        var d = Eval(new PrintJobFlags(IsUserInterventionRequired: jobIntervention, IsBlocked: jobBlocked),
            new PrintQueueFlags(NeedUserIntervention: queueIntervention));

        Assert.Equal("Drucker benötigt Aufmerksamkeit", d.Message);
    }

    [Fact]
    public void JobError_WithoutCause_IsWaitingUntilGraceElapsed_ThenNeedsHelp_NeverFails()
    {
        var job = new PrintJobFlags(IsInError: true);

        var early = Eval(job, errorFor: 1);
        Assert.Equal(PrintJobPhase.InProgress, early.Phase);
        Assert.True(early.GenericError);
        Assert.Equal(PrinterAttention.None, early.Attention);

        var late = Eval(job, errorFor: PrintJobStateEvaluator.GenericErrorGrace.TotalSeconds);
        Assert.Equal(PrintJobPhase.InProgress, late.Phase);   // kein Fehlschlag und keine Stornierung mehr
        Assert.Equal(PrinterAttention.NeedsHelp, late.Attention);
        Assert.Equal(PrintJobStateEvaluator.StatusAttention, late.Message);
        Assert.True(late.GenericError);

        var muchLater = Eval(job, errorFor: 3600);
        Assert.Equal(PrintJobPhase.InProgress, muchLater.Phase);
        Assert.Equal(PrinterAttention.NeedsHelp, muchLater.Attention);
    }

    [Fact]
    public void QueueError_WithoutCause_NeedsHelpOnlyWhenPersistent()
    {
        var queue = new PrintQueueFlags(IsInError: true);

        var early = Eval(null, queue, seen: false, sinceSubmitted: 3, errorFor: 3);
        Assert.Equal(PrintJobPhase.InProgress, early.Phase);
        Assert.Equal(PrinterAttention.None, early.Attention);
        var late = Eval(null, queue, seen: false, sinceSubmitted: 3, errorFor: 30);
        Assert.Equal(PrintJobPhase.InProgress, late.Phase);
        Assert.Equal(PrinterAttention.NeedsHelp, late.Attention);
    }

    [Theory]
    [InlineData(false, true, false, false, PrinterAttention.PaperJam)]
    [InlineData(true, false, false, false, PrinterAttention.PaperOut)]
    [InlineData(false, false, true, false, PrinterAttention.Offline)]
    [InlineData(false, false, false, true, PrinterAttention.NeedsHelp)]
    public void Attention_IsReportedWithCause(bool paperOut, bool jam, bool offline, bool intervention, PrinterAttention expected)
    {
        var d = Eval(new PrintJobFlags(IsPrinting: true),
            new PrintQueueFlags(IsPaperOut: paperOut, IsPaperJammed: jam, IsOffline: offline, NeedUserIntervention: intervention));

        Assert.Equal(PrintJobPhase.InProgress, d.Phase);
        Assert.Equal(expected, d.Attention);
    }

    [Fact]
    public void RunningJob_HasNoAttention()
    {
        Assert.Equal(PrinterAttention.None, Eval(new PrintJobFlags(IsPrinting: true)).Attention);
    }

    [Fact]
    public void PaperOutWithErrorFlag_IsPaperOut_NotNeedsHelp()
    {
        var d = Eval(new PrintJobFlags(IsInError: true, IsPaperOut: true), errorFor: 60);

        Assert.Equal(PrinterAttention.PaperOut, d.Attention);
    }

    // ---- DecideBlock (Druckersperre) ----

    private static PrintBlockDecision Block(PrinterAttention attention, double attentionSeconds = 0, bool timedOut = false,
        bool jobCompleted = false, bool statusPrinting = true) =>
        PrintJobStateEvaluator.DecideBlock(attention, TimeSpan.FromSeconds(attentionSeconds), timedOut, jobCompleted, statusPrinting);

    [Fact]
    public void DecideBlock_NothingHappening_DoesNothing()
    {
        Assert.Equal(PrintBlockStep.None, Block(PrinterAttention.None, 0).Step);
        Assert.Equal(PrintBlockStep.None, Block(PrinterAttention.None, 0, jobCompleted: true).Step);
    }

    [Fact]
    public void DecideBlock_CauseIsConfirmedOnlyAfterFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), PrintJobStateEvaluator.AttentionConfirm);

        Assert.Equal(PrintBlockStep.None, Block(PrinterAttention.PaperOut, 4.9, statusPrinting: false).Step);
        var confirmed = Block(PrinterAttention.PaperOut, 5, statusPrinting: false);
        Assert.Equal(PrintBlockStep.Block, confirmed.Step);
        Assert.Equal(PrinterAttention.PaperOut, confirmed.Reason);
        Assert.False(confirmed.ByTimeout);
    }

    [Theory]
    [InlineData(PrinterAttention.PaperOut)]
    [InlineData(PrinterAttention.PaperJam)]
    [InlineData(PrinterAttention.Offline)]
    [InlineData(PrinterAttention.NeedsHelp)]
    public void DecideBlock_ConfirmedCause_BlocksWithThatCause_EvenBeforeTimeout(PrinterAttention cause)
    {
        var d = Block(cause, 30, timedOut: false, statusPrinting: false);

        Assert.Equal(PrintBlockStep.Block, d.Step);
        Assert.Equal(cause, d.Reason);
    }

    [Fact]
    public void DecideBlock_TimeoutWithoutCause_BlocksAsPaperOut()
    {
        var d = Block(PrinterAttention.None, 0, timedOut: true);

        Assert.Equal(PrintBlockStep.Block, d.Step);
        Assert.Equal(PrinterAttention.PaperOut, d.Reason);
        Assert.True(d.ByTimeout);
    }

    [Fact]
    public void DecideBlock_TimeoutWithUnconfirmedCause_BlocksWithThatCause()
    {
        var d = Block(PrinterAttention.Offline, 2, timedOut: true, statusPrinting: false);

        Assert.Equal(PrintBlockStep.Block, d.Step);
        Assert.Equal(PrinterAttention.Offline, d.Reason);
        Assert.True(d.ByTimeout);
    }

    [Fact]
    public void DecideBlock_TimeoutWithJobCompletedAndNoError_KeepsGraceRule()
    {
        var d = Block(PrinterAttention.None, 0, timedOut: true, jobCompleted: true, statusPrinting: true);

        Assert.Equal(PrintBlockStep.CompletedByGrace, d.Step);
    }

    [Fact]
    public void DecideBlock_GraceRuleNeedsCompletedJobAndPrintingStatus()
    {
        Assert.Equal(PrintBlockStep.Block, Block(PrinterAttention.None, 0, timedOut: true, jobCompleted: false).Step);
        Assert.Equal(PrintBlockStep.Block, Block(PrinterAttention.PaperOut, 2, timedOut: true, jobCompleted: true, statusPrinting: false).Step);
        Assert.Equal(PrintBlockStep.None, Block(PrinterAttention.None, 0, timedOut: false, jobCompleted: true).Step);
    }

    [Fact]
    public void QueueError_IsIgnored_WhileJobPrintsWithoutOwnError()
    {
        var d = Eval(new PrintJobFlags(IsPrinting: true), new PrintQueueFlags(IsInError: true), errorFor: 60);

        Assert.Equal(PrintJobPhase.InProgress, d.Phase);
        Assert.False(d.GenericError);
    }

    [Fact]
    public void Completed_WinsOverQueueProblems()
    {
        var d = Eval(new PrintJobFlags(IsPrinted: true), new PrintQueueFlags(IsOffline: true));

        Assert.Equal(PrintJobPhase.Completed, d.Phase);
    }

    [Theory]
    [InlineData("PORTPROMPT:", true)]
    [InlineData("portprompt:", true)]
    [InlineData(" PORTPROMPT: ", true)]
    [InlineData("USB001", false)]
    [InlineData("WSD-1234", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFilePromptPort_DetectsPortPrompt(string? port, bool expected)
    {
        Assert.Equal(expected, PrintService.IsFilePromptPort(port));
    }
}

public class UserCancelDecisionTests
{
    [Fact]
    public void NotRequested_DoesNothing() =>
        Assert.Equal(UserCancelStep.NotRequested,
            PrintJobStateEvaluator.DecideUserCancel(false, jobKnown: true, TimeSpan.FromMinutes(1)));

    [Fact]
    public void KnownJob_IsCancelledAtOnce() =>
        Assert.Equal(UserCancelStep.CancelJob,
            PrintJobStateEvaluator.DecideUserCancel(true, jobKnown: true, TimeSpan.Zero));

    [Theory]
    [InlineData(0)]
    [InlineData(2500)]
    [InlineData(4999)]
    public void UnknownJob_KeepsSearchingWithinFiveSeconds(int milliseconds) =>
        Assert.Equal(UserCancelStep.KeepSearching,
            PrintJobStateEvaluator.DecideUserCancel(true, jobKnown: false, TimeSpan.FromMilliseconds(milliseconds)));

    [Theory]
    [InlineData(5000)]
    [InlineData(30000)]
    public void UnknownJob_GivesUpAfterFiveSeconds(int milliseconds) =>
        Assert.Equal(UserCancelStep.GiveUp,
            PrintJobStateEvaluator.DecideUserCancel(true, jobKnown: false, TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void KnownJob_IsCancelledEvenAfterSearchWindow() =>
        Assert.Equal(UserCancelStep.CancelJob,
            PrintJobStateEvaluator.DecideUserCancel(true, jobKnown: true, TimeSpan.FromSeconds(8)));
}

public class PrintLayoutResolverTests
{
    private static readonly PaperSizeInfo Tenfifteen = new("NorthAmerica4x6", "10 × 15 cm (4 × 6 Zoll)", 101.6, 152.4);
    private static readonly PaperSizeInfo A4 = new("ISOA4", "A4 (210 × 297 mm)", 210, 297);

    [Theory]
    [InlineData(Core.PageOrientation.Auto, LayoutKind.Strip, false)]
    [InlineData(Core.PageOrientation.Auto, LayoutKind.SingleBorderless, true)]
    [InlineData(Core.PageOrientation.Auto, LayoutKind.Overlay, true)]
    [InlineData(Core.PageOrientation.Portrait, LayoutKind.SingleBorderless, false)]
    [InlineData(Core.PageOrientation.Landscape, LayoutKind.Strip, true)]
    public void ResolveLandscape_FollowsOrientationAndLayout(Core.PageOrientation o, LayoutKind kind, bool expected)
    {
        Assert.Equal(expected, PrintLayoutResolver.ResolveLandscape(o, kind));
    }

    [Fact]
    public void MmDipConversion_UsesOneNinetySixthInch()
    {
        Assert.Equal(96.0, PrintLayoutResolver.MmToDip(25.4), 6);
        Assert.Equal(25.4, PrintLayoutResolver.DipToMm(96.0), 6);
        Assert.Equal(101.6, PrintLayoutResolver.DipToMm(384), 6);
        Assert.Equal(152.4, PrintLayoutResolver.DipToMm(576), 6);
    }

    [Fact]
    public void CreatePaperSize_ConvertsDipsToMillimeters_AndBuildsGermanDisplayName()
    {
        var info = PrintLayoutResolver.CreatePaperSize("NorthAmerica4x6", 384, 576);

        Assert.Equal("NorthAmerica4x6", info.Name);
        Assert.Equal(101.6, info.WidthMm);
        Assert.Equal(152.4, info.HeightMm);
        Assert.Equal("10 × 15 cm (4 × 6 Zoll)", info.DisplayName);
    }

    [Fact]
    public void CreatePaperSize_IsoA4()
    {
        var info = PrintLayoutResolver.CreatePaperSize("ISOA4", 793.7, 1122.5);

        Assert.Equal("ISOA4", info.Name);
        Assert.Equal(210.0, info.WidthMm, 0);
        Assert.Equal(297.0, info.HeightMm, 0);
        Assert.Equal("A4 (210 × 297 mm)", info.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Unknown")]
    public void BuildPaperName_Unknown_UsesMillimeters(string? name)
    {
        Assert.Equal("100x148mm", PrintLayoutResolver.BuildPaperName(name, 100.0, 148.0));
    }

    [Fact]
    public void BuildDisplayName_NonStandard_ShowsMillimetersWithGermanDecimal()
    {
        Assert.Equal("100 × 148,5 mm", PrintLayoutResolver.BuildDisplayName(null, 100, 148.5));
        Assert.Equal("13 × 18 cm (5 × 7 Zoll)", PrintLayoutResolver.BuildDisplayName(null, 127, 177.8));
    }

    [Fact]
    public void ResolvePaper_PrefersNameOverCustomSizeOverDriverDefault()
    {
        var sizes = new[] { A4, Tenfifteen };
        var settings = new PrinterSettings { PaperSizeName = "ISOA4", PaperWidthMm = 50, PaperHeightMm = 60 };
        Assert.Same(A4, PrintLayoutResolver.ResolvePaper(settings, sizes, Tenfifteen));

        settings.PaperSizeName = "GibtEsNicht";
        var custom = PrintLayoutResolver.ResolvePaper(settings, sizes, Tenfifteen);
        Assert.Equal(50, custom!.WidthMm);
        Assert.Equal(60, custom.HeightMm);

        settings.PaperWidthMm = null;
        Assert.Same(Tenfifteen, PrintLayoutResolver.ResolvePaper(settings, sizes, Tenfifteen));

        Assert.Null(PrintLayoutResolver.ResolvePaper(settings, sizes, null));
    }

    [Fact]
    public void ResolvePaper_NameMatchIsCaseInsensitive()
    {
        var settings = new PrinterSettings { PaperSizeName = "isoa4" };

        Assert.Same(A4, PrintLayoutResolver.ResolvePaper(settings, [A4], null));
    }

    [Fact]
    public void ResolvePageSpec_NamedPaper_Strip_IsPortrait()
    {
        var settings = new PrinterSettings { PaperSizeName = "ISOA4" };

        var spec = PrintLayoutResolver.ResolvePageSpec(settings, LayoutKind.Strip, [A4], null);

        Assert.Equal(210, spec.WidthMm);
        Assert.Equal(297, spec.HeightMm);
        Assert.Equal(300, spec.Dpi);
        Assert.False(spec.IsLandscape);
    }

    [Fact]
    public void ResolvePageSpec_NamedPaper_Single_IsLandscape()
    {
        var settings = new PrinterSettings { PaperSizeName = "ISOA4" };

        var spec = PrintLayoutResolver.ResolvePageSpec(settings, LayoutKind.SingleBorderless, [A4], null);

        Assert.Equal(297, spec.WidthMm);
        Assert.Equal(210, spec.HeightMm);
        Assert.True(spec.IsLandscape);
    }

    [Fact]
    public void ResolvePageSpec_CustomSize_IsUsedWhenNameUnknown_AndNormalizedByOrientation()
    {
        var settings = new PrinterSettings
        {
            PaperSizeName = "Weg",
            PaperWidthMm = 152.4,
            PaperHeightMm = 101.6,
            Orientation = Core.PageOrientation.Portrait,
        };

        var spec = PrintLayoutResolver.ResolvePageSpec(settings, LayoutKind.SingleBorderless, [A4], Tenfifteen);

        Assert.Equal(101.6, spec.WidthMm);
        Assert.Equal(152.4, spec.HeightMm);
    }

    [Fact]
    public void ResolvePageSpec_FallsBackToDriverDefault()
    {
        var spec = PrintLayoutResolver.ResolvePageSpec(new PrinterSettings(), LayoutKind.Strip, [], A4);

        Assert.Equal(210, spec.WidthMm);
        Assert.Equal(297, spec.HeightMm);
    }

    [Fact]
    public void ResolvePageSpec_WithoutAnyInformation_UsesPageSpecDefault()
    {
        var portrait = PrintLayoutResolver.ResolvePageSpec(new PrinterSettings { Orientation = Core.PageOrientation.Portrait },
            LayoutKind.Strip, [], null);
        Assert.Equal(PageSpec.Default, portrait);

        var landscape = PrintLayoutResolver.ResolvePageSpec(new PrinterSettings(), LayoutKind.Overlay, [], null);
        Assert.Equal(PageSpec.Default.ToOrientation(true), landscape);
        Assert.True(landscape.IsLandscape);
    }

    // ---- Papierzuordnung per Maß ----

    [Fact]
    public void FindPaperByDimensions_ExactMatch_ReturnsIndex()
    {
        Assert.Equal(1, PrintLayoutResolver.FindPaperByDimensions([A4, Tenfifteen], 101.6, 152.4));
    }

    [Theory]
    [InlineData(101, 151.5, 1)]
    [InlineData(102.5, 153.2, 1)]
    [InlineData(152.4, 101.6, 1)]   // andere Ausrichtung
    [InlineData(152.9, 100.9, 1)]   // andere Ausrichtung, innerhalb der Toleranz
    [InlineData(100, 150, -1)]      // 1,6 mm Abweichung in der Breite
    [InlineData(100, 148, -1)]
    [InlineData(105, 148, -1)]
    public void FindPaperByDimensions_UsesOneMillimeterToleranceInBothOrientations(double w, double h, int expected)
    {
        Assert.Equal(expected, PrintLayoutResolver.FindPaperByDimensions([A4, Tenfifteen], w, h));
    }

    [Fact]
    public void FindPaperByDimensions_PrefersClosestMatch_ThenFirst()
    {
        var near = new PaperSizeInfo("Near", "n", 100.5, 150.5);
        var closer = new PaperSizeInfo("Closer", "c", 100.1, 150.1);
        var twin = new PaperSizeInfo("Twin", "t", 100.1, 150.1);

        Assert.Equal(1, PrintLayoutResolver.FindPaperByDimensions([near, closer, twin], 100, 150));
    }

    [Fact]
    public void FindPaperByDimensions_EmptyList_ReturnsMinusOne()
    {
        Assert.Equal(-1, PrintLayoutResolver.FindPaperByDimensions([], 100, 150));
    }

    [Fact]
    public void FindPaperByDimensions_ToleranceBoundaryIsInclusive()
    {
        Assert.Equal(0, PrintLayoutResolver.FindPaperByDimensions([Tenfifteen], 100.6, 151.4));
    }

    [Theory]
    [InlineData(1200, 1800, 384, 576, false)]     // gleiches Verhältnis
    [InlineData(1800, 1200, 384, 576, false)]     // Ausrichtung egal
    [InlineData(1200, 1800, 378, 576, true)]      // ca. 1,6 % schmaler
    [InlineData(1200, 1800, 384, 570, true)]
    [InlineData(1200, 1800, 384, 580, false)]     // unter 1 % (1,5 → 1,510)
    [InlineData(0, 1800, 384, 576, false)]        // ungültig → keine Warnung
    public void HasAspectMismatch_WarnsAboveOnePercent(double aw, double ah, double bw, double bh, bool expected)
    {
        Assert.Equal(expected, PrintLayoutResolver.HasAspectMismatch(aw, ah, bw, bh));
    }

    [Fact]
    public void PixelsToMm_UsesDpi()
    {
        Assert.Equal(101.6, PrintLayoutResolver.PixelsToMm(1200, 300), 6);
        Assert.Equal(0, PrintLayoutResolver.PixelsToMm(1200, 0));
    }

    [Theory]
    [InlineData(1800, 1200, 384, 576, true)]   // Querformat-Bild auf Hochformat-Medium
    [InlineData(1200, 1800, 384, 576, false)]
    [InlineData(1200, 1800, 576, 384, true)]   // Hochformat-Bild auf Querformat-Medium
    [InlineData(1800, 1200, 576, 384, false)]
    [InlineData(1000, 1000, 384, 576, false)]  // quadratisches Bild
    [InlineData(1800, 1200, 500, 500, false)]  // quadratisches Medium
    public void NeedsRotation_OnlyWhenOrientationsDiffer(double iw, double ih, double mw, double mh, bool expected)
    {
        Assert.Equal(expected, PrintLayoutResolver.NeedsRotation(iw, ih, mw, mh));
    }

    [Fact]
    public void ComputeFillRect_SameAspect_FillsExactly()
    {
        var r = PrintLayoutResolver.ComputeFillRect(1200, 1800, 384, 576);

        Assert.Equal(0, r.X, 6);
        Assert.Equal(0, r.Y, 6);
        Assert.Equal(384, r.Width, 6);
        Assert.Equal(576, r.Height, 6);
    }

    [Fact]
    public void ComputeFillRect_WiderImage_CropsSidesCentered_WithoutStretching()
    {
        // 2:1-Bild in quadratischer Box: Höhe füllt, Breite überragt beidseitig.
        var r = PrintLayoutResolver.ComputeFillRect(2000, 1000, 100, 100);

        Assert.Equal(200, r.Width, 6);
        Assert.Equal(100, r.Height, 6);
        Assert.Equal(-50, r.X, 6);
        Assert.Equal(0, r.Y, 6);
        Assert.Equal(2.0, r.Width / r.Height, 6);
    }

    [Fact]
    public void ComputeFillRect_TallerImage_CropsTopAndBottom()
    {
        var r = PrintLayoutResolver.ComputeFillRect(1000, 2000, 100, 100);

        Assert.Equal(100, r.Width, 6);
        Assert.Equal(200, r.Height, 6);
        Assert.Equal(0, r.X, 6);
        Assert.Equal(-50, r.Y, 6);
    }
}

public class PrintServiceHardwareTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public void GetPrinters_IsNotEmpty()
    {
        var printers = new PrintService().GetPrinters();

        Assert.NotEmpty(printers);
        Assert.All(printers, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void GetPaperSizes_MicrosoftPrintToPdf_ContainsA4()
    {
        var sizes = new PrintService().GetPaperSizes("Microsoft Print to PDF");

        Assert.Contains(sizes, s => Math.Abs(s.WidthMm - 210) < 1 && Math.Abs(s.HeightMm - 297) < 1);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void GetPageSpec_MicrosoftPrintToPdf_A4Strip_IsPortraitA4()
    {
        var settings = new PrinterSettings { PrinterName = "Microsoft Print to PDF", PaperSizeName = "ISOA4" };

        var spec = new PrintService().GetPageSpec(settings, LayoutKind.Strip);

        Assert.Equal(210, spec.WidthMm, 0);
        Assert.Equal(297, spec.HeightMm, 0);
    }
}

public class PageSpecCacheTests
{
    private static PageSpecKey Key(string? printer = "Drucker A", LayoutKind kind = LayoutKind.SingleBorderless) =>
        PageSpecKey.From(new PrinterSettings { PrinterName = printer }, kind);

    [Fact]
    public void Store_ThenFreshWithinLifetime_ReturnsValue()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var cache = new PageSpecCache(time);
        var spec = new PageSpec(148, 100);

        Assert.False(cache.TryGetFresh(Key(), out _));
        cache.Store(Key(), spec);

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.True(cache.TryGetFresh(Key(), out var cached));
        Assert.Same(spec, cached);
    }

    [Fact]
    public void Expired_IsNotFresh_ButStaysAvailableAsLastGood()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var cache = new PageSpecCache(time);
        var spec = new PageSpec(148, 100);
        cache.Store(Key(), spec);

        time.Advance(PageSpecCache.DefaultLifetime + TimeSpan.FromSeconds(1));

        Assert.False(cache.TryGetFresh(Key(), out _));
        Assert.True(cache.TryGetLastGood(Key(), out var lastGood));
        Assert.Same(spec, lastGood);
    }

    [Fact]
    public void Entries_AreSeparatePerPrinterSettingsAndLayoutKind()
    {
        var cache = new PageSpecCache(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        cache.Store(Key("Drucker A"), new PageSpec(148, 100));

        Assert.False(cache.TryGetFresh(Key("Drucker B"), out _));
        Assert.False(cache.TryGetFresh(Key("Drucker A", LayoutKind.Strip), out _));
        Assert.False(cache.TryGetLastGood(Key("Drucker B"), out _));
        Assert.True(cache.TryGetFresh(Key("Drucker A"), out _));
    }

    [Fact]
    public void Key_CoversAllValuesThatInfluenceThePageSize()
    {
        var baseline = new PrinterSettings { PrinterName = "A", PaperSizeName = "P", PaperWidthMm = 100, PaperHeightMm = 150 };
        var key = PageSpecKey.From(baseline, LayoutKind.SingleBorderless);

        // Kopien und Zeitlimit beeinflussen die Seitengröße nicht.
        Assert.Equal(key, PageSpecKey.From(
            new PrinterSettings { PrinterName = "A", PaperSizeName = "P", PaperWidthMm = 100, PaperHeightMm = 150, Copies = 3, TimeoutSeconds = 99 },
            LayoutKind.SingleBorderless));
        Assert.NotEqual(key, PageSpecKey.From(
            new PrinterSettings { PrinterName = "A", PaperSizeName = "Q", PaperWidthMm = 100, PaperHeightMm = 150 }, LayoutKind.SingleBorderless));
        Assert.NotEqual(key, PageSpecKey.From(
            new PrinterSettings { PrinterName = "A", PaperSizeName = "P", PaperWidthMm = 101, PaperHeightMm = 150 }, LayoutKind.SingleBorderless));
        Assert.NotEqual(key, PageSpecKey.From(
            new PrinterSettings { PrinterName = "A", PaperSizeName = "P", PaperWidthMm = 100, PaperHeightMm = 150, Orientation = PageOrientation.Landscape },
            LayoutKind.SingleBorderless));
    }

    [Fact]
    public async Task ParallelAccess_IsThreadSafe()
    {
        var cache = new PageSpecCache(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 500; n++)
            {
                cache.Store(Key($"Drucker {n % 5}"), new PageSpec(100 + i, 150));
                cache.TryGetFresh(Key($"Drucker {n % 5}"), out _);
                cache.TryGetLastGood(Key($"Drucker {(n + 1) % 5}"), out _);
            }
        })));

        Assert.True(cache.TryGetFresh(Key("Drucker 0"), out _));
    }
}
