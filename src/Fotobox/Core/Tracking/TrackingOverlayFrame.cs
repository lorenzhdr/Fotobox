using System.Globalization;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Core.Tracking;

// Datenmodell des Tracking-Overlays: Alles ist unveränderlich, UI-frei und besteht aus eigenen Kopien der Rohdaten (nie aus
// CameraFrame oder BgraImage). Koordinaten sind normiert (0..1) und ungespiegelt, können aber außerhalb liegen (Handpunkte, YuNet-Boxen);
// die Spiegelung übernimmt die Zeichnung über OverlayMapper.

/// <summary>Eine rohe Personen- bzw. Gesichtserkennung der Zielerfassung (vor Verfolgung und Bestätigung).</summary>
public sealed record TrackingPersonItem(NormRect Box, double Confidence, DetectionKind Kind);

/// <summary>Ein von der Zielerfassung bestätigtes Ziel („TARGET n“).</summary>
public sealed record TrackingTargetItem(int Id, NormRect Box, string Label, bool IsLockingOn);

/// <summary>Ein verfolgtes Gesicht mit seinen fünf Punkten.</summary>
public sealed record TrackingFaceItem(int Id, FaceLandmarks Face);

/// <summary>
/// Eine Hand mit 21 Punkten. <see cref="Number"/> stammt von einem eigenen Verfolger nur für diese Anzeige und kann von den Nummern der
/// Erkennung abweichen. <see cref="Handedness"/> ist die rohe Händigkeit des Modells (nicht auf die Spiegelung kalibriert, nur zur
/// Beschriftung ohne links/rechts). <see cref="IsPeace"/>/<see cref="Progress"/> kommen von der Peaceerkennung (0 ohne Zuordnung).
/// <see cref="Verdicts"/> sind die Warum-Zeilen (leer, wenn die Warum-Info aus ist).
/// </summary>
public sealed record TrackingHandItem(int Number, IReadOnlyList<HandPoint> Points, NormRect Box, double Score, double? Handedness,
    bool IsPeace, double Progress, IReadOnlyList<string> Verdicts);

/// <summary>Ein Bart in der Lage der Verfolgung (ohne Gleiten).</summary>
public sealed record TrackingMoustacheItem(int FaceId, MoustachePose Pose);

/// <summary>Ein erkanntes Herz (Mitte und Größe normiert, <see cref="SizeNorm"/> relativ zur Bildhöhe, <see cref="Progress"/> 0..1).</summary>
public sealed record TrackingHeartItem(NormPoint Center, double SizeNorm, double Progress, HeartShape Shape);

/// <summary>Warum-Zeilen zu einem Handpaar (Herz), gezeichnet bei <see cref="At"/>.</summary>
public sealed record TrackingPairItem(NormPoint At, IReadOnlyList<string> Lines);

/// <summary>Zustand eines Dienstes für die Statuszeile; <see cref="Rate"/> = Analysen pro Sekunde (null = unbekannt oder veraltet).</summary>
public sealed record TrackingStatusEntry(string Name, bool Active, double? Rate, string? Detail)
{
    /// <summary>„Name 4,8/s (Detail)“, „Name aktiv“ oder „Name aus“.</summary>
    public string ToText()
    {
        var text = !Active ? $"{Name} aus"
            : Rate is { } rate && rate > 0 ? string.Create(TrackingOverlayFrame.Culture, $"{Name} {rate:0.0}/s")
            : $"{Name} aktiv";
        return string.IsNullOrEmpty(Detail) ? text : $"{text} ({Detail})";
    }
}

/// <summary>
/// Was das Tracking-Overlay zu einem Zeitpunkt zeichnet. Erzeugt vom <see cref="TrackingOverlayFrameBuilder"/>; <see cref="Options"/> ist
/// eine eigene Kopie der Einstellungen (Ebenen, Stil, Größe). Bild-Auflösungen getrennt: Personen stammen aus der Zielerfassung,
/// alles Übrige aus dem Gestendienst (0 = unbekannt, dann 16:9).
/// </summary>
public sealed class TrackingOverlayFrame
{
    /// <summary>Dezimalkomma wie im Rest der Oberfläche.</summary>
    internal static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("de-DE");

    public static TrackingOverlayFrame Empty { get; } = new();

    public TrackingOverlaySettings Options { get; init; } = new();
    public double PersonImageWidth { get; init; }
    public double PersonImageHeight { get; init; }
    public double GestureImageWidth { get; init; }
    public double GestureImageHeight { get; init; }
    public IReadOnlyList<TrackingPersonItem> Persons { get; init; } = [];
    public IReadOnlyList<TrackingTargetItem> Targets { get; init; } = [];
    public IReadOnlyList<TrackingFaceItem> Faces { get; init; } = [];
    public IReadOnlyList<TrackingHandItem> Hands { get; init; } = [];
    public IReadOnlyList<TrackingMoustacheItem> Moustaches { get; init; } = [];
    public IReadOnlyList<TrackingHeartItem> Hearts { get; init; } = [];
    public IReadOnlyList<TrackingPairItem> Pairs { get; init; } = [];
    /// <summary>Die Peaceerkennung hat gerade ein Peace-Zeichen im Blick (laufendes Halten oder erkannt).</summary>
    public bool PeaceNow { get; init; }
    /// <summary>Die Mittelfingererkennung meldet gerade einen Mittelfinger (gilt für das Bild, nicht für eine bestimmte Hand).</summary>
    public bool MiddleFingerNow { get; init; }
    /// <summary>Die Herzerkennung meldet gerade ein Herz (gilt für das Bild).</summary>
    public bool HeartNow { get; init; }
    public IReadOnlyList<TrackingStatusEntry> Status { get; init; } = [];

    /// <summary>Statuszeile als Text (leer, wenn sie ausgeschaltet ist).</summary>
    public string StatusText => Options.ShowStatusLine && Status.Count > 0 ? string.Join("   ", Status.Select(s => s.ToText())) : "";

    /// <summary>Gibt es etwas zu zeichnen (ohne Statuszeile)?</summary>
    public bool HasContent =>
        Persons.Count > 0 || Targets.Count > 0 || Faces.Count > 0 || Hands.Count > 0 || Moustaches.Count > 0 || Hearts.Count > 0
        || Pairs.Count > 0 || MiddleFingerNow || HeartNow || PeaceNow;
}
