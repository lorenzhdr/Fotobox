using Fotobox.Core;
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Media.Devices;

namespace Fotobox.Services.Camera;

/// <summary>
/// Reine Abbildungen zwischen den erweiterten WinRT-Reglern (<c>ZoomControl</c>, <c>ExposureControl</c>, <c>WhiteBalanceControl</c>,
/// <c>FocusControl</c>, <c>ExposureCompensationControl</c>) und dem einheitlichen Rohwert-Modell (<see cref="CameraControlInfo"/>).
/// </summary>
internal static class CameraControlMapping
{
    /// <summary>Belichtungszeit als Sekunden (Rohwert des Reglers <see cref="CameraControlKind.Exposure"/> im erweiterten Weg).</summary>
    public static double ToSeconds(TimeSpan value) => value.TotalSeconds;

    /// <summary>Sekunden zurück als Zeitspanne (auf ganze Ticks gerundet, auf den darstellbaren Bereich begrenzt).</summary>
    public static TimeSpan ToTimeSpan(double seconds)
    {
        if (double.IsNaN(seconds))
            return TimeSpan.Zero;
        var ticks = Math.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
        if (ticks >= TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;
        if (ticks <= TimeSpan.MinValue.Ticks)
            return TimeSpan.MinValue;
        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>Weißabgleich: nur das Preset „Auto“ gilt als Automatik; jedes andere (auch ein Szenenpreset) als manuell.</summary>
    public static bool IsAutoWhiteBalance(ColorTemperaturePreset preset) => preset == ColorTemperaturePreset.Auto;

    /// <summary>Preset beim Ein-/Ausschalten der Weißabgleich-Automatik.</summary>
    public static ColorTemperaturePreset WhiteBalancePresetFor(bool auto) =>
        auto ? ColorTemperaturePreset.Auto : ColorTemperaturePreset.Manual;

    /// <summary>
    /// Übergangsmodus des Zooms: direkt, wenn möglich (der Regler folgt dem Schieber sofort), sonst automatisch, sonst der erste
    /// angebotene; null, wenn keiner gemeldet wird (dann gilt der Standard des Treibers).
    /// </summary>
    public static ZoomTransitionMode? PickZoomMode(IReadOnlyList<ZoomTransitionMode>? supported)
    {
        if (supported is null || supported.Count == 0)
            return null;
        if (supported.Contains(ZoomTransitionMode.Direct))
            return ZoomTransitionMode.Direct;
        if (supported.Contains(ZoomTransitionMode.Auto))
            return ZoomTransitionMode.Auto;
        return supported[0];
    }

    /// <summary>Fokus: Automatik = Dauerfokus (<see cref="FocusMode.Continuous"/>), Wertregler nur mit <see cref="FocusMode.Manual"/>.</summary>
    public static (bool Auto, bool Manual) FocusSupport(IReadOnlyList<FocusMode>? supported) =>
        (supported?.Contains(FocusMode.Continuous) == true, supported?.Contains(FocusMode.Manual) == true);

    /// <summary>Mitte eines Bereichs (Standardwert ohne vom Treiber gemeldeten Standard).</summary>
    public static double Mid(double min, double max) => min + (max - min) / 2;

    /// <summary>Standardwert 0 (Belichtungskorrektur), in den Bereich geklemmt.</summary>
    public static double ZeroClamped(double min, double max) => Math.Clamp(0, Math.Min(min, max), Math.Max(min, max));
}

/// <summary>
/// Ersatzweg, wenn der Haupt-Zugriff einen Regler nicht anbietet: je Regler gilt der erste Weg, der ihn unterstützt (der Weg
/// wird bei jedem Aufruf neu bestimmt, die Fähigkeiten ändern sich innerhalb einer Session nicht). Flimmerschutz kommt nur vom Hauptweg.
/// </summary>
internal sealed class FallbackControlTarget(ICameraControlTarget primary, ICameraControlTarget fallback) : ICameraControlTarget
{
    private ICameraControlTarget For(CameraControlKind kind) => primary.GetCaps(kind).Supported ? primary : fallback;

    public ControlCaps GetCaps(CameraControlKind kind)
    {
        var p = primary.GetCaps(kind);
        return p.Supported ? p : fallback.GetCaps(kind);
    }

    public bool TryGetValue(CameraControlKind kind, out double value) => For(kind).TryGetValue(kind, out value);
    public bool TryGetAuto(CameraControlKind kind, out bool value) => For(kind).TryGetAuto(kind, out value);
    public bool TrySetValue(CameraControlKind kind, double value) => For(kind).TrySetValue(kind, value);
    public bool TrySetAuto(CameraControlKind kind, bool value) => For(kind).TrySetAuto(kind, value);
    public bool TryGetPowerline(out CameraPowerline value) => primary.TryGetPowerline(out value);
    public bool TrySetPowerline(CameraPowerline value) => primary.TrySetPowerline(value);
}

/// <summary>
/// Erweiterte WinRT-Regler des <c>VideoDeviceController</c> für Zoom, Belichtungskorrektur, Belichtungszeit, Weißabgleich und Fokus.
/// Die Aufrufe sind synchron (der Aufrufer läuft auf dem Threadpool, MTA); asynchrone WinRT-Operationen werden je Aufruf mit
/// einer Frist abgewartet und werfen bei Zeitüberschreitung eine <see cref="TimeoutException"/> (der Aufrufer fängt je Regler).
/// Nicht erweiterte Arten gelten als nicht unterstützt.
/// </summary>
internal sealed class ExtendedControlTarget(VideoDeviceController controller) : ICameraControlTarget
{
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private static void Wait(IAsyncAction operation)
    {
        var task = operation.AsTask();
        if (!task.Wait(CallTimeout))
            throw new TimeoutException("Die Kamera reagiert nicht.");
    }

    public ControlCaps GetCaps(CameraControlKind kind)
    {
        switch (kind)
        {
            case CameraControlKind.Zoom:
                {
                    var z = controller.ZoomControl;
                    return z.Supported ? new ControlCaps(true, z.Min, z.Max, z.Step, z.Min, false) : default;
                }
            case CameraControlKind.ExposureCompensation:
                {
                    var e = controller.ExposureCompensationControl;
                    return e.Supported
                        ? new ControlCaps(true, e.Min, e.Max, e.Step, CameraControlMapping.ZeroClamped(e.Min, e.Max), false)
                        : default;
                }
            case CameraControlKind.Exposure:
                {
                    var e = controller.ExposureControl;
                    if (!e.Supported)
                        return default;
                    var min = CameraControlMapping.ToSeconds(e.Min);
                    var max = CameraControlMapping.ToSeconds(e.Max);
                    return new ControlCaps(true, min, max, CameraControlMapping.ToSeconds(e.Step), CameraControlMapping.Mid(min, max), true);
                }
            case CameraControlKind.WhiteBalance:
                {
                    var w = controller.WhiteBalanceControl;
                    return w.Supported ? new ControlCaps(true, w.Min, w.Max, w.Step, CameraControlMapping.Mid(w.Min, w.Max), true) : default;
                }
            case CameraControlKind.Focus:
                {
                    var f = controller.FocusControl;
                    if (!f.Supported)
                        return default;
                    var (auto, manual) = CameraControlMapping.FocusSupport(f.SupportedFocusModes);
                    // Ohne Manuell-Modus gibt es keinen Wertregler (Bereich 0..0), höchstens die Automatik.
                    return manual
                        ? new ControlCaps(true, f.Min, f.Max, f.Step, CameraControlMapping.Mid(f.Min, f.Max), auto)
                        : new ControlCaps(auto, 0, 0, 0, 0, auto);
                }
            default:
                return default;
        }
    }

    public bool TryGetValue(CameraControlKind kind, out double value)
    {
        value = 0;
        switch (kind)
        {
            case CameraControlKind.Zoom:
                value = controller.ZoomControl.Value;
                return true;
            case CameraControlKind.ExposureCompensation:
                value = controller.ExposureCompensationControl.Value;
                return true;
            case CameraControlKind.Exposure:
                value = CameraControlMapping.ToSeconds(controller.ExposureControl.Value);
                return true;
            case CameraControlKind.WhiteBalance:
                value = controller.WhiteBalanceControl.Value;
                return true;
            case CameraControlKind.Focus:
                if (!CameraControlMapping.FocusSupport(controller.FocusControl.SupportedFocusModes).Manual)
                    return false;
                value = controller.FocusControl.Value;
                return true;
            default:
                return false;
        }
    }

    public bool TryGetAuto(CameraControlKind kind, out bool value)
    {
        value = false;
        switch (kind)
        {
            case CameraControlKind.Exposure:
                value = controller.ExposureControl.Auto;
                return true;
            case CameraControlKind.WhiteBalance:
                value = CameraControlMapping.IsAutoWhiteBalance(controller.WhiteBalanceControl.Preset);
                return true;
            case CameraControlKind.Focus:
                value = controller.FocusControl.Mode == FocusMode.Continuous;
                return true;
            default:
                return false;
        }
    }

    public bool TrySetValue(CameraControlKind kind, double value)
    {
        switch (kind)
        {
            case CameraControlKind.Zoom:
                {
                    var z = controller.ZoomControl;
                    var settings = new ZoomSettings { Value = (float)value };
                    if (CameraControlMapping.PickZoomMode(z.SupportedModes) is { } mode)
                        settings.Mode = mode;
                    z.Configure(settings);
                    return true;
                }
            case CameraControlKind.ExposureCompensation:
                Wait(controller.ExposureCompensationControl.SetValueAsync((float)value));
                return true;
            case CameraControlKind.Exposure:
                Wait(controller.ExposureControl.SetValueAsync(CameraControlMapping.ToTimeSpan(value)));
                return true;
            case CameraControlKind.WhiteBalance:
                {
                    var w = controller.WhiteBalanceControl;
                    if (w.Preset != ColorTemperaturePreset.Manual)
                        Wait(w.SetPresetAsync(ColorTemperaturePreset.Manual));
                    Wait(w.SetValueAsync((uint)Math.Round(Math.Max(0, value), MidpointRounding.AwayFromZero)));
                    return true;
                }
            case CameraControlKind.Focus:
                {
                    var f = controller.FocusControl;
                    if (!CameraControlMapping.FocusSupport(f.SupportedFocusModes).Manual)
                        return false;
                    f.Configure(new FocusSettings { Mode = FocusMode.Manual, Value = (uint)Math.Round(Math.Max(0, value), MidpointRounding.AwayFromZero) });
                    Wait(f.FocusAsync());
                    return true;
                }
            default:
                return false;
        }
    }

    public bool TrySetAuto(CameraControlKind kind, bool value)
    {
        switch (kind)
        {
            case CameraControlKind.Exposure:
                Wait(controller.ExposureControl.SetAutoAsync(value));
                return true;
            case CameraControlKind.WhiteBalance:
                Wait(controller.WhiteBalanceControl.SetPresetAsync(CameraControlMapping.WhiteBalancePresetFor(value)));
                return true;
            case CameraControlKind.Focus:
                {
                    var f = controller.FocusControl;
                    var (auto, manual) = CameraControlMapping.FocusSupport(f.SupportedFocusModes);
                    if (value)
                    {
                        if (!auto)
                            return false;
                        f.Configure(new FocusSettings { Mode = FocusMode.Continuous });
                    }
                    else
                    {
                        if (!manual)
                            return false;
                        f.Configure(new FocusSettings { Mode = FocusMode.Manual, Value = f.Value });
                    }
                    Wait(f.FocusAsync());
                    return true;
                }
            default:
                return false;
        }
    }

    public bool TryGetPowerline(out CameraPowerline value)
    {
        value = CameraPowerline.Unchanged;
        return false;
    }

    public bool TrySetPowerline(CameraPowerline value) => false;
}
