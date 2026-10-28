namespace Fotobox.Core.Peace;

/// <summary>
/// „Plopp“: Beim ersten Erscheinen eines Bartes wächst er in <see cref="DurationSeconds"/> federnd von 0 auf 1 (gedämpfte Feder mit
/// leichtem Überschwinger). Rein und UI-frei: Die Ansicht übergibt die seit dem Erscheinen vergangene Zeit.
/// </summary>
public static class MoustachePop
{
    /// <summary>Dauer des Wachsens in Sekunden; danach gilt exakt 1.</summary>
    public const double DurationSeconds = 0.35;
    /// <summary>Dämpfungsgrad der Feder (0,55 ergibt rund 12 % Überschwinger).</summary>
    public const double DampingRatio = 0.55;
    /// <summary>Restausschlag, ab dem die Feder nach <see cref="DurationSeconds"/> als ausgeschwungen gilt.</summary>
    private const double Residual = 0.015;

    private static readonly double Omega = Math.Log(1 / Residual) / (DampingRatio * DurationSeconds);

    /// <summary>Größter Faktor der Kurve (Überschwinger), unabhängig von der Zeit.</summary>
    public static double Peak { get; } = ComputePeak();

    /// <summary>Skalierung 0 … ~1,12 zur Zeit <paramref name="elapsedSeconds"/> seit dem Erscheinen (≤ 0 → 0, ab der Dauer → 1).</summary>
    public static double Scale(double elapsedSeconds)
    {
        if (!(elapsedSeconds > 0))
            return 0;
        if (elapsedSeconds >= DurationSeconds)
            return 1;
        return Curve(elapsedSeconds);
    }

    /// <summary>Läuft die Animation nach <paramref name="elapsedSeconds"/> noch?</summary>
    public static bool IsRunning(double elapsedSeconds) => elapsedSeconds < DurationSeconds;

    private static double Curve(double t)
    {
        var zeta = DampingRatio;
        var wd = Omega * Math.Sqrt(1 - zeta * zeta);
        var decay = Math.Exp(-zeta * Omega * t);
        return 1 - decay * (Math.Cos(wd * t) + zeta * Omega / wd * Math.Sin(wd * t));
    }

    private static double ComputePeak()
    {
        var peak = 0.0;
        for (var i = 0; i <= 350; i++)
            peak = Math.Max(peak, Curve(i * DurationSeconds / 350));
        return peak;
    }
}

/// <summary>
/// Zustand der Wippfeder eines Bartes: senkrechter Versatz in Bartbreiten (nach unten positiv) und Winkel in Bogenmaß, jeweils mit
/// Geschwindigkeit. <see cref="Advance"/> entwickelt ihn exakt (gedämpfte Feder, geschlossene Form) und begrenzt das Ergebnis auf
/// <see cref="MoustacheWobble.MaxOffset"/> bzw. <see cref="MoustacheWobble.MaxAngle"/>; nahe der Ruhe rastet er auf exakt 0 ein.
/// </summary>
public readonly record struct WobbleState(double Offset, double OffsetVelocity, double Angle, double AngleVelocity)
{
    /// <summary>Ruhelage.</summary>
    public static WobbleState Rest => default;

    /// <summary>Liegt der Zustand exakt in Ruhe?</summary>
    public bool IsRest => this == default;

    /// <summary>Der Zustand nach <paramref name="seconds"/> Sekunden ohne neuen Anstoß.</summary>
    public WobbleState Advance(double seconds)
    {
        if (!(seconds > 0) || IsRest)
            return this;

        var (offset, offsetVelocity) = Step(Offset, OffsetVelocity, seconds);
        var (angle, angleVelocity) = Step(Angle, AngleVelocity, seconds);

        // Ruhe: kaum noch Ausschlag und Geschwindigkeit → exakt 0 (damit gleiche Zustände wirklich gleich sind).
        if (Math.Abs(offset) < MoustacheWobble.RestOffset && Math.Abs(offsetVelocity) < MoustacheWobble.RestOffsetVelocity)
            (offset, offsetVelocity) = (0, 0);
        if (Math.Abs(angle) < MoustacheWobble.RestAngle && Math.Abs(angleVelocity) < MoustacheWobble.RestAngleVelocity)
            (angle, angleVelocity) = (0, 0);

        return new WobbleState(
            Math.Clamp(offset, -MoustacheWobble.MaxOffset, MoustacheWobble.MaxOffset), offsetVelocity,
            Math.Clamp(angle, -MoustacheWobble.MaxAngle, MoustacheWobble.MaxAngle), angleVelocity);
    }

    // x'' = −ω0² x − 2 ζ ω0 x' (unterdämpft), geschlossene Lösung.
    private static (double X, double V) Step(double x0, double v0, double t)
    {
        var w0 = MoustacheWobble.Omega0;
        var zeta = MoustacheWobble.DampingRatio;
        var wd = w0 * Math.Sqrt(1 - zeta * zeta);
        var decay = Math.Exp(-zeta * w0 * t);
        var cos = Math.Cos(wd * t);
        var sin = Math.Sin(wd * t);
        var b = (v0 + zeta * w0 * x0) / wd;
        var x = decay * (x0 * cos + b * sin);
        var v = decay * (v0 * cos - (x0 * wd + zeta * w0 * b) * sin);
        return (x, v);
    }
}

/// <summary>
/// Wippen des Bartes: Eine kleine gedämpfte Feder auf senkrechten Versatz und Winkel, angestoßen durch schnelle Änderungen der
/// Mundgeometrie (Mundbreite und Abstand Nase–Mundmitte, jeweils auf den Augenabstand normiert; roh gegen geglättet). Im Stillstand
/// läuft sie aus. YuNet liefert nur Mundwinkel, keine Lippen: Ein sich öffnender Mund zeigt sich als größerer Abstand Nase–Mundmitte.
/// UI-frei; Zeit kommt als Parameter (die Ansicht entwickelt den Zustand pro Bild weiter, siehe <see cref="WobbleState.Advance"/>).
/// </summary>
public sealed class MoustacheWobble
{
    /// <summary>Größter senkrechter Versatz in Bartbreiten (3 %).</summary>
    public const double MaxOffset = 0.03;
    /// <summary>Größter Winkel in Bogenmaß (2°).</summary>
    public const double MaxAngle = 2 * Math.PI / 180;
    /// <summary>Eigenfrequenz der Feder (6 Hz) in rad/s.</summary>
    public const double Omega0 = 2 * Math.PI * 6;
    /// <summary>Dämpfungsgrad: rund zwei sichtbare Schwingungen, nach etwa einer halben Sekunde ruhig.</summary>
    public const double DampingRatio = 0.22;
    /// <summary>Zeitkonstante der Glättung der Mundmaße in Sekunden.</summary>
    public const double SmoothingSeconds = 0.35;
    /// <summary>Abweichungen (roh − geglättet, in Augenabständen) darunter lösen nichts aus (Rauschen der Erkennung).</summary>
    public const double Deadband = 0.012;
    /// <summary>Anstoß: Versatzgeschwindigkeit (Bartbreiten/s) je Augenabstand Abstandsänderung Nase–Mund.</summary>
    public const double OffsetGain = 14;
    /// <summary>Anstoß: Winkelgeschwindigkeit (rad/s) je Augenabstand Änderung der Mundbreite.</summary>
    public const double AngleGain = 16;

    internal const double RestOffset = 0.0004, RestOffsetVelocity = 0.01;
    internal const double RestAngle = 0.0003, RestAngleVelocity = 0.01;

    private WobbleState _state;
    private DateTimeOffset _stateAt;
    private double _mouthWidth, _noseMouth;
    private bool _seeded;

    /// <summary>Setzt Ruhe und vergisst die geglätteten Mundmaße (neue Spur, lange Lücke).</summary>
    public void Reset()
    {
        _state = WobbleState.Rest;
        _seeded = false;
    }

    /// <summary>
    /// Neue Beobachtung. <paramref name="mouthWidthRatio"/> = Mundbreite ÷ Augenabstand, <paramref name="noseMouthRatio"/> = Abstand
    /// Nase–Mundmitte ÷ Augenabstand. Die erste Beobachtung stößt nichts an.
    /// </summary>
    public void OnSample(DateTimeOffset now, double mouthWidthRatio, double noseMouthRatio)
    {
        if (!double.IsFinite(mouthWidthRatio) || !double.IsFinite(noseMouthRatio))
            return;

        if (!_seeded)
        {
            _seeded = true;
            _mouthWidth = mouthWidthRatio;
            _noseMouth = noseMouthRatio;
            _state = WobbleState.Rest;
            _stateAt = now;
            return;
        }

        var dt = Math.Max(0, (now - _stateAt).TotalSeconds);
        var state = _state.Advance(dt);
        _stateAt = now;

        // Roh gegen geglättet (vor dem Nachziehen der Glättung), Totband gegen Rauschen.
        var dNoseMouth = Dead(noseMouthRatio - _noseMouth);
        var dMouthWidth = Dead(mouthWidthRatio - _mouthWidth);
        var alpha = dt > 0 ? 1 - Math.Exp(-dt / SmoothingSeconds) : 0;
        _noseMouth += alpha * (noseMouthRatio - _noseMouth);
        _mouthWidth += alpha * (mouthWidthRatio - _mouthWidth);

        var maxOffsetVelocity = MaxOffset * Omega0 * 1.4;
        var maxAngleVelocity = MaxAngle * Omega0 * 1.4;
        _state = state with
        {
            OffsetVelocity = Math.Clamp(state.OffsetVelocity + OffsetGain * dNoseMouth, -maxOffsetVelocity, maxOffsetVelocity),
            AngleVelocity = Math.Clamp(state.AngleVelocity + AngleGain * dMouthWidth, -maxAngleVelocity, maxAngleVelocity),
        };
    }

    /// <summary>Zustand zum Zeitpunkt <paramref name="now"/> (entwickelt die Feder seit dem letzten Anstoß).</summary>
    public WobbleState At(DateTimeOffset now)
    {
        var dt = (now - _stateAt).TotalSeconds;
        if (!(dt > 0))
            return _state;
        _state = _state.Advance(dt);
        _stateAt = now;
        return _state;
    }

    private static double Dead(double value) =>
        Math.Abs(value) <= Deadband ? 0 : value - Math.Sign(value) * Deadband;
}
