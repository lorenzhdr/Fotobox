namespace Fotobox.Core;

/// <summary>Regler der Webcam-Hardware (WinRT <c>VideoDeviceController</c>). Nur hinten erweitern.</summary>
public enum CameraControlKind { Zoom, Pan, Tilt, Brightness, Contrast, Hue, Exposure, BacklightCompensation, Focus, WhiteBalance, ExposureCompensation }

/// <summary>Flimmerschutz (Netzfrequenz des Raumlichts). Nur hinten erweitern.</summary>
public enum CameraPowerline { Unchanged, Off, Hz50, Hz60, Auto }

/// <summary>
/// Ein gespeicherter Reglerwert. <see cref="Value"/> ist der Rohwert des Treibers (die Einheit ist je Kamera verschieden),
/// <see cref="Auto"/> schaltet die Automatik des Reglers an oder aus; <c>null</c> = nicht gesetzt (der Treiber behält seinen Stand).
/// </summary>
public sealed class CameraControlValue
{
    public CameraControlKind Kind { get; set; }
    public double? Value { get; set; }
    public bool? Auto { get; set; }

    /// <summary>Kopie aller Felder.</summary>
    public CameraControlValue Clone() => new() { Kind = Kind, Value = Value, Auto = Auto };
}

/// <summary>Benanntes Profil mit Reglerwerten, an eine Kamera (<see cref="DeviceId"/>, aufgelöste Geräte-Id) gebunden.</summary>
public sealed class CameraControlProfile
{
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public List<CameraControlValue> Values { get; set; } = [];

    /// <summary>Tiefe Kopie (auch der Werte).</summary>
    public CameraControlProfile Clone() => new()
    {
        Name = Name,
        DeviceId = DeviceId,
        Values = Values is null ? [] : Values.Select(v => v?.Clone()!).ToList(),
    };
}

/// <summary>
/// Fähigkeiten und aktueller Stand eines Reglers der laufenden Kamera-Session. <paramref name="Supported"/> = der Treiber bietet ihn an,
/// <paramref name="Writable"/> = er lässt sich verstellen, <paramref name="AutoSupported"/> = es gibt eine Automatik.
/// <paramref name="Value"/> und <paramref name="Auto"/> sind der aktuelle Stand (null = unbekannt).
/// </summary>
public sealed record CameraControlInfo(
    CameraControlKind Kind,
    bool Supported,
    bool Writable,
    double Min,
    double Max,
    double Step,
    double Default,
    bool AutoSupported,
    double? Value,
    bool? Auto);

/// <summary>Alle Regler einer Kamera-Session samt Flimmerschutz. <paramref name="DeviceId"/> ist die aufgelöste Geräte-Id (null ohne Session).</summary>
public sealed record CameraControlsSnapshot(
    string? DeviceId,
    bool Writable,
    IReadOnlyList<CameraControlInfo> Controls,
    bool PowerlineSupported,
    CameraPowerline? Powerline)
{
    /// <summary>Keine Session oder keine Unterstützung.</summary>
    public static CameraControlsSnapshot Empty { get; } = new(null, false, [], false, null);
}
