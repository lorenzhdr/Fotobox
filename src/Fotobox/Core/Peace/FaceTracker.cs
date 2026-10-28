using Fotobox.Core.Fun;

namespace Fotobox.Core.Peace;

/// <summary>Ein Gesicht mit stabiler Verfolgungs-Id (gleiche Person über mehrere Analysen = gleiche Id).</summary>
public sealed record TrackedFace(int Id, FaceLandmarks Face);

/// <summary>
/// Vergibt Gesichtern stabile Ids: Zuordnung gierig nach Überdeckung (IoU ≥ 0,3) oder, falls die Boxen sich nicht genug überdecken,
/// nach Mittenabstand (&lt; 0,5 Gesichtsbreiten). Ein Gesicht ohne Treffer behält seine Id <see cref="LostTimeout"/> lang
/// (überbrückt einzelne Aussetzer des Detektors). Nicht threadsicher.
/// </summary>
public sealed class FaceTracker
{
    public const double MinIoU = 0.3;
    public const double MaxCenterDistanceFactor = 0.5;
    public static readonly TimeSpan LostTimeout = TimeSpan.FromMilliseconds(700);

    private sealed class Track
    {
        public required int Id { get; init; }
        public required NormRect Box { get; set; }
        public required DateTimeOffset LastSeen { get; set; }
    }

    private readonly TimeProvider _time;
    private readonly List<Track> _tracks = [];
    private int _nextId = 1;

    public FaceTracker(TimeProvider time) => _time = time;

    /// <summary>Ordnet die Gesichter einer Analyse zu. Liefert genau die übergebenen Gesichter (gleiche Reihenfolge) mit Ids.</summary>
    /// <param name="aspect">Breite/Höhe des Bilds, für isotrope Mittenabstände.</param>
    public IReadOnlyList<TrackedFace> Update(IReadOnlyList<FaceLandmarks> faces, double aspect = 16.0 / 9)
    {
        if (!(aspect > 0))
            aspect = 1;
        var now = _time.GetUtcNow();
        _tracks.RemoveAll(t => now - t.LastSeen >= LostTimeout);

        var pairs = new List<(double Score, int Track, int Face)>();
        for (var t = 0; t < _tracks.Count; t++)
        {
            var box = _tracks[t].Box;
            for (var f = 0; f < faces.Count; f++)
            {
                var other = faces[f].Box;
                var iou = box.IoU(other);
                if (iou >= MinIoU)
                {
                    pairs.Add((2 + iou, t, f));
                    continue;
                }
                var fw = box.Width * aspect;
                if (fw <= 0)
                    continue;
                var dx = (box.CenterX - other.CenterX) * aspect;
                var dy = box.CenterY - other.CenterY;
                var ratio = Math.Sqrt(dx * dx + dy * dy) / fw;
                if (ratio < MaxCenterDistanceFactor)
                    pairs.Add((1 - ratio / MaxCenterDistanceFactor + 1e-6, t, f));
            }
        }
        pairs = pairs.OrderByDescending(p => p.Score).ThenBy(p => p.Track).ThenBy(p => p.Face).ToList();

        var ids = new int[faces.Count];
        var trackUsed = new bool[_tracks.Count];
        var faceUsed = new bool[faces.Count];
        foreach (var (_, t, f) in pairs)
        {
            if (trackUsed[t] || faceUsed[f])
                continue;
            trackUsed[t] = faceUsed[f] = true;
            _tracks[t].Box = faces[f].Box;
            _tracks[t].LastSeen = now;
            ids[f] = _tracks[t].Id;
        }

        for (var f = 0; f < faces.Count; f++)
        {
            if (faceUsed[f])
                continue;
            var track = new Track { Id = _nextId++, Box = faces[f].Box, LastSeen = now };
            _tracks.Add(track);
            ids[f] = track.Id;
        }

        var result = new List<TrackedFace>(faces.Count);
        for (var f = 0; f < faces.Count; f++)
            result.Add(new TrackedFace(ids[f], faces[f]));
        return result;
    }

    /// <summary>Alle Gesichter vergessen (die Id-Zählung läuft weiter, Ids bleiben eindeutig).</summary>
    public void Reset() => _tracks.Clear();
}
