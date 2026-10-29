namespace Fotobox.Core.Music;

/// <summary>Die Wahl für eine Kisscam: Datei, Startzeit und effektive Lautstärke (0–100).</summary>
public sealed record KisscamPick(string Path, TimeSpan StartAt, int VolumePercent);

/// <summary>Wählt den Song einer Kisscam rein zufällig (Wiederholung erlaubt) unter den angehakten, abspielbaren Songs.</summary>
public static class KisscamSongPicker
{
    /// <summary>
    /// Wählt unter den Songs mit Häkchen, die nicht als unabspielbar markiert sind. Keiner = <c>null</c> (die Kisscam läuft ohne Musik).
    /// Liest nur den übergebenen Stand, greift nie auf das Dateisystem zu.
    /// </summary>
    public static KisscamPick? Pick(KisscamLibrarySnapshot? snapshot, int totalVolumePercent, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (snapshot is null)
            return null;
        var candidates = snapshot.Songs.Where(s => s.Enabled && s.Playable).ToList();
        if (candidates.Count == 0)
            return null;
        var song = candidates[candidates.Count == 1 ? 0 : random.Next(candidates.Count)];
        return new KisscamPick(song.FullPath, song.EffectiveStart, EffectiveVolume(totalVolumePercent, song.VolumePercent));
    }

    /// <summary>Gesamtlautstärke × Song-Lautstärke / 100, kaufmännisch gerundet und auf 0–100 geklemmt.</summary>
    public static int EffectiveVolume(int totalVolumePercent, int songVolumePercent) =>
        Math.Clamp((int)Math.Round(totalVolumePercent * (double)songVolumePercent / 100.0, MidpointRounding.AwayFromZero), 0, 100);
}
