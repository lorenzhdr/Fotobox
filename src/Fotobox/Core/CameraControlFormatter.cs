using System.Globalization;

namespace Fotobox.Core;

/// <summary>Gruppen der Regler im Admin (Reihenfolge = Reihenfolge der Anzeige).</summary>
public enum CameraControlGroup { ZoomAndFraming, Picture, Exposure, FocusAndWhiteBalance }

/// <summary>Deutsche Namen und kulturfeste Anzeige (de-DE) der Kamera-Regler.</summary>
public static class CameraControlFormatter
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string DisplayName(CameraControlKind kind) => kind switch
    {
        CameraControlKind.Zoom => "Zoom",
        CameraControlKind.Pan => "Schwenken",
        CameraControlKind.Tilt => "Neigen",
        CameraControlKind.Brightness => "Helligkeit",
        CameraControlKind.Contrast => "Kontrast",
        CameraControlKind.Hue => "Farbton",
        CameraControlKind.Exposure => "Belichtung",
        CameraControlKind.BacklightCompensation => "Gegenlichtausgleich",
        CameraControlKind.Focus => "Fokus",
        CameraControlKind.WhiteBalance => "Weißabgleich",
        CameraControlKind.ExposureCompensation => "Belichtungskorrektur",
        _ => kind.ToString(),
    };

    public static CameraControlGroup GroupOf(CameraControlKind kind) => kind switch
    {
        CameraControlKind.Zoom or CameraControlKind.Pan or CameraControlKind.Tilt => CameraControlGroup.ZoomAndFraming,
        CameraControlKind.Brightness or CameraControlKind.Contrast or CameraControlKind.Hue => CameraControlGroup.Picture,
        CameraControlKind.Exposure or CameraControlKind.ExposureCompensation or CameraControlKind.BacklightCompensation => CameraControlGroup.Exposure,
        CameraControlKind.Focus or CameraControlKind.WhiteBalance => CameraControlGroup.FocusAndWhiteBalance,
        _ => CameraControlGroup.Picture,
    };

    public static string GroupTitle(CameraControlGroup group) => group switch
    {
        CameraControlGroup.ZoomAndFraming => "Zoom & Ausschnitt",
        CameraControlGroup.Picture => "Bild",
        CameraControlGroup.Exposure => "Belichtung",
        CameraControlGroup.FocusAndWhiteBalance => "Fokus & Weißabgleich",
        _ => group.ToString(),
    };

    /// <summary>
    /// Anzeige eines Rohwerts: standardmäßig Prozent des Bereichs („62 %“, bei Min == Max „–“); Zoom mit Min &gt; 0 als Faktor
    /// („1,5×“); Weißabgleich in Kelvin („4500 K“) bei einem Bereich von 1000 bis 15000; Gegenlichtausgleich im Bereich 0..1 als „Aus“/„An“; Belichtungskorrektur in EV mit Vorzeichen („+0,5 EV“, „0 EV“, „−1,3 EV“).
    /// </summary>
    public static string FormatValue(CameraControlInfo info, double value)
    {
        if (!double.IsFinite(value))
            return "–";

        if (info.Kind == CameraControlKind.ExposureCompensation)
        {
            var ev = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            if (ev == 0)
                return "0 EV";
            return (ev > 0 ? "+" : "−") + Math.Abs(ev).ToString("0.0", De) + " EV";
        }

        if (info.Kind == CameraControlKind.BacklightCompensation && info.Min == 0 && info.Max == 1)
            return value >= 0.5 ? "An" : "Aus";

        if (info.Kind == CameraControlKind.WhiteBalance && info.Min >= 1000 && info.Max <= 15000)
            return Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", De) + " K";

        if (info.Kind == CameraControlKind.Zoom && info.Min > 0)
            return (value / info.Min).ToString("0.0", De) + "×";

        var range = info.Max - info.Min;
        if (!(range > 0) || !double.IsFinite(range))
            return "–";
        var percent = Math.Clamp((value - info.Min) / range * 100, 0, 100);
        return Math.Round(percent, MidpointRounding.AwayFromZero).ToString("0", De) + " %";
    }

    public static string PowerlineLabel(CameraPowerline value) => value switch
    {
        CameraPowerline.Unchanged => "Nicht verändern",
        CameraPowerline.Off => "Aus",
        CameraPowerline.Hz50 => "50 Hz",
        CameraPowerline.Hz60 => "60 Hz",
        CameraPowerline.Auto => "Automatisch",
        _ => value.ToString(),
    };
}
