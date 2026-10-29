using System.Globalization;
using System.Text;
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
    private TimeSpan _gapTolerance;
    private TimeSpan _rearmQuietTime;
    private HeartSnapshot _snapshot = new([], true);
    // Diagnose (nur Log, ohne Einfluss auf die Auslösung): Drosselung über die Engine-Zeit.
    private static readonly TimeSpan DiagnosisInterval = TimeSpan.FromSeconds(1);
    private DateTimeOffset _lastDiagnosisAt = DateTimeOffset.MinValue;
    // Diagnose: Sammelstatistik je Fenster (Engine-Zeit); nur Zähler, ohne Einfluss auf die Auslösung.
    private static readonly TimeSpan StatsWindow = TimeSpan.FromSeconds(30);
    private DateTimeOffset _statsWindowStart = DateTimeOffset.MinValue;
    private int _statAnalyses, _statWithHand, _statWithTwoHands, _statHeartHits, _statLongestStreak, _statStreak, _statHoldAborts, _statTriggers;

    /// <summary>
    /// Untergrenze der Aussetzer-Toleranz. Herzen werden mit zwei sich berührenden Händen geformt und flackern in der Erkennung
    /// (die Hand-Pipeline verliert eine der Hände zeitweise); die Peace-Frist (<see cref="PeaceEngine.MinGapTolerance"/>) war dafür zu knapp,
    /// die Haltezeit kam kaum zustande.
    /// </summary>
    public static readonly TimeSpan MinGapTolerance = TimeSpan.FromMilliseconds(700);

    /// <summary>Aussetzer-Toleranz in Analyseabständen (gilt, wenn größer als <see cref="MinGapTolerance"/>).</summary>
    public const double GapToleranceIntervals = 4;

    /// <summary>Analyserate, für die eine noch nicht konfigurierte Engine ihre Fristen vorbelegt (Empfehlung der Peaceerkennung).</summary>
    private const int DefaultAnalysesPerSecond = 5;

    public HeartEngine(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        (_gapTolerance, _rearmQuietTime) = Timings(DefaultAnalysesPerSecond);
    }

    /// <summary>
    /// Aussetzer-Toleranz = max(<see cref="MinGapTolerance"/>, <see cref="GapToleranceIntervals"/> Analyseabstände); Ruhefrist fürs
    /// Wiederscharfwerden = max(bisheriger Peace-Wert, Toleranz + 1 Analyseabstand). Die Ruhefrist muss länger als die Toleranz sein:
    /// Sonst gilt ein im Flackern weitergehaltenes Herz als „beobachtete Ruhe“, und die Engine löst (z. B. eine zweite Kisscam) erneut aus.
    /// </summary>
    internal static (TimeSpan GapTolerance, TimeSpan RearmQuietTime) Timings(int analysesPerSecond)
    {
        var intervalSeconds = 1.0 / Math.Max(1, analysesPerSecond);
        var gap = TimeSpan.FromSeconds(GapToleranceIntervals * intervalSeconds);
        if (gap < MinGapTolerance)
            gap = MinGapTolerance;
        var peaceRearm = TimeSpan.FromSeconds(PeaceEngine.AnalysisIntervalFactor * intervalSeconds);
        if (peaceRearm < PeaceEngine.MinRearmQuietTime)
            peaceRearm = PeaceEngine.MinRearmQuietTime;
        var rearm = gap + TimeSpan.FromSeconds(intervalSeconds);
        return (gap, rearm > peaceRearm ? rearm : peaceRearm);
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
                AbortHold(_time.GetUtcNow(), "Gesten ausgeschaltet");
        }
    }

    /// <summary>
    /// Übernimmt Einstellungen und Analyserate. Löst nie <see cref="Triggered"/> aus (nur Snapshot und <see cref="Changed"/> werden
    /// aktualisiert): Der Aufrufer konfiguriert mitten in <c>SetMode</c>, und ein Auslösen würde dort synchron einen Durchgang starten
    /// und die Betriebsart verschachtelt umschalten. Ausgelöst wird nur aus <see cref="OnUpdate"/> und <see cref="Tick"/>.
    /// </summary>
    /// <param name="analysesPerSecond">Analyserate der Handstufe; bestimmt Aussetzer-Toleranz und Ruhefrist (siehe <see cref="Timings"/>).</param>
    public void Configure(HeartSettings settings, int analysesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
        (_gapTolerance, _rearmQuietTime) = Timings(analysesPerSecond);
        if (!CanDetect)
        {
            // Ausgeschaltet bzw. ohne Form: Halten und Anzeige verwerfen; die Entwaffnung bleibt erhalten.
            AbortHold(_time.GetUtcNow(), "Erkennung ausgeschaltet");
            _best = null;
            _bestFaces = [];
            _lastHeartAt = DateTimeOffset.MinValue;
        }
        Evaluate(_time.GetUtcNow(), force: true, allowTrigger: false);
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
            AbortHold(now, "Aussetzer zu lang"); // Halten beginnt von vorn

        var anyHeart = matches.Count > 0;
        if (anyHeart)
        {
            var heartBegins = _lastHeartAt == DateTimeOffset.MinValue || now - _lastHeartAt > _gapTolerance;
            _best = matches[0];
            _bestFaces = FaceBoxesFor(_best, hands, faces, aspect);
            _lastHeartAt = now;
            _lastAnyHeartAt = now;
            if (_holdStart is null && _armed && _gesturesEnabled)
                _holdStart = now;
            if (heartBegins)
                LogHeartBegins(_best, hands);
        }
        else if (CanDetect && hands.Count > 0)
        {
            LogNoHeartDiagnosis(hands, aspect, now);
        }
        if (CanDetect)
            RecordStats(now, hands.Count, anyHeart);

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
        AbortHold(_time.GetUtcNow(), "Reset");
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

    private void Evaluate(DateTimeOffset now, bool force, bool allowTrigger = true)
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
            AbortHold(now, now - _lastHeartAt > _gapTolerance ? "Aussetzer zu lang"
                : !_armed ? "Engine nicht scharf" : !_gesturesEnabled ? "Gesten nicht erlaubt" : "Erkennung ausgeschaltet");
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
        if (allowTrigger && _armed && _gesturesEnabled && CanDetect && _best is not null && _holdStart is { } start && now - start >= hold)
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

    // ---- Diagnose (nur Log; verändert außer der Drosselzeit und dem Verwerfen des Haltens keinen Zustand)

    // Verwirft das laufende Halten und protokolliert den Abbruch samt Lücke und Fortschritt (nur wenn gerade gehalten wurde).
    private void AbortHold(DateTimeOffset now, string reason)
    {
        if (_holdStart is not { } start)
            return;
        _holdStart = null;
        _statHoldAborts++;
        try
        {
            var gap = now - _lastHeartAt;
            var gapExceeded = gap > _gapTolerance;
            var held = ((gapExceeded ? _lastHeartAt : now) - start).TotalMilliseconds;
            var hold = Math.Max(1, _settings.HoldMilliseconds);
            AppLog.Info(string.Create(CultureInfo.InvariantCulture,
                $"Herz-Diagnose: Halten abgebrochen ({reason}); Lücke seit letztem Herz {gap.TotalMilliseconds:0} ms, "
                + $"GapTolerance {_gapTolerance.TotalMilliseconds:0} ms; Fortschritt {Math.Max(0, held):0} von {hold} ms "
                + $"({Math.Clamp(held / hold * 100, 0, 100):0} %)."));
        }
        catch
        {
            // Diagnose darf die Erkennung nie beeinträchtigen.
        }
    }

    // Hängt die rohen Landmarks der beteiligten Hände an (Paar: beide, Fingerherz: eine), damit sich der Treffer offline nachrechnen lässt.
    private void LogHeartBegins(HeartMatch best, IReadOnlyList<HandLandmarks> hands)
    {
        try
        {
            var sb = new StringBuilder(1024);
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"Herz-Diagnose: Herz-Treffer beginnt (Form {best.Shape}, Güte {HeartDiagnosisFormat.Num(best.Score)}, "
                + $"Hände im Bild {hands.Count}, scharf {(_armed ? "ja" : "nein")}, Gesten erlaubt {(_gesturesEnabled ? "ja" : "nein")}, "
                + $"Halten {(_holdStart is not null ? "läuft" : "läuft nicht")}, GapTolerance {_gapTolerance.TotalMilliseconds:0} ms, "
                + $"Haltedauer {_settings.HoldMilliseconds} ms)"));
            var first = true;
            foreach (var index in new[] { best.HandA, best.HandB })
            {
                if (index < 0 || index >= hands.Count)
                    continue;
                sb.Append(first ? " | " : " ").Append("Hand").Append(HeartDiagnosisFormat.Int(index)).Append(" [")
                  .Append(HeartDiagnosisFormat.Points(hands[index])).Append(']');
                first = false;
            }
            sb.Append('.');
            AppLog.Info(sb.ToString());
        }
        catch
        {
            // Diagnose darf die Erkennung nie beeinträchtigen.
        }
    }

    // Zählt die Analysen des Fensters und schreibt nach StatsWindow (Engine-Zeit) eine Sammelzeile, falls Hände zu sehen waren.
    private void RecordStats(DateTimeOffset now, int handCount, bool anyHeart)
    {
        try
        {
            if (_statsWindowStart == DateTimeOffset.MinValue)
                _statsWindowStart = now;
            else if (now - _statsWindowStart >= StatsWindow)
            {
                if (_statWithHand > 0)
                {
                    AppLog.Info(string.Create(CultureInfo.InvariantCulture,
                        $"Herz-Diagnose: Statistik {StatsWindow.TotalSeconds:0} s: Analysen {_statAnalyses}, mit ≥1 Hand {_statWithHand}, "
                        + $"mit ≥2 Händen {_statWithTwoHands}, Herz-Treffer {_statHeartHits}, längste Trefferserie {_statLongestStreak} Analysen, "
                        + $"Halteabbrüche {_statHoldAborts}, Auslösungen {_statTriggers}."));
                }
                _statsWindowStart = now;
                _statAnalyses = _statWithHand = _statWithTwoHands = _statHeartHits = _statLongestStreak = _statStreak = _statHoldAborts = _statTriggers = 0;
            }

            _statAnalyses++;
            if (handCount >= 1)
                _statWithHand++;
            if (handCount >= 2)
                _statWithTwoHands++;
            if (anyHeart)
            {
                _statHeartHits++;
                _statStreak++;
                if (_statStreak > _statLongestStreak)
                    _statLongestStreak = _statStreak;
            }
            else
            {
                _statStreak = 0;
            }
        }
        catch
        {
            // Diagnose darf die Erkennung nie beeinträchtigen.
        }
    }

    // Hände sichtbar, aber kein Herz: höchstens einmal pro Sekunde die komplette Klassifikator-Diagnose des nächstliegenden Paars
    // samt rohen Landmarks (normiert), damit sich der Fall offline nachrechnen lässt.
    private void LogNoHeartDiagnosis(IReadOnlyList<HandLandmarks> hands, double aspect, DateTimeOffset now)
    {
        if (now - _lastDiagnosisAt < DiagnosisInterval)
            return;
        _lastDiagnosisAt = now;
        try
        {
            var sb = new StringBuilder(2048);
            sb.Append("Herz-Diagnose: Hände ohne Herz: Hände=").Append(HeartDiagnosisFormat.Int(hands.Count)).Append(" (Scores ");
            for (var i = 0; i < hands.Count; i++)
            {
                if (i > 0)
                    sb.Append('/');
                sb.Append(HeartDiagnosisFormat.Num(hands[i].Score));
            }
            sb.Append("), Seitenverh.=").Append(HeartDiagnosisFormat.Num(aspect))
              .Append(", Empfindlichkeit=").Append(HeartDiagnosisFormat.Int(_settings.SensitivityPercent))
              .Append(", MinHandScore=").Append(HeartDiagnosisFormat.Num(_settings.MinHandScore))
              .Append(", scharf=").Append(_armed ? "ja" : "nein")
              .Append(", Gesten erlaubt=").Append(_gesturesEnabled ? "ja" : "nein");
            if (_lastHeartAt != DateTimeOffset.MinValue)
                sb.Append(", letztes Herz vor ").Append((now - _lastHeartAt).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)).Append(" ms");
            sb.Append(" | ");

            var nearest = hands.Count >= 2 ? HeartGestureClassifier.FindNearestPair(hands, aspect) : null;
            if (nearest is { } pair)
            {
                sb.Append("nächstes Paar ").Append(HeartDiagnosisFormat.Int(pair.A)).Append('/').Append(HeartDiagnosisFormat.Int(pair.B))
                  .Append(" (Kontaktabstände/S").Append(HeartDiagnosisFormat.Num(pair.Cost)).Append("): ")
                  .Append(HeartGestureClassifier.Diagnose(hands[pair.A], hands[pair.B], _settings, aspect).ToLogString())
                  .Append(" | Hand").Append(HeartDiagnosisFormat.Int(pair.A)).Append(" [").Append(HeartDiagnosisFormat.Points(hands[pair.A])).Append(']')
                  .Append(" Hand").Append(HeartDiagnosisFormat.Int(pair.B)).Append(" [").Append(HeartDiagnosisFormat.Points(hands[pair.B])).Append(']');
            }
            else
            {
                sb.Append("einzelne Hand 0: ")
                  .Append(HeartGestureClassifier.DiagnoseFingerHeart(hands[0], _settings, aspect).ToLogString())
                  .Append(" | Hand0 [").Append(HeartDiagnosisFormat.Points(hands[0])).Append(']');
            }
            sb.Append('.');
            AppLog.Info(sb.ToString());
        }
        catch
        {
            // Diagnose darf die Erkennung nie beeinträchtigen.
        }
    }

    private HeartTrigger Fire(DateTimeOffset now)
    {
        // Verbraucht die Auslösung in jedem Fall, damit dieselbe Haltezeit nicht noch einmal zählt.
        var best = _best!;
        _armed = false;
        _lastAnyHeartAt = now;
        _observedQuietSince = null;
        _holdStart = null;
        _statTriggers++;

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
