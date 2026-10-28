using Fotobox.Core.Fun;

namespace Fotobox.Core.Peace;

/// <summary>
/// UI-freie Verfolgung der Bärte: Je Gesicht (Id aus dem <see cref="FaceTracker"/>) ein Bart mit geglätteter Lage und stabilem
/// Stil. Eine Sitzung (<see cref="StartSession"/>) legt fest, wer einen Bart bekommt und wann er endet. Zeit über
/// <see cref="TimeProvider"/>; nicht threadsicher (UI-Thread).
/// </summary>
public sealed class MoustacheTracker
{
    /// <summary>Glättung (EMA) von Mitte, Breite und Winkel.</summary>
    public const double SmoothingAlpha = 0.5;
    /// <summary>So lange dürfen alle bebarteten Gesichter fehlen, bevor „bis zum nächsten Foto bzw. bis die Person verschwindet“ endet.</summary>
    public static readonly TimeSpan GoneTimeout = TimeSpan.FromMilliseconds(1500);
    /// <summary>So lange wird ein Bart ohne frische Beobachtung noch gezeichnet (überbrückt Aussetzer der Erkennung).</summary>
    public static readonly TimeSpan ViewTimeout = TimeSpan.FromMilliseconds(700);
    /// <summary>Gesichter, die vor dem Start der Sitzung zuletzt gesehen wurden, zählen nur, wenn die Beobachtung höchstens so alt ist.</summary>
    public static readonly TimeSpan SeedMaxAge = TimeSpan.FromSeconds(1);

    private static readonly MoustacheStyle[] ConcreteStyles =
        [MoustacheStyle.Twirl, MoustacheStyle.Walrus, MoustacheStyle.Chevron, MoustacheStyle.Brush];

    private sealed class Entry
    {
        public required int FaceId { get; init; }
        public required MoustacheStyle Style { get; init; }
        public required DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; set; }
        public MoustachePose Pose { get; set; }
        public NormRect FaceBox { get; set; }
    }

    private readonly TimeProvider _time;
    private readonly Func<int, int> _random;
    private readonly Dictionary<int, Entry> _entries = [];

    private bool _active;
    private MoustacheTargets _targets;
    private MoustacheStyle _style;
    private int? _ownerFaceId;
    private MoustacheVisibility _visibility;
    private TimeSpan _duration;
    private DateTimeOffset _sessionStart;
    private DateTimeOffset _expiresAt;
    private DateTimeOffset _accountedAt;
    private bool _hold;

    private IReadOnlyList<TrackedFace> _lastFaces = [];
    private double _lastWidth;
    private double _lastHeight;
    private DateTimeOffset _lastFacesAt = DateTimeOffset.MinValue;

    public MoustacheTracker(TimeProvider time, Func<int, int>? random = null)
    {
        _time = time;
        _random = random ?? (n => Random.Shared.Next(n));
    }

    /// <summary>Läuft eine Bart-Sitzung?</summary>
    public bool IsActive => _active;

    /// <summary>Aktuell zu zeichnende Bärte (nur Gesichter, die innerhalb von <see cref="ViewTimeout"/> gesehen wurden).</summary>
    public IReadOnlyList<MoustacheView> Views
    {
        get
        {
            if (!_active)
                return [];
            var now = _time.GetUtcNow();
            return _entries.Values
                .Where(e => now - e.LastSeen <= ViewTimeout)
                .OrderBy(e => e.FaceId)
                .Select(e => new MoustacheView(e.FaceId, e.Style, e.Pose))
                .ToList();
        }
    }

    /// <summary>
    /// Startet (oder erneuert) die Sitzung: Stile werden neu gewürfelt, die feste Dauer beginnt von vorn. Bereits bekannte Gesichter
    /// der letzten Analyse (höchstens <see cref="SeedMaxAge"/> alt) bekommen sofort einen Bart.
    /// </summary>
    /// <param name="ownerFaceId">Gesicht der auslösenden Hand (nur für <see cref="MoustacheTargets.PeaceShower"/>); null = niemand.</param>
    public void StartSession(MoustacheTargets targets, MoustacheStyle style, int? ownerFaceId, MoustacheVisibility visibility, int durationSeconds)
    {
        var now = _time.GetUtcNow();
        _entries.Clear();
        _active = true;
        _targets = targets;
        _style = style;
        _ownerFaceId = ownerFaceId;
        _visibility = visibility;
        _duration = TimeSpan.FromSeconds(Math.Max(0, durationSeconds));
        _sessionStart = now;
        _expiresAt = now + _duration;
        _accountedAt = now;

        if (_lastFaces.Count > 0 && now - _lastFacesAt <= SeedMaxAge)
            Apply(_lastFaces, _lastWidth, _lastHeight, now, refreshLastSeen: false);
    }

    /// <summary>Beendet die Sitzung; alle Bärte verschwinden.</summary>
    public void EndSession()
    {
        _active = false;
        _entries.Clear();
        _hold = false;
    }

    /// <summary>Das Foto ist fertig: „Bis zum nächsten Foto“ endet, eine feste Dauer läuft weiter.</summary>
    public void OnPhotoCompleted()
    {
        if (_active && _visibility == MoustacheVisibility.UntilPhotoOrGone)
            EndSession();
    }

    /// <summary>Hält die feste Dauer an, solange <paramref name="hold"/> gesetzt ist (Countdown, Aufnahme).</summary>
    public void SetHoldExpiry(bool hold)
    {
        Account(_time.GetUtcNow());
        _hold = hold;
    }

    /// <summary>Gesichter einer Analyse (mit Ids) samt Bildgröße in Pixeln (für die Geometrie).</summary>
    public void OnFaces(IReadOnlyList<TrackedFace> faces, double imageWidth, double imageHeight)
    {
        var now = _time.GetUtcNow();
        _lastFaces = faces;
        _lastWidth = imageWidth;
        _lastHeight = imageHeight;
        _lastFacesAt = now;
        if (_active)
            Apply(faces, imageWidth, imageHeight, now, refreshLastSeen: true);
    }

    /// <summary>Zeitgesteuerte Enden (feste Dauer, Personen verschwunden) und Aufräumen.</summary>
    public void Tick()
    {
        if (!_active)
            return;
        var now = _time.GetUtcNow();
        Account(now);

        if (_visibility == MoustacheVisibility.FixedDuration)
        {
            if (now >= _expiresAt)
                EndSession();
            return;
        }

        var lastSeen = _sessionStart;
        foreach (var entry in _entries.Values)
        {
            if (entry.LastSeen > lastSeen)
                lastSeen = entry.LastSeen;
        }
        if (now - lastSeen > GoneTimeout)
            EndSession();
    }

    /// <summary>Zustand für die Foto-Planung (null ohne Sitzung). <see cref="MoustacheTrack.Age"/> = Zeit seit der letzten Beobachtung des Gesichts.</summary>
    public MoustachePlanInput? ExportPlanInput()
    {
        if (!_active)
            return null;
        var now = _time.GetUtcNow();
        var tracks = _entries.Values
            .Where(e => now - e.LastSeen <= GoneTimeout)
            .OrderBy(e => e.FaceId)
            .Select(e => new MoustacheTrack(e.FaceId, e.Style, e.FaceBox, e.Pose, now - e.LastSeen))
            .ToList();
        return new MoustachePlanInput(_targets, _ownerFaceId, tracks);
    }

    // ---- intern

    private void Account(DateTimeOffset now)
    {
        if (_active && _hold && _visibility == MoustacheVisibility.FixedDuration && now > _accountedAt)
            _expiresAt += now - _accountedAt;
        _accountedAt = now;
    }

    private void Apply(IReadOnlyList<TrackedFace> faces, double w, double h, DateTimeOffset now, bool refreshLastSeen)
    {
        foreach (var face in faces)
        {
            if (_targets == MoustacheTargets.PeaceShower && face.Id != _ownerFaceId)
                continue;

            var raw = MoustacheGeometry.FromFace(face.Face, w, h);
            if (!_entries.TryGetValue(face.Id, out var entry))
            {
                entry = new Entry
                {
                    FaceId = face.Id,
                    Style = _style == MoustacheStyle.Random ? ConcreteStyles[Math.Clamp(_random(ConcreteStyles.Length), 0, ConcreteStyles.Length - 1)] : _style,
                    FirstSeen = now,
                    LastSeen = now,
                    Pose = raw,
                };
                _entries[face.Id] = entry;
            }
            else
            {
                entry.Pose = Smooth(entry.Pose, raw);
                if (refreshLastSeen)
                    entry.LastSeen = now;
            }
            entry.FaceBox = face.Face.Box;
            if (refreshLastSeen)
                entry.LastSeen = now;
        }

        // Sehr alte Spuren (längst abgelaufen) aufräumen, damit die Tabelle nicht wächst.
        foreach (var id in _entries.Where(kv => now - kv.Value.LastSeen > TimeSpan.FromSeconds(3)).Select(kv => kv.Key).ToList())
            _entries.Remove(id);
    }

    private static MoustachePose Smooth(MoustachePose old, MoustachePose observed)
    {
        static double Mix(double o, double n) => SmoothingAlpha * n + (1 - SmoothingAlpha) * o;
        var delta = observed.AngleRad - old.AngleRad;
        while (delta > Math.PI) delta -= 2 * Math.PI;
        while (delta <= -Math.PI) delta += 2 * Math.PI;
        return new MoustachePose(
            new NormPoint(Mix(old.Center.X, observed.Center.X), Mix(old.Center.Y, observed.Center.Y)),
            Mix(old.WidthPx, observed.WidthPx),
            old.AngleRad + SmoothingAlpha * delta);
    }
}
