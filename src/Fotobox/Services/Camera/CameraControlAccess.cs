using System.Globalization;
using Fotobox.Core;
using Windows.Media.Capture;
using Windows.Media.Devices;

namespace Fotobox.Services.Camera;

/// <summary>Fähigkeiten eines Reglers laut Treiber.</summary>
internal readonly record struct ControlCaps(bool Supported, double Min, double Max, double Step, double Default, bool AutoSupported);

/// <summary>
/// Dünne Schicht über dem <c>VideoDeviceController</c> einer Kamera (in Tests ersetzbar). Alle Aufrufe sind synchron und gehören
/// auf den Threadpool; der Aufrufer sichert sie mit einer Frist ab. Jede Methode darf werfen (der Aufrufer fängt je Regler).
/// </summary>
internal interface ICameraControlTarget
{
    ControlCaps GetCaps(CameraControlKind kind);
    bool TryGetValue(CameraControlKind kind, out double value);
    bool TryGetAuto(CameraControlKind kind, out bool value);
    bool TrySetValue(CameraControlKind kind, double value);
    bool TrySetAuto(CameraControlKind kind, bool value);
    /// <summary>false = der Treiber bietet keinen Flimmerschutz an (oder die Abfrage scheitert).</summary>
    bool TryGetPowerline(out CameraPowerline value);
    bool TrySetPowerline(CameraPowerline value);
}

/// <summary>Abbildung <see cref="CameraPowerline"/> ↔ WinRT <see cref="PowerlineFrequency"/>.</summary>
internal static class CameraPowerlineMap
{
    /// <summary>null bei <see cref="CameraPowerline.Unchanged"/> (nichts setzen) und unbekannten Werten.</summary>
    public static PowerlineFrequency? ToWinRt(CameraPowerline value) => value switch
    {
        CameraPowerline.Off => PowerlineFrequency.Disabled,
        CameraPowerline.Hz50 => PowerlineFrequency.FiftyHertz,
        CameraPowerline.Hz60 => PowerlineFrequency.SixtyHertz,
        CameraPowerline.Auto => PowerlineFrequency.Auto,
        _ => null,
    };

    /// <summary>null bei einem unbekannten WinRT-Wert.</summary>
    public static CameraPowerline? FromWinRt(PowerlineFrequency value) => value switch
    {
        PowerlineFrequency.Disabled => CameraPowerline.Off,
        PowerlineFrequency.FiftyHertz => CameraPowerline.Hz50,
        PowerlineFrequency.SixtyHertz => CameraPowerline.Hz60,
        PowerlineFrequency.Auto => CameraPowerline.Auto,
        _ => null,
    };
}

/// <summary>Echte Implementierung auf <see cref="VideoDeviceController"/>.</summary>
internal sealed class VideoDeviceControllerTarget(VideoDeviceController controller) : ICameraControlTarget
{
    // null = diese Art hat keinen MediaDeviceControl-Weg (Belichtungskorrektur: nur über den erweiterten Weg).
    private MediaDeviceControl? Get(CameraControlKind kind) => kind switch
    {
        CameraControlKind.Zoom => controller.Zoom,
        CameraControlKind.Pan => controller.Pan,
        CameraControlKind.Tilt => controller.Tilt,
        CameraControlKind.Brightness => controller.Brightness,
        CameraControlKind.Contrast => controller.Contrast,
        CameraControlKind.Hue => controller.Hue,
        CameraControlKind.Exposure => controller.Exposure,
        CameraControlKind.BacklightCompensation => controller.BacklightCompensation,
        CameraControlKind.Focus => controller.Focus,
        CameraControlKind.WhiteBalance => controller.WhiteBalance,
        _ => null,
    };

    public ControlCaps GetCaps(CameraControlKind kind)
    {
        if (Get(kind)?.Capabilities is not { } c)
            return default;
        return new ControlCaps(c.Supported, c.Min, c.Max, c.Step, c.Default, c.AutoModeSupported);
    }

    public bool TryGetValue(CameraControlKind kind, out double value)
    {
        value = 0;
        return Get(kind) is { } c && c.TryGetValue(out value);
    }

    public bool TryGetAuto(CameraControlKind kind, out bool value)
    {
        value = false;
        return Get(kind) is { } c && c.TryGetAuto(out value);
    }

    public bool TrySetValue(CameraControlKind kind, double value) => Get(kind) is { } c && c.TrySetValue(value);
    public bool TrySetAuto(CameraControlKind kind, bool value) => Get(kind) is { } c && c.TrySetAuto(value);

    public bool TryGetPowerline(out CameraPowerline value)
    {
        value = CameraPowerline.Unchanged;
        if (!controller.TryGetPowerlineFrequency(out var raw))
            return false;
        value = CameraPowerlineMap.FromWinRt(raw) ?? CameraPowerline.Unchanged;
        return true;
    }

    public bool TrySetPowerline(CameraPowerline value) =>
        CameraPowerlineMap.ToWinRt(value) is { } raw && controller.TrySetPowerlineFrequency(raw);
}

/// <summary>
/// Zuletzt erfolgreich angewendeter bzw. gelesener Stand der Regler EINER Session (je Art Wert und Automatik, dazu der
/// Flimmerschutz). <see cref="CameraControlAccess.Apply"/> schreibt nur Abweichendes, damit nicht bei jeder Reglerbewegung alle
/// Regler neu geschrieben werden (der Fokus fokussierte sonst bei jedem Tick neu, der Weißabgleich setzte sein Preset neu). Gilt
/// nur für die Session, zu der er gehört (neue Session = neuer Cache); ein Eintrag (null = unbekannt) wird nach einem
/// fehlgeschlagenen Schreiben verworfen. Threadsicher.
/// </summary>
internal sealed class ControlStateCache
{
    private readonly object _lock = new();
    private readonly Dictionary<CameraControlKind, (double? Value, bool? Auto)> _state = [];
    private CameraPowerline? _powerline;

    /// <summary>Ersetzt den Stand durch den gelesenen (der Treiber ist die Wahrheit).</summary>
    public void Seed(CameraControlsSnapshot snapshot)
    {
        lock (_lock)
        {
            _state.Clear();
            foreach (var c in snapshot.Controls)
            {
                if (c.Supported && c.Writable)
                    _state[c.Kind] = (c.Value, c.Auto);
            }
            _powerline = snapshot.PowerlineSupported ? snapshot.Powerline : null;
        }
    }

    public (double? Value, bool? Auto) Get(CameraControlKind kind)
    {
        lock (_lock)
            return _state.TryGetValue(kind, out var s) ? s : (null, null);
    }

    public void Set(CameraControlKind kind, double? value, bool? auto)
    {
        lock (_lock)
            _state[kind] = (value, auto);
    }

    public void Forget(CameraControlKind kind)
    {
        lock (_lock)
            _state.Remove(kind);
    }

    public CameraPowerline? Powerline
    {
        get { lock (_lock) return _powerline; }
        set { lock (_lock) _powerline = value; }
    }

    /// <summary>
    /// Obergrenze der Zahl der Regler, die <see cref="CameraControlAccess.Apply"/> schreiben muss (ohne den Treiber zu fragen):
    /// Einträge, die der Cache nicht schon exakt kennt, plus der Flimmerschutz, falls abweichend. Dient nur der Fristberechnung.
    /// </summary>
    public int CountPending(IReadOnlyList<CameraControlValue> values, CameraPowerline powerline)
    {
        lock (_lock)
        {
            var n = 0;
            foreach (var v in values)
            {
                if (v is null)
                    continue;
                if (!_state.TryGetValue(v.Kind, out var s))
                {
                    n++;
                    continue;
                }
                var same = v.Auto == true
                    ? s.Auto == true
                    : (v.Auto is null || s.Auto == v.Auto) && (v.Value is not { } d || (s.Value is { } c && Math.Abs(c - d) <= 1e-9));
                if (!same)
                    n++;
            }
            if (powerline != CameraPowerline.Unchanged && _powerline != powerline)
                n++;
            return n;
        }
    }
}

/// <summary>Auslesen und Anwenden der Kamerareglerwerte (UI- und WinRT-frei über <see cref="ICameraControlTarget"/>). Wirft nie.</summary>
internal static class CameraControlAccess
{
    /// <summary>
    /// Werte, die für die Kamera <paramref name="deviceId"/> gelten: die gespeicherten nur, wenn sie zu genau dieser Kamera gehören
    /// (Ordinal-Vergleich ohne Groß-/Kleinschreibung), sonst keine. Nie null-Einträge.
    /// </summary>
    public static IReadOnlyList<CameraControlValue> ValuesFor(string? savedDeviceId, IReadOnlyList<CameraControlValue>? savedValues, string? deviceId)
    {
        if (savedValues is null || string.IsNullOrEmpty(savedDeviceId) || string.IsNullOrEmpty(deviceId)
            || !string.Equals(savedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            return [];
        return savedValues.Where(v => v is not null).ToList();
    }

    /// <summary>Liest Fähigkeiten und Stand aller Regler; ein scheiternder Regler gilt als nicht unterstützt (Log).</summary>
    public static CameraControlsSnapshot Read(ICameraControlTarget target, string? deviceId, bool writable)
    {
        var list = new List<CameraControlInfo>();
        foreach (var kind in Enum.GetValues<CameraControlKind>())
        {
            try
            {
                var caps = target.GetCaps(kind);
                if (!caps.Supported)
                {
                    list.Add(Unsupported(kind));
                    continue;
                }

                double? value = target.TryGetValue(kind, out var v) ? v : null;
                bool? auto = caps.AutoSupported && target.TryGetAuto(kind, out var a) ? a : null;
                list.Add(new CameraControlInfo(kind, true, writable, caps.Min, caps.Max, caps.Step, caps.Default, caps.AutoSupported, value, auto));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Kameraregler {kind} konnte nicht gelesen werden.", ex);
                list.Add(Unsupported(kind));
            }
        }

        var powerlineSupported = false;
        CameraPowerline? powerline = null;
        try
        {
            if (target.TryGetPowerline(out var p))
            {
                powerlineSupported = true;
                powerline = p == CameraPowerline.Unchanged ? null : p;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Flimmerschutz der Kamera konnte nicht gelesen werden.", ex);
        }

        return new CameraControlsSnapshot(deviceId, writable, list, powerlineSupported, powerline);
    }

    /// <summary>
    /// Wendet Werte an: nur unterstützte Regler; Automatik an (wenn der Regler eine hat) ersetzt den Wert, sonst Automatik aus
    /// (bei <c>Auto == false</c>) und der Wert, auf den Bereich geklemmt und eingerastet. Danach der Flimmerschutz (außer
    /// <see cref="CameraPowerline.Unchanged"/>, nur wenn unterstützt). Lehnt der Treiber etwas ab oder wirft er, wird geloggt
    /// und mit dem nächsten Regler weitergemacht. <paramref name="live"/> nur für den Text im Log. Mit <paramref name="cache"/>
    /// (Stand der Session) werden nur Abweichungen geschrieben (ein Wert innerhalb eines halben Schritts gilt als gleich) und
    /// erfolgreiche Schreibvorgänge eingetragen; ohne Cache wird alles geschrieben.
    /// </summary>
    public static void Apply(ICameraControlTarget target, IReadOnlyList<CameraControlValue> values, CameraPowerline powerline, bool live,
        ControlStateCache? cache = null)
    {
        foreach (var v in values)
        {
            if (v is null)
                continue;
            try
            {
                var caps = target.GetCaps(v.Kind);
                if (!caps.Supported)
                    continue;
                ApplyOne(target, v, caps, cache);
            }
            catch (Exception ex)
            {
                cache?.Forget(v.Kind); // Stand unbekannt (ggf. halb geschrieben)
                AppLog.Warn($"Kameraregler {v.Kind} konnte nicht gesetzt werden.", ex);
            }
        }

        if (powerline == CameraPowerline.Unchanged || cache?.Powerline == powerline)
            return;
        try
        {
            if (!target.TryGetPowerline(out _))
            {
                AppLog.Info("Flimmerschutz: Die Kamera bietet ihn nicht an, er wird nicht gesetzt.");
            }
            else if (target.TrySetPowerline(powerline))
            {
                if (cache is not null)
                    cache.Powerline = powerline;
            }
            else
            {
                if (cache is not null)
                    cache.Powerline = null;
                AppLog.Warn(live
                    ? $"Flimmerschutz {powerline} wurde vom Treiber abgelehnt (wirkt ggf. erst nach einem Neustart der Kamera)."
                    : $"Flimmerschutz {powerline} wurde vom Treiber abgelehnt.");
            }
        }
        catch (Exception ex)
        {
            if (cache is not null)
                cache.Powerline = null;
            AppLog.Warn("Flimmerschutz der Kamera konnte nicht gesetzt werden.", ex);
        }
    }

    private static void ApplyOne(ICameraControlTarget target, CameraControlValue v, ControlCaps caps, ControlStateCache? cache)
    {
        if (v.Auto == true && caps.AutoSupported)
        {
            if (cache?.Get(v.Kind).Auto == true)
                return; // Läuft schon in Automatik.
            if (target.TrySetAuto(v.Kind, true))
            {
                cache?.Set(v.Kind, null, true); // Der Treiber führt den Wert nach: unbekannt.
            }
            else
            {
                cache?.Forget(v.Kind);
                AppLog.Warn($"Kameraregler {v.Kind}: Automatik wurde vom Treiber abgelehnt.");
            }
            return;
        }

        if (v.Auto == false && caps.AutoSupported && cache?.Get(v.Kind).Auto != false)
        {
            if (target.TrySetAuto(v.Kind, false))
            {
                cache?.Set(v.Kind, cache.Get(v.Kind).Value, false);
            }
            else
            {
                cache?.Forget(v.Kind);
                AppLog.Warn($"Kameraregler {v.Kind}: Automatik ausschalten wurde vom Treiber abgelehnt.");
            }
        }

        if (v.Value is not { } raw || !double.IsFinite(raw))
            return;

        var value = CameraControlMath.ClampAndSnap(raw, caps.Min, caps.Max, caps.Step);
        var tolerance = caps.Step > 0 ? caps.Step / 2 : 1e-9;
        if (cache?.Get(v.Kind).Value is { } current && Math.Abs(current - value) <= tolerance)
            return; // Steht schon dort.

        if (target.TrySetValue(v.Kind, value))
        {
            // Ein Wert ohne ausdrückliches „Automatik aus“ lässt die Automatik offen (je Treiber): unbekannt.
            cache?.Set(v.Kind, value, caps.AutoSupported && v.Auto == false ? false : null);
        }
        else
        {
            cache?.Forget(v.Kind);
            AppLog.Warn($"Kameraregler {v.Kind}: Wert {value.ToString("0.###", CultureInfo.InvariantCulture)} wurde vom Treiber abgelehnt.");
        }
    }

    private static CameraControlInfo Unsupported(CameraControlKind kind) =>
        new(kind, false, false, 0, 0, 0, 0, false, null, null);
}
