using System.IO;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Prüft, dass die XAML-Dateien der Admin-Ansicht zur Laufzeit ladbar sind (ohne sie anzuzeigen).</summary>
public class AdminViewTests
{
    [StaFact]
    public void PinDialogs_CanBeCreated()
    {
        var limiter = new PinAttemptLimiter(TimeProvider.System);

        var pin = new PinDialog(_ => false, limiter, owner: null);
        var first = new FirstRunPinDialog(owner: null);

        Assert.NotNull(pin);
        Assert.NotNull(first);
    }

    [StaFact]
    public void AdminWindow_CanBeCreated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AdminViewModelTests.FakeStore(dir);
            var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi());

            var window = new AdminWindow(vm);

            Assert.Same(vm, window.DataContext);
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [StaFact]
    public void TestPageRenderer_ProducesPageSizedFrozenBitmap()
    {
        var page = PageSpec.Default;

        var bitmap = TestPageRenderer.Render(page);

        Assert.Equal(page.PixelWidth, bitmap.PixelWidth);
        Assert.Equal(page.PixelHeight, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
    }
}
