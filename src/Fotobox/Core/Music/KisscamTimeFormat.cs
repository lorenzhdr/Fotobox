using System.Globalization;

namespace Fotobox.Core.Music;

/// <summary>Anzeige und Eingabe von Startzeiten und Songlängen als <c>m:ss</c>.</summary>
public static class KisscamTimeFormat
{
    /// <summary>Sekunden als „m:ss“ (z. B. 62 → „1:02“); negative Werte gelten als 0.</summary>
    public static string Format(int totalSeconds)
    {
        var seconds = Math.Max(0, totalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>Songlänge als „m:ss“ (auf ganze Sekunden gerundet); unbekannt = „–“.</summary>
    public static string FormatDuration(TimeSpan? duration) =>
        duration is { } d && d > TimeSpan.Zero ? Format((int)Math.Round(d.TotalSeconds, MidpointRounding.AwayFromZero)) : "–";

    /// <summary>
    /// Liest „m:ss“ (Sekundenteil 0–59) oder eine reine Sekundenzahl („90“). Leerraum am Rand ist erlaubt; negative Werte, Brüche und
    /// anderer Text ergeben <c>false</c>. Das Ergebnis wird nicht geklemmt (das macht der Aufrufer über <see cref="SettingsLimits"/>).
    /// </summary>
    public static bool TryParse(string? text, out int totalSeconds)
    {
        totalSeconds = 0;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
            return false;

        var colon = value.IndexOf(':');
        if (colon < 0)
            return TryParseDigits(value, out totalSeconds);

        if (value.IndexOf(':', colon + 1) >= 0
            || !TryParseDigits(value[..colon], out var minutes)
            || !TryParseDigits(value[(colon + 1)..], out var seconds)
            || seconds > 59
            || minutes > int.MaxValue / 60 - 1)
            return false;

        totalSeconds = minutes * 60 + seconds;
        return true;
    }

    private static bool TryParseDigits(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 9)
            return false;
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
