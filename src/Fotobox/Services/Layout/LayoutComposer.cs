// Stub – wird von Stream C implementiert
using System.Windows.Media.Imaging;
using Fotobox.Core;

namespace Fotobox.Services.Layout;

public sealed class LayoutComposer : ILayoutComposer
{
    public int GetRequiredPhotoCount(LayoutSettings layout) =>
        layout.Kind == LayoutKind.Strip ? Math.Clamp(layout.StripPhotoCount, 2, 4) : 1;

    public BitmapSource Compose(IReadOnlyList<BitmapSource> photos, LayoutSettings layout, PageSpec page, string overlayDirectory, DateTime timestamp) =>
        throw new NotImplementedException("Stub – wird von Stream C implementiert.");
}
