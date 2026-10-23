using System.IO;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

/// <summary>Klemmt Einstellungswerte auf gültige Bereiche und ersetzt fehlende Unterobjekte.</summary>
internal static class SettingsNormalizer
{
    public const string DefaultFilePrefix = "Fotobox";
    public const int MinPinIterations = 10_000;
    private const int MaxPrefixLength = 60;

    public static AppSettings Normalize(AppSettings s)
    {
        s.Camera ??= new CameraSettings();
        s.Flow ??= new FlowSettings();
        s.Layout ??= new LayoutSettings();
        s.Printer ??= new PrinterSettings();
        s.Storage ??= new StorageSettings();
        s.Security ??= new SecuritySettings();

        s.Flow.CountdownSeconds = Math.Clamp(s.Flow.CountdownSeconds, SettingsLimits.CountdownMin, SettingsLimits.CountdownMax);
        s.Flow.PreviewSeconds = Math.Clamp(s.Flow.PreviewSeconds, SettingsLimits.PreviewMin, SettingsLimits.PreviewMax);
        s.Flow.PauseBetweenShotsSeconds = Math.Clamp(s.Flow.PauseBetweenShotsSeconds, SettingsLimits.PauseMin, SettingsLimits.PauseMax);
        s.Flow.ErrorDisplaySeconds = Math.Clamp(s.Flow.ErrorDisplaySeconds, SettingsLimits.ErrorDisplayMin, SettingsLimits.ErrorDisplayMax);

        if (!Enum.IsDefined(s.Layout.Kind))
            s.Layout.Kind = LayoutKind.SingleBorderless;
        s.Layout.StripPhotoCount = Math.Clamp(s.Layout.StripPhotoCount, SettingsLimits.StripPhotosMin, SettingsLimits.StripPhotosMax);

        if (!Enum.IsDefined(s.Printer.Orientation))
            s.Printer.Orientation = PageOrientation.Auto;
        s.Printer.Copies = Math.Clamp(s.Printer.Copies, SettingsLimits.CopiesMin, SettingsLimits.CopiesMax);
        s.Printer.TimeoutSeconds = Math.Clamp(s.Printer.TimeoutSeconds, SettingsLimits.PrintTimeoutMin, SettingsLimits.PrintTimeoutMax);

        s.Storage.JpegQuality = Math.Clamp(s.Storage.JpegQuality, SettingsLimits.JpegQualityMin, SettingsLimits.JpegQualityMax);
        s.Storage.FilePrefix = SanitizePrefix(s.Storage.FilePrefix);

        if (s.Security.PinIterations < MinPinIterations)
            s.Security.PinIterations = MinPinIterations;

        return s;
    }

    /// <summary>Entfernt ungültige Dateinamenzeichen; leer → "Fotobox".</summary>
    public static string SanitizePrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return DefaultFilePrefix;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(prefix.Where(c => !char.IsControl(c) && Array.IndexOf(invalid, c) < 0).ToArray())
            .Trim().TrimEnd('.');
        if (cleaned.Length > MaxPrefixLength)
            cleaned = cleaned[..MaxPrefixLength].TrimEnd();
        return cleaned.Length == 0 ? DefaultFilePrefix : cleaned;
    }
}
