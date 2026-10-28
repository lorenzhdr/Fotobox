using System.Windows;
using System.Windows.Media;
using Fotobox.Core;

namespace Fotobox.Views.Booth.Peace;

/// <summary>
/// Die Bartformen als eingefrorene Geometrien im Einheitsmaß: Breite 1, Mitte (0, 0), Höhe bis etwa 0,3. Eine Quelle für
/// Livebild, Foto und Admin-Skizzen. Platzhalter: bis zur echten Zeichnung ist jede Form eine Ellipse.
/// </summary>
public static class MoustacheShapes
{
    private static readonly Geometry Placeholder = CreatePlaceholder();

    /// <summary>
    /// Geometrie der Form. <see cref="MoustacheStyle.Random"/> ist keine Form: Der Aufrufer löst es vorher in eine konkrete Form
    /// auf; hier liefert es vorsichtshalber <see cref="MoustacheStyle.Twirl"/>.
    /// </summary>
    public static Geometry Get(MoustacheStyle style) => Placeholder;

    private static Geometry CreatePlaceholder()
    {
        var geometry = new EllipseGeometry(new Point(0, 0), 0.5, 0.15);
        geometry.Freeze();
        return geometry;
    }
}
