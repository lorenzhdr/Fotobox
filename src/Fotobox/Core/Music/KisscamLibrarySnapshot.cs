namespace Fotobox.Core.Music;

/// <summary>Ein Song im Musikordner mit seinen gespeicherten Werten und bekannten Eigenschaften (unveränderlich).</summary>
/// <param name="FileName">Reiner Dateiname im Musikordner (Schlüssel in der Config).</param>
/// <param name="FullPath">Vollständiger Pfad der Datei.</param>
/// <param name="Enabled">Häkchen: Der Song darf in der Kisscam gespielt werden.</param>
/// <param name="StartSeconds">Startzeit in Sekunden (wie gespeichert, auch wenn sie hinter dem Songende liegt).</param>
/// <param name="VolumePercent">Song-Lautstärke in Prozent der Gesamtlautstärke.</param>
/// <param name="Duration">Songlänge; null = unbekannt (noch nicht gelesen oder nicht lesbar).</param>
/// <param name="Playable"><c>false</c>, sobald der Player die Datei nicht abspielen konnte (bis zur Änderung der Datei oder „Aktualisieren“).</param>
public sealed record KisscamSong(string FileName, string FullPath, bool Enabled, int StartSeconds, int VolumePercent,
    TimeSpan? Duration, bool Playable)
{
    /// <summary>Die Startzeit liegt hinter (oder auf) dem Songende; gespielt wird dann ab 0:00.</summary>
    public bool StartBeyondEnd =>
        Duration is { } d && d > TimeSpan.Zero && TimeSpan.FromSeconds(StartSeconds) >= d;

    /// <summary>Tatsächliche Startzeit der Wiedergabe: die gespeicherte, bei <see cref="StartBeyondEnd"/> 0:00.</summary>
    public TimeSpan EffectiveStart => StartBeyondEnd ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, StartSeconds));
}

public enum KisscamLibraryStatus
{
    /// <summary>Der Ordner wird gerade geprüft und aufgelistet (noch keine Songs bekannt).</summary>
    Loading,

    /// <summary>Ordner und Config sind in Ordnung (die Liste kann leer sein).</summary>
    Ready,

    /// <summary>Der Ordner lässt sich nicht anlegen oder lesen (Zeitüberschreitung, kein Zugriff, ungültiger Pfad); die Songs sind der letzte bekannte Stand.</summary>
    FolderUnavailable,

    /// <summary>Die Config ist unlesbar. Die Songs sind aufgelistet, aber nicht angehakt; die nächste Änderung im Admin ersetzt die Datei (die alte bleibt als <c>.defekt</c> erhalten).</summary>
    ConfigBroken,
}

/// <summary>Unveränderlicher Stand der Musikbibliothek; wird nie blockierend gelesen.</summary>
/// <param name="Folder">Aufgelöster Musikordner (leer, solange keiner gesetzt ist).</param>
/// <param name="SaveFailed">Die letzte Änderung konnte nicht in die Config geschrieben werden.</param>
public sealed record KisscamLibrarySnapshot(string Folder, KisscamLibraryStatus Status, IReadOnlyList<KisscamSong> Songs, bool SaveFailed)
{
    public static KisscamLibrarySnapshot Empty { get; } = new(string.Empty, KisscamLibraryStatus.Loading, [], false);

    /// <summary>Mindestens ein Song ist angehakt (auch wenn er nicht abspielbar ist).</summary>
    public bool HasEnabledSong => Songs.Any(s => s.Enabled);
}
