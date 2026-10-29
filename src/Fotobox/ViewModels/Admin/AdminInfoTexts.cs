namespace Fotobox.ViewModels.Admin;

/// <summary>
/// Erklärtexte der Info-Symbole (ⓘ) im Admin. Eine Quelle für Tooltip und <c>AutomationProperties.HelpText</c>;
/// die Aussagen müssen zum Verhalten der Erkennungsdienste passen (siehe Kommentare).
/// </summary>
public static class AdminInfoTexts
{
    /// <summary>Beschriftung des Symbols für Bildschirmleser (Analysen pro Sekunde).</summary>
    public const string AnalysesPerSecondName = "Info: Analysen pro Sekunde";

    // PersonDetectionService.OnFrame: Rate = AnalysesPerSecond, Bild wird verkleinert (≤ 640 px), Analyse auf dem Threadpool.
    public const string FunAnalysesPerSecond =
        "Wie oft pro Sekunde das Livebild auf Personen untersucht wird. Das Bild wird dafür verkleinert; das Livebild selbst bleibt flüssig und hat immer Vorrang.\n" +
        "\n" +
        "Höher: Die Zielmarken folgen Bewegungen schneller, die Analyse kostet aber mehr Rechenleistung (CPU).\n" +
        "Niedriger: Das spart Leistung, die Marken reagieren träger.\n" +
        "\n" +
        "Empfehlung: 5 (Standard). Auf schwachen PCs 2 bis 3. Mehr als 5 bringt meist kaum sichtbare Verbesserung.\n" +
        "\n" +
        "Tipp: Zeigt die Statuszeile der Vorschau dauerhaft weniger Analysen/s als eingestellt, ist der PC ausgelastet. Senken Sie dann den Wert.";

    // PeaceOverlayViewModel.BuildOptions: Hände + Gesichter mit AnalysesPerSecond; mit Bart Gesichter max(10, 2 × Rate) ≤ 20/s,
    // Handstufe nur jeden 2. Zyklus (Handrate bleibt). PeaceEngine.Configure: Aussetzer-Frist = 2,5 Analyseabstände (mind. 250 ms).
    public const string PeaceAnalysesPerSecond =
        "Wie oft pro Sekunde Hände und Gesichter im Livebild ausgewertet werden. Das Livebild bleibt flüssig und hat immer Vorrang.\n" +
        "\n" +
        "Höher: Das Peace-Zeichen wird schneller erkannt und die Haltedauer genauer gemessen, die Analyse kostet aber mehr Rechenleistung (CPU).\n" +
        "Niedriger: Das spart Leistung, die Erkennung reagiert träger, und kurze Haltedauern werden ungenauer, weil Aussetzer erst nach etwa 2,5 Analyseabständen zählen.\n" +
        "\n" +
        "Der Wert gilt gemeinsam für die Peace-, die Mittelfinger- und die Herzerkennung. Ist ein Bart sichtbar, werden Gesichter automatisch häufiger verfolgt (mindestens 10, höchstens 20 pro Sekunde); die Handerkennung bleibt beim eingestellten Wert.\n" +
        "\n" +
        "Empfehlung: 5 (Standard), auf schwachen PCs 3.\n" +
        "\n" +
        "Tipp: Zeigt die Statuszeile der Vorschau dauerhaft weniger Analysen/s als eingestellt, ist der PC ausgelastet. Senken Sie dann den Wert.";

    // MiddleFingerEngine.Configure nutzt denselben Wert (PeaceSettings.AnalysesPerSecond) und dieselbe 2,5-Abstände-Regel (gilt auch für die Herzerkennung).
    public const string MiddleFingerAnalysesPerSecond =
        "Wie oft pro Sekunde Hände und Gesichter im Livebild ausgewertet werden. Das Livebild bleibt flüssig und hat immer Vorrang.\n" +
        "\n" +
        "Höher: Der Mittelfinger wird schneller erkannt und die Haltedauer genauer gemessen, die Analyse kostet aber mehr Rechenleistung (CPU).\n" +
        "Niedriger: Das spart Leistung, die Erkennung reagiert träger, und kurze Haltedauern werden ungenauer, weil Aussetzer erst nach etwa 2,5 Analyseabständen zählen.\n" +
        "\n" +
        "Der Wert gilt gemeinsam für die Peace-, die Mittelfinger- und die Herzerkennung und ist auf der Peace-Seite ebenfalls einstellbar. Ist ein Bart sichtbar, werden Gesichter automatisch häufiger verfolgt (mindestens 10, höchstens 20 pro Sekunde); die Handerkennung bleibt beim eingestellten Wert.\n" +
        "\n" +
        "Empfehlung: 5 (Standard), auf schwachen PCs 3.\n" +
        "\n" +
        "Tipp: Zeigt die Statuszeile der Vorschau dauerhaft weniger Analysen/s als eingestellt, ist der PC ausgelastet. Senken Sie dann den Wert.";

    // HeartEngine nutzt denselben Wert (PeaceSettings.AnalysesPerSecond) und dieselbe 2,5-Abstände-Regel wie PeaceEngine.Configure.
    public const string HeartAnalysesPerSecond =
        "Wie oft pro Sekunde Hände und Gesichter im Livebild ausgewertet werden. Das Livebild bleibt flüssig und hat immer Vorrang.\n" +
        "\n" +
        "Höher: Das Herz wird schneller erkannt und die Haltedauer genauer gemessen, die Analyse kostet aber mehr Rechenleistung (CPU).\n" +
        "Niedriger: Das spart Leistung, die Erkennung reagiert träger, und kurze Haltedauern werden ungenauer, weil Aussetzer erst nach etwa 2,5 Analyseabständen zählen.\n" +
        "\n" +
        "Der Wert gilt gemeinsam für die Peace-, die Mittelfinger- und die Herzerkennung und ist auf der Peace-Seite ebenfalls einstellbar.\n" +
        "\n" +
        "Empfehlung: 5 (Standard), auf schwachen PCs 3.\n" +
        "\n" +
        "Tipp: Zeigt die Statuszeile der Vorschau dauerhaft weniger Analysen/s als eingestellt, ist der PC ausgelastet. Senken Sie dann den Wert.";
}
