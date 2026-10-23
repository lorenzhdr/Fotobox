using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using Fotobox.Core;

namespace Fotobox.Services.Printing;

/// <summary>Reine Hilfsfunktionen für Papiergröße, Ausrichtung und Geometrie (ohne Spooler-Zugriff, gut testbar).</summary>
public static class PrintLayoutResolver
{
    private const double DipsPerInch = 96.0;
    private const double MmPerInch = 25.4;

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static readonly Regex IsoName = new(@"^(?<p>ISO|JIS)?(?<s>[AB])(?<n>\d+)$", RegexOptions.Compiled);

    /// <summary>WPF-Einheiten (1/96 Zoll) → Millimeter.</summary>
    public static double DipToMm(double dip) => dip / DipsPerInch * MmPerInch;

    /// <summary>Millimeter → WPF-Einheiten (1/96 Zoll).</summary>
    public static double MmToDip(double mm) => mm / MmPerInch * DipsPerInch;

    /// <summary>Auto: Strip → Hochformat, sonst Querformat.</summary>
    public static bool ResolveLandscape(Core.PageOrientation orientation, LayoutKind layout) => orientation switch
    {
        Core.PageOrientation.Portrait => false,
        Core.PageOrientation.Landscape => true,
        _ => layout != LayoutKind.Strip,
    };

    /// <summary>Schlüssel für <see cref="PaperSizeInfo.Name"/>: Enum-Name oder "{w}x{h}mm" bei unbekannter Größe.</summary>
    public static string BuildPaperName(string? mediaSizeName, double widthMm, double heightMm)
    {
        if (!string.IsNullOrWhiteSpace(mediaSizeName) && !string.Equals(mediaSizeName, "Unknown", StringComparison.OrdinalIgnoreCase))
            return mediaSizeName;
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(widthMm):0.#}x{Math.Round(heightMm):0.#}mm");
    }

    /// <summary>Lesbarer deutscher Name, z. B. "10 × 15 cm (4 × 6 Zoll)" oder "A4 (210 × 297 mm)".</summary>
    public static string BuildDisplayName(string? mediaSizeName, double widthMm, double heightMm)
    {
        var mmText = string.Format(German, "{0} × {1} mm", FormatMm(widthMm), FormatMm(heightMm));

        var pretty = PrettyStandardName(mediaSizeName);
        if (pretty is not null)
            return $"{pretty} ({mmText})";

        var wi = widthMm / MmPerInch;
        var hi = heightMm / MmPerInch;
        if (IsNearWhole(wi) && IsNearWhole(hi))
        {
            return string.Format(German, "{0} × {1} cm ({2} × {3} Zoll)",
                FormatCm(widthMm), FormatCm(heightMm), Math.Round(wi), Math.Round(hi));
        }

        return mmText;
    }

    /// <summary>Erzeugt eine Papiergröße aus WPF-Einheiten (Breite/Höhe in 1/96 Zoll), Maße auf 0,1 mm gerundet.</summary>
    public static PaperSizeInfo CreatePaperSize(string? mediaSizeName, double widthDip, double heightDip)
    {
        var w = Math.Round(DipToMm(widthDip), 1);
        var h = Math.Round(DipToMm(heightDip), 1);
        return new PaperSizeInfo(BuildPaperName(mediaSizeName, w, h), BuildDisplayName(mediaSizeName, w, h), w, h);
    }

    /// <summary>
    /// Wählt die Papiergröße: PaperSizeName aus den Treibergrößen → PaperWidthMm/PaperHeightMm (Hochformat normiert)
    /// → Standardgröße des Druckers → null.
    /// </summary>
    public static PaperSizeInfo? ResolvePaper(PrinterSettings printer, IReadOnlyList<PaperSizeInfo> available, PaperSizeInfo? driverDefault)
    {
        if (!string.IsNullOrWhiteSpace(printer.PaperSizeName))
        {
            var match = available.FirstOrDefault(p => string.Equals(p.Name, printer.PaperSizeName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        if (printer.PaperWidthMm is > 0 and { } w && printer.PaperHeightMm is > 0 and { } h)
        {
            var (shortSide, longSide) = (Math.Min(w, h), Math.Max(w, h));
            return new PaperSizeInfo(
                BuildPaperName(null, shortSide, longSide),
                BuildDisplayName(null, shortSide, longSide),
                shortSide, longSide);
        }

        return driverDefault;
    }

    /// <summary>Seitenspezifikation (300 dpi) mit aufgelöster Ausrichtung; ohne Papier → <see cref="PageSpec.Default"/>.</summary>
    public static PageSpec ResolvePageSpec(PrinterSettings printer, LayoutKind layout, IReadOnlyList<PaperSizeInfo> available, PaperSizeInfo? driverDefault)
    {
        var paper = ResolvePaper(printer, available, driverDefault);
        var landscape = ResolveLandscape(printer.Orientation, layout);

        var portrait = paper is { WidthMm: > 0, HeightMm: > 0 }
            ? new PageSpec(Math.Min(paper.WidthMm, paper.HeightMm), Math.Max(paper.WidthMm, paper.HeightMm))
            : PageSpec.Default;
        return portrait.ToOrientation(landscape);
    }

    /// <summary>Muss das Bild um 90° gedreht werden, damit es in der nativen Ausrichtung des Mediums liegt?</summary>
    public static bool NeedsRotation(double imageWidth, double imageHeight, double mediaWidth, double mediaHeight)
    {
        var imageLandscape = imageWidth > imageHeight;
        var imagePortrait = imageHeight > imageWidth;
        var mediaLandscape = mediaWidth > mediaHeight;
        var mediaPortrait = mediaHeight > mediaWidth;
        return (imageLandscape && mediaPortrait) || (imagePortrait && mediaLandscape);
    }

    /// <summary>Crop-to-fill: Zielrechteck (ggf. größer als die Box, zentriert), nie gestreckt, nie Rand.</summary>
    public static Rect ComputeFillRect(double imageWidth, double imageHeight, double boxWidth, double boxHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
            return new Rect(0, 0, boxWidth, boxHeight);

        var scale = Math.Max(boxWidth / imageWidth, boxHeight / imageHeight);
        var w = imageWidth * scale;
        var h = imageHeight * scale;
        return new Rect((boxWidth - w) / 2, (boxHeight - h) / 2, w, h);
    }

    private static string? PrettyStandardName(string? mediaSizeName)
    {
        if (string.IsNullOrWhiteSpace(mediaSizeName))
            return null;

        var iso = IsoName.Match(mediaSizeName);
        if (iso.Success)
        {
            var prefix = iso.Groups["p"].Value == "JIS" ? "JIS " : string.Empty;
            return $"{prefix}{iso.Groups["s"].Value}{iso.Groups["n"].Value}";
        }

        return mediaSizeName switch
        {
            "NorthAmericaLetter" => "Letter",
            "NorthAmericaLegal" => "Legal",
            "NorthAmericaTabloid" => "Tabloid",
            "NorthAmericaExecutive" => "Executive",
            _ => null,
        };
    }

    private static bool IsNearWhole(double inches) => Math.Round(inches) >= 1 && Math.Abs(inches - Math.Round(inches)) <= 0.06;

    private static string FormatMm(double mm) =>
        Math.Abs(mm - Math.Round(mm)) < 0.05 ? Math.Round(mm).ToString("0", German) : mm.ToString("0.#", German);

    // Fotopapier wird üblicherweise in ganzen Zentimetern benannt (4 × 6 Zoll = 10 × 15 cm, 5 × 7 Zoll = 13 × 18 cm).
    private static string FormatCm(double mm) => Math.Round(mm / 10.0).ToString("0", German);
}
