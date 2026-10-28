using System.IO;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

/// <summary>Klemmt Einstellungswerte auf gültige Bereiche und ersetzt fehlende Unterobjekte.</summary>
internal static class SettingsNormalizer
{
    public const int MinPinIterations = 10_000;
    /// <summary>
    /// Obergrenze, bis zu der gespeicherte PBKDF2-Iterationen als plausibel gelten (<see cref="PinService.Verify"/>).
    /// Wird hier bewusst NICHT geklemmt: ein anderer Wert passte nicht mehr zum vorhandenen Hash und sperrte den Admin aus.
    /// </summary>
    public const int MaxPlausiblePinIterations = 10_000_000;

    /// <summary>
    /// Normalisiert und migriert. Migration (Version &lt; <see cref="AppSettings.CurrentVersion"/>): Der alte Standardpräfix
    /// „Fotobox“ (exakt) wird zu „Fotobox+“, eigene Präfixe bleiben. Danach steht <see cref="AppSettings.Version"/> auf dem
    /// aktuellen Stand; eine neuere Version (Datei einer späteren App) wird nie herabgesetzt. Fehlt <c>Version</c> in der Datei,
    /// gilt der Standard der aktuellen App (keine Migration) – die App schreibt das Feld aber immer. Die Migration läuft beim
    /// Laden im Speicher und wird mit dem nächsten Speichern festgeschrieben; sie ist idempotent.
    /// </summary>
    public static AppSettings Normalize(AppSettings s)
    {
        var loadedVersion = s.Version;

        s.Camera ??= new CameraSettings();
        s.Flow ??= new FlowSettings();
        s.Layout ??= new LayoutSettings();
        s.Printer ??= new PrinterSettings();
        s.Storage ??= new StorageSettings();
        s.Security ??= new SecuritySettings();
        s.Input ??= new InputSettings();
        s.GuestScreen ??= new GuestScreenSettings();
        s.Appearance ??= new AppearanceSettings();
        s.Video ??= new VideoSettings();
        s.Fun ??= new FunSettings();
        s.Peace ??= new PeaceSettings();

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
        // Migration: bis Version 1 hieß der Standardpräfix „Fotobox“; ein selbst gewählter Präfix bleibt unverändert.
        if (loadedVersion < AppSettings.CurrentVersion && s.Storage.FilePrefix == StorageSettings.LegacyFilePrefix)
            s.Storage.FilePrefix = StorageSettings.DefaultFilePrefix;
        if (s.Version < AppSettings.CurrentVersion)
            s.Version = AppSettings.CurrentVersion;

        NormalizeVideo(s.Video);
        NormalizeFun(s.Fun);
        NormalizePeace(s.Peace);

        // PinIterations wird bewusst nicht geklemmt: Ein anderer Wert passte nicht mehr zum gespeicherten Hash (siehe PinService).

        NormalizeKeys(s.Input);

        if (!Enum.IsDefined(s.GuestScreen.HintStyle))
            s.GuestScreen.HintStyle = HintStyle.Pill;
        s.GuestScreen.HintText = SanitizeHintText(s.GuestScreen.HintText);
        if (!Enum.IsDefined(s.GuestScreen.VideoHintStyle))
            s.GuestScreen.VideoHintStyle = HintStyle.Pill;
        s.GuestScreen.VideoHintText = SanitizeHintText(s.GuestScreen.VideoHintText);
        if (!Enum.IsDefined(s.GuestScreen.WheelHintStyle))
            s.GuestScreen.WheelHintStyle = WheelHintStyle.Matching;
        s.GuestScreen.WheelHintText = SanitizeHintText(s.GuestScreen.WheelHintText);

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
    /// Videoeinstellungen: Zahlen auf <see cref="SettingsLimits"/> klemmen. Die Mindestlänge liegt immer unter der Höchstdauer:
    /// Ist sie nach dem Klemmen nicht kleiner, wird sie auf <c>Höchstdauer − 1</c> gesetzt (der nächstgültige Wert; die
    /// Höchstdauer ist mindestens <see cref="SettingsLimits.VideoMaxDurationMin"/>, die Mindestlänge also nie negativ).
    /// Unbekannte Qualität → <see cref="VideoQuality.High"/>. Leere Texte/Ordner → null, Präfix leer → Standard.
    /// </summary>
    private static void NormalizeVideo(VideoSettings v)
    {
        v.MaxDurationSeconds = Math.Clamp(v.MaxDurationSeconds, SettingsLimits.VideoMaxDurationMin, SettingsLimits.VideoMaxDurationMax);
        v.CountdownSeconds = Math.Clamp(v.CountdownSeconds, SettingsLimits.VideoCountdownMin, SettingsLimits.VideoCountdownMax);
        v.MinDurationSeconds = Math.Clamp(v.MinDurationSeconds, SettingsLimits.VideoMinDurationMin, SettingsLimits.VideoMinDurationMax);
        if (v.MinDurationSeconds >= v.MaxDurationSeconds)
            v.MinDurationSeconds = v.MaxDurationSeconds - 1;
        v.WarningSeconds = Math.Clamp(v.WarningSeconds, SettingsLimits.VideoWarningMin, SettingsLimits.VideoWarningMax);
        v.SuccessDisplaySeconds = Math.Clamp(v.SuccessDisplaySeconds, SettingsLimits.VideoSuccessDisplayMin, SettingsLimits.VideoSuccessDisplayMax);

        if (!Enum.IsDefined(v.Quality))
            v.Quality = VideoQuality.High;

        var successText = CleanLine(v.SuccessText, SettingsLimits.VideoSuccessTextMaxLength);
        v.SuccessText = string.IsNullOrEmpty(successText) ? null : successText;

        // Id exakt lassen (Geräte-Id); zu lang → verwerfen, denn gekürzt passte sie nie. Name nur zur Anzeige, gehört zur Id.
        var microphoneId = v.MicrophoneId;
        if (string.IsNullOrWhiteSpace(microphoneId) || microphoneId.Length > SettingsLimits.MicrophoneIdMaxLength)
            microphoneId = null;
        v.MicrophoneId = microphoneId;
        v.MicrophoneName = microphoneId is null || string.IsNullOrWhiteSpace(v.MicrophoneName)
            ? null
            : TrimToLength(v.MicrophoneName.Trim(), SettingsLimits.MicrophoneNameMaxLength);

        if (string.IsNullOrWhiteSpace(v.Folder))
            v.Folder = null;
        v.FilePrefix = SanitizePrefix(v.FilePrefix, VideoSettings.DefaultFilePrefix);
    }

    /// <summary>
    /// Fun-Einstellungen: Zahlen auf <see cref="SettingsLimits"/> klemmen, unbekannte Enum-Werte auf den Standard setzen.
    /// Eigene Texte: Steuerzeichen/Zeilenumbrüche → Leerzeichen, Länge auf <see cref="SettingsLimits.HintTextMaxLength"/>
    /// begrenzt, nur Leerraum → null (= Standardtext).
    /// </summary>
    private static void NormalizeFun(FunSettings f)
    {
        var defaults = new FunSettings();

        f.TextSeconds = Math.Clamp(f.TextSeconds, SettingsLimits.FunTextSecondsMin, SettingsLimits.FunTextSecondsMax);
        f.CooldownSeconds = Math.Clamp(f.CooldownSeconds, SettingsLimits.FunCooldownSecondsMin, SettingsLimits.FunCooldownSecondsMax);
        f.RepeatSeconds = Math.Clamp(f.RepeatSeconds, SettingsLimits.FunRepeatSecondsMin, SettingsLimits.FunRepeatSecondsMax);
        f.MinSizePercent = Math.Clamp(f.MinSizePercent, SettingsLimits.FunMinSizePercentMin, SettingsLimits.FunMinSizePercentMax);
        f.MaxTargets = Math.Clamp(f.MaxTargets, SettingsLimits.FunMaxTargetsMin, SettingsLimits.FunMaxTargetsMax);
        f.SensitivityPercent = Math.Clamp(f.SensitivityPercent, SettingsLimits.FunSensitivityMin, SettingsLimits.FunSensitivityMax);
        f.DwellMilliseconds = Math.Clamp(f.DwellMilliseconds, SettingsLimits.FunDwellMillisecondsMin, SettingsLimits.FunDwellMillisecondsMax);
        f.LockOnMilliseconds = Math.Clamp(f.LockOnMilliseconds, SettingsLimits.FunLockOnMillisecondsMin, SettingsLimits.FunLockOnMillisecondsMax);
        f.LockOnIntensityPercent = Math.Clamp(f.LockOnIntensityPercent, SettingsLimits.FunLockOnIntensityMin, SettingsLimits.FunLockOnIntensityMax);
        f.AnalysesPerSecond = Math.Clamp(f.AnalysesPerSecond, SettingsLimits.FunAnalysesPerSecondMin, SettingsLimits.FunAnalysesPerSecondMax);

        if (!Enum.IsDefined(f.Method))
            f.Method = defaults.Method;
        if (!Enum.IsDefined(f.Scope))
            f.Scope = defaults.Scope;
        if (!Enum.IsDefined(f.BoxStyle))
            f.BoxStyle = defaults.BoxStyle;
        if (!Enum.IsDefined(f.Color))
            f.Color = defaults.Color;
        if (!Enum.IsDefined(f.Animation))
            f.Animation = defaults.Animation;
        if (!Enum.IsDefined(f.TextPosition))
            f.TextPosition = defaults.TextPosition;
        if (!Enum.IsDefined(f.TextStyle))
            f.TextStyle = defaults.TextStyle;
        if (!Enum.IsDefined(f.TextHintStyle))
            f.TextHintStyle = defaults.TextHintStyle;

        f.SingularText = SanitizeHintText(f.SingularText);
        f.PluralText = SanitizeHintText(f.PluralText);
    }

    /// <summary>
    /// Peace-Einstellungen: Zahlen auf <see cref="SettingsLimits"/> klemmen, Haltedauer auf 100er-Schritte runden, unbekannte
    /// Enum-Werte auf den Standard setzen; ohne gewählte Aktion gilt die Selbstzerstörung.
    /// </summary>
    private static void NormalizePeace(PeaceSettings p)
    {
        var defaults = new PeaceSettings();

        var step = SettingsLimits.PeaceHoldMillisecondsStep;
        var hold = (int)(Math.Round(p.HoldMilliseconds / (double)step, MidpointRounding.AwayFromZero) * step);
        p.HoldMilliseconds = Math.Clamp(hold, SettingsLimits.PeaceHoldMillisecondsMin, SettingsLimits.PeaceHoldMillisecondsMax);
        p.CooldownSeconds = Math.Clamp(p.CooldownSeconds, SettingsLimits.PeaceCooldownSecondsMin, SettingsLimits.PeaceCooldownSecondsMax);
        p.SensitivityPercent = Math.Clamp(p.SensitivityPercent, SettingsLimits.PeaceSensitivityMin, SettingsLimits.PeaceSensitivityMax);
        p.AnalysesPerSecond = Math.Clamp(p.AnalysesPerSecond, SettingsLimits.PeaceAnalysesPerSecondMin, SettingsLimits.PeaceAnalysesPerSecondMax);
        p.SelfDestructSeconds = Math.Clamp(p.SelfDestructSeconds, SettingsLimits.PeaceSelfDestructSecondsMin, SettingsLimits.PeaceSelfDestructSecondsMax);
        p.PhotoCountdownSeconds = Math.Clamp(p.PhotoCountdownSeconds, SettingsLimits.PeacePhotoCountdownMin, SettingsLimits.PeacePhotoCountdownMax);
        p.MoustacheDurationSeconds = Math.Clamp(p.MoustacheDurationSeconds, SettingsLimits.PeaceMoustacheDurationMin, SettingsLimits.PeaceMoustacheDurationMax);
        p.MoustacheAutoPhotoCountdownSeconds = Math.Clamp(p.MoustacheAutoPhotoCountdownSeconds,
            SettingsLimits.PeaceMoustacheAutoPhotoCountdownMin, SettingsLimits.PeaceMoustacheAutoPhotoCountdownMax);

        if (!Enum.IsDefined(p.MoustacheTargets))
            p.MoustacheTargets = defaults.MoustacheTargets;
        if (!Enum.IsDefined(p.MoustacheStyle))
            p.MoustacheStyle = defaults.MoustacheStyle;
        if (!Enum.IsDefined(p.MoustacheVisibility))
            p.MoustacheVisibility = defaults.MoustacheVisibility;

        p.RandomStyles = Fotobox.Core.Peace.MoustacheStyles.NormalizeRandomStyles(p.RandomStyles);

        if (!p.HasAnyAction)
            p.ActionSelfDestruct = true;
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
    /// Eine Taste gehört höchstens zu einer der Aktionen Admin, Auslösen, Abbrechen und Video (Vorrang in dieser Reihenfolge;
    /// VideoStart/VideoStop haben gegenüber den ersten dreien immer das Nachsehen). Ausnahme: Dieselbe Taste darf in
    /// <see cref="InputSettings.VideoStartKeys"/> und <see cref="InputSettings.VideoStopKeys"/> stehen (Umschalten).
    /// Admin und Auslösen behalten immer mindestens eine Taste; die Video-Listen dürfen leer sein.
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

        // Video erst nach den Ersatzregeln: Sie können Strg+Alt+A bzw. die Leertaste in Admin/Auslösen hineinnehmen,
        // und diese Tasten müssen dann aus den Video-Listen verschwinden. Start und Stopp prüfen getrennt (je eigene Kopie).
        var taken = new HashSet<KeyChord>(admin.Concat(trigger).Concat(cancel));
        var videoStart = Clean(input.VideoStartKeys, new HashSet<KeyChord>(taken));
        var videoStop = Clean(input.VideoStopKeys, new HashSet<KeyChord>(taken));

        input.AdminKeys = admin.Select(k => k.ToString()).ToList();
        input.TriggerKeys = trigger.Select(k => k.ToString()).ToList();
        input.CancelCountdownKeys = cancel.Select(k => k.ToString()).ToList();
        input.VideoStartKeys = videoStart.Select(k => k.ToString()).ToList();
        input.VideoStopKeys = videoStop.Select(k => k.ToString()).ToList();

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

    /// <summary>Entfernt ungültige Dateinamenzeichen; leer → <paramref name="fallback"/> (Standard: Fotopräfix „Fotobox+“).</summary>
    public static string SanitizePrefix(string? prefix, string fallback = StorageSettings.DefaultFilePrefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(prefix.Where(c => !char.IsControl(c) && Array.IndexOf(invalid, c) < 0).ToArray())
            .Trim().TrimEnd('.');
        cleaned = TrimToLength(cleaned, SettingsLimits.FilePrefixMaxLength)!.TrimEnd();
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
