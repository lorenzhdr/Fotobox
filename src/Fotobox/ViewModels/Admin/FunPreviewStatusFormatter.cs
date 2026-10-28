using System.Globalization;
using Fotobox.Core;
using Fotobox.Core.Fun;

namespace Fotobox.ViewModels.Admin;

/// <summary>Statuszeile unter der Live-Vorschau im Tab „Fun“: Haupttext plus optional eine Warnzeile.</summary>
/// <param name="Text">Haupttext (Anzahl, Rate, Methode bzw. Lade-/Fehlermeldung).</param>
/// <param name="Warning">Zweite Zeile in der Warnfarbe (Grund des Ausweichens auf die Gesichtserkennung), sonst null.</param>
public sealed record FunPreviewStatusLines(string Text, string? Warning);

/// <summary>Formatiert den Zustand des Erkennungsdienstes für die Admin-Vorschau (reine Funktion, deutsche Texte).</summary>
public static class FunPreviewStatusFormatter
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public const string Loading = "Erkennung wird geladen …";
    public const string NotAvailable = "Die Erkennung ist nicht verfügbar.";

    /// <param name="status">Zustand der offenen Anforderung; null = es gibt keine (die Anforderung ist gescheitert).</param>
    /// <param name="personCount">Aktuell bestätigte Personen (<see cref="FunSnapshot.Count"/>).</param>
    public static FunPreviewStatusLines Format(DetectionStatus? status, int personCount)
    {
        if (status is null)
            return new FunPreviewStatusLines(NotAvailable, null);

        if (status.Active is not { } active)
        {
            return string.IsNullOrWhiteSpace(status.UnavailableReason)
                ? new FunPreviewStatusLines(Loading, null)
                : new FunPreviewStatusLines(status.UnavailableReason!, null);
        }

        var rate = Math.Max(0, status.AnalysesPerSecond).ToString("0.0", German);
        var text = $"{PersonText(personCount)} · {rate} Analysen/s · Methode: {MethodText(active)}";
        var warning = active == DetectionKind.Face && !string.IsNullOrWhiteSpace(status.FallbackReason)
            ? status.FallbackReason
            : null;
        return new FunPreviewStatusLines(text, warning);
    }

    public static string PersonText(int count) => count switch
    {
        <= 0 => "Keine Person erkannt",
        1 => "1 Person erkannt",
        _ => string.Create(German, $"{count} Personen erkannt"),
    };

    public static string MethodText(DetectionKind kind) => kind == DetectionKind.Body ? "Ganzkörper (KI)" : "Gesicht (Windows)";
}
