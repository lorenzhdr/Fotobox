using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Core.Heart;

/// <summary>
/// Eine erkannte Herzgeste. Alle Koordinaten sind normiert (0..1) auf das UNgespiegelte Kamerabild. <paramref name="HandA"/> und
/// <paramref name="HandB"/> sind die Indizes der beteiligten Hände in der Handliste des Updates (<paramref name="HandB"/> = −1 beim
/// <see cref="HeartShape.FingerHeart"/>, das nur eine Hand braucht). <paramref name="SizeNorm"/> = Kantenlänge des Herzens relativ zur
/// Bildhöhe, <paramref name="Score"/> = Güte der Erkennung 0..1.
/// </summary>
public sealed record HeartMatch(HeartShape Shape, int HandA, int HandB, NormPoint Center, double SizeNorm, NormRect Box, double Score);

/// <summary>
/// Ausgelöste Herzgeste (nach der Haltezeit). <paramref name="HeartBox"/> umschließt das Herz, <paramref name="FaceBoxes"/> die Gesichter
/// im Umfeld (für den Kisscam-Zoom); alles normiert und ungespiegelt.
/// </summary>
public sealed record HeartTrigger(HeartAction Action, HeartShape Shape, NormPoint Center, NormRect HeartBox, IReadOnlyList<NormRect> FaceBoxes);

/// <summary>Ein Herz für die Anzeige: Mitte und Größe normiert (ungespiegelt), <paramref name="Progress"/> 0..1 = Haltefortschritt.</summary>
public sealed record HeartView(NormPoint Center, double SizeNorm, double Progress, HeartShape Shape);

/// <summary>Momentaufnahme der Herzerkennung; <paramref name="Armed"/> = die Erkennung ist scharf (bereit auszulösen).</summary>
public sealed record HeartSnapshot(IReadOnlyList<HeartView> Hearts, bool Armed)
{
    public static HeartSnapshot Empty { get; } = new([], false);
}
