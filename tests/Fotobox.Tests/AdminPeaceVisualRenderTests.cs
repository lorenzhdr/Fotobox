using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur, wenn die Umgebungsvariable <c>FOTOBOX_RENDER_DIR</c> auf einen
/// Ordner zeigt (sonst sofort beendet, auch in der CI). Rendert die Startseite des Admin-Tabs „Fun“ und die Seite „Peaceerkennung“
/// im hellen und dunklen Schema als PNG.
/// Aufruf: <c>FOTOBOX_RENDER_DIR=C:\pfad\render dotnet test --filter "FullyQualifiedName~AdminPeaceVisualRenderTests"</c>.
/// </summary>
[Trait("Category", "Visual")]
[Collection(WpfCollection.Name)]
public sealed class AdminPeaceVisualRenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-peace-visual-" + Guid.NewGuid().ToString("N"));

    public AdminPeaceVisualRenderTests() => Directory.CreateDirectory(Path.Combine(_dir, "overlays"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string? RenderDir()
    {
        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void Render(string outDir, string name, AdminThemeMode theme, FunPage page, int width, int height)
    {
        var store = new AdminViewModelTests.FakeStore(_dir);
        using var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
            new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi());
        vm.IsAdminThemeLight = theme == AdminThemeMode.Light;
        vm.IsAdminThemeDark = theme == AdminThemeMode.Dark;
        vm.FunPage = page;
        vm.PeaceActionPhoto = true;
        vm.PeaceActionMoustache = true;
        var window = new AdminWindow(vm) { ShowInTaskbar = false, Width = width, Height = height };
        var tabs = FindTabControl(window);
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Fun");

        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle((Brush)window.Resources["BgBrush"], null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(outDir, name));
        encoder.Save(stream);
    }

    private static TabControl FindTabControl(DependencyObject parent) =>
        TryFind(parent) ?? throw new InvalidOperationException("Kein TabControl gefunden.");

    private static TabControl? TryFind(DependencyObject parent)
    {
        if (parent is TabControl tabs)
            return tabs;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (TryFind(child) is { } found)
                return found;
        }
        return null;
    }

    [StaFact]
    public void Render_StartPageAndPeacePage()
    {
        var dir = RenderDir();
        if (dir is null)
            return;
        SynchronizationContext.SetSynchronizationContext(null);

        foreach (var theme in new[] { AdminThemeMode.Light, AdminThemeMode.Dark })
        {
            var suffix = theme == AdminThemeMode.Light ? "hell" : "dunkel";
            Render(dir, $"admin-fun-start-{suffix}.png", theme, FunPage.Start, 1100, 750);
            Render(dir, $"admin-fun-peace-{suffix}.png", theme, FunPage.Peace, 1100, 3300);
        }
    }
}
