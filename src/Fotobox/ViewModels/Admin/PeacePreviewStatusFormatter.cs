using System.Globalization;
using Fotobox.Core.Peace;

namespace Fotobox.ViewModels.Admin;

/// <summary>Statuszeile unter der Live-Vorschau der Peaceerkennung: Haupttext plus optional eine Warnzeile.</summary>
/// <param name="Text">Haupttext (Hände, Gesichter, Rate, „Peace erkannt“ bzw. Lade-/Fehlermeldung).</param>
/// <param name="Warning">Zweite Zeile in der Warnfarbe (nicht verfügbare Stufe, keine Kamera), sonst null.</param>
public sealed record PeacePreviewStatusLines(string Text, string? Warning);

/// <summary>Formatiert den Zustand des Gestendienstes für die Admin-Vorschau (reine Funktion, deutsche Texte).</summary>
public static class PeacePreviewStatusFormatter
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public const string Loading = "Erkennung wird geladen …";
    public const string NotAvailable = "Die Erkennung ist nicht verfügbar.";
    public const string NoCamera = "Keine Kamera – Vorschau nicht verfügbar.";
    public const string PeaceDetected = "Peace erkannt ✌";
    public const string MiddleFingerDetected = "Mittelfinger erkannt";

    /// <param name="status">Zustand der offenen Anforderung; null = es gibt keine (die Anforderung ist gescheitert).</param>
    /// <param name="hands">Aktuell verfolgte Hände.</param>
    /// <param name="faces">Aktuell erkannte Gesichter; negativ = unbekannt (dann entfällt die Angabe).</param>
    /// <param name="peaceNow">Zeigt gerade mindestens eine Hand das Peace-Zeichen?</param>
    /// <param name="cameraRunning">Die Kamera liefert ein Livebild.</param>
    /// <param name="middleFingerNow">Zeigt gerade eine Hand den Mittelfinger (Seite „Mittelfinger“)?</param>
    public static PeacePreviewStatusLines Format(GestureStatus? status, int hands, int faces, bool peaceNow, bool cameraRunning,
        bool middleFingerNow = false)
    {
        var warnings = new List<string>();
        if (!cameraRunning)
            warnings.Add(NoCamera);

        if (status is null)
            return new PeacePreviewStatusLines(NotAvailable, Join(warnings));

        var anyReason = false;
        if (!string.IsNullOrWhiteSpace(status.HandUnavailableReason))
        {
            warnings.Add(status.HandUnavailableReason!);
            anyReason = true;
        }
        if (!string.IsNullOrWhiteSpace(status.FaceUnavailableReason))
        {
            warnings.Add(status.FaceUnavailableReason!);
            anyReason = true;
        }

        if (!status.HandsReady && !status.FacesReady)
            return new PeacePreviewStatusLines(anyReason ? NotAvailable : Loading, Join(warnings));

        var parts = new List<string>();
        if (status.HandsReady)
            parts.Add(string.Create(German, $"Hände: {Math.Max(0, hands)}"));
        if (status.FacesReady && faces >= 0)
            parts.Add(string.Create(German, $"Gesichter: {faces}"));
        parts.Add(Math.Max(0, status.AnalysesPerSecond).ToString("0.0", German) + " Analysen/s");
        if (status.MeanCycleMs > 0)
            parts.Add(string.Create(German, $"Analyse ⌀ {Math.Round(status.MeanCycleMs):0} ms"));
        if (peaceNow)
            parts.Add(PeaceDetected);
        if (middleFingerNow)
            parts.Add(MiddleFingerDetected);
        return new PeacePreviewStatusLines(string.Join(" · ", parts), Join(warnings));
    }

    private static string? Join(List<string> warnings) => warnings.Count == 0 ? null : string.Join("\n", warnings);
}
