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
        s.Appearance ??= new AppearanceSettings();

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
        s.Layout.CaptionText = SanitizeCaption(s.Layout.CaptionText);
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

        // PinIterations wird bewusst nicht geklemmt: Ein anderer Wert passte nicht mehr zum gespeicherten Hash (siehe PinService).

        NormalizeKeys(s.Input);

        if (!Enum.IsDefined(s.GuestScreen.HintStyle))
            s.GuestScreen.HintStyle = HintStyle.Pill;
        s.GuestScreen.HintText = SanitizeHintText(s.GuestScreen.HintText);

        if (!Enum.IsDefined(s.Appearance.AdminTheme))
            s.Appearance.AdminTheme = AdminThemeMode.System;

        // Id exakt lassen (Geräte-Pfad); zu lang → verwerfen, denn gekürzt passte sie nie. Name nur zur Anzeige, gehört zur Id.
        var monitorId = s.Appearance.GuestMonitorId;
        if (string.IsNullOrWhiteSpace(monitorId) || monitorId.Length > SettingsLimits.MonitorIdMaxLength)
            monitorId = null;
        s.Appearance.GuestMonitorId = monitorId;
        s.Appearance.GuestMonitorName = monitorId is null || string.IsNullOrWhiteSpace(s.Appearance.GuestMonitorName)
            ? null
            : TrimToLength(s.Appearance.GuestMonitorName, SettingsLimits.MonitorNameMaxLength);

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
        var cleaned = CleanLine(text, SettingsLimits.HintTextMaxLength);
        return cleaned is null || cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Bildunterschrift: Steuerzeichen und Zeilenumbrüche werden zu Leerzeichen, Ränder abgeschnitten, Länge auf
    /// <see cref="SettingsLimits.CaptionMaxLength"/> begrenzt. Nur Leerraum → leerer Text (wie eine nicht gesetzte Unterschrift);
    /// null bleibt null.
    /// </summary>
    private static string? SanitizeCaption(string? text) => CleanLine(text, SettingsLimits.CaptionMaxLength);

    /// <summary>Steuerzeichen → Leerzeichen, trimmen, kürzen (ohne Surrogatpaar zu zerschneiden), erneut trimmen. null bleibt null.</summary>
    private static string? CleanLine(string? text, int maxLength)
    {
        if (text is null)
            return null;

        var cleaned = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return TrimToLength(cleaned, maxLength)!.TrimEnd();
    }

    /// <summary>Leer → Segoe UI; Steuerzeichen raus, Länge begrenzt.</summary>
    public static string SanitizeFontName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        cleaned = TrimToLength(cleaned, SettingsLimits.FontNameMaxLength)!.TrimEnd();
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

        // Erst Admin, dann Auslösen: Der Admin-Ersatz nimmt Strg+Alt+A ggf. aus der Auslöseliste, der Auslöse-Ersatz (Leertaste)
        // nimmt die Leertaste ggf. aus Admin – und gibt Admin dann wieder seinen Standard. Beide Ersatztasten sind verschieden,
        // so behalten am Ende immer beide Aktionen mindestens eine Taste (und ein zweiter Durchlauf ändert nichts mehr).
        var adminDefault = Parse(InputSettings.DefaultAdminKey);
        var space = Parse(InputSettings.DefaultTriggerKey);
        if (admin.Count == 0)
            SetAdminDefault();
        if (trigger.Count == 0)
        {
            admin.Remove(space);
            cancel.Remove(space);
            trigger.Add(space);
            if (admin.Count == 0)
                SetAdminDefault();
        }

        void SetAdminDefault()
        {
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
        cleaned = TrimToLength(cleaned, SettingsLimits.FilePrefixMaxLength)!.TrimEnd();
        return cleaned.Length == 0 ? DefaultFilePrefix : cleaned;
    }
}
