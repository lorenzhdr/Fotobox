namespace Fotobox.ViewModels.Admin;

/// <summary>Regeln für die PIN-Eingabe (4–12 Ziffern) mit deutschen Fehlermeldungen.</summary>
public static class PinRules
{
    public const int MinLength = 4;
    public const int MaxLength = 12;

    public static bool IsValid(string? pin) =>
        pin is { Length: >= MinLength and <= MaxLength } && pin.All(char.IsAsciiDigit);

    /// <summary>Prüft PIN und Wiederholung. Gibt null zurück, wenn alles passt.</summary>
    public static string? GetError(string? pin, string? repeat)
    {
        if (string.IsNullOrEmpty(pin))
            return "Bitte geben Sie eine PIN ein.";
        if (!pin.All(char.IsAsciiDigit))
            return "Die PIN darf nur aus Ziffern bestehen.";
        if (pin.Length < MinLength || pin.Length > MaxLength)
            return $"Die PIN muss aus {MinLength} bis {MaxLength} Ziffern bestehen.";
        if (!string.Equals(pin, repeat, StringComparison.Ordinal))
            return "Die beiden PIN-Eingaben stimmen nicht überein.";
        return null;
    }
}
