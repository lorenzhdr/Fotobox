namespace Fotobox.Core;

/// <summary>Regeln für die PIN-Eingabe (Länge: <see cref="SettingsLimits.PinMinLength"/> bis <see cref="SettingsLimits.PinMaxLength"/> Ziffern) mit deutschen Fehlermeldungen.</summary>
public static class PinRules
{
    /// <summary>Längenangabe für Beschriftungen, z. B. „4 bis 12 Ziffern“.</summary>
    public static string LengthText => $"{SettingsLimits.PinMinLength} bis {SettingsLimits.PinMaxLength} Ziffern";

    public static bool IsValid(string? pin) =>
        pin is { Length: >= SettingsLimits.PinMinLength and <= SettingsLimits.PinMaxLength } && pin.All(char.IsAsciiDigit);

    /// <summary>Prüft PIN und Wiederholung. Gibt null zurück, wenn alles passt.</summary>
    public static string? GetError(string? pin, string? repeat)
    {
        if (string.IsNullOrEmpty(pin))
            return "Bitte geben Sie eine PIN ein.";
        if (!pin.All(char.IsAsciiDigit))
            return "Die PIN darf nur aus Ziffern bestehen.";
        if (pin.Length < SettingsLimits.PinMinLength || pin.Length > SettingsLimits.PinMaxLength)
            return $"Die PIN muss aus {SettingsLimits.PinMinLength} bis {SettingsLimits.PinMaxLength} Ziffern bestehen.";
        if (!string.Equals(pin, repeat, StringComparison.Ordinal))
            return "Die beiden PIN-Eingaben stimmen nicht überein.";
        return null;
    }
}
