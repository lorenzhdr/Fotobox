namespace Fotobox.Core.Fun;

/// <summary>Ein verfolgtes Ziel. <see cref="IsConfirmed"/>: lange genug erkannt (Verweildauer).</summary>
public sealed record TrackedTarget(int Id, NormRect Box, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool IsConfirmed);

/// <summary>
/// Ordnet die Boxen aufeinanderfolgender Analysen einander zu (gierig nach IoU), glättet die Position und bestätigt ein
/// Ziel erst nach der Verweildauer. Ein Ziel ohne Treffer bleibt <see cref="LostTimeout"/> lang mit seiner letzten Position
/// erhalten (überbrückt einzelne Aussetzer des Detektors). Nicht threadsicher.
/// </summary>
public sealed class TargetTracker
{
    public const double MinIoU = 0.3;
    public const double SmoothingAlpha = 0.5;
    public static readonly TimeSpan LostTimeout = TimeSpan.FromMilliseconds(700);

    private sealed class Track
    {
        public required int Id { get; init; }
        public required NormRect Box { get; set; }
        public required DateTimeOffset FirstSeen { get; init; }
        public required DateTimeOffset LastSeen { get; set; }
    }

    private readonly TimeProvider _time;
    private readonly List<Track> _tracks = [];
    private TimeSpan _dwell = TimeSpan.Zero;
    private int _nextId = 1;

    public TargetTracker(TimeProvider time) => _time = time;

    /// <summary>Verweildauer, ab der ein Ziel bestätigt ist (0 = sofort).</summary>
    public void Configure(TimeSpan dwell) => _dwell = dwell < TimeSpan.Zero ? TimeSpan.Zero : dwell;

    /// <summary>Verarbeitet die Boxen einer Analyse und liefert alle aktuellen Ziele.</summary>
    public IReadOnlyList<TrackedTarget> Update(IReadOnlyList<NormRect> boxes)
    {
        var now = _time.GetUtcNow();
        // Abgelaufene Ziele zuerst entfernen, damit sie nicht durch eine späte Box "wiederbelebt" werden.
        _tracks.RemoveAll(t => now - t.LastSeen >= LostTimeout);

        // Alle Paare mit ausreichender Überdeckung, bestes zuerst (stabile Reihenfolge bei Gleichstand).
        var pairs = new List<(double IoU, int Track, int Box)>();
        for (var t = 0; t < _tracks.Count; t++)
        {
            for (var b = 0; b < boxes.Count; b++)
            {
                var iou = _tracks[t].Box.IoU(boxes[b]);
                if (iou >= MinIoU)
                    pairs.Add((iou, t, b));
            }
        }
        pairs = pairs.OrderByDescending(p => p.IoU).ThenBy(p => p.Track).ThenBy(p => p.Box).ToList();

        var trackUsed = new bool[_tracks.Count];
        var boxUsed = new bool[boxes.Count];
        foreach (var (_, t, b) in pairs)
        {
            if (trackUsed[t] || boxUsed[b])
                continue;
            trackUsed[t] = boxUsed[b] = true;
            var track = _tracks[t];
            track.Box = Smooth(track.Box, boxes[b]);
            track.LastSeen = now;
        }

        for (var b = 0; b < boxes.Count; b++)
        {
            if (boxUsed[b])
                continue;
            _tracks.Add(new Track { Id = _nextId++, Box = boxes[b], FirstSeen = now, LastSeen = now });
        }

        return Prune();
    }

    /// <summary>Entfernt Ziele, die zu lange nicht gesehen wurden, und liefert die übrigen (Bestätigung nach aktueller Zeit).</summary>
    public IReadOnlyList<TrackedTarget> Prune()
    {
        var now = _time.GetUtcNow();
        _tracks.RemoveAll(t => now - t.LastSeen >= LostTimeout);
        return _tracks
            .Select(t => new TrackedTarget(t.Id, t.Box, t.FirstSeen, t.LastSeen, now - t.FirstSeen >= _dwell))
            .ToList();
    }

    /// <summary>Alle Ziele vergessen (die Id-Zählung läuft weiter, Ids bleiben eindeutig).</summary>
    public void Reset() => _tracks.Clear();

    private static NormRect Smooth(NormRect old, NormRect observed)
    {
        static double Mix(double o, double n) => SmoothingAlpha * n + (1 - SmoothingAlpha) * o;
        return new NormRect(Mix(old.X, observed.X), Mix(old.Y, observed.Y), Mix(old.Width, observed.Width), Mix(old.Height, observed.Height));
    }
}
