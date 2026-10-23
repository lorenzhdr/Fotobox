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

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CompletedOrPrintedFlag_MeansCompleted(bool completed, bool printed)
    {
        var d = Eval(new PrintJobFlags(IsCompleted: completed, IsPrinted: printed));

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
    public void JobError_WithoutCause_IsWaitingUntilGraceElapsed_ThenFails()
    {
        var job = new PrintJobFlags(IsInError: true);

        var early = Eval(job, errorFor: 1);
        Assert.Equal(PrintJobPhase.InProgress, early.Phase);
        Assert.True(early.GenericError);

        var late = Eval(job, errorFor: PrintJobStateEvaluator.GenericErrorGrace.TotalSeconds);
        Assert.Equal(PrintJobPhase.Failed, late.Phase);
        Assert.Equal(PrintJobStateEvaluator.FailedError, late.Message);
    }

    [Fact]
    public void QueueError_WithoutCause_FailsOnlyWhenPersistent()
    {
        var queue = new PrintQueueFlags(IsInError: true);

        Assert.Equal(PrintJobPhase.InProgress, Eval(null, queue, seen: false, sinceSubmitted: 3, errorFor: 3).Phase);
        Assert.Equal(PrintJobPhase.Failed, Eval(null, queue, seen: false, sinceSubmitted: 3, errorFor: 30).Phase);
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
