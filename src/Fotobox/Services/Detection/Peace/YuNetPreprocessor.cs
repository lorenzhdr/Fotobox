using Fotobox.Core.Peace;

namespace Fotobox.Services.Detection.Peace;

/// <summary>
/// Größen eines YuNet-Laufs: Inhalt (verkleinertes Bild) und die auf Vielfache von 32 aufgefüllte Eingabe (rechts/unten schwarz).
/// Das Modell braucht Vielfache von 32 (sonst scheitern die Additionen der Strides, z. B. bei 640 × 360).
/// </summary>
internal readonly record struct YuNetInputSize(int ContentWidth, int ContentHeight, int PaddedWidth, int PaddedHeight)
{
    public int RequiredLength => 3 * PaddedWidth * PaddedHeight;
}

/// <summary>Vorverarbeitung für YuNet: NCHW, Kanalreihenfolge BGR, Werte 0–255 (Float, ohne Normalisierung).</summary>
internal static class YuNetPreprocessor
{
    public const int MaxWidth = 640;
    public const int Multiple = 32;

    /// <summary>Verkleinert auf höchstens <paramref name="maxWidth"/> Breite (Seitenverhältnis bleibt, nie vergrößert) und füllt auf Vielfache von 32 auf.</summary>
    public static YuNetInputSize ComputeSize(int width, int height, int maxWidth = MaxWidth)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Das Bild ist leer.");
        var cw = Math.Min(maxWidth, width);
        var ch = Math.Max(1, (int)Math.Round((double)height * cw / width));
        return new YuNetInputSize(cw, ch, RoundUp(cw), RoundUp(ch));
    }

    private static int RoundUp(int value) => (value + Multiple - 1) / Multiple * Multiple;

    /// <summary>Schreibt das Bild nach <paramref name="destination"/> (Länge mindestens <see cref="YuNetInputSize.RequiredLength"/>).</summary>
    public static void Preprocess(BgraImage image, YuNetInputSize size, float[] destination)
    {
        ArgumentNullException.ThrowIfNull(image);
        AffineCrop.Sample(image, image.Width / 2.0, image.Height / 2.0, 0, image.Width, image.Height,
            size.ContentWidth, size.ContentHeight, destination, size.PaddedWidth, size.PaddedHeight, bgr: true, scale: 1f);
    }
}
