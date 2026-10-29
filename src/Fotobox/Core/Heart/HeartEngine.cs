using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Core.Heart;

/// <summary>
/// UI-freie Zustandsmaschine der Herzerkennung: Zeigt irgendeine Hand-Kombination ein gültiges Herz
/// (<see cref="HeartGestureClassifier.FindHearts"/>) und hält es <see cref="HeartSettings.HoldMilliseconds"/> lang, löst sie genau einmal
/// <see cref="Triggered"/> aus. Es gibt keine Paar-Identität über Bilder hinweg: Das Halten gilt global („irgendein Herz“), ein Wechsel
/// der beteiligten Hände ist unerheblich. Danach ist die Engine erst wieder scharf, wenn <see cref="RearmQuietTime"/> lang (in
/// Hand-Updates beobachtet) kein Herz zu sehen war. Alle Zeit läuft über <see cref="TimeProvider"/>; Ereignisse kommen synchron;
/// nicht threadsicher (UI-Thread).
/// </summary>
public sealed class HeartEngine
{
    private readonly TimeProvider _time;

    private HeartSettings _settings = new();
    private bool _armed = true;
    private bool _gesturesEnabled;
    private bool _detectedNow;
    private HeartMatch? _best;
    private IReadOnlyList<NormRect> _bestFaces = [];
    private DateTimeOffset _lastHeartAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAnyHeartAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _holdStart;
    // Wiederscharf nur nach tatsächlich beobachteter Ruhe: Ohne Hand-Updates (z. B. während Foto/Kisscam, dort nur Gesichter)
    // kann das Herz unbemerkt stehen geblieben sein, die reine Uhrzeit genügt dann nicht (wie bei der Peaceerkennung).
    private DateTimeOffset _lastHandUpdateAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _observedQuietSince;
    private TimeSpan _gapTolerance = PeaceEngine.MinGapTolerance;
    private TimeSpan _rearmQuietTime = PeaceEngine.MinRearmQuietTime;
    private HeartSnapshot _snapshot = new([], true);

    public HeartEngine(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Ein Herz wurde lange genug gehalten (die Engine ist danach bis zum Wiederscharfwerden gesperrt).</summary>
    public event EventHandler<HeartTrigger>? Triggered;

    /// <summary>Der <see cref="Snapshot"/> oder <see cref="DetectedNow"/> hat sich geändert.</summary>
    public event EventHandler? Changed;

    public HeartSnapshot Snapshot => _snapshot;

    /// <summary>Zeigt gerade (innerhalb der Aussetzer-Toleranz) irgendeine Hand-Kombination ein Herz? Für die Admin-Anzeige.</summary>
    public bool DetectedNow => _detectedNow;

    /// <summary>Form des besten Herzens, solange <see cref="DetectedNow"/> gilt, sonst null.</summary>
    public HeartShape? DetectedShape => _detectedNow ? _best?.Shape : null;

    /// <summary>Ist die Engine scharf (kann nach der Haltedauer auslösen, sofern <see cref="GesturesEnabled"/>)?</summary>
    public bool IsArmed => _armed;

    /// <summary>Längster Aussetzer, den das Halten übersteht (abhängig von der Analyserate).</summary>
    public TimeSpan GapTolerance => _gapTolerance;

    /// <summary>So lange darf kein Herz zu sehen sein, bevor die Engine wieder scharf wird (abhängig von der Analyserate).</summary>
    public TimeSpan RearmQuietTime => _rearmQuietTime;

    /// <summary>
    /// Auslösen erlaubt (nur im Leerlauf). Bei false wird nie ausgelöst und kein Halten gezählt; die Prüfung „Herz wieder weg“
    /// läuft aber weiter, damit ein Herz aus einem früheren Durchgang nicht sofort erneut auslöst.
    /// </summary>
    public bool GesturesEnabled
    {
        get => _gesturesEnabled;
        set
        {
            if (_gesturesEnabled == value)
                return;
            _gesturesEnabled = value;
            if (!value)
                _holdStart = null;
        }
    }

    /// <param name="analysesPerSecond">Analyserate der Handstufe; bestimmt Aussetzer-Toleranz und Ruhefrist (mindestens 2,5 Analyseabstände).</param>
    public void Configure(HeartSettings settings, int analysesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
        var interval = TimeSpan.FromSeconds(PeaceEngine.AnalysisIntervalFactor / Math.Max(1, analysesPerSecond));
        _gapTolerance = interval > PeaceEngine.MinGapTolerance ? interval : PeaceEngine.MinGapTolerance;
        _rearmQuietTime = interval > PeaceEngine.MinRearmQuietTime ? interval : PeaceEngine.MinRearmQuietTime;
        if (!CanDetect)
        {
            // Ausgeschaltet bzw. ohne Form: Halten und Anzeige verwerfen; die Entwaffnung bleibt erhalten.
            _holdStart = null;
            _best = null;
            _bestFaces = [];
            _lastHeartAt = DateTimeOffset.MinValue;
        }
        Evaluate(_time.GetUtcNow(), force: true);
    }

    /// <summary>
    /// Verarbeitet eine Analyse. <paramref name="faces"/> sind bereits verfolgte Gesichter (Ids aus einem <see cref="FaceTracker"/>);
    /// <paramref name="aspect"/> ist Breite/Höhe des Kamerabilds.
    /// </summary>
    public void OnUpdate(IReadOnlyList<HandLandmarks> hands, IReadOnlyList<TrackedFace> faces, double aspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(hands);
        ArgumentNullException.ThrowIfNull(faces);
        if (!(aspect > 0))
            aspect = 1;
        var now = _time.GetUtcNow();

        var matches = CanDetect ? HeartGestureClassifier.FindHearts(hands, _settings, aspect) : [];
        if (_holdStart is not null && now - _lastHeartAt > _gapTolerance)
            _holdStart = null; // Aussetzer zu lang: Halten beginnt von vorn

        var anyHeart = matches.Count > 0;
        if (anyHeart)
        {
            _best = matches[0];
            _bestFaces = FaceBoxesFor(_best, hands, faces, aspect);
            _lastHeartAt = now;
            _lastAnyHeartAt = now;
            if (_holdStart is null && _armed && _gesturesEnabled)
                _holdStart = now;
        }

        // Ausgeschaltet bzw. ohne Form wird nichts beobachtet; die Ruhe zählt erst wieder ab dem ersten Update nach dem Einschalten.
        if (CanDetect)
            TrackObservedQuiet(now, anyHeart);
        Evaluate(now, force: true);
    }

    /// <summary>Zeitgesteuerte Übergänge (Haltedauer erreicht, Aussetzer zu lang, wieder scharf).</summary>
    public void Tick() => Evaluate(_time.GetUtcNow(), force: false);

    /// <summary>
    /// Vergisst Herz und Haltezeit (Betriebsart aus). Die Entwaffnung bleibt erhalten: Wer ausgelöst hat, muss das Herz auch danach
    /// erst lösen; die Ruhe wird nach dem Reset neu beobachtet.
    /// </summary>
    public void Reset()
    {
        _holdStart = null;
        _best = null;
        _bestFaces = [];
        _lastHeartAt = DateTimeOffset.MinValue;
        if (!_armed)
        {
            _lastAnyHeartAt = _time.GetUtcNow();
            _observedQuietSince = null;
            _lastHandUpdateAt = DateTimeOffset.MinValue;
        }
        Evaluate(_time.GetUtcNow(), force: false);
    }

    // ---- intern

    private bool CanDetect => _settings.Enabled && _settings.HasAnyShape;

    // Führt mit, seit wann (lückenlos beobachtet) kein Herz zu sehen ist. Eine Lücke zwischen zwei Hand-Updates, länger als die
    // Ruhefrist, war unbeobachtet und startet neu. Gebraucht wird das nur, solange die Engine entwaffnet ist.
    private void TrackObservedQuiet(DateTimeOffset now, bool anyHeart)
    {
        if (!_armed)
        {
            var gap = _lastHandUpdateAt == DateTimeOffset.MinValue || now - _lastHandUpdateAt > _rearmQuietTime;
            if (anyHeart)
                _observedQuietSince = null;
            else if (gap)
                _observedQuietSince = now;
            else
                _observedQuietSince ??= _lastAnyHeartAt;
        }
        _lastHandUpdateAt = now;
    }

    private void Evaluate(DateTimeOffset now, bool force)
    {
        var changed = force;

        if (_best is not null && now - _lastHeartAt > _gapTolerance)
        {
            _best = null;
            _bestFaces = [];
        }
        var detected = _best is not null;
        if (detected != _detectedNow)
        {
            _detectedNow = detected;
            changed = true;
        }

        if (_holdStart is not null && (now - _lastHeartAt > _gapTolerance || !_armed || !_gesturesEnabled || !CanDetect))
        {
            _holdStart = null;
            changed = true;
        }

        if (!_armed && now - _lastAnyHeartAt >= _rearmQuietTime
            && _observedQuietSince is { } quietSince && _lastHandUpdateAt - quietSince >= _rearmQuietTime)
        {
            _armed = true;
            changed = true;
        }

        HeartTrigger? trigger = null;
        var hold = TimeSpan.FromMilliseconds(Math.Max(1, _settings.HoldMilliseconds));
        if (_armed && _gesturesEnabled && CanDetect && _best is not null && _holdStart is { } start && now - start >= hold)
        {
            trigger = Fire(now);
            changed = true;
        }

        if (!changed && _holdStart is not null)
            changed = true; // Fortschritt läuft

        var old = _snapshot;
        Rebuild(now);
        if (!changed && old.Armed == _snapshot.Armed && old.Hearts.Count == _snapshot.Hearts.Count)
            return;

        if (trigger is not null)
            Triggered?.Invoke(this, trigger);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private HeartTrigger Fire(DateTimeOffset now)
    {
        // Verbraucht die Auslösung in jedem Fall, damit dieselbe Haltezeit nicht noch einmal zählt.
        var best = _best!;
        _armed = false;
        _lastAnyHeartAt = now;
        _observedQuietSince = null;
        _holdStart = null;

        var hands = best.Shape == HeartShape.FingerHeart ? "1 Hand" : "2 Hände";
        AppLog.Info($"Herzerkennung: Herz erkannt (Form {best.Shape}, {hands}), Aktion {_settings.Action}.");
        return new HeartTrigger(_settings.Action, best.Shape, best.Center, best.Box, _bestFaces);
    }

    private void Rebuild(DateTimeOffset now)
    {
        var views = new List<HeartView>(1);
        if (_best is { } best)
        {
            var hold = Math.Max(1, _settings.HoldMilliseconds);
            var progress = _holdStart is { } start ? Math.Clamp((now - start).TotalMilliseconds / hold, 0, 1) : 0;
            views.Add(new HeartView(best.Center, best.SizeNorm, progress, best.Shape));
        }
        _snapshot = new HeartSnapshot(views, _armed);
    }

    // Gesichter der Personen, zu denen die Hände des Herzens gehören (je Gesicht höchstens einmal).
    private static IReadOnlyList<NormRect> FaceBoxesFor(HeartMatch match, IReadOnlyList<HandLandmarks> hands, IReadOnlyList<TrackedFace> faces, double aspect)
    {
        if (faces.Count == 0)
            return [];
        var boxes = new List<NormRect>(2);
        var ids = new HashSet<int>();
        foreach (var index in new[] { match.HandA, match.HandB })
        {
            if (index < 0 || index >= hands.Count)
                continue;
            if (HandFaceMatcher.Match(hands[index], faces, aspect) is { } face && ids.Add(face.Id))
                boxes.Add(face.Face.Box);
        }
        return boxes;
    }
}
