namespace Fotobox.Core.Peace;

/// <summary>
/// UI-freie Zustandsmaschine der Mittelfingererkennung: Zeigt irgendeine Hand den Mittelfinger (<see cref="MiddleFingerGestureClassifier"/>)
/// und hält ihn <see cref="MiddleFingerSettings.HoldMilliseconds"/> lang, löst sie genau einmal <see cref="Triggered"/> aus. Danach ist
/// sie erst wieder scharf, wenn <see cref="RearmQuietTime"/> lang kein Mittelfinger zu sehen war. Es gibt keine Handverfolgung: Das
/// Halten gilt global („irgendeine Hand“). Alle Zeit läuft über <see cref="TimeProvider"/>; Ereignisse kommen synchron; nicht
/// threadsicher (UI-Thread).
/// <para>
/// Gehalten wird nur, solange <see cref="Applicable"/> gesetzt ist (es gibt etwas aufzuheben). Beim Einschalten von
/// <see cref="Applicable"/> startet die Engine entwaffnet: Ein schon sichtbarer Mittelfinger (z. B. beim Senken eines
/// Peace-Zeichens, bei dem kurz nur der Mittelfinger oben bleibt) muss zuerst für <see cref="RearmQuietTime"/> verschwinden. Ein erst
/// danach neu erscheinender Mittelfinger zählt dagegen sofort, auch wenn davor Peace zu sehen war; die Haltedauer
/// (Standard 500 ms) fängt kurze Übergangshaltungen ab.
/// </para>
/// </summary>
public sealed class MiddleFingerEngine
{
    private readonly TimeProvider _time;

    private MiddleFingerSettings _settings = new();
    private bool _applicable;
    private bool _armed = true;
    private bool _detectedNow;
    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;
    private DateTimeOffset? _holdStart;
    private TimeSpan _gapTolerance = PeaceEngine.MinGapTolerance;
    private TimeSpan _rearmQuietTime = PeaceEngine.MinRearmQuietTime;

    public MiddleFingerEngine(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Der Mittelfinger wurde lange genug gehalten (die Engine ist danach bis zum Wiederscharfwerden gesperrt).</summary>
    public event EventHandler? Triggered;

    /// <summary><see cref="DetectedNow"/> hat sich geändert.</summary>
    public event EventHandler? Changed;

    /// <summary>Zeigt gerade (innerhalb der Aussetzer-Toleranz) irgendeine Hand den Mittelfinger? Für die Admin-Anzeige.</summary>
    public bool DetectedNow => _detectedNow;

    /// <summary>Ist die Engine scharf (kann nach der Haltedauer auslösen, sofern <see cref="Applicable"/>)?</summary>
    public bool IsArmed => _armed;

    /// <summary>Längster Aussetzer, den das Halten übersteht (abhängig von der Analyserate).</summary>
    public TimeSpan GapTolerance => _gapTolerance;

    /// <summary>So lange darf kein Mittelfinger zu sehen sein, bevor die Engine wieder scharf wird (abhängig von der Analyserate).</summary>
    public TimeSpan RearmQuietTime => _rearmQuietTime;

    /// <summary>
    /// Gibt es etwas, das der Mittelfinger aufheben würde? Nur dann wird gehalten und ausgelöst. Beim Wechsel false → true startet die
    /// Engine entwaffnet (letzter Mittelfinger = jetzt); false beendet ein laufendes Halten.
    /// </summary>
    public bool Applicable
    {
        get => _applicable;
        set
        {
            if (_applicable == value)
                return;
            _applicable = value;
            if (value)
            {
                _armed = false;
                _lastSeen = _time.GetUtcNow();
            }
            _holdStart = null;
        }
    }

    /// <summary>Übernimmt Einstellungen; <paramref name="analysesPerSecond"/> = Handrate der Peaceerkennung (bestimmt Aussetzer- und Wiederscharf-Frist).</summary>
    public void Configure(MiddleFingerSettings settings, int analysesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
        var interval = TimeSpan.FromSeconds(PeaceEngine.AnalysisIntervalFactor / Math.Max(1, analysesPerSecond));
        _gapTolerance = interval > PeaceEngine.MinGapTolerance ? interval : PeaceEngine.MinGapTolerance;
        _rearmQuietTime = interval > PeaceEngine.MinRearmQuietTime ? interval : PeaceEngine.MinRearmQuietTime;
        if (!Active)
            _holdStart = null;
    }

    /// <summary>Verarbeitet eine Analyse (alle Hände des Bildes); <paramref name="aspect"/> = Breite/Höhe des Kamerabilds.</summary>
    public void OnUpdate(IReadOnlyList<HandLandmarks> hands, double aspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(hands);
        if (!(aspect > 0))
            aspect = 1;
        var now = _time.GetUtcNow();

        var any = false;
        foreach (var hand in hands)
        {
            if (MiddleFingerGestureClassifier.Classify(hand, _settings.SensitivityPercent, aspect).IsMiddleFinger)
            {
                any = true;
                break;
            }
        }

        if (_holdStart is not null && now - _lastSeen > _gapTolerance)
            _holdStart = null; // Aussetzer zu lang: Halten beginnt von vorn
        if (any)
        {
            _lastSeen = now;
            if (_holdStart is null && _armed && _applicable && Active)
                _holdStart = now;
        }

        Evaluate(now, detected: any);
    }

    /// <summary>Zeitgesteuerte Übergänge (Haltedauer erreicht, Aussetzer zu lang, wieder scharf).</summary>
    public void Tick() => Evaluate(_time.GetUtcNow(), detected: null);

    /// <summary>
    /// Vergisst das laufende Halten und die Anzeige. „Muss erst verschwinden“ bleibt erhalten: Wer ausgelöst hat (oder bei dem
    /// der Mittelfinger zuletzt sichtbar war), muss die Hand auch danach erst senken.
    /// </summary>
    public void Reset()
    {
        _holdStart = null;
        if (!_armed)
            _lastSeen = _time.GetUtcNow();
        if (_detectedNow)
        {
            _detectedNow = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- intern

    /// <summary>Ist die Erkennung überhaupt eingeschaltet und hat sie eine Aktion?</summary>
    private bool Active => _settings.Enabled && _settings.HasAnyAction;

    private void Evaluate(DateTimeOffset now, bool? detected)
    {
        // Anzeige: in einer Analyse gilt deren Ergebnis, sonst läuft sie nach der Aussetzer-Toleranz aus.
        var detectedNow = detected ?? (_detectedNow && now - _lastSeen < _gapTolerance);
        var changed = detectedNow != _detectedNow;
        _detectedNow = detectedNow;

        if (_holdStart is not null && (now - _lastSeen > _gapTolerance || !_armed || !_applicable || !Active))
            _holdStart = null;

        if (!_armed && now - _lastSeen >= _rearmQuietTime)
            _armed = true;

        var fire = false;
        if (_armed && _applicable && Active && _holdStart is { } start
            && now - start >= TimeSpan.FromMilliseconds(Math.Max(1, _settings.HoldMilliseconds)))
        {
            // Verbraucht die Auslösung, damit dieselbe Haltezeit nicht noch einmal zählt.
            _armed = false;
            _holdStart = null;
            _lastSeen = now;
            fire = true;
            AppLog.Info("Mittelfingererkennung: Zeichen erkannt.");
        }

        if (fire)
            Triggered?.Invoke(this, EventArgs.Empty);
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
