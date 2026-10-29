using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;

namespace Fotobox.ViewModels.Admin;

/// <summary>Eine Gruppe von Reglern im Admin (Titel aus <see cref="CameraControlFormatter.GroupTitle"/>).</summary>
public sealed record CameraControlGroupItem(string Title, IReadOnlyList<CameraControlItem> Items);

/// <summary>Eintrag der Flimmerschutz-Auswahl.</summary>
public sealed record PowerlineOption(CameraPowerline Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Eintrag der Profil-Auswahl der Kamerasteuerung (nur Name; das Profil steht im Entwurf).</summary>
public sealed record ControlProfileOption(string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Ein Regler der Kamera im Admin (Schieberegler, Wertanzeige, Haken „Automatisch“). Änderungen durch den Benutzer
/// (<see cref="Value"/>, <see cref="Auto"/>) melden sich über den Rückruf; programmatisches Setzen
/// (<see cref="SetFromModel"/>) bleibt still.
/// </summary>
public sealed class CameraControlItem : ObservableObject
{
    private readonly Action<CameraControlItem>? _changed;
    private readonly bool _snapshotValid;
    private readonly bool _writable;
    private double _value;
    private bool _auto;

    internal CameraControlItem(CameraControlInfo info, bool snapshotValid, bool writable, Action<CameraControlItem>? changed)
    {
        Info = info;
        _snapshotValid = snapshotValid;
        _writable = writable && info.Supported && info.Writable;
        _changed = changed;

        Name = CameraControlFormatter.DisplayName(info.Kind);
        Group = CameraControlFormatter.GroupOf(info.Kind);
        _value = Clamp(info.Value ?? info.Default);
        _auto = info.AutoSupported && info.Auto == true;
    }

    internal CameraControlInfo Info { get; }

    public CameraControlKind Kind => Info.Kind;
    public string Name { get; }
    public CameraControlGroup Group { get; }

    /// <summary>Der Treiber bietet den Regler an.</summary>
    public bool Supported => Info.Supported;

    /// <summary>Der Regler lässt sich verstellen (Kamera frei, Regler beschreibbar).</summary>
    public bool Writable => _writable;

    public double Minimum => Info.Min;
    public double Maximum => Info.Max;

    /// <summary>Schrittweite des Treibers (0 = unbekannt).</summary>
    public double Step => Info.Step;

    /// <summary>Schrittweite für <c>SmallChange</c>/<c>TickFrequency</c> des Schiebereglers (bei unbekannter Schrittweite 1/100 des Bereichs).</summary>
    public double SliderStep => Info.Step > 0 && double.IsFinite(Info.Step)
        ? Info.Step
        : Math.Max((Info.Max - Info.Min) / 100.0, 1e-6);

    public double Default => Info.Default;
    public bool AutoSupported => Info.AutoSupported;

    /// <summary>Der Hinweis „Von dieser Kamera nicht unterstützt“ erscheint anstelle des Reglers.</summary>
    public bool ShowNotSupported => _snapshotValid && !Info.Supported;

    /// <summary>Schieberegler, Wert und Haken erscheinen.</summary>
    public bool ShowControls => Info.Supported;

    public double Value
    {
        get => _value;
        set
        {
            if (!IsSliderEnabled)
            {
                OnPropertyChanged();   // Anzeige zurücksetzen
                return;
            }

            var snapped = CameraControlMath.ClampAndSnap(value, Minimum, Maximum, Step);
            if (Math.Abs(snapped - _value) <= 1e-9)
            {
                if (Math.Abs(snapped - value) > 1e-9)
                    OnPropertyChanged();   // der Schieberegler steht zwischen zwei Schritten: auf den Schritt zurücksetzen
                return;
            }

            _value = snapped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayValue));
            _changed?.Invoke(this);
        }
    }

    /// <summary>Automatik des Reglers (nur wenn <see cref="AutoSupported"/>).</summary>
    public bool Auto
    {
        get => _auto;
        set
        {
            if (!IsAutoEnabled)
            {
                OnPropertyChanged();
                return;
            }

            if (_auto == value)
                return;
            _auto = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSliderEnabled));
            _changed?.Invoke(this);
        }
    }

    /// <summary>Wert in lesbarer Form („62 %“, „1,5×“, „4500 K“).</summary>
    public string DisplayValue => Info.Supported ? CameraControlFormatter.FormatValue(Info, _value) : string.Empty;

    /// <summary>Der Schieberegler lässt sich bedienen: beschreibbar, Bereich vorhanden und Automatik aus.</summary>
    public bool IsSliderEnabled => _writable && Maximum > Minimum && !_auto;

    /// <summary>Der Haken „Automatisch“ lässt sich bedienen.</summary>
    public bool IsAutoEnabled => _writable && Info.AutoSupported;

    /// <summary>Aktueller Stand als Reglerwert (Automatik nur bei Reglern mit Automatik).</summary>
    internal CameraControlValue ToValue() => new()
    {
        Kind = Kind,
        Value = _value,
        Auto = Info.AutoSupported ? _auto : null,
    };

    /// <summary>Setzt Wert und Automatik ohne Rückruf (Anzeige nach Lesen, Profil laden, Zurücksetzen).</summary>
    internal void SetFromModel(double? value, bool? auto)
    {
        if (!Info.Supported)
            return;

        if (value is { } v && double.IsFinite(v))
        {
            _value = Clamp(v);
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(DisplayValue));
        }
        if (Info.AutoSupported && auto is { } a)
        {
            _auto = a;
            OnPropertyChanged(nameof(Auto));
        }
        OnPropertyChanged(nameof(IsSliderEnabled));
    }

    private double Clamp(double value)
    {
        if (!double.IsFinite(value))
            return Info.Min;
        var (lo, hi) = Info.Min <= Info.Max ? (Info.Min, Info.Max) : (Info.Max, Info.Min);
        return Math.Clamp(value, lo, hi);
    }
}
