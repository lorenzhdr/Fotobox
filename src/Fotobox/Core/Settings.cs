using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fotobox.Core;

public enum LayoutKind { SingleBorderless, Strip, Overlay }
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
    public const int PrintTimeoutMin = 10, PrintTimeoutMax = 600;
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
}

public sealed class LayoutSettings
{
    public LayoutKind Kind { get; set; } = LayoutKind.SingleBorderless;
    public int StripPhotoCount { get; set; } = 3;
    public string? OverlayFileName { get; set; }
    public string? CaptionText { get; set; }
    public bool ShowDate { get; set; } = false;
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
    public int TimeoutSeconds { get; set; } = 120;
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
