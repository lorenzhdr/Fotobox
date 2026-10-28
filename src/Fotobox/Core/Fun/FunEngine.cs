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

    private FunSnapshot _snapshot = FunSnapshot.Empty;
    private readonly List<FunSoundCue> _pendingCues = [];

    public FunEngine(TimeProvider time)
    {
        _time = time;
        _tracker = new TargetTracker(time);
        _tracker.Configure(TimeSpan.FromMilliseconds(_settings.DwellMilliseconds));
    }

    public FunSnapshot Snapshot => _snapshot;

    /// <summary>Nur bei geänderter Darstellung (synchron).</summary>
    public event EventHandler? Changed;

    /// <summary>Signalton fällig; wird nur ausgelöst, wenn <see cref="FunSettings.SoundEnabled"/> gesetzt ist.</summary>
    public event EventHandler<FunSoundCue>? SoundCue;

    /// <summary>Einstellungen übernehmen (Kopie); wirkt sofort, auch auf den angezeigten Text.</summary>
    public void Configure(FunSettings settings, bool mirror)
    {
        _settings = settings.Clone();
        _mirror = mirror;
        _tracker.Configure(TimeSpan.FromMilliseconds(Math.Max(0, _settings.DwellMilliseconds)));
        Advance();
    }

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

        var alive = tracked.Select(t => t.Id).ToHashSet();
        _known.RemoveWhere(id => !alive.Contains(id));
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

        // Episode: Sie endet, wenn eine Weile niemand mehr da war.
        if (count == 0)
        {
            _emptySince ??= now;
            if (now - _emptySince.Value >= EpisodeEndAfterEmpty)
            {
                _episodeMax = 0;
                _episodeTriggered = false;
                _known.Clear();
            }
        }
        else
        {
            _emptySince = null;
        }

        // Niemand mehr da: Einrasten wird abgebrochen, ein laufender Text sofort ausgeblendet.
        if (count == 0)
        {
            if (_phase == FunPhase.LockingOn)
                _phase = FunPhase.Watching;
            else if (_phase == FunPhase.ShowingText)
                EnterCooldown(now);
        }

        StepTime(now);

        Trigger(now, _confirmed.Count);

        Publish(now);
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
                Cue(FunSoundCue.Text);
            }
            else if (_phase == FunPhase.ShowingText && _phaseEnd <= now)
            {
                _phase = FunPhase.Cooldown;
                _phaseEnd += TimeSpan.FromSeconds(Math.Max(0, _settings.CooldownSeconds));
                if (_simActive)
                {
                    // Simulation zu Ende: das Ziel verschwindet mit dem Text.
                    _simActive = false;
                    _confirmed = [];
                    _emptySince ??= now;
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
            else if (count > _episodeMax)
            {
                // B: Zuwachs – nur die neuen Ziele rasten ein, in jeder Phase.
                LockOn(_confirmed.Where(t => !_known.Contains(t.Id)), now);
                _episodeMax = count;
                _episodeTriggered = true;
                switch (_phase)
                {
                    case FunPhase.ShowingText:
                        var end = now + TimeSpan.FromSeconds(Math.Max(0, _settings.TextSeconds));
                        if (end > _phaseEnd)
                            _phaseEnd = end;
                        break;
                    case FunPhase.LockingOn:
                        var lockEnd = now + LockOnDuration;
                        if (lockEnd > _phaseEnd)
                            _phaseEnd = lockEnd;
                        break;
                    default:
                        StartLockingOn(now);
                        break;
                }
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
        _phaseEnd = now + LockOnDuration;
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
            return FunSnapshot.Empty with { Phase = _phase };
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
            var locking = hasLock && now < started + LockOnDuration;
            targets.Add(new FunTarget(t.Id, t.Box, $"TARGET {i + 1}", locking, hasLock ? started : default));
        }

        var showText = _phase == FunPhase.ShowingText;
        return new FunSnapshot(targets, _phase, showText, showText ? FunTextFormatter.Resolve(_settings, count) : "", count);
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
