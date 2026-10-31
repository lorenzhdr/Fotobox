using System.Windows.Media;
using Fotobox.Core;

namespace Fotobox.Views.Booth;

/// <summary>
/// Die feste Farbpalette der Druckbereich-Markierung (<see cref="PrintAreaColor"/>): einzige Quelle für Gastansicht (<see cref="PrintAreaOverlay"/>)
/// und die Farbpunkte im Admin. Die Farben sind bewusst fest (wie die Zielerfassung), keine Admin-Farben.
/// </summary>
public static class PrintAreaPalette
{
    public static Color ColorOf(PrintAreaColor color) => color switch
    {
        PrintAreaColor.Black => Color.FromRgb(0x00, 0x00, 0x00),
        PrintAreaColor.Red => Color.FromRgb(0xFF, 0x3B, 0x30),
        PrintAreaColor.Yellow => Color.FromRgb(0xFF, 0xD6, 0x0A),
        PrintAreaColor.Green => Color.FromRgb(0x39, 0xFF, 0x14),
        PrintAreaColor.Blue => Color.FromRgb(0x0A, 0x84, 0xFF),
        PrintAreaColor.Pink => Color.FromRgb(0xFF, 0x2D, 0x95),
        PrintAreaColor.Orange => Color.FromRgb(0xFF, 0x95, 0x00),
        _ => Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    public static Brush BrushOf(PrintAreaColor color) => color switch
    {
        PrintAreaColor.Black => Black,
        PrintAreaColor.Red => Red,
        PrintAreaColor.Yellow => Yellow,
        PrintAreaColor.Green => Green,
        PrintAreaColor.Blue => Blue,
        PrintAreaColor.Pink => Pink,
        PrintAreaColor.Orange => Orange,
        _ => White,
    };

    // Als Eigenschaften für {x:Static} in den Farbpunkten des Admins.
    public static Brush White { get; } = Make(PrintAreaColor.White);
    public static Brush Black { get; } = Make(PrintAreaColor.Black);
    public static Brush Red { get; } = Make(PrintAreaColor.Red);
    public static Brush Yellow { get; } = Make(PrintAreaColor.Yellow);
    public static Brush Green { get; } = Make(PrintAreaColor.Green);
    public static Brush Blue { get; } = Make(PrintAreaColor.Blue);
    public static Brush Pink { get; } = Make(PrintAreaColor.Pink);
    public static Brush Orange { get; } = Make(PrintAreaColor.Orange);

    private static Brush Make(PrintAreaColor color)
    {
        var brush = new SolidColorBrush(ColorOf(color));
        brush.Freeze();
        return brush;
    }
}
