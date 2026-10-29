using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;

namespace Fotobox.ViewModels.Admin;

// Kamerasteuerung (Tab „Kamera“, Karte „Bildeinstellungen“): Hardware-Regler der Webcam live, Flimmerschutz und Profile.
// Der Anfangsstand kommt immer vom Treiber (GetControlsAsync). Im Entwurf (Camera.ControlValues) stehen nur verstellte Regler.
// Alles wirkt vorübergehend live (ApplyControlsLiveAsync) und nie über einen Kamera-Neustart; gespeichert wird mit „Übernehmen“.
// Schließen ohne Speichern nimmt die Live-Änderungen zurück (RevertCameraControlsAsync).
public sealed partial class AdminViewModel
{
    public const string ControlsNotRunningText = "Die Kamera läuft nicht – die Regler sind nicht verfügbar.";
    public const string ControlsReadOnlyText =
        "Die Kamera wird gerade von einer anderen App genutzt – die Regler lassen sich nicht verstellen.";
    public const string ControlUnsupportedText = "Von dieser Kamera nicht unterstützt";
    public const string ControlProfileModifiedLabel = "(geändert)";
    public const string ControlProfileDuplicateText = "Ein Profil mit diesem Namen gibt es bereits.";

    /// <summary>Länger darf das Zurücknehmen der Regler (beim Schließen, vor dem Kamerawechsel) nicht dauern.</summary>
    private static readonly TimeSpan ControlsRevertTimeout = TimeSpan.FromSeconds(5);

    // Letzter gültiger Lesestand (Empty = Kamera läuft nicht oder keine Session).
    private CameraControlsSnapshot _controlsSnapshot = CameraControlsSnapshot.Empty;
    private bool _controlsValid;
    private string? _controlsKey;               // Geräte-Id, zu der die Anzeige gehört
    private int _controlsVersion;
    private bool _controlsLastRunning;
    private IReadOnlyList<CameraControlItem> _controlItems = [];
    private IReadOnlyList<CameraControlGroupItem> _controlGroups = [];

    // Treiber-Schnappschuss je Gerät beim ersten Lesen (nur beschreibbare Regler) bzw. nach dem Speichern der dann wirksame Stand.
    private readonly Dictionary<string, IReadOnlyList<CameraControlValue>> _controlBaselines = new(StringComparer.OrdinalIgnoreCase);

    // Seit dem letzten Speichern wurde etwas live angewendet; _lastLive ist der zuletzt gesendete Stand.
    private bool _controlsLiveDirty;
    private string? _liveDirtyDeviceId;
    private IReadOnlyList<CameraControlValue> _lastLive = [];
    private bool _restoringAfterClose;               // das Schließen ohne Speichern läuft: keine Entwurfswerte mehr live senden
    private CameraPowerline _lastLivePowerline;   // Flimmerschutz des zuletzt gesendeten Stands (gilt nur bei _controlsLiveDirty)

    // Zuletzt gespeicherter Stand (Grundlage der Grundwerte beim Zurücknehmen).
    private string? _savedControlsDeviceId;
    private List<CameraControlValue> _savedControlValues = [];
    private CameraPowerline _savedPowerline;

    private IReadOnlyList<ControlProfileOption> _controlProfileOptions = [];
    private ControlProfileOption? _selectedControlProfile;
    private string _controlProfileName = string.Empty;
    private bool _suppressProfileSelection;

    public IRelayCommand ResetControlsCommand { get; private set; } = null!;
    public IRelayCommand SaveNewControlProfileCommand { get; private set; } = null!;
    public IRelayCommand OverwriteControlProfileCommand { get; private set; } = null!;
    public IRelayCommand RenameControlProfileCommand { get; private set; } = null!;
    public IRelayCommand DeleteControlProfileCommand { get; private set; } = null!;

    /// <summary>Flimmerschutz-Auswahl (Nicht verändern / Aus / 50 Hz / 60 Hz / Automatisch).</summary>
    public IReadOnlyList<PowerlineOption> PowerlineOptions { get; } =
        Enum.GetValues<CameraPowerline>().Select(p => new PowerlineOption(p, CameraControlFormatter.PowerlineLabel(p))).ToList();

    private void InitCameraControls()
    {
        _savedControlsDeviceId = _draft.Camera.ControlsDeviceId;
        _savedControlValues = CameraSettings.CloneValues(_draft.Camera.ControlValues);
        _savedPowerline = _draft.Camera.Powerline;

        ResetControlsCommand = new RelayCommand(ResetControls, () => ControlsEditable);
        SaveNewControlProfileCommand = new RelayCommand(SaveNewControlProfile, CanSaveNewControlProfile);
        OverwriteControlProfileCommand = new RelayCommand(OverwriteControlProfile, () => ControlsEditable && FindSelectedControlProfile() is not null);
        RenameControlProfileCommand = new RelayCommand(RenameControlProfile, CanRenameControlProfile);
        DeleteControlProfileCommand = new RelayCommand(DeleteControlProfile, () => FindSelectedControlProfile() is not null);
    }

    // ---------------------------------------------------------------- Anzeige

    /// <summary>Die Regler samt Gruppen; leer, solange die Kamera nicht läuft.</summary>
    public IReadOnlyList<CameraControlGroupItem> CameraControlGroups => _controlGroups;

    /// <summary>Alle Regler (flach, in der Reihenfolge der Anzeige).</summary>
    public IReadOnlyList<CameraControlItem> CameraControlItems => _controlItems;

    /// <summary>Die Regler werden angezeigt (Kamera läuft und hat eine Session gemeldet).</summary>
    public bool ControlGroupsVisible => _controlsValid;

    /// <summary>Die Regler lassen sich verstellen (Kamera läuft und ist nicht von einer anderen App belegt).</summary>
    public bool ControlsEditable => _controlsValid && _controlsSnapshot.Writable;

    /// <summary>Hinweis, wenn die Regler nicht verfügbar sind; sonst leer.</summary>
    public string ControlsHint => !_controlsValid ? ControlsNotRunningText : !_controlsSnapshot.Writable ? ControlsReadOnlyText : string.Empty;

    public bool HasControlsHint => ControlsHint.Length > 0;

    public bool PowerlineEnabled => ControlsEditable && _controlsSnapshot.PowerlineSupported;

    /// <summary>Hinweis „Von dieser Kamera nicht unterstützt“ am Flimmerschutz.</summary>
    public bool ShowPowerlineUnsupported => _controlsValid && !_controlsSnapshot.PowerlineSupported;

    public PowerlineOption? SelectedPowerline
    {
        get => PowerlineOptions.FirstOrDefault(o => o.Value == _draft.Camera.Powerline);
        set
        {
            if (value is null || value.Value == _draft.Camera.Powerline)
                return;
            _draft.Camera.Powerline = value.Value;
            OnPropertyChanged();
            if (PowerlineEnabled)
                SendControlsLive(_lastLive);
            HideSaveStatusIfDirty();
        }
    }

    private static CameraControlsSnapshot WritableOnly(CameraControlsSnapshot s) =>
        s.Writable ? s with { Controls = s.Controls.Where(c => c.Supported && c.Writable).ToList() } : s with { Controls = [] };

    private string? CurrentControlsKey() => _controlsValid ? Camera.ActiveDeviceId ?? _controlsSnapshot.DeviceId : null;

    public CameraControlItem? GetControl(CameraControlKind kind) => _controlItems.FirstOrDefault(i => i.Kind == kind);

    // ---------------------------------------------------------------- Lesen

    /// <summary>
    /// Liest die Regler der laufenden Kamera (nur die jeweils letzte Anforderung zählt) und baut die Anzeige neu auf.
    /// Läuft die Kamera nicht, bleibt die Anzeige leer. Wirft nicht.
    /// </summary>
    public async Task RefreshControlsAsync()
    {
        if (_disposed)
            return;

        var version = Interlocked.Increment(ref _controlsVersion);
        if (Camera.Status != CameraStatus.Running)
        {
            ApplyControlsRead(CameraControlsSnapshot.Empty);
            return;
        }

        CameraControlsSnapshot snapshot;
        try
        {
            snapshot = await Camera.GetControlsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Die Kamera-Regler konnten nicht gelesen werden.", ex);
            snapshot = CameraControlsSnapshot.Empty;
        }

        if (_disposed || version != Volatile.Read(ref _controlsVersion))
            return;
        ApplyControlsRead(snapshot ?? CameraControlsSnapshot.Empty);
    }

    private void OnCameraStatusChangedForControls()
    {
        if (_disposed)
            return;

        if (Camera.Status != CameraStatus.Running)
        {
            _controlsLastRunning = false;
            Interlocked.Increment(ref _controlsVersion);   // eine laufende Abfrage verwerfen
            if (_controlsValid)
                ApplyControlsRead(CameraControlsSnapshot.Empty);
            return;
        }

        // Wechsel auf „läuft“ (Neustart, Wiederverbindung) oder anderes Gerät: neu lesen.
        var active = Camera.ActiveDeviceId;
        var deviceChanged = _controlsValid && active is not null && !string.Equals(active, _controlsKey, StringComparison.OrdinalIgnoreCase);
        if (!_controlsLastRunning || !_controlsValid || deviceChanged)
        {
            _controlsLastRunning = true;
            Forget(RefreshControlsAsync());
        }
    }

    private void ApplyControlsRead(CameraControlsSnapshot snapshot)
    {
        var valid = Camera.Status == CameraStatus.Running && snapshot.DeviceId is not null;
        _controlsSnapshot = valid ? snapshot : CameraControlsSnapshot.Empty;
        _controlsValid = valid;
        var key = CurrentControlsKey();
        var keyChanged = !string.Equals(key, _controlsKey, StringComparison.OrdinalIgnoreCase);
        _controlsKey = key;

        // Grundwerte: Treiberstand beim ersten Lesen je Gerät (nur beschreibbare Regler).
        if (key is not null && _controlsSnapshot.Writable && !_controlBaselines.ContainsKey(key))
            _controlBaselines[key] = CameraControlMath.CurrentValues(WritableOnly(_controlsSnapshot)).ToList();

        BuildControlItems();

        // Nach einer Wiederverbindung kennt die Kamera nur den gespeicherten Stand: Was live verstellt war, erneut senden.
        if (valid && _controlsSnapshot.Writable && _controlsLiveDirty && key is not null
            && string.Equals(key, _liveDirtyDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            ApplyValuesToItems(_lastLive);
            Forget(SafeApplyLiveAsync(_lastLive, _draft.Camera.Powerline));
        }
        else if (valid)
        {
            SyncPowerlineLive();
        }

        RebuildControlProfileOptions(resetSelection: keyChanged);
        RaiseControlsChanged();
    }

    private void BuildControlItems()
    {
        var items = new List<CameraControlItem>();
        foreach (var kind in Enum.GetValues<CameraControlKind>())
        {
            var info = _controlsSnapshot.Controls.FirstOrDefault(c => c.Kind == kind)
                       ?? new CameraControlInfo(kind, false, false, 0, 1, 0, 0, false, null, null);
            items.Add(new CameraControlItem(info, _controlsValid, ControlsEditable, OnControlItemChanged));
        }
        _controlItems = items;
        _controlGroups = Enum.GetValues<CameraControlGroup>()
            .Select(g => new CameraControlGroupItem(
                CameraControlFormatter.GroupTitle(g),
                items.Where(i => i.Group == g).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
    }

    private void RaiseControlsChanged()
    {
        OnPropertyChanged(nameof(CameraControlGroups));
        OnPropertyChanged(nameof(CameraControlItems));
        OnPropertyChanged(nameof(ControlGroupsVisible));
        OnPropertyChanged(nameof(ControlsEditable));
        OnPropertyChanged(nameof(ControlsHint));
        OnPropertyChanged(nameof(HasControlsHint));
        OnPropertyChanged(nameof(PowerlineEnabled));
        OnPropertyChanged(nameof(ShowPowerlineUnsupported));
        OnPropertyChanged(nameof(SelectedPowerline));
        NotifyControlCommands();
    }

    private void NotifyControlCommands()
    {
        // Vor InitCameraControls (Konstruktor) gibt es die Befehle noch nicht.
        ResetControlsCommand?.NotifyCanExecuteChanged();
        SaveNewControlProfileCommand?.NotifyCanExecuteChanged();
        OverwriteControlProfileCommand?.NotifyCanExecuteChanged();
        RenameControlProfileCommand?.NotifyCanExecuteChanged();
        DeleteControlProfileCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ControlProfileModifiedText));
        OnPropertyChanged(nameof(ControlProfileHint));
        OnPropertyChanged(nameof(HasControlProfileHint));
    }

    // ---------------------------------------------------------------- Verstellen

    private void OnControlItemChanged(CameraControlItem item)
    {
        if (_disposed)
            return;
        var key = CurrentControlsKey();
        if (key is null)
            return;

        var camera = _draft.Camera;
        // Werte gehören immer zu genau einer Kamera.
        if (camera.ControlsDeviceId is not null && !string.Equals(camera.ControlsDeviceId, key, StringComparison.OrdinalIgnoreCase))
            camera.ControlValues = [];
        camera.ControlsDeviceId = key;

        var values = camera.ControlValues.Where(v => v.Kind != item.Kind).ToList();
        values.Add(item.ToValue());
        camera.ControlValues = values.OrderBy(v => v.Kind).ToList();

        SendControlsLive(BuildLiveValues(key));
        OnPropertyChanged(nameof(ControlProfileModifiedText));
        HideSaveStatusIfDirty();
    }

    /// <summary>Der Stand, den die Kamera live bekommt: verstellte Regler des Entwurfs über den Grundwerten, nur beschreibbare Regler.</summary>
    private IReadOnlyList<CameraControlValue> BuildLiveValues(string key)
    {
        var camera = _draft.Camera;
        var draftValues = string.Equals(camera.ControlsDeviceId, key, StringComparison.OrdinalIgnoreCase) ? camera.ControlValues : [];
        var writable = _controlItems.Where(i => i.Writable).Select(i => i.Kind).ToHashSet();
        return CameraControlMath.Merge(draftValues, GetControlBaseline(key)).Where(v => writable.Contains(v.Kind)).ToList();
    }

    /// <summary>Grundwerte des Geräts: zuletzt gespeicherte Werte (nur dieses Geräts) über dem Treiber-Schnappschuss.</summary>
    private IReadOnlyList<CameraControlValue> GetControlBaseline(string key)
    {
        _controlBaselines.TryGetValue(key, out var snapshotValues);
        var saved = string.Equals(_savedControlsDeviceId, key, StringComparison.OrdinalIgnoreCase) ? _savedControlValues : [];
        return CameraControlMath.Merge(saved, snapshotValues ?? []);
    }

    private void SendControlsLive(IReadOnlyList<CameraControlValue> values)
    {
        var key = CurrentControlsKey();
        if (key is null)
            return;
        _lastLive = values.Select(v => v.Clone()).ToList();
        _lastLivePowerline = _draft.Camera.Powerline;
        _controlsLiveDirty = true;
        _liveDirtyDeviceId = key;
        Forget(SafeApplyLiveAsync(_lastLive, _draft.Camera.Powerline));
    }

    /// <summary>
    /// Sendet den Flimmerschutz des Entwurfs live, wenn er vom wirksamen Stand abweicht (zuletzt live gesendet, sonst gespeichert):
    /// nach dem Ersetzen des Entwurfs (Import, Zurücksetzen) und beim Lesen einer neuen Session. Gleicher Weg wie die Auswahl in der
    /// ComboBox, daher nimmt „Schließen“ ohne Speichern ihn ebenfalls zurück.
    /// </summary>
    private void SyncPowerlineLive()
    {
        // Nach dem Beginn des Schließens nichts Ungespeichertes mehr an die Kamera schicken (ein spätes Lesen der neuen Session
        // träfe sonst nach dem Zurücknehmen ein).
        if (_disposed || _restoringAfterClose || !PowerlineEnabled)
            return;
        var applied = _controlsLiveDirty ? _lastLivePowerline : _savedPowerline;
        if (_draft.Camera.Powerline != applied)
            SendControlsLive(_lastLive);
    }

    private Task SafeApplyLiveAsync(IReadOnlyList<CameraControlValue> values, CameraPowerline powerline)
    {
        try
        {
            return Camera.ApplyControlsLiveAsync(values.Select(v => v.Clone()).ToList(), powerline);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private void ApplyValuesToItems(IEnumerable<CameraControlValue> values)
    {
        foreach (var v in values)
            GetControl(v.Kind)?.SetFromModel(v.Value, v.Auto);
        OnPropertyChanged(nameof(ControlProfileModifiedText));
    }

    // ---------------------------------------------------------------- Zurücknehmen

    /// <summary>
    /// Nimmt Live-Änderungen zurück: sendet die Grundwerte der aktiven Kamera (zuletzt gespeichert über dem Treiber-Schnappschuss)
    /// und den gespeicherten Flimmerschutz. Nur wenn seit dem letzten Speichern live etwas angewendet wurde und die Kamera noch
    /// die verstellte ist. Wirft nicht; wartet höchstens <see cref="ControlsRevertTimeout"/>.
    /// </summary>
    public async Task RevertCameraControlsAsync()
    {
        if (!_controlsLiveDirty)
            return;
        var deviceId = _liveDirtyDeviceId;
        _controlsLiveDirty = false;
        _liveDirtyDeviceId = null;
        _lastLive = [];
        if (deviceId is null)
            return;

        try
        {
            var active = Camera.ActiveDeviceId;
            if (!string.Equals(active, deviceId, StringComparison.OrdinalIgnoreCase))
                return;   // eine andere Kamera läuft: die verstellte lässt sich nicht mehr erreichen

            await Camera.ApplyControlsLiveAsync(GetControlBaseline(deviceId).Select(v => v.Clone()).ToList(), _savedPowerline)
                .WaitAsync(ControlsRevertTimeout);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Die Kamera-Regler konnten nicht zurückgesetzt werden.", ex);
        }
    }

    /// <summary>
    /// Für <c>AdminLauncher</c> nach dem Schließen: stellt bei Bedarf die Kamera wieder her (<paramref name="restoreCamera"/>) und
    /// nimmt danach die Live-Änderungen der Regler zurück (verkettet, nicht parallel). Wirft nicht.
    /// </summary>
    public async Task RestoreAfterCloseAsync(AppSettings saved, bool restoreCamera)
    {
        _restoringAfterClose = true;
        try
        {
            if (restoreCamera)
            {
                // Wechselt die Wiederherstellung die Kamera, ist die verstellte danach nicht mehr erreichbar: vorher zurücknehmen.
                var running = LastAppliedCamera?.DeviceId;
                if (_controlsLiveDirty && !string.Equals(running, saved.Camera.DeviceId, StringComparison.OrdinalIgnoreCase))
                    await RevertCameraControlsAsync();
                await RestoreCameraAsync(saved);
            }

            await RevertCameraControlsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Kamera-Regler konnten nach dem Schließen nicht zurückgenommen werden.", ex);
        }
    }

    /// <summary>Nach erfolgreichem Speichern: der gespeicherte Stand ist die neue Basis, nichts mehr zurückzunehmen.</summary>
    private void OnControlsSaved()
    {
        // Was live auf der Kamera liegt, ist jetzt der Stand, zu dem „Schließen“ zurückkehrt.
        if (_controlsLiveDirty && _liveDirtyDeviceId is { } id)
        {
            _controlBaselines.TryGetValue(id, out var current);
            _controlBaselines[id] = CameraControlMath.Merge(_lastLive, current ?? []).ToList();
        }
        _controlsLiveDirty = false;
        _liveDirtyDeviceId = null;
        _lastLive = [];

        _savedControlsDeviceId = _draft.Camera.ControlsDeviceId;
        _savedControlValues = CameraSettings.CloneValues(_draft.Camera.ControlValues);
        _savedPowerline = _draft.Camera.Powerline;
    }

    // ---------------------------------------------------------------- Zurücksetzen

    private void ResetControls() => ResetControlsCore();

    /// <summary>
    /// „Alle zurücksetzen“ (auch nach „Standardwerte wiederherstellen“): verstellte Werte im Entwurf löschen, Flimmerschutz 50 Hz,
    /// Treiber-Standardwerte (mit Automatik, wo möglich) live senden. Die Grundwerte des Geräts bleiben; Schließen ohne Speichern
    /// nimmt das Zurücksetzen daher zurück.
    /// </summary>
    private void ResetControlsCore()
    {
        var camera = _draft.Camera;
        camera.ControlValues = [];
        camera.ControlsDeviceId = null;
        camera.Powerline = CameraPowerline.Hz50;
        OnPropertyChanged(nameof(SelectedPowerline));

        if (ControlsEditable)
        {
            var defaults = CameraControlMath.Defaults(WritableOnly(_controlsSnapshot));
            ApplyValuesToItems(defaults);
            SendControlsLive(defaults);
        }
        NotifyControlCommands();
        HideSaveStatusIfDirty();
    }

    // ---------------------------------------------------------------- Profile

    /// <summary>Profile der aktiven Kamera (Auswahlliste).</summary>
    public IReadOnlyList<ControlProfileOption> ControlProfileOptions => _controlProfileOptions;

    /// <summary>Name im Eingabefeld (für „Als neues Profil speichern“ und „Umbenennen“).</summary>
    public string ControlProfileName
    {
        get => _controlProfileName;
        set
        {
            if (SetProperty(ref _controlProfileName, value ?? string.Empty))
                NotifyControlCommands();
        }
    }

    public ControlProfileOption? SelectedControlProfile
    {
        get => _selectedControlProfile;
        set
        {
            // WPF setzt die Auswahl beim Austausch der Liste auf null: während des Neuaufbaus ignorieren.
            if (_suppressProfileSelection || Equals(_selectedControlProfile, value))
                return;
            _selectedControlProfile = value;
            OnPropertyChanged();

            if (value is not null)
            {
                ControlProfileName = value.Name;
                if (FindSelectedControlProfile() is { } profile && ControlsEditable)
                    LoadControlProfile(profile);
            }
            NotifyControlCommands();
        }
    }

    /// <summary>„(geändert)“, wenn die Regler vom gewählten Profil abweichen; sonst leer.</summary>
    public string ControlProfileModifiedText
    {
        get
        {
            if (!_controlsValid || FindSelectedControlProfile() is not { } profile)
                return string.Empty;
            var kinds = profile.Values.Select(v => v.Kind).ToHashSet();
            var current = _controlItems.Where(i => i.Supported && kinds.Contains(i.Kind)).Select(i => i.ToValue());
            return CameraControlMath.SameValues(current, profile.Values, _controlsSnapshot) ? string.Empty : ControlProfileModifiedLabel;
        }
    }

    /// <summary>Hinweis zu Limit und doppelten Namen; sonst leer.</summary>
    public string ControlProfileHint
    {
        get
        {
            if (!_controlsValid)
                return string.Empty;
            var name = NormalizedProfileName();
            var selected = FindSelectedControlProfile();
            if (name.Length > 0 && ProfileNameTaken(name) && !string.Equals(selected?.Name, name, StringComparison.OrdinalIgnoreCase))
                return ControlProfileDuplicateText;
            if (ProfilesOfActiveDevice().Count() >= SettingsLimits.CameraProfilesMax)
                return $"Es sind höchstens {SettingsLimits.CameraProfilesMax} Profile je Kamera möglich.";
            return string.Empty;
        }
    }

    public bool HasControlProfileHint => ControlProfileHint.Length > 0;

    private string NormalizedProfileName()
    {
        var name = (_controlProfileName ?? string.Empty).Trim();
        return name.Length > SettingsLimits.CameraProfileNameMaxLength ? name[..SettingsLimits.CameraProfileNameMaxLength].Trim() : name;
    }

    private IEnumerable<CameraControlProfile> ProfilesOfActiveDevice()
    {
        var key = CurrentControlsKey();
        if (key is null)
            return [];
        return _draft.Camera.ControlProfiles.Where(p => string.Equals(p.DeviceId, key, StringComparison.OrdinalIgnoreCase));
    }

    private bool ProfileNameTaken(string name) =>
        ProfilesOfActiveDevice().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private CameraControlProfile? FindSelectedControlProfile() =>
        _selectedControlProfile is null
            ? null
            : ProfilesOfActiveDevice().FirstOrDefault(p => string.Equals(p.Name, _selectedControlProfile.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Baut die Auswahlliste aus dem Entwurf neu auf. Die Auswahl bleibt (nach Name), es sei denn die Kamera hat gewechselt.</summary>
    private void RebuildControlProfileOptions(bool resetSelection = false, string? selectName = null)
    {
        var previous = selectName ?? (resetSelection ? null : _selectedControlProfile?.Name);
        var options = ProfilesOfActiveDevice().Select(p => new ControlProfileOption(p.Name)).ToList();
        var selected = previous is null
            ? null
            : options.FirstOrDefault(o => string.Equals(o.Name, previous, StringComparison.OrdinalIgnoreCase));

        _suppressProfileSelection = true;
        try
        {
            _controlProfileOptions = options;
            _selectedControlProfile = selected;
            OnPropertyChanged(nameof(ControlProfileOptions));
            OnPropertyChanged(nameof(SelectedControlProfile));
        }
        finally
        {
            _suppressProfileSelection = false;
        }
        NotifyControlCommands();
    }

    private bool CanSaveNewControlProfile()
    {
        if (!_controlsValid || CurrentControlsKey() is null)
            return false;
        var name = NormalizedProfileName();
        return name.Length > 0 && !ProfileNameTaken(name) && ProfilesOfActiveDevice().Count() < SettingsLimits.CameraProfilesMax;
    }

    private bool CanRenameControlProfile()
    {
        if (FindSelectedControlProfile() is not { } selected)
            return false;
        var name = NormalizedProfileName();
        if (name.Length == 0)
            return false;
        // Der eigene Name darf sich nur in der Schreibweise unterscheiden.
        return string.Equals(selected.Name, name, StringComparison.OrdinalIgnoreCase) || !ProfileNameTaken(name);
    }

    /// <summary>Aktueller Stand aller unterstützten Regler (Wert und Automatik) für ein Profil.</summary>
    private List<CameraControlValue> CaptureProfileValues() =>
        _controlItems.Where(i => i.Supported).Select(i => i.ToValue()).ToList();

    private void SaveNewControlProfile()
    {
        if (!CanSaveNewControlProfile() || CurrentControlsKey() is not { } key)
            return;

        var name = NormalizedProfileName();
        _draft.Camera.ControlProfiles.Add(new CameraControlProfile { Name = name, DeviceId = key, Values = CaptureProfileValues() });
        ControlProfileName = name;
        RebuildControlProfileOptions(selectName: name);
        HideSaveStatusIfDirty();
    }

    private void OverwriteControlProfile()
    {
        if (FindSelectedControlProfile() is not { } profile)
            return;
        if (!_ui.Confirm("Profil überschreiben", $"Soll das Profil „{profile.Name}“ mit den aktuellen Reglerwerten überschrieben werden?"))
            return;

        profile.Values = CaptureProfileValues();
        NotifyControlCommands();
        HideSaveStatusIfDirty();
    }

    private void RenameControlProfile()
    {
        if (!CanRenameControlProfile() || FindSelectedControlProfile() is not { } profile)
            return;

        var name = NormalizedProfileName();
        profile.Name = name;
        ControlProfileName = name;
        RebuildControlProfileOptions(selectName: name);
        HideSaveStatusIfDirty();
    }

    private void DeleteControlProfile()
    {
        if (FindSelectedControlProfile() is not { } profile)
            return;
        if (!_ui.Confirm("Profil löschen", $"Soll das Profil „{profile.Name}“ gelöscht werden?"))
            return;

        _draft.Camera.ControlProfiles.Remove(profile);
        RebuildControlProfileOptions(resetSelection: true);
        HideSaveStatusIfDirty();
    }

    /// <summary>Lädt die Werte eines Profils in Regler und Entwurf und sendet sie live.</summary>
    private void LoadControlProfile(CameraControlProfile profile)
    {
        if (CurrentControlsKey() is not { } key)
            return;

        var supported = _controlItems.Where(i => i.Supported).Select(i => i.Kind).ToHashSet();
        var camera = _draft.Camera;
        camera.ControlsDeviceId = key;
        camera.ControlValues = CameraSettings.CloneValues(profile.Values).Where(v => supported.Contains(v.Kind)).OrderBy(v => v.Kind).ToList();

        var live = BuildLiveValues(key);
        ApplyValuesToItems(live);
        SendControlsLive(live);
        HideSaveStatusIfDirty();
    }
}
