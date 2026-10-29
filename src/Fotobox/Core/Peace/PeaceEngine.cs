namespace Fotobox.Core.Peace;

/// <summary>
/// UI-freie Zustandsmaschine der Peaceerkennung: ordnet Hände zu, prüft das Peace-Zeichen, misst die Haltedauer und löst genau
/// eine Aktion aus. Danach ist sie erst wieder scharf, wenn <see cref="RearmQuietTime"/> lang (in Hand-Updates beobachtet) keine
/// Hand Peace zeigt und die Sperrzeit vorbei ist. Alle Zeit läuft über <see cref="TimeProvider"/>; nicht threadsicher (UI-Thread).
/// </summary>
public sealed class PeaceEngine
{
    /// <summary>Untergrenze für den längsten Aussetzer des Zeichens, den das Halten übersteht.</summary>
    public static readonly TimeSpan MinGapTolerance = TimeSpan.FromMilliseconds(250);
    /// <summary>Untergrenze dafür, wie lange keine Hand Peace zeigen darf, bevor die Engine wieder scharf wird.</summary>
    public static readonly TimeSpan MinRearmQuietTime = TimeSpan.FromMilliseconds(400);
    /// <summary>Beide Fristen umfassen mindestens so viele Analyseabstände, damit eine verspätete oder verpasste Analyse nicht stört.</summary>
    public const double AnalysisIntervalFactor = 2.5;

    private sealed class HandState
    {
        public required int Id { get; init; }
        public required HandLandmarks Hand { get; set; }
        public bool IsPeace { get; set; }
        public int? FaceId { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset LastPeace { get; set; }
        public DateTimeOffset? HoldStart { get; set; }
        public NormPoint Center { get; set; }
    }

    private readonly TimeProvider _time;
    private readonly Func<int, int> _random;
    private readonly HandTracker _handTracker;
    private readonly Dictionary<int, HandState> _states = [];

    private PeaceSettings _settings = new();
    private bool _moustacheAvailable;
    private bool _noActionLogged;
    private bool _armed = true;
    private DateTimeOffset _lastTriggerAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAnyPeaceAt = DateTimeOffset.MinValue;
    // Wiederscharf nur nach tatsächlich beobachteter Ruhe: Ohne Hand-Updates (z. B. während Foto/Vorschau, dort nur Gesichter)
    // kann die Hand unbemerkt oben geblieben sein, die reine Uhrzeit genügt dann nicht.
    private DateTimeOffset _lastHandUpdateAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _observedQuietSince;
    private double _aspect = 16.0 / 9;
    private IReadOnlyList<TrackedFace> _faces = [];
    private PeaceSnapshot _snapshot = PeaceSnapshot.Empty;
    private bool _gesturesEnabled;
    private TimeSpan _gapTolerance = MinGapTolerance;
    private TimeSpan _rearmQuietTime = MinRearmQuietTime;

    public PeaceEngine(TimeProvider time, Func<int, int>? random = null)
    {
        _time = time;
        _random = random ?? (n => Random.Shared.Next(n));
        _handTracker = new HandTracker(time);
    }

    /// <summary>Eine Peace-Aktion wurde ausgelöst (die Engine ist danach bis zum Wiederscharfwerden gesperrt).</summary>
    public event EventHandler<PeaceTrigger>? Triggered;

    /// <summary>Der <see cref="Snapshot"/> hat sich geändert.</summary>
    public event EventHandler? Changed;

    public PeaceSnapshot Snapshot => _snapshot;

    /// <summary>Ist die Engine scharf (kann nach Haltedauer auslösen, sofern <see cref="GesturesEnabled"/>)?</summary>
    public bool IsArmed => _armed;

    /// <summary>Längster Aussetzer, den das Halten übersteht (abhängig von der Analyserate).</summary>
    public TimeSpan GapTolerance => _gapTolerance;

    /// <summary>So lange darf keine Hand Peace zeigen, bevor die Engine wieder scharf wird (abhängig von der Analyserate).</summary>
    public TimeSpan RearmQuietTime => _rearmQuietTime;

    /// <summary>
    /// Auslösen erlaubt (nur im Leerlauf). Bei false wird nie ausgelöst und kein Halten gezählt; die Prüfung „Hand wieder unten“
    /// läuft aber weiter, damit ein Zeichen aus einem früheren Durchgang nicht sofort erneut auslöst.
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
                ResetHolds();
        }
    }

    /// <param name="moustacheAvailable">Steht die Gesichtserkennung bereit? Sonst fällt die Aktion Schnurrbart aus der Auswahl.</param>
    public void Configure(PeaceSettings settings, bool moustacheAvailable)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
        _noActionLogged = false;
        _moustacheAvailable = moustacheAvailable;
        var interval = TimeSpan.FromSeconds(AnalysisIntervalFactor / Math.Max(1, settings.AnalysesPerSecond));
        _gapTolerance = interval > MinGapTolerance ? interval : MinGapTolerance;
        _rearmQuietTime = interval > MinRearmQuietTime ? interval : MinRearmQuietTime;
        _handTracker.LostAfter = interval;
    }

    /// <summary>
    /// Verarbeitet eine Analyse. <paramref name="faces"/> sind bereits verfolgte Gesichter (Ids aus einem <see cref="FaceTracker"/>);
    /// <paramref name="aspect"/> ist Breite/Höhe des Kamerabilds.
    /// </summary>
    public void OnUpdate(IReadOnlyList<HandLandmarks> hands, IReadOnlyList<TrackedFace> faces, double aspect = 16.0 / 9)
    {
        _aspect = aspect > 0 ? aspect : 1;
        _faces = faces;
        var now = _time.GetUtcNow();
        var anyPeace = false;

        foreach (var tracked in _handTracker.Update(hands, _aspect))
        {
            if (!_states.TryGetValue(tracked.Id, out var state))
            {
                state = new HandState { Id = tracked.Id, Hand = tracked.Hand, LastSeen = now, LastPeace = DateTimeOffset.MinValue };
                _states[tracked.Id] = state;
            }

            state.Hand = tracked.Hand;
            state.LastSeen = now;
            state.Center = PalmCenter(tracked.Hand);
            state.FaceId = HandFaceMatcher.Match(tracked.Hand, faces, _aspect)?.Id;

            var verdict = PeaceGestureClassifier.Classify(tracked.Hand, _settings.SensitivityPercent, _aspect);
            state.IsPeace = verdict.IsPeace;
            anyPeace |= verdict.IsPeace;
            if (state.HoldStart is not null && now - state.LastPeace > _gapTolerance)
                state.HoldStart = null; // Aussetzer zu lang: Halten beginnt von vorn
            if (verdict.IsPeace)
            {
                state.LastPeace = now;
                _lastAnyPeaceAt = now;
                if (state.HoldStart is null && _armed && _gesturesEnabled)
                    state.HoldStart = now;
            }
        }

        TrackObservedQuiet(now, anyPeace);
        Evaluate(now, force: true);
    }

    /// <summary>Zeitgesteuerte Übergänge (Haltedauer erreicht, Aussetzer zu lang, wieder scharf).</summary>
    public void Tick() => Evaluate(_time.GetUtcNow(), force: false);

    /// <summary>
    /// Vergisst alle Hände und Haltezeiten (Betriebsart aus). Sperrzeit und „Hand muss erst runter“ bleiben erhalten:
    /// Wer ausgelöst hat, muss auch danach erst die Hand senken.
    /// </summary>
    public void Reset()
    {
        _states.Clear();
        _handTracker.Reset();
        _faces = [];
        if (!_armed)
        {
            _lastAnyPeaceAt = _time.GetUtcNow();
            _observedQuietSince = null;
            _lastHandUpdateAt = DateTimeOffset.MinValue; // die Ruhe muss nach dem Reset neu beobachtet werden
        }
        if (_snapshot.Hands.Count > 0 || _snapshot.Faces.Count > 0)
        {
            _snapshot = PeaceSnapshot.Empty;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- intern

    // Führt mit, seit wann (lückenlos beobachtet) keine Hand Peace zeigt. Eine Lücke zwischen zwei Hand-Updates, länger als die
    // Ruhefrist, war unbeobachtet und startet neu. Gebraucht wird das nur, solange die Engine entwaffnet ist.
    private void TrackObservedQuiet(DateTimeOffset now, bool anyPeace)
    {
        if (!_armed)
        {
            var gap = _lastHandUpdateAt == DateTimeOffset.MinValue || now - _lastHandUpdateAt > _rearmQuietTime;
            if (anyPeace)
                _observedQuietSince = null;
            else if (gap)
                _observedQuietSince = now;
            else
                _observedQuietSince ??= _lastAnyPeaceAt;
        }
        _lastHandUpdateAt = now;
    }

    private void Evaluate(DateTimeOffset now, bool force)
    {
        var changed = force;

        foreach (var id in _states.Where(kv => now - kv.Value.LastSeen >= _handTracker.LostAfter).Select(kv => kv.Key).ToList())
        {
            _states.Remove(id);
            changed = true;
        }

        foreach (var state in _states.Values)
        {
            if (state.HoldStart is not null && (now - state.LastPeace > _gapTolerance || !_armed || !_gesturesEnabled))
            {
                state.HoldStart = null;
                changed = true;
            }
        }

        if (!_armed && now - _lastTriggerAt >= Cooldown && now - _lastAnyPeaceAt >= _rearmQuietTime
            && _observedQuietSince is { } quietSince && _lastHandUpdateAt - quietSince >= _rearmQuietTime)
            _armed = true;

        PeaceTrigger? trigger = null;
        var hold = TimeSpan.FromMilliseconds(Math.Max(1, _settings.HoldMilliseconds));
        if (_armed && _gesturesEnabled && now - _lastTriggerAt >= Cooldown)
        {
            var winner = _states.Values
                .Where(s => s.HoldStart is { } start && now - start >= hold)
                .OrderBy(s => s.HoldStart)
                .ThenBy(s => s.Id)
                .FirstOrDefault();
            if (winner is not null)
            {
                trigger = Fire(winner, now);
                changed = true;
            }
        }

        if (!changed && _states.Values.Any(s => s.HoldStart is not null))
            changed = true; // Fortschritt läuft

        if (!changed)
            return;

        Rebuild(now);
        if (trigger is not null)
            Triggered?.Invoke(this, trigger);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private TimeSpan Cooldown => TimeSpan.FromSeconds(Math.Max(0, _settings.CooldownSeconds));

    private PeaceTrigger? Fire(HandState winner, DateTimeOffset now)
    {
        // Verbraucht die Auslösung in jedem Fall, damit dieselbe Haltezeit nicht noch einmal zählt.
        _armed = false;
        _lastTriggerAt = now;
        _lastAnyPeaceAt = now;
        _observedQuietSince = null;
        ResetHolds();

        var actions = new List<PeaceAction>(3);
        if (_settings.ActionSelfDestruct)
            actions.Add(PeaceAction.SelfDestruct);
        if (_settings.ActionPhoto)
            actions.Add(PeaceAction.Photo);
        if (_settings.ActionMoustache && _moustacheAvailable)
            actions.Add(PeaceAction.Moustache);

        if (actions.Count == 0)
        {
            if (!_noActionLogged)
            {
                _noActionLogged = true;
                AppLog.Info("Peaceerkennung: Zeichen erkannt, aber keine Aktion verfügbar (nichts gewählt bzw. Gesichtsmodell fehlt).");
            }
            return null;
        }

        var index = actions.Count == 1 ? 0 : Math.Clamp(_random(actions.Count), 0, actions.Count - 1);
        var action = actions[index];
        AppLog.Info($"Peaceerkennung: Zeichen erkannt, Aktion {action}.");
        return new PeaceTrigger(action, winner.Id, winner.FaceId, winner.Center);
    }

    private void ResetHolds()
    {
        foreach (var state in _states.Values)
            state.HoldStart = null;
    }

    private void Rebuild(DateTimeOffset now)
    {
        var hold = Math.Max(1, _settings.HoldMilliseconds);
        var views = new List<HandView>(_states.Count);
        foreach (var state in _states.Values.OrderBy(s => s.Id))
        {
            var progress = state.HoldStart is { } start ? Math.Clamp((now - start).TotalMilliseconds / hold, 0, 1) : 0;
            views.Add(new HandView(state.Id, state.Hand.Points, state.Hand.Box, state.IsPeace, progress, state.FaceId));
        }
        _snapshot = new PeaceSnapshot(views, _faces.Select(f => f.Face).ToList());
    }

    private static NormPoint PalmCenter(HandLandmarks hand)
    {
        var idx = new[] { 0, 5, 9, 13, 17 };
        double x = 0, y = 0;
        foreach (var i in idx)
        {
            x += hand.Points[i].X;
            y += hand.Points[i].Y;
        }
        return new NormPoint(x / idx.Length, y / idx.Length);
    }
}
