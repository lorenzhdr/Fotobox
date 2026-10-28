using System.Windows.Media;
using Fotobox.Core.Peace;

namespace Fotobox.Views.Booth.Peace;

/// <summary>
/// Haarfarben der Bärte: je Farbe vier Stufen von der Wurzel (dunkel) bis zur Spitze (hell), realistische Töne. Die Haartextur
/// mischt daraus je Haar einen Farbverlauf; „Grau“ ist Salz und Pfeffer (die Haare streuen stark zwischen Dunkelgrau und Fast-Weiß).
/// </summary>
public static class MoustachePalette
{
    /// <summary>Die vier Stufen (Wurzel → Spitze) einer Haarfarbe.</summary>
    public static IReadOnlyList<Color> Stops(MoustacheColor color) => color switch
    {
        MoustacheColor.Black => Stops4(0x07, 0x06, 0x06, 0x14, 0x10, 0x0F, 0x2C, 0x25, 0x22, 0x56, 0x49, 0x42),
        MoustacheColor.DarkBrown => Stops4(0x16, 0x0D, 0x08, 0x2B, 0x1A, 0x10, 0x4A, 0x2E, 0x1A, 0x78, 0x50, 0x30),
        MoustacheColor.Brown => Stops4(0x2A, 0x17, 0x0C, 0x4F, 0x2D, 0x17, 0x7E, 0x4C, 0x2A, 0xB6, 0x80, 0x4E),
        MoustacheColor.Ginger => Stops4(0x4E, 0x1C, 0x0C, 0x86, 0x34, 0x14, 0xBE, 0x5C, 0x24, 0xEA, 0xA0, 0x5C),
        MoustacheColor.Blond => Stops4(0x6C, 0x4E, 0x22, 0xA2, 0x7C, 0x3C, 0xD4, 0xAE, 0x66, 0xF6, 0xDE, 0xA0),
        MoustacheColor.Gray => Stops4(0x2C, 0x2C, 0x30, 0x6A, 0x6A, 0x70, 0xAA, 0xAA, 0xB0, 0xF0, 0xF0, 0xF2),
        _ => Stops(MoustacheColor.DarkBrown),
    };

    /// <summary>Streuung der Haarhelligkeit je Haar (0 … 1): Grau mischt hell und dunkel kräftig, die übrigen Farben streuen wenig.</summary>
    internal static double Speckle(MoustacheColor color) => color == MoustacheColor.Gray ? 0.55 : 0.0;

    /// <summary>Farbe auf der Rampe bei <paramref name="t"/> (0 = Wurzel, 1 = Spitze), linear zwischen den Stufen.</summary>
    internal static Color Ramp(IReadOnlyList<Color> stops, double t)
    {
        t = Math.Clamp(t, 0, 1) * (stops.Count - 1);
        var i = Math.Min((int)t, stops.Count - 2);
        var f = t - i;
        var a = stops[i];
        var b = stops[i + 1];
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * f),
            (byte)Math.Round(a.G + (b.G - a.G) * f),
            (byte)Math.Round(a.B + (b.B - a.B) * f));
    }

    private static Color[] Stops4(byte r0, byte g0, byte b0, byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, byte r3, byte g3, byte b3) =>
    [
        Color.FromRgb(r0, g0, b0), Color.FromRgb(r1, g1, b1), Color.FromRgb(r2, g2, b2), Color.FromRgb(r3, g3, b3),
    ];
}
