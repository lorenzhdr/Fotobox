using Fotobox.Core.Fun;

namespace Fotobox.Core.Peace;

/// <summary>Eine Hand mit stabiler Verfolgungs-Id.</summary>
public sealed record TrackedHand(int Id, HandLandmarks Hand);

/// <summary>
/// Vergibt Händen stabile Ids: Zuordnung über den Abstand der Handgelenke (bezogen auf die Handgröße) bzw. die Überdeckung der
/// Boxen (IoU ≥ 0,3). Eine Hand ohne Treffer behält ihre Id <see cref="LostTimeout"/> lang. Nicht threadsicher.
/// </summary>
public sealed class HandTracker
{
    public const double MinIoU = 0.3;
    /// <summary>Größter Handgelenk-Abstand (in Handgrößen), bei dem es noch dieselbe Hand ist.</summary>
    public const double MaxWristDistanceFactor = 1.0;
    public static readonly TimeSpan LostTimeout = TimeSpan.FromMilliseconds(400);

    private sealed class Track
    {
        public required int Id { get; init; }
        public required NormRect Box { get; set; }
        public required NormPoint Wrist { get; set; }
        public required DateTimeOffset LastSeen { get; set; }
    }

    private readonly TimeProvider _time;
    private readonly List<Track> _tracks = [];
    private int _nextId = 1;
    private TimeSpan _lostAfter = LostTimeout;

    /// <summary>Wirksame Verlustfrist; nie kürzer als <see cref="LostTimeout"/> (bei seltenen Analysen länger).</summary>
    public TimeSpan LostAfter
    {
        get => _lostAfter;
        set => _lostAfter = value > LostTimeout ? value : LostTimeout;
    }

    public HandTracker(TimeProvider time) => _time = time;

    /// <summary>Ordnet die Hände einer Analyse zu; liefert die Hände mit Ids (Hände ohne 21 Punkte werden übersprungen).</summary>
    /// <param name="aspect">Breite/Höhe des Bilds, für isotrope Abstände.</param>
    public IReadOnlyList<TrackedHand> Update(IReadOnlyList<HandLandmarks> hands, double aspect = 16.0 / 9)
    {
        if (!(aspect > 0))
            aspect = 1;
        var now = _time.GetUtcNow();
        _tracks.RemoveAll(t => now - t.LastSeen >= _lostAfter);

        var valid = hands.Where(h => h.Points is { Count: >= PeaceGestureClassifier.PointCount }).ToList();

        var pairs = new List<(double Score, int Track, int Hand)>();
        for (var t = 0; t < _tracks.Count; t++)
        {
            for (var h = 0; h < valid.Count; h++)
            {
                var hand = valid[h];
                var iou = _tracks[t].Box.IoU(hand.Box);
                if (iou >= MinIoU)
                {
                    pairs.Add((2 + iou, t, h));
                    continue;
                }
                var size = Math.Max(hand.Box.Width * aspect, hand.Box.Height);
                if (size <= 0)
                    continue;
                var wrist = hand.Points[0];
                var dx = (wrist.X - _tracks[t].Wrist.X) * aspect;
                var dy = wrist.Y - _tracks[t].Wrist.Y;
                var ratio = Math.Sqrt(dx * dx + dy * dy) / size;
                if (ratio <= MaxWristDistanceFactor)
                    pairs.Add((1 - ratio / (MaxWristDistanceFactor + 1e-9), t, h));
            }
        }
        pairs = pairs.OrderByDescending(p => p.Score).ThenBy(p => p.Track).ThenBy(p => p.Hand).ToList();

        var ids = new int[valid.Count];
        var trackUsed = new bool[_tracks.Count];
        var handUsed = new bool[valid.Count];
        foreach (var (_, t, h) in pairs)
        {
            if (trackUsed[t] || handUsed[h])
                continue;
            trackUsed[t] = handUsed[h] = true;
            Store(_tracks[t], valid[h], now);
            ids[h] = _tracks[t].Id;
        }

        for (var h = 0; h < valid.Count; h++)
        {
            if (handUsed[h])
                continue;
            var track = new Track
            {
                Id = _nextId++,
                Box = valid[h].Box,
                Wrist = new NormPoint(valid[h].Points[0].X, valid[h].Points[0].Y),
                LastSeen = now,
            };
            _tracks.Add(track);
            ids[h] = track.Id;
        }

        var result = new List<TrackedHand>(valid.Count);
        for (var h = 0; h < valid.Count; h++)
            result.Add(new TrackedHand(ids[h], valid[h]));
        return result;
    }

    /// <summary>Alle Hände vergessen (die Id-Zählung läuft weiter).</summary>
    public void Reset() => _tracks.Clear();

    private static void Store(Track track, HandLandmarks hand, DateTimeOffset now)
    {
        track.Box = hand.Box;
        track.Wrist = new NormPoint(hand.Points[0].X, hand.Points[0].Y);
        track.LastSeen = now;
    }
}
