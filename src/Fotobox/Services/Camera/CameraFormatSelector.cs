using Fotobox.Core;

namespace Fotobox.Services.Camera;

/// <summary>
/// Ein von der Kamera angebotenes Videoformat (nur die für die Auswahl relevanten Eigenschaften).
/// <paramref name="Subtype"/> ist das Pixelformat (z. B. NV12, YUY2, MJPG, H264); null = unbekannt.
/// </summary>
public readonly record struct CameraFormatCandidate(int Width, int Height, double FrameRate, string? Subtype = null);

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
    /// Komprimierte Videoformate (H264/HEVC, <see cref="SubtypeRank"/> 0) kommen nur infrage, wenn die Kamera sonst nichts anbietet.
    /// </summary>
    public static int SelectIndex(IReadOnlyList<CameraFormatCandidate> formats, int? preferredWidth, int? preferredHeight)
    {
        if (formats.Count == 0)
            return -1;

        // Der Frame-Reader liefert für komprimierte Formate kein Bild: erst nur Formate mit Rang > 0, dann Rückfall auf alle.
        var best = SelectCore(formats, preferredWidth, preferredHeight, f => SubtypeRank(f.Subtype) > 0);
        return best >= 0 ? best : SelectCore(formats, preferredWidth, preferredHeight, _ => true);
    }

    private static int SelectCore(IReadOnlyList<CameraFormatCandidate> formats, int? preferredWidth, int? preferredHeight,
        Func<CameraFormatCandidate, bool> allowed)
    {
        if (preferredWidth is > 0 && preferredHeight is > 0)
        {
            var match = BestBy(formats, f => allowed(f) && f.Width == preferredWidth && f.Height == preferredHeight, CompareFormat);
            if (match >= 0)
                return match;
        }

        // Bevorzugt: <= 1080p und >= 15 fps; dann <= 1080p ohne fps-Bedingung; zuletzt das kleinste Format.
        var best = BestBy(formats, f => allowed(f) && FitsLimit(f) && f.FrameRate >= MinFrameRate, CompareAuto);
        if (best >= 0)
            return best;

        best = BestBy(formats, f => allowed(f) && FitsLimit(f), CompareAuto);
        if (best >= 0)
            return best;

        best = -1;
        for (var i = 0; i < formats.Count; i++)
        {
            if (!allowed(formats[i]))
                continue;
            if (best < 0 || Pixels(formats[i]) < Pixels(formats[best]) ||
                (Pixels(formats[i]) == Pixels(formats[best]) && CompareFormat(formats[i], formats[best]) > 0))
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
        return pixels != 0 ? pixels : CompareFormat(a, b);
    }

    /// <summary>Bildrate; nur bei gleichem Rang entscheidet das Pixelformat (Tie-Breaker).</summary>
    private static int CompareFormat(CameraFormatCandidate a, CameraFormatCandidate b)
    {
        var rate = CompareFrameRate(a, b);
        return rate != 0 ? rate : SubtypeRank(a.Subtype).CompareTo(SubtypeRank(b.Subtype));
    }

    /// <summary>
    /// Höher ist besser: unkomprimiert (3) &gt; MJPG (2) &gt; unbekannt (1) &gt; komprimierte Videoformate wie H264/HEVC (0).
    /// Der Frame-Reader liefert für komprimierte Videoformate keine SoftwareBitmap, die Kamera bliebe ohne Bild.
    /// </summary>
    internal static int SubtypeRank(string? subtype)
    {
        if (string.IsNullOrWhiteSpace(subtype))
            return 1;

        var s = subtype.Trim().ToUpperInvariant();
        if (s is "NV12" or "YUY2" or "YUYV" or "UYVY" or "YV12" or "IYUV" or "I420" or "P010" or "BGRA8" or "RGB32"
            or "RGB24" or "ARGB32" or "RGB565" or "RGB555")
            return 3;
        if (s is "MJPG" or "MJPEG" or "JPEG")
            return 2;
        if (s.StartsWith("H264", StringComparison.Ordinal) || s.StartsWith("HEVC", StringComparison.Ordinal)
            || s.StartsWith("H265", StringComparison.Ordinal) || s.StartsWith("MPEG", StringComparison.Ordinal)
            || s.StartsWith("MP4", StringComparison.Ordinal) || s.StartsWith("WMV", StringComparison.Ordinal)
            || s.StartsWith("VC1", StringComparison.Ordinal) || s.StartsWith("VP8", StringComparison.Ordinal)
            || s.StartsWith("VP9", StringComparison.Ordinal) || s.StartsWith("AV1", StringComparison.Ordinal))
            return 0;
        return 1;
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
