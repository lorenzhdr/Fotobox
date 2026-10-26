using System.IO;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

/// <summary>Klemmt Einstellungswerte auf gültige Bereiche und ersetzt fehlende Unterobjekte.</summary>
internal static class SettingsNormalizer
{
    public const string DefaultFilePrefix = "Fotobox";
    public const int MinPinIterations = 10_000;
    /// <summary>
    /// Obergrenze, bis zu der gespeicherte PBKDF2-Iterationen als plausibel gelten (<see cref="PinService.Verify"/>).
    /// Wird hier bewusst NICHT geklemmt: ein anderer Wert passte nicht mehr zum vorhandenen Hash und sperrte den Admin aus.
    /// </summary>
    public const int MaxPlausiblePinIterations = 10_000_000;

    public static AppSettings Normalize(AppSettings s)
    {
        s.Camera ??= new CameraSettings();
        s.Flow ??= new FlowSettings();
        s.Layout ??= new LayoutSettings();
        s.Printer ??= new PrinterSettings();
        s.Storage ??= new StorageSettings();
        s.Security ??= new SecuritySettings();
        s.Input ??= new InputSettings();
        s.GuestScreen ??= new GuestScreenSettings();

        s.Flow.CountdownSeconds = Math.Clamp(s.Flow.CountdownSeconds, SettingsLimits.CountdownMin, SettingsLimits.CountdownMax);
        s.Flow.PreviewSeconds = Math.Clamp(s.Flow.PreviewSeconds, SettingsLimits.PreviewMin, SettingsLimits.PreviewMax);
        s.Flow.PauseBetweenShotsSeconds = Math.Clamp(s.Flow.PauseBetweenShotsSeconds, SettingsLimits.PauseMin, SettingsLimits.PauseMax);
        s.Flow.ErrorDisplaySeconds = Math.Clamp(s.Flow.ErrorDisplaySeconds, SettingsLimits.ErrorDisplayMin, SettingsLimits.ErrorDisplayMax);
        s.Flow.ExpectedPrintSeconds = Math.Clamp(s.Flow.ExpectedPrintSeconds, SettingsLimits.ExpectedPrintMin, SettingsLimits.ExpectedPrintMax);

        if (!Enum.IsDefined(s.Layout.Kind))
            s.Layout.Kind = LayoutKind.SingleBorderless;
        s.Layout.StripPhotoCount = Math.Clamp(s.Layout.StripPhotoCount, SettingsLimits.StripPhotosMin, SettingsLimits.StripPhotosMax);
        if (!Enum.IsDefined(s.Layout.StripStyle))
            s.Layout.StripStyle = StripStyle.DoubleCut;
        s.Layout.CaptionText = TrimToLength(s.Layout.CaptionText, SettingsLimits.CaptionMaxLength);
        s.Layout.CaptionFontFamily = SanitizeFontName(s.Layout.CaptionFontFamily);
        s.Layout.GuestChoices = NormalizeGuestChoices(s.Layout);

        if (!Enum.IsDefined(s.Printer.Orientation))
            s.Printer.Orientation = PageOrientation.Auto;
        s.Printer.Copies = Math.Clamp(s.Printer.Copies, SettingsLimits.CopiesMin, SettingsLimits.CopiesMax);
        // Das Timeout darf die erwartete Druckdauer nie unterschreiten, sonst bricht der Druck ab, bevor der Balken voll ist.
        s.Printer.TimeoutSeconds = Math.Clamp(
            Math.Max(s.Printer.TimeoutSeconds, s.Flow.ExpectedPrintSeconds + SettingsLimits.PrintTimeoutReserveSeconds),
            SettingsLimits.PrintTimeoutMin, SettingsLimits.PrintTimeoutMax);

        s.Storage.JpegQuality = Math.Clamp(s.Storage.JpegQuality, SettingsLimits.JpegQualityMin, SettingsLimits.JpegQualityMax);
        s.Storage.FilePrefix = SanitizePrefix(s.Storage.FilePrefix);

        if (s.Security.PinIterations < MinPinIterations)
            s.Security.PinIterations = MinPinIterations;

        NormalizeKeys(s.Input);

        if (!Enum.IsDefined(s.GuestScreen.HintStyle))
            s.GuestScreen.HintStyle = HintStyle.Pill;
        s.GuestScreen.HintText = SanitizeHintText(s.GuestScreen.HintText);

        return s;
    }

    /// <summary>
    /// Wahlliste der Gäste: undefinierte Werte und Duplikate raus, Overlay nur mit gesetzter Rahmendatei. Die Reihenfolge
    /// der Liste bleibt erhalten (sie ist die Reihenfolge beim Scrollen mit dem Mausrad). Ist die Liste danach nicht leer,
    /// enthält sie immer auch den Standard (fehlt er, wird er hinten angehängt; unabhängig von der Overlay-Regel – ein
    /// Standard-Overlay ohne Datei fängt die Admin-Validierung ab). Eine Liste mit nur einem Eintrag bedeutet: kein Wechsel.
    /// </summary>
    private static List<GuestLayout> NormalizeGuestChoices(LayoutSettings layout)
    {
        var hasOverlayFile = !string.IsNullOrWhiteSpace(layout.OverlayFileName);
        var result = new List<GuestLayout>();
        foreach (var g in layout.GuestChoices ?? [])
        {
            if (Enum.IsDefined(g) && (g != GuestLayout.Overlay || hasOverlayFile) && !result.Contains(g))
                result.Add(g);
        }

        var standard = GuestLayouts.From(layout);
        if (result.Count > 0 && !result.Contains(standard))
            result.Add(standard);
        return result;
    }

    /// <summary>Kürzt <paramref name="text"/> auf <paramref name="maxLength"/> Zeichen (statt das Speichern im Admin zu blockieren), ohne ein Surrogatpaar zu zerschneiden.</summary>
    private static string? TrimToLength(string? text, int maxLength)
    {
        if (text is null || text.Length <= maxLength)
            return text;

        var length = maxLength;
        if (char.IsHighSurrogate(text[length - 1]))
            length--; // kein Surrogatpaar zerschneiden
        return text[..length];
    }

    /// <summary>
    /// Eigener Hinweistext: Steuerzeichen und Zeilenumbrüche werden zu Leerzeichen, Ränder abgeschnitten, Länge auf
    /// <see cref="SettingsLimits.HintTextMaxLength"/> begrenzt. Nur Leerraum → null (= automatischer Text).
    /// </summary>
    private static string? SanitizeHintText(string? text)
    {
        if (text is null)
            return null;

        var cleaned = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        cleaned = TrimToLength(cleaned, SettingsLimits.HintTextMaxLength)!.TrimEnd();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Leer → Segoe UI; Steuerzeichen raus, Länge begrenzt.</summary>
    public static string SanitizeFontName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > SettingsLimits.FontNameMaxLength)
            cleaned = cleaned[..SettingsLimits.FontNameMaxLength].TrimEnd();
        return cleaned.Length == 0 ? LayoutSettings.DefaultCaptionFont : cleaned;
    }

    /// <summary>
    /// Ungültige, gesperrte und doppelte Einträge werden einzeln verworfen (nie die ganze Datei).
    /// Eine Taste gehört höchstens zu einer Aktion (Vorrang: Admin, Auslösen, Abbrechen).
    /// Admin und Auslösen behalten immer mindestens eine Taste.
    /// </summary>
    public static void NormalizeKeys(InputSettings input)
    {
        var used = new HashSet<KeyChord>();
        var admin = Clean(input.AdminKeys, used);
        var trigger = Clean(input.TriggerKeys, used);
        var cancel = Clean(input.CancelCountdownKeys, used);

        if (trigger.Count == 0)
        {
            var space = Parse(InputSettings.DefaultTriggerKey);
            admin.Remove(space);
            cancel.Remove(space);
            trigger.Add(space);
        }
        if (admin.Count == 0)
        {
            var adminDefault = Parse(InputSettings.DefaultAdminKey);
            trigger.Remove(adminDefault);
            cancel.Remove(adminDefault);
            admin.Add(adminDefault);
        }

        input.AdminKeys = admin.Select(k => k.ToString()).ToList();
        input.TriggerKeys = trigger.Select(k => k.ToString()).ToList();
        input.CancelCountdownKeys = cancel.Select(k => k.ToString()).ToList();

        static List<KeyChord> Clean(List<string>? entries, HashSet<KeyChord> used)
        {
            var result = new List<KeyChord>();
            foreach (var entry in entries ?? [])
            {
                if (result.Count >= SettingsLimits.KeysPerActionMax)
                    break;
                if (KeyChord.TryParse(entry, out var chord) && chord.IsAllowed && used.Add(chord))
                    result.Add(chord);
            }
            return result;
        }

        static KeyChord Parse(string text) => KeyChord.TryParse(text, out var c) ? c : throw new InvalidOperationException(text);
    }

    /// <summary>Entfernt ungültige Dateinamenzeichen; leer → "Fotobox".</summary>
    public static string SanitizePrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return DefaultFilePrefix;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(prefix.Where(c => !char.IsControl(c) && Array.IndexOf(invalid, c) < 0).ToArray())
            .Trim().TrimEnd('.');
        if (cleaned.Length > SettingsLimits.FilePrefixMaxLength)
            cleaned = cleaned[..SettingsLimits.FilePrefixMaxLength].TrimEnd();
        return cleaned.Length == 0 ? DefaultFilePrefix : cleaned;
    }
}
