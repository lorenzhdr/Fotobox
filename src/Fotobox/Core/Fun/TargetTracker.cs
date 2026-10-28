namespace Fotobox.Core.Fun;

/// <summary>Ein verfolgtes Ziel. <see cref="IsConfirmed"/>: lange genug erkannt (Verweildauer).</summary>
public sealed record TrackedTarget(int Id, NormRect Box, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool IsConfirmed);

/// <summary>
/// Ordnet die Boxen aufeinanderfolgender Analysen einander zu (gierig nach IoU), glättet die Position und bestätigt ein
/// Ziel erst nach der Verweildauer. Ein Ziel ohne Treffer bleibt <see cref="LostTimeout"/> lang mit seiner letzten Position
/// erhalten (überbrückt einzelne Aussetzer des Detektors). Ein bestätigtes Ziel, das länger fehlt, wird weitere
/// <see cref="RecoveryWindow"/> (ab dem letzten Treffer) vorgemerkt: Taucht nahe seiner letzten Position wieder eine Box auf,
/// übernimmt sie Id und Bestätigung (keine neue Person, keine neue Verweildauer). Nicht threadsicher.
/// </summary>
public sealed class TargetTracker
{
    public const double MinIoU = 0.3;
    public const double SmoothingAlpha = 0.5;
    /// <summary>Mittenabstand (in Boxbreiten der verlorenen Spur), unter dem eine Box eine verlorene Spur wiederfindet.</summary>
    public const double RecoveryDistance = 0.5;
    public static readonly TimeSpan LostTimeout = TimeSpan.FromMilliseconds(700);
    /// <summary>So lange (ab dem letzten Treffer) wird eine bestätigte, verlorene Spur für das Wiederfinden vorgemerkt.</summary>
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromSeconds(3);

    private sealed class Track
    {
        public required int Id { get; init; }
        public required NormRect Box { get; set; }
        public required DateTimeOffset FirstSeen { get; init; }
        public required DateTimeOffset LastSeen { get; set; }
        /// <summary>War die Spur mindestens einmal bestätigt? Nur solche werden nach dem Verlust vorgemerkt.</summary>
        public bool WasConfirmed { get; set; }
    }

    private readonly TimeProvider _time;
    private readonly List<Track> _tracks = [];
    private readonly List<Track> _lost = [];
    private TimeSpan _dwell = TimeSpan.Zero;
    private int _nextId = 1;

    public TargetTracker(TimeProvider time) => _time = time;

    /// <summary>Verweildauer, ab der ein Ziel bestätigt ist (0 = sofort).</summary>
    public void Configure(TimeSpan dwell) => _dwell = dwell < TimeSpan.Zero ? TimeSpan.Zero : dwell;

    /// <summary>Ids der vorgemerkten (verlorenen, aber noch wiederfindbaren) Ziele.</summary>
    public IEnumerable<int> RememberedIds => _lost.Select(t => t.Id);

    /// <summary>Gibt es verlorene Ziele, die noch wiedergefunden werden könnten?</summary>
    public bool HasRemembered => _lost.Count > 0;

    /// <summary>Verarbeitet die Boxen einer Analyse und liefert alle aktuellen Ziele.</summary>
    public IReadOnlyList<TrackedTarget> Update(IReadOnlyList<NormRect> boxes)
    {
        var now = _time.GetUtcNow();
        // Abgelaufene Ziele zuerst entfernen, damit sie nicht durch eine späte Box "wiederbelebt" werden.
        Expire(now);

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

        // Übrige Boxen: zuerst eine kürzlich verlorene, bestätigte Spur wiederfinden, sonst eine neue Spur anlegen.
        RecoverLost(boxes, boxUsed, now);
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
        Expire(now);
        var result = new List<TrackedTarget>(_tracks.Count);
        foreach (var t in _tracks)
        {
            var confirmed = now - t.FirstSeen >= _dwell;
            if (confirmed)
                t.WasConfirmed = true;
            result.Add(new TrackedTarget(t.Id, t.Box, t.FirstSeen, t.LastSeen, confirmed));
        }
        return result;
    }

    /// <summary>Alle Ziele vergessen (die Id-Zählung läuft weiter, Ids bleiben eindeutig).</summary>
    public void Reset()
    {
        _tracks.Clear();
        _lost.Clear();
    }

    // Aktive Spuren ohne Treffer wandern nach LostTimeout (wenn sie je bestätigt waren) in die Vormerkung; diese läuft nach
    // RecoveryWindow ab dem letzten Treffer ab.
    private void Expire(DateTimeOffset now)
    {
        for (var i = _tracks.Count - 1; i >= 0; i--)
        {
            var track = _tracks[i];
            if (now - track.LastSeen < LostTimeout)
                continue;
            _tracks.RemoveAt(i);
            if (track.WasConfirmed)
                _lost.Add(track);
        }
        _lost.RemoveAll(t => now - t.LastSeen >= RecoveryWindow);
    }

    // Ordnet übrige Boxen verlorenen Spuren zu (IoU ≥ MinIoU oder Mittenabstand < RecoveryDistance Boxbreiten); beste zuerst.
    private void RecoverLost(IReadOnlyList<NormRect> boxes, bool[] boxUsed, DateTimeOffset now)
    {
        if (_lost.Count == 0)
            return;

        var candidates = new List<(double Quality, int Lost, int Box)>();
        for (var l = 0; l < _lost.Count; l++)
        {
            var old = _lost[l].Box;
            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxUsed[b])
                    continue;
                var iou = old.IoU(boxes[b]);
                var distance = old.Width > 0
                    ? Math.Sqrt(Math.Pow(old.CenterX - boxes[b].CenterX, 2) + Math.Pow(old.CenterY - boxes[b].CenterY, 2)) / old.Width
                    : double.MaxValue;
                if (iou >= MinIoU || distance < RecoveryDistance)
                    candidates.Add((Math.Max(iou, 1 - distance / RecoveryDistance), l, b));
            }
        }

        var lostUsed = new bool[_lost.Count];
        foreach (var (_, l, b) in candidates.OrderByDescending(c => c.Quality).ThenBy(c => c.Lost).ThenBy(c => c.Box))
        {
            if (lostUsed[l] || boxUsed[b])
                continue;
            lostUsed[l] = boxUsed[b] = true;
            var track = _lost[l];
            track.Box = boxes[b];
            track.LastSeen = now;
            _tracks.Add(track);
        }

        for (var l = _lost.Count - 1; l >= 0; l--)
        {
            if (lostUsed[l])
                _lost.RemoveAt(l);
        }
    }

    private static NormRect Smooth(NormRect old, NormRect observed)
    {
        static double Mix(double o, double n) => SmoothingAlpha * n + (1 - SmoothingAlpha) * o;
        return new NormRect(Mix(old.X, observed.X), Mix(old.Y, observed.Y), Mix(old.Width, observed.Width), Mix(old.Height, observed.Height));
    }
}
