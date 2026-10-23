using Fotobox.Core;

namespace Fotobox.Services.Camera;

/// <summary>Ein von der Kamera angebotenes Videoformat (nur die für die Auswahl relevanten Eigenschaften).</summary>
public readonly record struct CameraFormatCandidate(int Width, int Height, double FrameRate);

/// <summary>Reine Auswahllogik für Kameraformate (ohne WinRT, daher testbar).</summary>
public static class CameraFormatSelector
{
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;
    public const double MinFrameRate = 15;
    private const double TargetFrameRate = 30;

    /// <summary>
    /// Liefert den Index des besten Formats oder -1 (leere Liste).
    /// Gewünschte Auflösung, falls angeboten; sonst höchste Auflösung bis 1920 × 1080 mit mindestens 15 fps (16:9 bevorzugt).
    /// </summary>
    public static int SelectIndex(IReadOnlyList<CameraFormatCandidate> formats, int? preferredWidth, int? preferredHeight)
    {
        if (formats.Count == 0)
            return -1;

        if (preferredWidth is > 0 && preferredHeight is > 0)
        {
            var match = BestBy(formats, f => f.Width == preferredWidth && f.Height == preferredHeight, CompareFrameRate);
            if (match >= 0)
                return match;
        }

        // Bevorzugt: <= 1080p und >= 15 fps; dann <= 1080p ohne fps-Bedingung; zuletzt das kleinste Format.
        var best = BestBy(formats, f => FitsLimit(f) && f.FrameRate >= MinFrameRate, CompareAuto);
        if (best >= 0)
            return best;

        best = BestBy(formats, FitsLimit, CompareAuto);
        if (best >= 0)
            return best;

        best = 0;
        for (var i = 1; i < formats.Count; i++)
        {
            if (Pixels(formats[i]) < Pixels(formats[best]) ||
                (Pixels(formats[i]) == Pixels(formats[best]) && CompareFrameRate(formats[i], formats[best]) > 0))
                best = i;
        }
        return best;
    }

    /// <summary>Eindeutige Auflösungen, absteigend nach Pixelanzahl (dann Breite).</summary>
    public static IReadOnlyList<CameraResolution> DistinctResolutions(IEnumerable<CameraFormatCandidate> formats) =>
        formats.Where(f => f.Width > 0 && f.Height > 0)
               .Select(f => new CameraResolution(f.Width, f.Height))
               .Distinct()
               .OrderByDescending(r => (long)r.Width * r.Height)
               .ThenByDescending(r => r.Width)
               .ToList();

    public static bool IsSixteenByNine(CameraFormatCandidate f) =>
        f.Height > 0 && Math.Abs((double)f.Width / f.Height - 16.0 / 9.0) < 0.02;

    private static bool FitsLimit(CameraFormatCandidate f) =>
        f.Width > 0 && f.Height > 0 && f.Width <= MaxWidth && f.Height <= MaxHeight;

    private static long Pixels(CameraFormatCandidate f) => (long)f.Width * f.Height;

    /// <summary>Positiv, wenn a besser ist: ab 15 fps gültig, bis 30 fps mehr ist besser, darüber weniger (geringere Last).</summary>
    private static int CompareFrameRate(CameraFormatCandidate a, CameraFormatCandidate b)
    {
        var okA = a.FrameRate >= MinFrameRate;
        var okB = b.FrameRate >= MinFrameRate;
        if (okA != okB)
            return okA ? 1 : -1;
        var effA = Math.Min(a.FrameRate, TargetFrameRate);
        var effB = Math.Min(b.FrameRate, TargetFrameRate);
        if (effA != effB)
            return effA > effB ? 1 : -1;
        return b.FrameRate.CompareTo(a.FrameRate);
    }

    private static int CompareAuto(CameraFormatCandidate a, CameraFormatCandidate b)
    {
        var sixteenA = IsSixteenByNine(a);
        var sixteenB = IsSixteenByNine(b);
        if (sixteenA != sixteenB)
            return sixteenA ? 1 : -1;
        var pixels = Pixels(a).CompareTo(Pixels(b));
        return pixels != 0 ? pixels : CompareFrameRate(a, b);
    }

    private static int BestBy(IReadOnlyList<CameraFormatCandidate> formats, Func<CameraFormatCandidate, bool> filter,
        Comparison<CameraFormatCandidate> compare)
    {
        var best = -1;
        for (var i = 0; i < formats.Count; i++)
        {
            if (!filter(formats[i]))
                continue;
            if (best < 0 || compare(formats[i], formats[best]) > 0)
                best = i;
        }
        return best;
    }
}
