using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.ViewModels.Booth;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Gastansicht bei einer Druckersperre: Admin-Ausweg, Sperrtexte je Ursache, Ladekreis, Aufheben nach der Admin-Ansicht
/// (teilt Harness und Fakes mit den Foto-Tests).
/// </summary>
public partial class BoothViewModelTests
{
    /// <summary>Foto-Durchgang bis zum Druck (Zustand Printing); der Druck endet erst mit <c>Printer.Pending</c>.</summary>
    private static Harness PrintingHarness(BitmapSource? preview = null, Action<Harness>? beforeTrigger = null)
    {
        var h = new Harness();
        var flow = h.Settings.Current.Flow;
        flow.CountdownSeconds = 0;
        flow.PreviewSeconds = 2;
        flow.ExpectedPrintSeconds = 120;
        h.Settings.Current.Printer.TimeoutSeconds = 300;
        h.Camera.Photo = CreateBitmap();
        h.Composer.Result = preview ?? CreateBitmap();
        h.Storage.Works = true;
        h.Printer.Pending = new TaskCompletionSource<PrintResult>();
        beforeTrigger?.Invoke(h);
        Assert.True(h.ViewModel.TryTrigger());
        Assert.True(SpinUntil(h, () => h.Controller.State == BoothState.Printing, advanceInPreview: true));
        return h;
    }

    /// <summary>Wie <see cref="PrintingHarness"/>, aber der Drucker hat ein Problem gemeldet: Zustand PrinterBlocked.</summary>
    private static Harness BlockedByPrinterHarness(PrinterAttention reason = PrinterAttention.PaperOut)
    {
        var h = PrintingHarness();
        Assert.NotNull(h.Printer.LastBlock);
        h.Printer.LastBlock!.Blocked.Report(reason);
        Assert.True(SpinWait.SpinUntil(() => h.Controller.State == BoothState.PrinterBlocked, TimeSpan.FromSeconds(10)));
        return h;
    }

    [Fact]
    public void CanOpenAdmin_InPrinterBlock_ButNotWhileAdminIsOpen()
    {
        var h = BlockedByPrinterHarness();

        Assert.True(h.ViewModel.CanOpenAdmin);

        h.ViewModel.IsAdminOpen = true;
        Assert.False(h.ViewModel.CanOpenAdmin);
        h.ViewModel.IsAdminOpen = false;
        Assert.True(h.ViewModel.CanOpenAdmin);
    }

    [Fact]
    public void PrinterBlocked_ShowsOnlyTheBlockScreen_AndHidesPhotoAndPrintAreas()
    {
        var h = BlockedByPrinterHarness();
        var vm = h.ViewModel;

        Assert.True(vm.ShowPrinterBlocked);
        Assert.False(vm.ShowResult);
        Assert.False(vm.ShowPrintInfo);
        Assert.False(vm.ShowPrintRing);
        Assert.False(vm.ShowDone);
        Assert.False(vm.ShowPrintCancelled);
        Assert.False(vm.IsError);
        Assert.False(vm.IsIdle);
        Assert.False(vm.IsPrinting);
        Assert.False(vm.CanMoveWindow);
        // Mitten im Durchgang: weder Foto, Video noch Abbrechen wirken.
        Assert.False(vm.TryTrigger());
        Assert.False(vm.TryCancel());
    }

    [Theory]
    [InlineData(PrinterAttention.PaperOut, "Papier leer",
        "Bitte hol jemanden, der es auffüllt. Dein Foto wird danach automatisch gedruckt.")]
    [InlineData(PrinterAttention.PaperJam, "Papierstau",
        "Bitte hol jemanden, der ihn behebt. Dein Foto wird danach automatisch gedruckt.")]
    [InlineData(PrinterAttention.Offline, "Drucker nicht erreichbar",
        "Bitte hol jemanden, der nach dem Drucker schaut. Dein Foto wird danach automatisch gedruckt.")]
    [InlineData(PrinterAttention.NeedsHelp, "Drucker braucht Hilfe",
        "Bitte hol jemanden, der nach dem Drucker schaut. Dein Foto wird danach automatisch gedruckt.")]
    public void PrinterBlocked_TitleAndText_FollowTheReason(PrinterAttention reason, string title, string text)
    {
        var h = BlockedByPrinterHarness(reason);

        Assert.Equal(title, h.ViewModel.PrinterBlockedTitle);
        Assert.Equal(text, h.ViewModel.PrinterBlockedText);
    }

    [Fact]
    public void PrinterBlocked_TextsChange_WhenTheReasonChangesOrIsGone()
    {
        var h = BlockedByPrinterHarness(PrinterAttention.PaperOut);
        Assert.Equal("Papier leer", h.ViewModel.PrinterBlockedTitle);

        h.Printer.LastBlock!.Blocked.Report(PrinterAttention.PaperJam);
        Assert.Equal("Papierstau", h.ViewModel.PrinterBlockedTitle);

        // Problem behoben, der Auftrag wartet noch auf den Druck: neutrale Meldung, der Zustand bleibt.
        h.Printer.LastBlock!.Blocked.Report(PrinterAttention.None);
        Assert.Equal("Dein Foto wird gedruckt …", h.ViewModel.PrinterBlockedTitle);
        Assert.Equal("Gleich ist es so weit.", h.ViewModel.PrinterBlockedText);
        Assert.True(h.ViewModel.ShowPrinterBlocked);
    }

    [Fact]
    public void PrinterBlocked_IsNotShown_OutsideTheBlock()
    {
        var h = PrintingHarness();

        Assert.False(h.ViewModel.ShowPrinterBlocked);
        Assert.Equal(BoothState.Printing, h.Controller.State);
    }

    [Fact]
    public void PrintRing_ShowsAfterExpectedTime_AndFollowsTheControllerUpToTheTimeout()
    {
        var h = PrintingHarness();
        var vm = h.ViewModel;

        // Vor Ablauf der erwarteten Zeit: kein Ring.
        Assert.False(vm.ShowPrintRing);
        Assert.Equal(0, vm.PrintRingProgress);

        h.Time.Advance(TimeSpan.FromSeconds(120));
        Assert.True(vm.IsPrintOverdue);
        Assert.True(vm.ShowPrintRing);
        Assert.Equal(h.Controller.PrintRingProgress, vm.PrintRingProgress);
        Assert.InRange(vm.PrintRingProgress, 0, 0.05);

        // Der Ring füllt sich von 120 s (erwartet) bis 300 s (Timeout): nach rund 212 s etwa zur Hälfte.
        h.Time.Advance(TimeSpan.FromSeconds(90));
        Assert.True(vm.ShowPrintRing);
        Assert.InRange(vm.PrintRingProgress, 0.49, 0.53);
        Assert.Equal(h.Controller.PrintRingProgress, vm.PrintRingProgress);

        h.Time.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(1, vm.PrintRingProgress);
        Assert.Equal(BoothState.Printing, h.Controller.State);

        h.Printer.Pending!.SetResult(new PrintResult(PrintOutcome.Completed));
        Assert.True(SpinWait.SpinUntil(() => h.Controller.State == BoothState.Printed, TimeSpan.FromSeconds(10)));
        Assert.False(vm.ShowPrintRing);
    }

    [Fact]
    public void PrintRing_IsHidden_WhileThePrinterIsBlocked()
    {
        var h = PrintingHarness();
        h.Time.Advance(TimeSpan.FromSeconds(150));
        Assert.True(h.ViewModel.ShowPrintRing);

        h.Printer.LastBlock!.Blocked.Report(PrinterAttention.PaperOut);
        Assert.True(SpinWait.SpinUntil(() => h.Controller.State == BoothState.PrinterBlocked, TimeSpan.FromSeconds(10)));

        Assert.False(h.ViewModel.ShowPrintRing);
        Assert.True(h.ViewModel.ShowPrinterBlocked);
    }

    // ---- Aufheben über die Admin-Ansicht

    [Fact]
    public void AdminClosed_WithoutOpening_KeepsTheBlock()
    {
        var h = BlockedByPrinterHarness();

        // Falsche oder abgebrochene PIN: Die Admin-Ansicht wurde nie gezeigt.
        Assert.False(h.ViewModel.ReleasePrinterBlockAfterAdmin(new AdminResult(false, false)));

        Assert.Equal(BoothState.PrinterBlocked, h.Controller.State);
        Assert.True(h.ViewModel.ShowPrinterBlocked);
    }

    [Fact]
    public void AdminClosed_AfterOpening_ReleasesTheBlock_WithoutErrorDisplay()
    {
        var h = BlockedByPrinterHarness();
        var states = new List<BoothState>();
        h.Controller.StateChanged += (_, _) => states.Add(h.Controller.State);

        Assert.True(h.ViewModel.ReleasePrinterBlockAfterAdmin(new AdminResult(false, false, false, Opened: true)));

        Assert.True(SpinWait.SpinUntil(() => h.Controller.State == BoothState.Idle, TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(BoothState.Error, states);
        Assert.False(h.ViewModel.IsError);
        Assert.False(h.ViewModel.ShowPrinterBlocked);
        Assert.Null(h.Controller.ErrorMessage);
    }

    [Fact]
    public void AdminClosed_OutsideTheBlock_DoesNothing()
    {
        var h = new Harness();

        Assert.False(h.ViewModel.ReleasePrinterBlockAfterAdmin(new AdminResult(true, false, true, Opened: true)));
        Assert.Equal(BoothState.Idle, h.Controller.State);

        var printing = PrintingHarness();
        Assert.False(printing.ViewModel.ReleasePrinterBlockAfterAdmin(new AdminResult(false, false, false, Opened: true)));
        Assert.Equal(BoothState.Printing, printing.Controller.State);
    }

    [Fact]
    public void AdminClosed_WithExitRequest_DoesNotRelease()
    {
        var h = BlockedByPrinterHarness();

        Assert.False(h.ViewModel.ReleasePrinterBlockAfterAdmin(new AdminResult(false, true, false, Opened: true)));
        Assert.Equal(BoothState.PrinterBlocked, h.Controller.State);
    }

    [Fact]
    public void AdminResult_Opened_DefaultsToFalse()
    {
        Assert.False(new AdminResult(false, false).Opened);
        Assert.False(new AdminResult(true, true, true).Opened);
        Assert.True(new AdminResult(false, false, Opened: true).Opened);
    }

    // ---- BoothWindow

    [StaFact]
    public void BoothWindow_PrinterBlockBindings_ShowBlockScreen_AndRing()
    {
        WpfCollection.EnsurePackUris();
        var h = PrintingHarness();
        var window = new BoothWindow(h.ViewModel, new FakeAdminLauncher(), h.Settings, new NoDisplays());
        var content = (FrameworkElement)window.Content;
        var view = (FrameworkElement)window.FindName("PrinterBlockedView");
        var ring = (FrameworkElement)window.FindName("PrintRing");
        void Update()
        {
            h.Settings.RaiseSettingsChanged();   // lässt die Gastansicht alle Eigenschaften neu lesen (UI-Thread)
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });
            content.Measure(new Size(1920, 1080));
            content.Arrange(new Rect(0, 0, 1920, 1080));
            content.UpdateLayout();
        }

        Update();
        Assert.Equal(Visibility.Collapsed, view.Visibility);
        Assert.Equal(Visibility.Collapsed, ring.Visibility);

        h.Time.Advance(TimeSpan.FromSeconds(150));
        Update();
        Assert.Equal(Visibility.Visible, ring.Visibility);
        Assert.Equal(Visibility.Collapsed, view.Visibility);

        h.Printer.LastBlock!.Blocked.Report(PrinterAttention.PaperOut);
        Assert.True(SpinWait.SpinUntil(() => h.Controller.State == BoothState.PrinterBlocked, TimeSpan.FromSeconds(10)));
        Update();
        Assert.Equal(Visibility.Visible, view.Visibility);
        Assert.Equal(Visibility.Collapsed, ring.Visibility);
        Assert.Contains(FindTextBlocks(view), t => t.Text == "Papier leer");
        Assert.Contains(FindTextBlocks(view), t => t.Text.StartsWith("Bitte hol jemanden, der es auffüllt.", StringComparison.Ordinal));
    }

    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock text)
                yield return text;
            foreach (var nested in FindTextBlocks(child))
                yield return nested;
        }
    }

    /// <summary>Ein Farbverlauf als Stand-in für das fertige Foto in der Vorschau.</summary>
    private static BitmapSource GradientPhoto(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                pixels[o] = (byte)(120 + 100 * x / width);      // B
                pixels[o + 1] = (byte)(80 + 120 * y / height);  // G
                pixels[o + 2] = (byte)(200 - 90 * y / height);  // R
                pixels[o + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur mit <c>FOTOBOX_RENDER_DIR</c> (sonst sofort beendet). Rendert die
    /// Gastansicht beim Druck (Balken ohne Ladekreis, Ladekreis zu 0, 50 und 100 %) und die Sperrmeldung je Ursache.
    /// </summary>
    [StaFact]
    [Trait("Category", "Visual")]
    public void Render_BoothWindow_PrinterBlockAndRing()
    {
        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        WpfCollection.EnsurePackUris();

        // Das Fenster entsteht vor dem Durchgang: Die Einblend-Animation der Overlays läuft in einem nicht gezeigten Fenster nicht
        // zu Ende, darum verlieren sie ihren Stil und sind voll deckend, bevor sie sichtbar werden.
        Grid content = null!;
        FrameworkElement flash = null!;
        var h = PrintingHarness(GradientPhoto(600, 900), beforeTrigger: harness =>
        {
            var window = new BoothWindow(harness.ViewModel, new FakeAdminLauncher(), harness.Settings, new NoDisplays());
            content = (Grid)window.Content;
            flash = (FrameworkElement)window.FindName("FlashOverlay");
            var fadeIn = window.FindResource("FadeInOverlay");
            foreach (var overlay in content.Children.OfType<Grid>().Where(g => ReferenceEquals(g.Style, fadeIn)))
            {
                overlay.Style = null;
                overlay.Opacity = 1;
            }
        });

        void Save(string name)
        {
            h.Settings.RaiseSettingsChanged();   // Eigenschaften neu lesen (UI-Thread)
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });
            // Der Aufnahmeblitz (400-ms-Animation) steht im nicht gezeigten Fenster mitten im Lauf: wegnehmen.
            flash.BeginAnimation(UIElement.OpacityProperty, null);
            flash.Opacity = 0;
            content.Measure(new Size(1920, 1080));
            content.Arrange(new Rect(0, 0, 1920, 1080));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(dir, name));
            encoder.Save(stream);
        }

        Save("printer-printing-bar.png");

        h.Time.Advance(TimeSpan.FromSeconds(121));
        Save("printer-ring-0.png");
        h.Time.Advance(TimeSpan.FromSeconds(90));
        Save("printer-ring-50.png");
        h.Time.Advance(TimeSpan.FromSeconds(90));
        Save("printer-ring-100.png");

        h.Printer.LastBlock!.Blocked.Report(PrinterAttention.PaperOut);
        Assert.True(SpinWait.SpinUntil(() => h.Controller.State == BoothState.PrinterBlocked, TimeSpan.FromSeconds(10)));
        Save("printer-blocked-paperout.png");

        h.Printer.LastBlock.Blocked.Report(PrinterAttention.Offline);
        Save("printer-blocked-offline.png");

        h.Printer.LastBlock.Blocked.Report(PrinterAttention.None);
        Save("printer-blocked-cleared.png");
    }
}
