using Fotobox.Core.Fun;

namespace Fotobox.Core.Peace;

/// <summary>
/// UI-freie Verfolgung der Bärte: Je Gesicht (Id aus dem <see cref="FaceTracker"/>) ein Bart mit geglätteter Lage und stabilem
/// Stil. Die Lage wird je Größe (Mitte x/y, Breite, Winkel) per One-Euro-Filter geglättet (ruhig im Stillstand, schnell bei
/// Bewegung); <see cref="Views"/> schreibt die Mitte um das Alter der Beobachtung (jetzt − Bildzeit, höchstens
/// <see cref="MaxPredictionAge"/> und <see cref="MaxPredictionFaceWidths"/> Gesichtsbreiten) per geschätzter Geschwindigkeit fort,
/// <see cref="ExportPlanInput"/> (Foto) liefert dagegen die gefilterte Lage ohne Vorhersage. Eine Sitzung (<see cref="StartSession"/>) legt fest, wer einen Bart bekommt und wann er endet. Zeit über
/// <see cref="TimeProvider"/>; nicht threadsicher (UI-Thread).
/// </summary>
public sealed class MoustacheTracker
{
    // One-Euro-Parameter. Mitte und Breite werden in Bildpixeln gefiltert, die Geschwindigkeit für die Grenzfrequenz in
    // Gesichtsbreiten pro Sekunde gemessen (unabhängig vom Abstand zur Kamera). Grenzfrequenz = MinCutoff + Beta · Geschwindigkeit:
    // bei 10 Beobachtungen/s ergibt MinCutoff 0,8 Hz im Stillstand rund 30 % Gewicht der neuen Messung (ruhig), bei einer
    // Bewegung von 0,5 Gesichtsbreiten/s steigt die Grenzfrequenz auf rund 6 Hz (Gewicht ~80 %, kaum Nachlauf).
    /// <summary>Grenzfrequenz (Hz) von Mitte, Breite und Winkel im Stillstand.</summary>
    public const double MinCutoff = 0.8;
    /// <summary>Zuwachs der Grenzfrequenz je Gesichtsbreite/s für die Mitte.</summary>
    public const double PositionBeta = 10;
    /// <summary>Zuwachs der Grenzfrequenz je (Breitenänderung/s ÷ Breite) für die Bartbreite.</summary>
    public const double WidthBeta = 6;
    /// <summary>Zuwachs der Grenzfrequenz je rad/s für den Winkel.</summary>
    public const double AngleBeta = 8;
    /// <summary>Grenzfrequenz (Hz) der Geschwindigkeitsschätzung.</summary>
    public const double VelocityCutoff = 2.0;
    /// <summary>Größtes Alter einer Beobachtung, um das die Mitte in <see cref="Views"/> fortgeschrieben wird.</summary>
    public static readonly TimeSpan MaxPredictionAge = TimeSpan.FromMilliseconds(150);
    /// <summary>
    /// Totband der Vorhersage in Gesichtsbreiten pro Sekunde: Nur der Anteil der geschätzten Geschwindigkeit darüber wird
    /// fortgeschrieben. Im Stillstand ist die Geschwindigkeit reines Rauschen (rund 0,1 Gesichtsbreiten/s); ohne Totband
    /// würde die Vorhersage den Bart unruhiger machen als die gefilterte Lage.
    /// </summary>
    public const double PredictionDeadband = 0.25;
    /// <summary>Größter Weg der Vorhersage in Gesichtsbreiten.</summary>
    public const double MaxPredictionFaceWidths = 0.3;
    /// <summary>Beobachtungen eines Gesichts, die näher beieinander liegen, werden nicht gefiltert (die Geschwindigkeitsschätzung würde springen).</summary>
    public static readonly TimeSpan MinSampleInterval = TimeSpan.FromMilliseconds(20);
    /// <summary>Liegen zwei Beobachtungen eines Gesichts weiter auseinander, beginnt die Glättung von vorn.</summary>
    public static readonly TimeSpan SmoothingGap = TimeSpan.FromMilliseconds(700);
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
        /// <summary>Gefilterte Lage zum Zeitpunkt <see cref="SampleTime"/> (ohne Vorhersage).</summary>
        public MoustachePose Pose { get; set; }
        public NormRect FaceBox { get; set; }
        public DateTimeOffset SampleTime { get; set; }
        public double ImageWidth { get; set; }
        public double ImageHeight { get; set; }
        // Mitte in Bildpixeln.
        public OneEuroFilter X { get; } = new(MinCutoff, PositionBeta, VelocityCutoff);
        public OneEuroFilter Y { get; } = new(MinCutoff, PositionBeta, VelocityCutoff);
        public OneEuroFilter Width { get; } = new(MinCutoff, WidthBeta, VelocityCutoff);
        public OneEuroFilter Angle { get; } = new(MinCutoff, AngleBeta, VelocityCutoff);
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
    private DateTimeOffset _lastFrameAt = DateTimeOffset.MinValue;

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
                .Select(e => new MoustacheView(e.FaceId, e.Style, Predict(e, now)))
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
            Apply(_lastFaces, _lastWidth, _lastHeight, now, _lastFrameAt, refreshLastSeen: false);
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

    /// <summary>
    /// Gesichter einer Analyse (mit Ids) samt Bildgröße in Pixeln (für die Geometrie). <paramref name="frameTime"/> = Zeitpunkt,
    /// zu dem das analysierte Bild eintraf (null, unbekannt oder in der Zukunft = jetzt); aus dem Alter berechnet <see cref="Views"/>
    /// die Vorhersage.
    /// </summary>
    public void OnFaces(IReadOnlyList<TrackedFace> faces, double imageWidth, double imageHeight, DateTimeOffset? frameTime = null)
    {
        var now = _time.GetUtcNow();
        var sample = frameTime is { } t && t > DateTimeOffset.MinValue && t <= now ? t : now;
        _lastFaces = faces;
        _lastWidth = imageWidth;
        _lastHeight = imageHeight;
        _lastFacesAt = now;
        _lastFrameAt = sample;
        if (_active)
            Apply(faces, imageWidth, imageHeight, now, sample, refreshLastSeen: true);
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

    private void Apply(IReadOnlyList<TrackedFace> faces, double w, double h, DateTimeOffset now, DateTimeOffset sample, bool refreshLastSeen)
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
                SeedFilters(entry, raw, w, h, sample);
                _entries[face.Id] = entry;
            }
            else
            {
                var dt = (sample - entry.SampleTime).TotalSeconds;
                if (dt > SmoothingGap.TotalSeconds || entry.ImageWidth != w || entry.ImageHeight != h)
                    SeedFilters(entry, raw, w, h, sample);
                else if (dt >= MinSampleInterval.TotalSeconds)
                    Smooth(entry, raw, face.Face.Box, w, h, dt, sample);
            }
            entry.FaceBox = face.Face.Box;
            if (refreshLastSeen)
                entry.LastSeen = now;
        }

        // Sehr alte Spuren (längst abgelaufen) aufräumen, damit die Tabelle nicht wächst.
        foreach (var id in _entries.Where(kv => now - kv.Value.LastSeen > TimeSpan.FromSeconds(3)).Select(kv => kv.Key).ToList())
            _entries.Remove(id);
    }

    // Neue Spur (oder lange Lücke): Lage sofort übernehmen, Filter und Geschwindigkeit zurücksetzen.
    private static void SeedFilters(Entry entry, MoustachePose raw, double w, double h, DateTimeOffset sample)
    {
        entry.X.Reset(raw.Center.X * w);
        entry.Y.Reset(raw.Center.Y * h);
        entry.Width.Reset(raw.WidthPx);
        entry.Angle.Reset(raw.AngleRad);
        entry.Pose = raw;
        entry.SampleTime = sample;
        entry.ImageWidth = w;
        entry.ImageHeight = h;
    }

    private static void Smooth(Entry entry, MoustachePose raw, NormRect box, double w, double h, double dt, DateTimeOffset sample)
    {
        var faceWidthPx = Math.Max(1, box.Width * w);
        var x = entry.X.Filter(raw.Center.X * w, dt, faceWidthPx);
        var y = entry.Y.Filter(raw.Center.Y * h, dt, faceWidthPx);
        var width = entry.Width.Filter(raw.WidthPx, dt, Math.Max(1, entry.Width.Value));

        // Winkel entrollen: Das Ziel liegt immer auf dem kürzesten Weg zum gefilterten Wert (kein Sprung bei ±π).
        var delta = WrapAngle(raw.AngleRad - entry.Angle.Value);
        var angle = entry.Angle.Filter(entry.Angle.Value + delta, dt);

        entry.Pose = new MoustachePose(new NormPoint(x / w, y / h), width, WrapAngle(angle));
        entry.SampleTime = sample;
    }

    private static double WrapAngle(double angle)
    {
        while (angle > Math.PI) angle -= 2 * Math.PI;
        while (angle <= -Math.PI) angle += 2 * Math.PI;
        return angle;
    }

    // Mitte um das Alter der Beobachtung fortschreiben (Breite und Winkel bleiben stehen).
    private static MoustachePose Predict(Entry entry, DateTimeOffset now)
    {
        var age = Math.Min((now - entry.SampleTime).TotalSeconds, MaxPredictionAge.TotalSeconds);
        if (!(age > 0) || entry.ImageWidth <= 0 || entry.ImageHeight <= 0)
            return entry.Pose;

        var faceWidthPx = entry.FaceBox.Width * entry.ImageWidth;
        var vx = entry.X.Velocity;
        var vy = entry.Y.Velocity;
        var speed = Math.Sqrt(vx * vx + vy * vy);
        var deadband = PredictionDeadband * faceWidthPx;
        if (speed <= deadband)
            return entry.Pose;
        var keep = (speed - deadband) / speed; // Betrag um das Totband verkleinern, Richtung beibehalten
        var dx = vx * keep * age;
        var dy = vy * keep * age;
        var limit = MaxPredictionFaceWidths * faceWidthPx;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length > limit && length > 0)
        {
            dx *= limit / length;
            dy *= limit / length;
        }
        var center = new NormPoint(entry.Pose.Center.X + dx / entry.ImageWidth, entry.Pose.Center.Y + dy / entry.ImageHeight);
        return entry.Pose with { Center = center };
    }
}
