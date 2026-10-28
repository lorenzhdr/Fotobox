using Fotobox.Core.Fun;

namespace Fotobox.Core.Peace;

/// <summary>Ein Gesicht mit stabiler Verfolgungs-Id (gleiche Person über mehrere Analysen = gleiche Id).</summary>
public sealed record TrackedFace(int Id, FaceLandmarks Face);

/// <summary>
/// Vergibt Gesichtern stabile Ids: Zuordnung gierig nach Überdeckung (IoU ≥ 0,3) oder, falls die Boxen sich nicht genug überdecken,
/// nach Mittenabstand (&lt; 0,5 Gesichtsbreiten). Zuerst werden die aktiven Spuren (im letzten Update gesehen) zugeordnet; Gesichter,
/// die danach übrig sind, greifen kürzlich verlorene Spuren wieder auf (bis <see cref="LostAfter"/>), wenn sie nahe der zuletzt
/// gesehenen bzw. per Geschwindigkeit vorhergesagten Lage erscheinen (&lt; 1 Gesichtsbreite). Eine Spur ohne Treffer behält ihre Id
/// <see cref="LostTimeout"/> lang (überbrückt einzelne Aussetzer des Detektors). Nicht threadsicher.
/// </summary>
public sealed class FaceTracker
{
    public const double MinIoU = 0.3;
    public const double MaxCenterDistanceFactor = 0.5;
    /// <summary>Wiederaufgreifen einer verlorenen Spur: größter Abstand zur vorhergesagten Mitte in Gesichtsbreiten.</summary>
    public const double MaxLostCenterDistanceFactor = 1.0;
    /// <summary>So weit (Sekunden) wird die Lage einer verlorenen Spur höchstens fortgeschrieben.</summary>
    public const double MaxPredictionSeconds = 0.5;
    /// <summary>Obergrenze der geschätzten Geschwindigkeit in Gesichtsbreiten pro Sekunde (fängt Ausreißer ab).</summary>
    public const double MaxSpeedFaceWidths = 3.0;
    public static readonly TimeSpan LostTimeout = TimeSpan.FromMilliseconds(700);

    private sealed class Track
    {
        public required int Id { get; init; }
        public required NormRect Box { get; set; }
        public required DateTimeOffset LastSeen { get; set; }
        /// <summary>Nummer des Updates, in dem die Spur zuletzt zugeordnet wurde (aktiv = im vorigen Update).</summary>
        public required long SeenInUpdate { get; set; }
        /// <summary>Geschwindigkeit der Mitte in normierten Einheiten pro Sekunde (x, y getrennt).</summary>
        public double Vx { get; set; }
        public double Vy { get; set; }
    }

    private readonly TimeProvider _time;
    private readonly List<Track> _tracks = [];
    private int _nextId = 1;
    private long _updateIndex;
    private TimeSpan _lostAfter = LostTimeout;

    /// <summary>Wirksame Verlustfrist; nie kürzer als <see cref="LostTimeout"/> (bei seltenen Analysen länger).</summary>
    public TimeSpan LostAfter
    {
        get => _lostAfter;
        set => _lostAfter = value > LostTimeout ? value : LostTimeout;
    }

    public FaceTracker(TimeProvider time) => _time = time;

    /// <summary>Ordnet die Gesichter einer Analyse zu. Liefert genau die übergebenen Gesichter (gleiche Reihenfolge) mit Ids.</summary>
    /// <param name="aspect">Breite/Höhe des Bilds, für isotrope Mittenabstände.</param>
    public IReadOnlyList<TrackedFace> Update(IReadOnlyList<FaceLandmarks> faces, double aspect = 16.0 / 9)
    {
        if (!(aspect > 0))
            aspect = 1;
        var now = _time.GetUtcNow();
        _updateIndex++;
        _tracks.RemoveAll(t => now - t.LastSeen >= _lostAfter);

        var ids = new int[faces.Count];
        var trackUsed = new bool[_tracks.Count];
        var faceUsed = new bool[faces.Count];

        // Durchgang 1: aktive Spuren (im letzten Update gesehen), Durchgang 2: kürzlich verlorene Spuren mit den übrigen Gesichtern.
        for (var pass = 1; pass <= 2; pass++)
        {
            var pairs = new List<(double Score, int Track, int Face)>();
            for (var t = 0; t < _tracks.Count; t++)
            {
                var track = _tracks[t];
                var active = track.SeenInUpdate == _updateIndex - 1;
                if (trackUsed[t] || active != (pass == 1))
                    continue;
                for (var f = 0; f < faces.Count; f++)
                {
                    if (faceUsed[f])
                        continue;
                    var score = pass == 1 ? ScoreActive(track, faces[f].Box, aspect) : ScoreLost(track, faces[f].Box, aspect, now);
                    if (score > 0)
                        pairs.Add((score, t, f));
                }
            }
            pairs = pairs.OrderByDescending(p => p.Score).ThenBy(p => p.Track).ThenBy(p => p.Face).ToList();
            foreach (var (_, t, f) in pairs)
            {
                if (trackUsed[t] || faceUsed[f])
                    continue;
                trackUsed[t] = faceUsed[f] = true;
                Follow(_tracks[t], faces[f].Box, now);
                ids[f] = _tracks[t].Id;
            }
        }

        for (var f = 0; f < faces.Count; f++)
        {
            if (faceUsed[f])
                continue;
            var track = new Track { Id = _nextId++, Box = faces[f].Box, LastSeen = now, SeenInUpdate = _updateIndex };
            _tracks.Add(track);
            ids[f] = track.Id;
        }

        var result = new List<TrackedFace>(faces.Count);
        for (var f = 0; f < faces.Count; f++)
            result.Add(new TrackedFace(ids[f], faces[f]));
        return result;
    }

    // Aktive Spur: Überdeckung oder Mittenabstand zur letzten Box (wie bisher). Score 0 = kein Treffer.
    private static double ScoreActive(Track track, NormRect other, double aspect)
    {
        var iou = track.Box.IoU(other);
        if (iou >= MinIoU)
            return 2 + iou;
        var fw = track.Box.Width * aspect;
        if (fw <= 0)
            return 0;
        var dx = (track.Box.CenterX - other.CenterX) * aspect;
        var dy = track.Box.CenterY - other.CenterY;
        var ratio = Math.Sqrt(dx * dx + dy * dy) / fw;
        return ratio < MaxCenterDistanceFactor ? 1 - ratio / MaxCenterDistanceFactor + 1e-6 : 0;
    }

    // Verlorene Spur: Überdeckung mit der letzten Box oder Nähe zur per Geschwindigkeit fortgeschriebenen Mitte (< 1 Gesichtsbreite).
    private static double ScoreLost(Track track, NormRect other, double aspect, DateTimeOffset now)
    {
        var iou = track.Box.IoU(other);
        if (iou >= MinIoU)
            return 2 + iou;
        var fw = track.Box.Width * aspect;
        if (fw <= 0)
            return 0;
        var seconds = Math.Clamp((now - track.LastSeen).TotalSeconds, 0, MaxPredictionSeconds);
        var px = track.Box.CenterX + track.Vx * seconds;
        var py = track.Box.CenterY + track.Vy * seconds;
        var dx = (px - other.CenterX) * aspect;
        var dy = py - other.CenterY;
        var ratio = Math.Sqrt(dx * dx + dy * dy) / fw;
        return ratio < MaxLostCenterDistanceFactor ? 1 - ratio / MaxLostCenterDistanceFactor + 1e-6 : 0;
    }

    // Übernimmt die neue Box und schätzt die Geschwindigkeit der Mitte (geglättet, begrenzt auf MaxSpeedFaceWidths).
    private void Follow(Track track, NormRect box, DateTimeOffset now)
    {
        var dt = (now - track.LastSeen).TotalSeconds;
        if (dt >= 0.02)
        {
            var maxSpeed = MaxSpeedFaceWidths * Math.Max(box.Width, track.Box.Width);
            var vx = (box.CenterX - track.Box.CenterX) / dt;
            var vy = (box.CenterY - track.Box.CenterY) / dt;
            vx = Math.Clamp(vx, -maxSpeed, maxSpeed);
            vy = Math.Clamp(vy, -maxSpeed, maxSpeed);
            track.Vx = 0.5 * track.Vx + 0.5 * vx;
            track.Vy = 0.5 * track.Vy + 0.5 * vy;
        }
        track.Box = box;
        track.LastSeen = now;
        track.SeenInUpdate = _updateIndex;
    }

    /// <summary>Alle Gesichter vergessen (die Id-Zählung läuft weiter, Ids bleiben eindeutig).</summary>
    public void Reset() => _tracks.Clear();
}
