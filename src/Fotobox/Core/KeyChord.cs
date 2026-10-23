using System.Windows.Input;

namespace Fotobox.Core;

/// <summary>
/// Eine Taste mit Modifikatoren (Strg/Alt/Umschalt). Textform für settings.json: "Ctrl+Alt+A", "Space", "Escape".
/// Bewusst kein WPF-<see cref="KeyGesture"/>: Das lehnt Buchstaben ohne Modifikator ab.
/// </summary>
public readonly record struct KeyChord(Key Key, ModifierKeys Modifiers)
{
    private const ModifierKeys AllowedModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

    /// <summary>Taste aus einem Tastaturereignis: <c>Key.System</c> (Alt gedrückt) → <paramref name="systemKey"/>.</summary>
    public static KeyChord FromKeyEvent(Key key, Key systemKey, ModifierKeys modifiers)
    {
        var k = key == Key.System ? systemKey : key;
        return new KeyChord(k, modifiers);
    }

    /// <summary>Liest "Ctrl+Alt+A" (Groß-/Kleinschreibung egal). Keine Prüfung auf <see cref="IsAllowed"/>.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        // "Ctrl++" o. Ä.: die Taste "+" heißt in WPF OemPlus/Add, daher ist ein leerer Teil immer ungültig.
        if (parts.Any(p => p.Length == 0))
            return false;

        var modifiers = ModifierKeys.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var m = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" or "strg" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" or "umschalt" => ModifierKeys.Shift,
                _ => ModifierKeys.None,
            };
            if (m == ModifierKeys.None || (modifiers & m) != 0)
                return false;
            modifiers |= m;
        }

        var keyText = parts[^1];
        if (keyText.All(char.IsDigit) || !Enum.TryParse<Key>(keyText, ignoreCase: true, out var key) || !Enum.IsDefined(key))
            return false;

        chord = new KeyChord(key, modifiers);
        return true;
    }

    /// <summary>
    /// Darf als Belegung verwendet werden? Nicht erlaubt: reine Modifikatortasten, Windows-Taste und
    /// System-Kombinationen (Alt+F4, Alt+Tab, Alt+Leertaste, Alt+Esc, Strg+Esc, Strg+Alt+Entf).
    /// </summary>
    public bool IsAllowed
    {
        get
        {
            if ((Modifiers & ~AllowedModifiers) != 0)
                return false;
            if (Key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed
                or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return false;

            var alt = Modifiers.HasFlag(ModifierKeys.Alt);
            var ctrl = Modifiers.HasFlag(ModifierKeys.Control);
            if (alt && Key is Key.F4 or Key.Tab or Key.Space or Key.Escape)
                return false;
            if (ctrl && Key == Key.Escape)
                return false;
            if (ctrl && alt && Key == Key.Delete)
                return false;
            return true;
        }
    }

    /// <summary>Kanonische Textform für settings.json, z. B. "Ctrl+Alt+A".</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    /// <summary>Gängige Namen statt der gleichwertigen Enum-Aliase (Next, Prior, Return …).</summary>
    private static string KeyName(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.PageDown => "PageDown",
        Key.PageUp => "PageUp",
        Key.CapsLock => "CapsLock",
        Key.PrintScreen => "PrintScreen",
        _ => key.ToString(),
    };

    /// <summary>Deutscher Anzeigename, z. B. "Strg+Alt+A", "Leertaste", "Bild ab".</summary>
    public string DisplayName
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Strg");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Umschalt");
            parts.Add(KeyDisplayName(Key));
            return string.Join("+", parts);
        }
    }

    private static string KeyDisplayName(Key key) => key switch
    {
        Key.Space => "Leertaste",
        Key.Enter => "Eingabetaste",
        Key.Escape => "Esc",
        Key.Back => "Rücktaste",
        Key.Tab => "Tab",
        Key.Delete => "Entf",
        Key.Insert => "Einfg",
        Key.Home => "Pos1",
        Key.End => "Ende",
        Key.PageUp => "Bild auf",
        Key.PageDown => "Bild ab",
        Key.Left => "Pfeil links",
        Key.Right => "Pfeil rechts",
        Key.Up => "Pfeil hoch",
        Key.Down => "Pfeil runter",
        Key.Add => "Ziffernblock +",
        Key.Subtract => "Ziffernblock −",
        Key.Multiply => "Ziffernblock ×",
        Key.Divide => "Ziffernblock ÷",
        Key.Decimal => "Ziffernblock ,",
        Key.MediaPlayPause => "Wiedergabe/Pause",
        Key.MediaNextTrack => "Nächster Titel",
        Key.MediaPreviousTrack => "Vorheriger Titel",
        Key.VolumeUp => "Lauter",
        Key.VolumeDown => "Leiser",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Ziffernblock " + (key - Key.NumPad0),
        _ => key.ToString(),
    };
}
