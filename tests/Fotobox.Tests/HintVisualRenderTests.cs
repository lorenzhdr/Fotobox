using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur, wenn die Umgebungsvariable <c>FOTOBOX_RENDER_DIR</c> auf einen
/// Ordner zeigt (sonst sofort beendet, auch in der CI). Rendert den Gast-Hinweis (alle Stile, unten mit Mausradzeile und oben ohne,
/// dazu die Mausrad-Darstellungen) und den Admin-Tab „Gastansicht“ in Hell und Dunkel als PNG.
/// Aufruf: <c>FOTOBOX_RENDER_DIR=C:\pfad\render dotnet test --filter "Category=Visual"</c>.
/// </summary>
[Collection(WpfCollection.Name)]
public class HintVisualRenderTests
{
    private static string? RenderDir()
    {
        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SavePng(Visual visual, int width, int height, string path, Brush? background = null)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        if (background is not null)
        {
            var fill = new DrawingVisual();
            using (var dc = fill.RenderOpen())
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            bitmap.Render(fill);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    /// <summary>Gastansicht-Attrappe 1920 × 1080: dunkelgrauer Verlauf, angedeutete Person und der dezente Verlauf unten wie in der echten Ansicht.</summary>
    private static Grid Stage(GuestHint hint)
    {
        var stage = new Grid
        {
            Width = 1920, Height = 1080, ClipToBounds = true,
            Background = new LinearGradientBrush(Color.FromRgb(0x5A, 0x64, 0x78), Color.FromRgb(0x23, 0x2A, 0x3B), 90),
        };
        stage.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 270, Height = 270, Fill = new SolidColorBrush(Color.FromArgb(0x2E, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 170, 0, 0),
        });
        stage.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 780, Height = 620, Fill = new SolidColorBrush(Color.FromArgb(0x2E, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 470, 0, 0),
        });
        stage.Children.Add(new Border
        {
            Height = 320, VerticalAlignment = VerticalAlignment.Bottom,
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xB0, 0, 0, 0), 90),
        });
        stage.Children.Add(hint);
        return stage;
    }

    private static void RenderHint(string dir, string name, HintStyle style, HintPosition position, bool wheel, WheelHintStyle wheelStyle)
    {
        var hint = new GuestHint
        {
            Text = position == HintPosition.Top ? "Drücke die Eingabetaste für ein Video 🎬" : "Drücke die Leertaste oder klicke für ein Foto 📸",
            HintStyle = style,
            Position = position,
            ShowLayoutHint = wheel,
            LayoutHintText = "Mausrad: Layout wechseln · 3 Fotos – 2 Streifen",
            LayoutHintStyle = wheelStyle,
        };
        var stage = Stage(hint);
        // In einem (außerhalb des Bildschirms) angezeigten Fenster rendern: Nur dort werden Effekte (Leuchten, Schatten) gezeichnet.
        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, Content = stage,
        };
        try
        {
            window.Show();
            Layout(stage, 1920, 1080);
            if (style == HintStyle.Ticker)
            {
                // Standbild mitten im Lauf: Animation anhalten und den Text ins Band schieben.
                var text = FindByType<TextBlock>(stage, t => t.RenderTransform is TranslateTransform && t.Text == hint.Text);
                var shift = (TranslateTransform)text!.RenderTransform;
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.X = 520;
            }
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            SavePng(stage, 1920, 1080, Path.Combine(dir, name + ".png"));
        }
        finally
        {
            window.Close();
        }
    }

    private static T? FindByType<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && match(t))
                return t;
            var nested = FindByType(child, match);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public void Render_GuestHint_AllStyles()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        foreach (var style in Enum.GetValues<HintStyle>())
        {
            RenderHint(dir, $"hint_{style}_bottom_wheel-Matching", style, HintPosition.Bottom, wheel: true, WheelHintStyle.Matching);
            RenderHint(dir, $"hint_{style}_top", style, HintPosition.Top, wheel: false, WheelHintStyle.Matching);
        }
        foreach (var wheelStyle in Enum.GetValues<WheelHintStyle>().Where(w => w != WheelHintStyle.Matching))
            RenderHint(dir, $"hint_Pill_bottom_wheel-{wheelStyle}", HintStyle.Pill, HintPosition.Bottom, wheel: true, wheelStyle);

        Assert.True(Directory.GetFiles(dir, "hint_*.png").Length >= 22);
    }

    [Trait("Category", "Visual")]
    [StaTheory]
    [InlineData(AdminThemeMode.Light)]
    [InlineData(AdminThemeMode.Dark)]
    public void Render_AdminGuestScreenTab(AdminThemeMode mode)
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        var work = Path.Combine(Path.GetTempPath(), "fotobox-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var store = new AdminViewModelTests.FakeStore(work);
            var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi());
            try
            {
                vm.Draft.Appearance.AdminTheme = mode;
                vm.IsGuestStripDouble = true;   // Mausradzeile erscheint in der Vorschau
                if (mode == AdminThemeMode.Dark)
                {
                    vm.HintStyle = HintStyle.Neon;
                    vm.VideoHintStyle = HintStyle.Cinema;
                    vm.WheelHintStyle = WheelHintStyle.Chip;
                }

                var window = new AdminWindow(vm);
                AdminThemeManager.Apply(window, mode);
                var tabs = window.Content is DependencyObject root ? FindTabControl(root) : null;
                Assert.NotNull(tabs);
                tabs!.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Gastansicht");
                var content = (FrameworkElement)window.Content;
                Layout(content, 1100, 750);

                var scroll = FindScrollViewer(content);
                Assert.NotNull(scroll);
                var card = (FrameworkElement)scroll!.Content;
                Layout(content, 1100, 750);
                var width = (int)Math.Ceiling(card.ActualWidth);
                var height = (int)Math.Ceiling(card.ActualHeight);
                Assert.True(width > 0 && height > 0);

                // Ganzer scrollbarer Inhalt (ohne Rücksicht auf den Ausschnitt des ScrollViewers) auf dem Fensterhintergrund.
                SavePng(card, width, height, Path.Combine(dir, $"admin_guestscreen_{mode}_content.png"), (Brush)window.FindResource("BgBrush"));
                // Der sichtbare Ausschnitt des Fensters, wie ihn der Admin zuerst sieht.
                SavePng(content, 1100, 750, Path.Combine(dir, $"admin_guestscreen_{mode}_window.png"), (Brush)window.FindResource("BgBrush"));
            }
            finally
            {
                vm.Dispose();
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static TabControl? FindTabControl(DependencyObject parent)
    {
        if (parent is TabControl tabs)
            return tabs;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindTabControl(VisualTreeHelper.GetChild(parent, i));
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>Der ScrollViewer des ausgewählten Tabs (der innerste mit Inhalt in der Tab-Fläche).</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll && scroll.Content is FrameworkElement)
            return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(parent, i));
            if (found is not null)
                return found;
        }
        return null;
    }
}
