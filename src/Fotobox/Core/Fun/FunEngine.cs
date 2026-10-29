namespace Fotobox.Core.Fun;

/// <summary>Ablaufphase der Zielerfassung.</summary>
public enum FunPhase { Watching, LockingOn, ShowingText, Cooldown }

/// <summary>Eine bestätigte Zielbox (ungespiegelte, normierte Koordinaten) mit Beschriftung „TARGET n“.</summary>
public sealed record FunTarget(int Id, NormRect Box, string Label, bool IsLockingOn, DateTimeOffset LockStartedAt);

/// <summary>Was die Gastansicht (oder die Admin-Vorschau) gerade zeigt.</summary>
public sealed record FunSnapshot(IReadOnlyList<FunTarget> Targets, FunPhase Phase, bool ShowText, string Text, int Count)
{
    public static FunSnapshot Empty { get; } = new([], FunPhase.Watching, false, "", 0);
}

/// <summary>
/// UI-freies Modell der Zielerfassung: macht aus Erkennungen Zielboxen und steuert Einrasten, Text und Pause.
/// Nicht threadsicher; der Aufrufer ruft alles auf dem UI-Thread auf. Zeit nur über den <see cref="TimeProvider"/>;
/// Übergänge passieren in <see cref="OnDetections"/> und <see cref="Tick"/>.
/// </summary>
public sealed class FunEngine
{
    /// <summary>Standarddauer des Einrastens (<see cref="FunSettings.LockOnMilliseconds"/>); die Engine nutzt die eingestellte Dauer.</summary>
    public static readonly TimeSpan LockOnDuration = TimeSpan.FromSeconds(1.2);
    public static readonly TimeSpan EpisodeEndAfterEmpty = TimeSpan.FromSeconds(2);

    /// <summary>Id des simulierten Ziels (<see cref="Simulate"/>); echte Ziele haben positive Ids.</summary>
    public const int SimulatedTargetId = 0;

    // Simuliertes Ziel: Ganzkörperbox in der Bildmitte, die Geometrie folgt dem Bereich.
    private static readonly NormRect SimulatedBodyBox = new(0.38, 0.2, 0.24, 0.7);

    private readonly TimeProvider _time;
    private readonly TargetTracker _tracker;

    private FunSettings _settings = new();
    private bool _mirror;
    private NormRect _region = NormRect.Full;

    private List<TrackedTarget> _confirmed = [];
    private bool _simActive;

    private FunPhase _phase = FunPhase.Watching;
    private DateTimeOffset _phaseEnd;
    private int _episodeMax;
    // Hat die laufende Episode schon eingerastet? Beginnt sie im Cooldown, holt sie das nach dessen Ende nach.
    private bool _episodeTriggered;
    private DateTimeOffset? _emptySince;
    private readonly HashSet<int> _known = [];
    private readonly Dictionary<int, DateTimeOffset> _lockStart = [];
    // Neue Personen, die während des Texts erschienen: Sie bekommen nur einen Zielrahmen und rasten nach dem Text ein.
    private readonly HashSet<int> _pending = [];
    // Neustarts des gemeinsamen Einrastens in der laufenden Einrastrunde (Begrenzung: FunSettings.MaxLockOnRestarts).
    private int _restartsThisRound;
    // Anzahl der Personen beim Beginn der Meldung: Sie bestimmt den Text (du/ihr, {Anzahl}) bis zum Textende.
    private int _textCount = 1;
    // Ende des letzten Texts (geplanter Zeitpunkt); Grundlage der Wiederholung. Null: kein Text seit der letzten Erfassung.
    private DateTimeOffset? _lastTextEnd;

    private FunSnapshot _snapshot = FunSnapshot.Empty;
    private readonly List<FunSoundCue> _pendingCues = [];

    public FunEngine(TimeProvider time)
    {
        _time = time;
        _tracker = new TargetTracker(time);
        _tracker.Configure(TimeSpan.FromMilliseconds(_settings.DwellMilliseconds), _settings.AnalysesPerSecond);
    }

    public FunSnapshot Snapshot => _snapshot;

    /// <summary>Nur bei geänderter Darstellung (synchron).</summary>
    public event EventHandler? Changed;

    /// <summary>Signalton fällig; wird nur ausgelöst, wenn <see cref="FunSettings.SoundEnabled"/> gesetzt ist.</summary>
    public event EventHandler<FunSoundCue>? SoundCue;

    /// <summary>
    /// Einstellungen übernehmen (Kopie); wirkt sofort, auch auf den angezeigten Text (die Personenzahl des Texts bleibt). Ändert sich
    /// die Einrast-Dauer mitten im Einrasten, rechnet die Engine das Ende neu (ab dem letzten Beginn) und hängt nie am alten Ende.
    /// </summary>
    public void Configure(FunSettings settings, bool mirror)
    {
        var previousDuration = LockDuration;
        _settings = settings.Clone();
        _mirror = mirror;
        _tracker.Configure(TimeSpan.FromMilliseconds(Math.Max(0, _settings.DwellMilliseconds)), _settings.AnalysesPerSecond);
        if (_phase == FunPhase.LockingOn && LockDuration != previousDuration)
        {
            var latestStart = _confirmed.Where(t => _lockStart.ContainsKey(t.Id)).Select(t => _lockStart[t.Id]).DefaultIfEmpty().Max();
            if (latestStart != default)
                _phaseEnd = latestStart + LockDuration;
        }
        Advance();
    }

    /// <summary>Eingestellte Dauer des Einrastens, auf <see cref="SettingsLimits"/> geklemmt.</summary>
    private TimeSpan LockDuration => TimeSpan.FromMilliseconds(LockOnTuning.ClampMilliseconds(_settings.LockOnMilliseconds));

    /// <summary>Sichtbarer Bildausschnitt (<see cref="OverlayMapper.VisibleRegion(double, double, double, double, OverlayFit)"/>); Standard das ganze Bild.</summary>
    public void SetVisibleRegion(NormRect region) => _region = region;

    /// <summary>
    /// Neue Erkennungen (ungespiegelt). Die Engine filtert selbst: Konfidenz, Zielbox je Bereich, Mindesthöhe,
    /// sichtbarer Bildausschnitt, die größten <see cref="FunSettings.MaxTargets"/>. Während einer Simulation ignoriert.
    /// </summary>
    public void OnDetections(IReadOnlyList<Detection> detections)
    {
        if (!_simActive)
        {
            var boxes = new List<NormRect>();
            var minHeight = _settings.MinSizePercent / 100.0;
            foreach (var d in detections)
            {
                if (d.Confidence < _settings.MinConfidence)
                    continue;
                // Die Mindestgröße gilt für die (geschätzte) ganze Person, nicht für die Zielbox: Sonst blieben beim Bereich
                // „Kopf“ fast alle Personen unter der Schwelle.
                if (TargetGeometry.ToTargetBox(d, TargetScope.FullBody).Height < minHeight)
                    continue;
                var box = TargetGeometry.ToTargetBox(d, _settings.Scope);
                if (box.IsEmpty)
                    continue;
                if (!_region.ContainsPoint(box.CenterX, box.CenterY))
                    continue;
                boxes.Add(box);
            }

            var limited = boxes.OrderByDescending(b => b.Area).Take(Math.Max(1, _settings.MaxTargets)).ToList();
            RefreshConfirmed(_tracker.Update(limited));
        }

        Advance();
    }

    /// <summary>Regelmäßig aufrufen (z. B. 10×/s): zeitgesteuerte Übergänge, Verweildauer, verlorene Ziele.</summary>
    public void Tick()
    {
        if (!_simActive)
            RefreshConfirmed(_tracker.Prune());
        Advance();
    }

    /// <summary>Alles leeren: keine Ziele, Phase Watching, Episode und Pause zurück.</summary>
    public void Reset()
    {
        ResetState();
        Advance();
    }

    /// <summary>Test-Knopf: ein simuliertes Ziel in der Bildmitte durchläuft den Ablauf wie ein Erstauftauchen.</summary>
    public void Simulate()
    {
        ResetState();
        _simActive = true;
        RefreshSimulated();
        Advance();
    }

    private void ResetState()
    {
        _tracker.Reset();
        _confirmed = [];
        _simActive = false;
        _phase = FunPhase.Watching;
        _phaseEnd = default;
        _episodeMax = 0;
        _episodeTriggered = false;
        _emptySince = null;
        _known.Clear();
        _lockStart.Clear();
        _pending.Clear();
        _restartsThisRound = 0;
        _textCount = 1;
        _lastTextEnd = null;
        _pendingCues.Clear();
    }

    // ---- Kern

    private void RefreshConfirmed(IReadOnlyList<TrackedTarget> tracked)
    {
        _confirmed = tracked
            .Where(t => t.IsConfirmed)
            .OrderByDescending(t => t.Box.Area)
            .Take(Math.Max(1, _settings.MaxTargets))
            .ToList();

        // Auch vorgemerkte (kurz verlorene) Spuren bleiben bekannt: Findet der Tracker sie wieder, ist das keine neue Person.
        var alive = tracked.Select(t => t.Id).Concat(_tracker.RememberedIds).ToHashSet();
        _known.RemoveWhere(id => !alive.Contains(id));
        _pending.RemoveWhere(id => !alive.Contains(id));
        foreach (var id in _lockStart.Keys.Where(id => !alive.Contains(id)).ToList())
            _lockStart.Remove(id);
    }

    private void RefreshSimulated()
    {
        var box = TargetGeometry.ToTargetBox(new Detection(SimulatedBodyBox, 1, DetectionKind.Body), _settings.Scope);
        _confirmed = [new TrackedTarget(SimulatedTargetId, box, default, default, true)];
    }

    private void Advance()
    {
        var now = _time.GetUtcNow();
        if (_simActive)
            RefreshSimulated(); // Geometrie folgt dem aktuellen Bereich

        var count = _confirmed.Count;

        // Episode: Sie endet, wenn eine Weile niemand mehr da war und der Tracker keine Spur mehr vormerkt
        // (eine wiedergefundene Spur gehört noch zur selben Episode).
        if (count == 0)
        {
            _emptySince ??= now;
            if (now - _emptySince.Value >= EpisodeEndAfterEmpty && !_tracker.HasRemembered)
                EndEpisode(now);
        }
        else
        {
            _emptySince = null;
        }

        // Niemand mehr da: Einrasten wird abgebrochen. Ein laufender Text bleibt bis zu seinem Ende oder dem Ende der
        // Episode stehen (kurze Aussetzer der Erkennung blenden ihn nicht aus).
        if (count == 0 && _phase == FunPhase.LockingOn)
            _phase = FunPhase.Watching;

        StepTime(now);

        Trigger(now, _confirmed.Count);

        Publish(now);
    }

    private void EndEpisode(DateTimeOffset now)
    {
        _episodeMax = 0;
        _episodeTriggered = false;
        _known.Clear();
        _pending.Clear();
        _lastTextEnd = null;
        // Ein noch laufender Text endet mit der Episode; die Pause beginnt jetzt.
        if (_phase == FunPhase.ShowingText)
            EnterCooldown(now);
    }

    private void StepTime(DateTimeOffset now)
    {
        // Mehrere Übergänge hintereinander möglich, wenn der Tick spät kommt; Enden zählen ab dem geplanten Zeitpunkt.
        for (var guard = 0; guard < 8; guard++)
        {
            if (_phase == FunPhase.LockingOn && _phaseEnd <= now)
            {
                Cue(FunSoundCue.Lock);
                _phase = FunPhase.ShowingText;
                _phaseEnd += TimeSpan.FromSeconds(Math.Max(0, _settings.TextSeconds));
                // Der Text bleibt bei der Personenzahl vom Beginn der Meldung (kein Umschalten du/ihr während der Anzeige).
                // Gezählt werden die tatsächlich eingerasteten Personen (wer während des Einrastens ging, zählt nicht mehr);
                // ohne eingerastete Person (SwapTriggers aus, Tausch ohne Auslösung) gilt die Zahl der Anwesenden.
                var locked = _confirmed.Count(t => _lockStart.ContainsKey(t.Id));
                _textCount = locked > 0 ? locked : Math.Max(1, _confirmed.Count);
                Cue(FunSoundCue.Text);
            }
            else if (_phase == FunPhase.ShowingText && _phaseEnd <= now)
            {
                var textEnd = _phaseEnd;
                if (_simActive)
                {
                    _phase = FunPhase.Cooldown;
                    _phaseEnd += TimeSpan.FromSeconds(Math.Max(0, _settings.CooldownSeconds));
                    // Simulation zu Ende: das Ziel verschwindet mit dem Text.
                    _simActive = false;
                    _confirmed = [];
                    _emptySince ??= now;
                }
                else
                {
                    var waiting = _confirmed.Where(t => _pending.Contains(t.Id)).ToList();
                    _pending.Clear();
                    _lastTextEnd = textEnd;
                    if (waiting.Count > 0)
                    {
                        // Personen, die während des Texts dazukamen und noch da sind: Alle Anwesenden rasten gemeinsam neu ein,
                        // danach folgt sofort eine neue Meldung mit der Gesamtzahl.
                        LockOn(_confirmed, textEnd);
                        _phase = FunPhase.LockingOn;
                        _phaseEnd = textEnd + LockDuration;
                        _restartsThisRound = 0;
                        Cue(FunSoundCue.Acquire);
                    }
                    else
                    {
                        _phase = FunPhase.Cooldown;
                        _phaseEnd += TimeSpan.FromSeconds(Math.Max(0, _settings.CooldownSeconds));
                    }
                }
            }
            else if (_phase == FunPhase.Cooldown && _phaseEnd <= now)
            {
                _phase = FunPhase.Watching;
            }
            else
            {
                break;
            }
        }
    }

    private void EnterCooldown(DateTimeOffset now)
    {
        _phase = FunPhase.Cooldown;
        _phaseEnd = now + TimeSpan.FromSeconds(Math.Max(0, _settings.CooldownSeconds));
    }

    private void Trigger(DateTimeOffset now, int count)
    {
        if (count > 0)
        {
            // Personen, die in dieser Episode noch nicht bekannt waren (neue Spur).
            var fresh = _confirmed.Where(t => !_known.Contains(t.Id)).ToList();

            if (_episodeMax == 0)
            {
                if (_phase == FunPhase.Watching)
                {
                    // A: Erstes Auftauchen – alle bestätigten Ziele rasten ein.
                    LockOn(_confirmed, now);
                    StartLockingOn(now);
                    Cue(FunSoundCue.Acquire);
                    _episodeTriggered = true;
                }
                // Im Cooldown beginnt die Episode still; sie rastet nach dem Cooldown ein (unten), ein Zuwachs löst sofort aus.
                _episodeMax = count;
            }
            else if (!_episodeTriggered && _phase == FunPhase.Watching && count <= _episodeMax)
            {
                // A nachgeholt: Die Gruppe kam während des Cooldowns und ist danach noch da.
                LockOn(_confirmed, now);
                StartLockingOn(now);
                Cue(FunSoundCue.Acquire);
                _episodeTriggered = true;
            }
            else if (count > _episodeMax || (_settings.SwapTriggers && fresh.Count > 0))
            {
                // B: Zuwachs (oder bei SwapTriggers: Personentausch).
                _episodeMax = Math.Max(_episodeMax, count);
                _episodeTriggered = true;
                switch (_phase)
                {
                    case FunPhase.ShowingText:
                        // Der Text läuft unverändert weiter; die Neuen bekommen nur einen Zielrahmen. Nach dem Text rasten alle
                        // Anwesenden gemeinsam neu ein.
                        foreach (var t in fresh)
                            _pending.Add(t.Id);
                        break;
                    case FunPhase.LockingOn:
                        OnArrivalDuringLockOn(now, fresh);
                        break;
                    default:
                        // Watching/Cooldown: Alle Anwesenden rasten gemeinsam ein.
                        LockOn(_confirmed, now);
                        StartLockingOn(now);
                        Cue(FunSoundCue.Acquire);
                        break;
                }
            }
            else if (_phase == FunPhase.Watching && _episodeTriggered && _settings.RepeatEnabled && _lastTextEnd is { } textEnd
                && now >= textEnd + TimeSpan.FromSeconds(Math.Max(0, _settings.RepeatSeconds)))
            {
                // Wiederholung: Dieselbe Gruppe ist noch da (die Pause ist vorbei, sonst wäre die Phase nicht Watching).
                LockOn(_confirmed, now);
                StartLockingOn(now);
                Cue(FunSoundCue.Acquire);
            }
        }

        // Alles, was jetzt da ist, gilt als bekannt (spätere Zuwächse betreffen nur wirklich neue Ziele).
        foreach (var t in _confirmed)
            _known.Add(t.Id);
    }

    private void StartLockingOn(DateTimeOffset now)
    {
        _phase = FunPhase.LockingOn;
        _phaseEnd = now + LockDuration;
        _lastTextEnd = null;
        _restartsThisRound = 0;
    }

    /// <summary>
    /// Neue Person(en) während des Einrastens: Solange die Grenze (<see cref="FunSettings.MaxLockOnRestarts"/>) nicht erreicht ist, startet
    /// die Animation für alle bestätigten Personen gemeinsam neu (ein Neustart und ein Erfassungston, auch bei mehreren Neuen im selben
    /// Durchgang). Danach rasten Spätkommer einzeln ein und verlängern das Ende nur bei Bedarf.
    /// </summary>
    private void OnArrivalDuringLockOn(DateTimeOffset now, List<TrackedTarget> fresh)
    {
        if (fresh.Count == 0)
            return;

        var maxRestarts = Math.Clamp(_settings.MaxLockOnRestarts, SettingsLimits.FunLockOnRestartsMin, SettingsLimits.FunLockOnRestartsMax);
        if (_restartsThisRound < maxRestarts)
        {
            _restartsThisRound++;
            LockOn(_confirmed, now);
            _phaseEnd = now + LockDuration;
            AppLog.Info($"Zielerfassung: Einrasten startet für {_confirmed.Count} Personen neu (Neustart {_restartsThisRound} von {maxRestarts}).");
        }
        else
        {
            LockOn(fresh, now);
            var lockEnd = now + LockDuration;
            if (lockEnd > _phaseEnd)
                _phaseEnd = lockEnd;
        }
        Cue(FunSoundCue.Acquire);
    }

    private void LockOn(IEnumerable<TrackedTarget> targets, DateTimeOffset now)
    {
        foreach (var t in targets.ToList())
        {
            _known.Add(t.Id);
            _lockStart[t.Id] = now;
        }
    }

    // ---- Ausgabe

    private void Cue(FunSoundCue cue) => _pendingCues.Add(cue);

    private void Publish(DateTimeOffset now)
    {
        var snapshot = BuildSnapshot(now);
        var changed = !SameSnapshot(_snapshot, snapshot);
        _snapshot = snapshot;

        var cues = _pendingCues.ToArray();
        _pendingCues.Clear();

        if (changed)
            Raise(() => Changed?.Invoke(this, EventArgs.Empty), "Changed");
        if (_settings.SoundEnabled)
        {
            foreach (var cue in cues)
                Raise(() => SoundCue?.Invoke(this, cue), "SoundCue");
        }
    }

    private static void Raise(Action action, string name)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"FunEngine: Handler von {name} hat eine Ausnahme geworfen.", ex);
        }
    }

    private FunSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var count = _confirmed.Count;
        if (count == 0)
        {
            // Der Text bleibt auch ohne sichtbare Ziele stehen (kurzer Aussetzer der Erkennung), bis er endet.
            return _phase == FunPhase.ShowingText
                ? new FunSnapshot([], _phase, true, FunTextFormatter.Resolve(_settings, _textCount), 0)
                : FunSnapshot.Empty with { Phase = _phase };
        }

        var ordered = _confirmed
            .OrderBy(t => _mirror ? 1 - t.Box.CenterX : t.Box.CenterX)
            .ThenBy(t => t.Id)
            .ToList();

        var targets = new List<FunTarget>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            var hasLock = _lockStart.TryGetValue(t.Id, out var started);
            var locking = hasLock && now < started + LockDuration;
            targets.Add(new FunTarget(t.Id, t.Box, $"TARGET {i + 1}", locking, hasLock ? started : default));
        }

        var showText = _phase == FunPhase.ShowingText;
        return new FunSnapshot(targets, _phase, showText, showText ? FunTextFormatter.Resolve(_settings, _textCount) : "", count);
    }

    private static bool SameSnapshot(FunSnapshot a, FunSnapshot b)
    {
        if (a.Phase != b.Phase || a.ShowText != b.ShowText || a.Text != b.Text || a.Count != b.Count || a.Targets.Count != b.Targets.Count)
            return false;
        for (var i = 0; i < a.Targets.Count; i++)
        {
            if (a.Targets[i] != b.Targets[i])
                return false;
        }
        return true;
    }
}
