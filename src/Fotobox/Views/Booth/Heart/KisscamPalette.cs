using System.Windows.Media;
using Fotobox.Core;

namespace Fotobox.Views.Booth.Heart;

/// <summary>Farben eines Kisscam-Farbschemas. <see cref="Frame"/> füllt das Rahmenband vollflächig (darauf prüfen Tests).</summary>
/// <param name="Frame">Rahmenband.</param>
/// <param name="FrameDark">Dunklerer Ton für Schatten und Kante des Rahmens.</param>
/// <param name="Banner">Bannerfläche (kräftig).</param>
/// <param name="BannerEdge">Kante des Banners.</param>
/// <param name="Accent">Heller Akzent: Innenkante des Rahmens, Lichter, Glühen.</param>
/// <param name="Text">Bannertext (hebt sich von <see cref="Banner"/> ab).</param>
/// <param name="Heart">Füllung der Eckherzen.</param>
/// <param name="HeartEdge">Kontur der Eckherzen.</param>
public sealed record KisscamColors(Color Frame, Color FrameDark, Color Banner, Color BannerEdge, Color Accent, Color Text, Color Heart, Color HeartEdge);

/// <summary>
/// Inhaltsfarben des Kisscam-Rahmens je <see cref="KisscamColor"/> (wie <c>MoustachePalette</c>: Farben des Bildes, keine Admin-Farben,
/// daher nicht in den Admin-Paletten).
/// </summary>
public static class KisscamPalette
{
    public static KisscamColors For(KisscamColor color) => color switch
    {
        KisscamColor.Red => Make(0xC6, 0x28, 0x28, 0x7F, 0x0A, 0x0A, 0xD5, 0x00, 0x00, 0xFF, 0xCD, 0xD2, 0xFF, 0x8A, 0x80, 0xFF, 0xFF, 0xFF, 0xFF, 0x3D, 0x4D, 0xFF, 0xFF, 0xFF),
        KisscamColor.Gold => Make(0xD4, 0xA0, 0x17, 0x80, 0x5E, 0x00, 0xF2, 0xB6, 0x00, 0xFF, 0xF1, 0xB3, 0xFF, 0xE0, 0x82, 0x3B, 0x2A, 0x00, 0xFF, 0xC1, 0x07, 0xFF, 0xF8, 0xE1),
        KisscamColor.Purple => Make(0x7B, 0x1F, 0xA2, 0x3E, 0x0F, 0x70, 0x8E, 0x24, 0xAA, 0xE1, 0xBE, 0xE7, 0xCE, 0x93, 0xD8, 0xFF, 0xFF, 0xFF, 0xD0, 0x6B, 0xE8, 0xFF, 0xFF, 0xFF),
        KisscamColor.BlackWhite => Make(0x12, 0x12, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00),
        _ => Make(0xE9, 0x1E, 0x63, 0x9C, 0x0F, 0x45, 0xD8, 0x14, 0x4E, 0xFF, 0xD1, 0xDC, 0xFF, 0xB3, 0xC7, 0xFF, 0xFF, 0xFF, 0xFF, 0x3D, 0x7F, 0xFF, 0xFF, 0xFF),
    };

    private static KisscamColors Make(byte fr, byte fg, byte fb, byte dr, byte dg, byte db, byte br, byte bg, byte bb, byte er, byte eg, byte eb,
        byte ar, byte ag, byte ab, byte tr, byte tg, byte tb, byte hr, byte hg, byte hb, byte xr, byte xg, byte xb) =>
        new(Color.FromRgb(fr, fg, fb), Color.FromRgb(dr, dg, db), Color.FromRgb(br, bg, bb), Color.FromRgb(er, eg, eb),
            Color.FromRgb(ar, ag, ab), Color.FromRgb(tr, tg, tb), Color.FromRgb(hr, hg, hb), Color.FromRgb(xr, xg, xb));
}
