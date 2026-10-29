namespace Fotobox.Core.Heart;

/// <summary>Eine wählbare Herz-Aktion für die Admin-Oberfläche: Anzeigename und kurze Beschreibung.</summary>
public sealed record HeartActionInfo(HeartAction Action, string DisplayName, string Description);

/// <summary>
/// Katalog der Herz-Aktionen (eine Quelle für Admin-Auswahl und Anzeige). Neue Aktionen hier UND im Enum <see cref="HeartAction"/>
/// (nur hinten) ergänzen; ein Test prüft, dass beide übereinstimmen.
/// </summary>
public static class HeartActions
{
    public static IReadOnlyList<HeartActionInfo> All { get; } =
    [
        new(HeartAction.Kisscam, "Kisscam", "Jumbotron-Rahmen, Timer zum Kusspartner-Suchen, dann ein Foto mit Kisscam-Banner."),
    ];

    /// <summary>Der Katalogeintrag zu <paramref name="action"/> oder null, wenn die Aktion (noch) nicht im Katalog steht.</summary>
    public static HeartActionInfo? Find(HeartAction action) => All.FirstOrDefault(a => a.Action == action);
}
