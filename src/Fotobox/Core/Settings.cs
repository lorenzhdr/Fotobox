using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fotobox.Core;

public enum LayoutKind { SingleBorderless, Strip, Overlay }
/// <summary>DoubleCut: zwei gleiche Streifen nebeneinander (zum Teilen); Single: ein Streifen über die ganze Seite.</summary>
public enum StripStyle { DoubleCut, Single }
/// <summary>Per Mausrad wählbare Layoutvariante der Gastansicht (fasst <see cref="LayoutKind"/> und <see cref="StripStyle"/> zusammen).</summary>
public enum GuestLayout { Single, StripDouble, StripSingle, Overlay }
/// <summary>Auto: Strip → Hochformat, sonst Querformat.</summary>
public enum PageOrientation { Auto, Portrait, Landscape }

/// <summary>Gemeinsame Wertebereiche für Admin-UI, Validierung und Normalisierung.</summary>
public static class SettingsLimits
{
    public const int CountdownMin = 1, CountdownMax = 30;
    public const int PreviewMin = 1, PreviewMax = 60;
    public const int PauseMin = 0, PauseMax = 30;
    public const int ErrorDisplayMin = 2, ErrorDisplayMax = 60;
    public const int StripPhotosMin = 2, StripPhotosMax = 4;
    public const int CopiesMin = 1, CopiesMax = 20;
    public const int PrintTimeoutMin = 10, PrintTimeoutMax = 900;
    /// <summary>Erwartete Druckdauer (Fortschrittsbalken in der Gastansicht).</summary>
    public const int ExpectedPrintMin = 30, ExpectedPrintMax = 600;
    /// <summary>Das Druck-Timeout liegt immer mindestens so weit über der erwarteten Druckdauer.</summary>
    public const int PrintTimeoutReserveSeconds = 60;
    public const int KeysPerActionMax = 8;
    public const int FontNameMaxLength = 100;
    public const int JpegQualityMin = 50, JpegQualityMax = 100;
}

public sealed class AppSettings
{
    /// <summary>Gemeinsame Serializer-Optionen für Store und <see cref="Clone"/> (Enums als Strings).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public int Version { get; set; } = 1;
    public CameraSettings Camera { get; set; } = new();
    public FlowSettings Flow { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();
    public PrinterSettings Printer { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public InputSettings Input { get; set; } = new();

    /// <summary>Tiefe Kopie via JSON-Roundtrip.</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions) ?? new AppSettings();
}

public sealed class CameraSettings
{
    public string? DeviceId { get; set; }
    public int? ResolutionWidth { get; set; }
    public int? ResolutionHeight { get; set; }
    public bool MirrorPreview { get; set; } = true;
    public bool MirrorPhoto { get; set; } = false;
}

public sealed class FlowSettings
{
    public int CountdownSeconds { get; set; } = 5;
    public int PreviewSeconds { get; set; } = 4;
    public int PauseBetweenShotsSeconds { get; set; } = 2;
    public int ErrorDisplaySeconds { get; set; } = 6;
    public bool PrintingEnabled { get; set; } = true;
    /// <summary>So lange braucht der Drucker etwa; danach ist der Fortschrittsbalken voll.</summary>
    public int ExpectedPrintSeconds { get; set; } = 240;
}

public sealed class LayoutSettings
{
    public LayoutKind Kind { get; set; } = LayoutKind.SingleBorderless;
    public int StripPhotoCount { get; set; } = 3;
    public StripStyle StripStyle { get; set; } = StripStyle.DoubleCut;
    public string? OverlayFileName { get; set; }
    public string? CaptionText { get; set; }
    public bool ShowDate { get; set; } = false;
    /// <summary>Schriftfamilie der Bildunterschrift (<c>FontFamily.Source</c>); fehlt sie, wird Segoe UI verwendet.</summary>
    public string CaptionFontFamily { get; set; } = DefaultCaptionFont;
    /// <summary>
    /// Layouts, zwischen denen Gäste per Mausrad wählen dürfen. Leer = kein Wechsel, es gilt immer
    /// <see cref="Kind"/>/<see cref="StripStyle"/>. Der Standard ist nach der Normalisierung enthalten, sobald die Liste nicht leer ist.
    /// Die Reihenfolge der Liste ist die Reihenfolge beim Scrollen (im Admin einstellbar).
    /// </summary>
    public List<GuestLayout> GuestChoices { get; set; } = [];

    public const string DefaultCaptionFont = "Segoe UI";
}

public sealed class PrinterSettings
{
    /// <summary>null = Standarddrucker.</summary>
    public string? PrinterName { get; set; }
    public string? PaperSizeName { get; set; }
    public double? PaperWidthMm { get; set; }
    public double? PaperHeightMm { get; set; }
    public PageOrientation Orientation { get; set; } = PageOrientation.Auto;
    public int Copies { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class StorageSettings
{
    /// <summary>null = Pictures\Fotobox.</summary>
    public string? PhotoFolder { get; set; }
    public bool DateSubfolder { get; set; } = true;
    public bool SaveOriginals { get; set; } = true;
    public int JpegQuality { get; set; } = 92;
    public string FilePrefix { get; set; } = "Fotobox";
}

public sealed class SecuritySettings
{
    public string? PinHash { get; set; }
    public string? PinSalt { get; set; }
    public int PinIterations { get; set; } = 100_000;
}

/// <summary>Tastenbelegung je Aktion; Einträge im Format von <see cref="KeyChord"/> (z. B. "Space", "Ctrl+Alt+A").</summary>
public sealed class InputSettings
{
    public List<string> TriggerKeys { get; set; } = [DefaultTriggerKey];
    public List<string> AdminKeys { get; set; } = [DefaultAdminKey];
    public List<string> CancelCountdownKeys { get; set; } = [DefaultCancelKey];
    /// <summary>Linksklick löst die Aufnahme aus.</summary>
    public bool TriggerOnLeftClick { get; set; } = true;
    /// <summary>Rechtsklick löst die Aufnahme aus.</summary>
    public bool TriggerOnRightClick { get; set; } = true;

    public const string DefaultTriggerKey = "Space";
    public const string DefaultAdminKey = "Ctrl+Alt+A";
    public const string DefaultCancelKey = "Escape";
}
