using System.Globalization;
using System.Text;
using Fotobox.Core.Peace;

namespace Fotobox.Core.Heart;

/// <summary>
/// Eine einzelne Prüfgröße der Herzerkennung für die Diagnose: <see cref="Value"/> gemessen, <see cref="Limit"/> die Schwelle,
/// <see cref="Op"/> die geforderte Beziehung (<c>Value Op Limit</c>), <see cref="Passed"/> ob die Prüfung bestanden wäre.
/// </summary>
public readonly record struct HeartCheck(string Name, double Value, string Op, double Limit, bool Passed)
{
    public override string ToString() =>
        $"{Name}={HeartDiagnosisFormat.Num(Value)}({Op}{HeartDiagnosisFormat.Num(Limit)} {(Passed ? "ok" : "FEHL")})";
}

/// <summary>
/// Ergebnis von <see cref="HeartGestureClassifier.Diagnose"/> bzw. <see cref="HeartGestureClassifier.DiagnoseFingerHeart"/>: alle
/// Prüfgrößen samt Schwelle (ohne bei der ersten Abweichung abzubrechen), das echte Urteil des Klassifikators und Zusatzangaben
/// (Handgrößen, Formkriterien). Nur für das Log; hat keinen Einfluss auf die Erkennung.
/// </summary>
public sealed class HeartDiagnosis(string kind, IReadOnlyList<HeartCheck> checks, HeartVerdict verdict, string info)
{
    /// <summary>„Paar“ oder „Fingerherz“.</summary>
    public string Kind { get; } = kind;

    /// <summary>Alle berechneten Prüfgrößen in der Reihenfolge der Klassifikator-Prüfung.</summary>
    public IReadOnlyList<HeartCheck> Checks { get; } = checks;

    /// <summary>Das Urteil von <c>ClassifyPair</c> bzw. <c>ClassifyFingerHeart</c> (erster Ablehnungsgrund, falls abgelehnt).</summary>
    public HeartVerdict Verdict { get; } = verdict;

    /// <summary>Zusatzangaben (Handgrößen, Neigungswinkel, Dach-Finger, hypothetische Form).</summary>
    public string Info { get; } = info;

    /// <summary>Hätten alle Prüfgrößen die Schwelle erreicht?</summary>
    public bool AllChecksPassed
    {
        get
        {
            foreach (var check in Checks)
            {
                if (!check.Passed)
                    return false;
            }
            return true;
        }
    }

    /// <summary>Namen der nicht bestandenen Prüfungen.</summary>
    public IEnumerable<string> FailedChecks => Checks.Where(c => !c.Passed).Select(c => c.Name);

    /// <summary>Kompakte Einzeiler-Darstellung für das Log.</summary>
    public string ToLogString()
    {
        var sb = new StringBuilder(512);
        sb.Append(Kind).Append(": Urteil=").Append(Verdict.IsHeart ? $"Herz ({Verdict.Shape})" : $"abgelehnt ({Verdict.RejectReason})");
        var failed = Checks.Count(c => !c.Passed);
        sb.Append(", nicht bestanden=").Append(failed.ToString(CultureInfo.InvariantCulture)).Append('/').Append(Checks.Count.ToString(CultureInfo.InvariantCulture));
        if (Info.Length > 0)
            sb.Append(", ").Append(Info);
        sb.Append(" | ");
        for (var i = 0; i < Checks.Count; i++)
        {
            if (i > 0)
                sb.Append("; ");
            sb.Append(Checks[i]);
        }
        return sb.ToString();
    }

    public override string ToString() => ToLogString();
}

/// <summary>Zahlenformat der Herz-Diagnosezeilen (kulturunabhängig, drei Nachkommastellen).</summary>
internal static class HeartDiagnosisFormat
{
    public static string Num(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    public static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Alle 21 Punkte einer Hand als normierte „x,y“-Paare, mit Semikolon getrennt.</summary>
    public static string Points(HandLandmarks hand)
    {
        var points = hand.Points;
        var count = Math.Min(points.Count, HeartGestureClassifier.PointCount);
        var sb = new StringBuilder(count * 12);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                sb.Append(';');
            sb.Append(Num(points[i].X)).Append(',').Append(Num(points[i].Y));
        }
        return sb.ToString();
    }
}
