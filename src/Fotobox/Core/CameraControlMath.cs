namespace Fotobox.Core;

/// <summary>
/// Rechenhilfen für die Kamerasteuerung (UI-frei): Werte auf den vom Treiber gemeldeten Bereich klemmen und einrasten,
/// Werte zusammenführen und vergleichen. Die Wertebereiche kommen zur Laufzeit vom Treiber, nicht aus <see cref="SettingsLimits"/>.
/// </summary>
public static class CameraControlMath
{
    /// <summary>Toleranz beim Vergleich von Werten, wenn der Regler keine (gültige) Schrittweite kennt.</summary>
    private const double DefaultTolerance = 1e-6;

    /// <summary>
    /// Klemmt <paramref name="value"/> auf [<paramref name="min"/>, <paramref name="max"/>] (vertauschte Grenzen werden getauscht)
    /// und rastet relativ zu <paramref name="min"/> auf <paramref name="step"/> ein (Schritt ≤ 0 oder nicht endlich: nur klemmen).
    /// NaN ergibt <paramref name="min"/>.
    /// </summary>
    public static double ClampAndSnap(double value, double min, double max, double step)
    {
        if (min > max)
            (min, max) = (max, min);
        if (double.IsNaN(value))
            return min;

        var result = Math.Clamp(value, min, max);
        if (step > 0 && double.IsFinite(step) && double.IsFinite(min) && double.IsFinite(result))
        {
            var snapped = min + Math.Round((result - min) / step, MidpointRounding.AwayFromZero) * step;
            if (double.IsFinite(snapped))
                result = Math.Clamp(snapped, min, max);
        }
        return result;
    }

    /// <summary>
    /// Führt zwei Wertelisten zusammen: je <see cref="CameraControlKind"/> gewinnt der Eintrag aus <paramref name="overrides"/>,
    /// sonst der aus <paramref name="baseline"/>. Ergebnis sind neue Objekte, nach Art sortiert. Bei doppelten Arten in einer Liste gilt der erste.
    /// </summary>
    public static IReadOnlyList<CameraControlValue> Merge(IEnumerable<CameraControlValue> overrides, IEnumerable<CameraControlValue> baseline)
    {
        var map = new Dictionary<CameraControlKind, CameraControlValue>();
        foreach (var v in overrides)
        {
            if (v is not null)
                map.TryAdd(v.Kind, v);
        }
        foreach (var v in baseline)
        {
            if (v is not null)
                map.TryAdd(v.Kind, v);
        }
        return map.Values.OrderBy(v => v.Kind).Select(v => v.Clone()).ToList();
    }

    /// <summary>Aktueller Stand aller unterstützten Regler (Automatik nur, wenn der Regler eine hat).</summary>
    public static IReadOnlyList<CameraControlValue> CurrentValues(CameraControlsSnapshot s) =>
        s.Controls
            .Where(c => c.Supported)
            .OrderBy(c => c.Kind)
            .Select(c => new CameraControlValue { Kind = c.Kind, Value = c.Value, Auto = c.AutoSupported ? c.Auto : null })
            .ToList();

    /// <summary>Standardwerte aller unterstützten Regler; mit Automatik, sofern der Regler sie kennt.</summary>
    public static IReadOnlyList<CameraControlValue> Defaults(CameraControlsSnapshot s) =>
        s.Controls
            .Where(c => c.Supported)
            .OrderBy(c => c.Kind)
            .Select(c => new CameraControlValue { Kind = c.Kind, Value = c.Default, Auto = c.AutoSupported ? true : null })
            .ToList();

    /// <summary>
    /// Sind beide Listen gleich? Gleiche Arten, gleiche Automatik, Werte innerhalb einer Toleranz (halbe Schrittweite des Reglers
    /// aus <paramref name="ranges"/>, sonst 1e-6). Für die Anzeige „(geändert)“ bei Profilen.
    /// </summary>
    public static bool SameValues(IEnumerable<CameraControlValue> a, IEnumerable<CameraControlValue> b, CameraControlsSnapshot? ranges = null)
    {
        var left = ToMap(a);
        var right = ToMap(b);
        if (left.Count != right.Count)
            return false;

        foreach (var (kind, l) in left)
        {
            if (!right.TryGetValue(kind, out var r) || l.Auto != r.Auto)
                return false;

            if (l.Value is null || r.Value is null)
            {
                if (l.Value is not null || r.Value is not null)
                    return false;
                continue;
            }

            if (Math.Abs(l.Value.Value - r.Value.Value) > ToleranceFor(kind, ranges))
                return false;
        }
        return true;
    }

    private static Dictionary<CameraControlKind, CameraControlValue> ToMap(IEnumerable<CameraControlValue> values)
    {
        var map = new Dictionary<CameraControlKind, CameraControlValue>();
        foreach (var v in values)
        {
            if (v is not null)
                map.TryAdd(v.Kind, v);
        }
        return map;
    }

    private static double ToleranceFor(CameraControlKind kind, CameraControlsSnapshot? ranges)
    {
        var info = ranges?.Controls.FirstOrDefault(c => c.Kind == kind);
        return info is { Step: > 0 } && double.IsFinite(info.Step) ? info.Step / 2 : DefaultTolerance;
    }
}
