namespace Fotobox.Core;

public sealed record CameraDevice(string Id, string Name);
public sealed record CameraResolution(int Width, int Height)
{ public override string ToString() => $"{Width} × {Height}"; }
public enum CameraStatus { Stopped, Starting, Running, NoDevice, AccessDenied, Error }

/// <summary>Ein Vorschau-Frame (Bgra32). Pixel-Puffer kann nach Rückkehr des Handlers wiederverwendet werden → synchron kopieren.</summary>
public sealed class CameraFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required byte[] Pixels { get; init; }
}

public sealed record PrinterInfo(string Name, bool IsDefault);
/// <summary>Name ist der Schlüssel, der in PrinterSettings.PaperSizeName gespeichert wird.</summary>
public sealed record PaperSizeInfo(string Name, string DisplayName, double WidthMm, double HeightMm);

public sealed record PageSpec(double WidthMm, double HeightMm, int Dpi = 300)
{
    public int PixelWidth => (int)Math.Round(WidthMm / 25.4 * Dpi);
    public int PixelHeight => (int)Math.Round(HeightMm / 25.4 * Dpi);
    public bool IsLandscape => WidthMm > HeightMm;
    public PageSpec ToOrientation(bool landscape) =>
        landscape == IsLandscape ? this : this with { WidthMm = HeightMm, HeightMm = WidthMm };
    /// <summary>10 × 15 cm (4 × 6 Zoll) Hochformat.</summary>
    public static PageSpec Default { get; } = new(101.6, 152.4);
}

public enum PrintOutcome { Completed, Failed, TimedOut, Cancelled }
public sealed record PrintResult(PrintOutcome Outcome, string? Message = null)
{ public bool Success => Outcome == PrintOutcome.Completed; }

public sealed record SavedPhotoSet(string LayoutPath, IReadOnlyList<string> OriginalPaths);
/// <summary>CameraChanged: Die gespeicherten Kameraeinstellungen weichen vom Stand vor dem Öffnen ab.</summary>
public sealed record AdminResult(bool Opened, bool SettingsChanged, bool ExitRequested, bool CameraChanged = false);
