using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Fotobox.Views.Admin;
using Fotobox.ViewModels.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Admin: Kamerasteuerung (Karte „Bildeinstellungen“ im Tab Kamera) – Regler, Live-Senden, Zurücknehmen, Profile.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminCameraControlsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-controls-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeCamera _camera = new();
    private readonly AdminViewModelTests.FakePrint _print = new();
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly AdminViewModelTests.FakeDisplays _displays = new(AdminViewModelTests.AdminMonitors.Primary);

    public AdminCameraControlsTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "overlays"));
        _store = new AdminViewModelTests.FakeStore(_dir);
        _pin = new AdminViewModelTests.FakePinService(_store);
        _camera.ControlsSnapshot = Snapshot();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AdminViewModel CreateViewModel() => new(_store, _pin, _camera, _print, _ui, null, _displays);

    private async Task<AdminViewModel> OpenAsync()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        return vm;
    }

    private static CameraControlInfo Info(CameraControlKind kind, double min, double max, double step, double def, double? value,
        bool autoSupported = false, bool? auto = null, bool writable = true) =>
        new(kind, true, writable, min, max, step, def, autoSupported, value, auto);

    /// <summary>Kamera mit Helligkeit (Automatik möglich, aus), Kontrast, Zoom, Belichtung (Automatik an) und Weißabgleich (Automatik an).</summary>
    private static CameraControlsSnapshot Snapshot(string device = "cam1", bool writable = true, bool powerline = true,
        double brightness = 60) =>
        new(device, writable,
        [
            Info(CameraControlKind.Zoom, 100, 400, 10, 100, 100),
            Info(CameraControlKind.Brightness, 0, 100, 1, 50, brightness, autoSupported: true, auto: false),
            Info(CameraControlKind.Contrast, 0, 100, 1, 50, 40),
            Info(CameraControlKind.Exposure, -10, 0, 1, -5, -3, autoSupported: true, auto: true),
            Info(CameraControlKind.WhiteBalance, 2800, 6500, 100, 4500, 5000, autoSupported: true, auto: true),
        ],
        powerline, powerline ? CameraPowerline.Hz50 : null);

    private static CameraControlValue Entry(CameraControlKind kind, double? value, bool? auto = null) =>
        new() { Kind = kind, Value = value, Auto = auto };

    private static CameraControlValue Find(IEnumerable<CameraControlValue> values, CameraControlKind kind) =>
        values.Single(v => v.Kind == kind);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition());
    }

    // ------------------------------------------------------------------ Anzeige

    [Fact]
    public async Task Open_ShowsDriverState_GroupedWithNames()
    {
        var vm = await OpenAsync();

        Assert.True(vm.ControlGroupsVisible);
        Assert.True(vm.ControlsEditable);
        Assert.Equal("", vm.ControlsHint);
        Assert.Equal(1, _camera.GetControlsCalls);
        Assert.Equal(["Zoom & Ausschnitt", "Bild", "Belichtung", "Fokus & Weißabgleich"], vm.CameraControlGroups.Select(g => g.Title));

        var brightness = vm.GetControl(CameraControlKind.Brightness)!;
        Assert.Equal("Helligkeit", brightness.Name);
        Assert.Equal(60, brightness.Value);
        Assert.Equal("60 %", brightness.DisplayValue);
        Assert.True(brightness.IsSliderEnabled);
        Assert.True(brightness.IsAutoEnabled);
        Assert.False(brightness.Auto);

        Assert.Equal("1,0×", vm.GetControl(CameraControlKind.Zoom)!.DisplayValue);
        Assert.Equal("5000 K", vm.GetControl(CameraControlKind.WhiteBalance)!.DisplayValue);

        // Automatik an: Schieberegler ausgegraut.
        var exposure = vm.GetControl(CameraControlKind.Exposure)!;
        Assert.True(exposure.Auto);
        Assert.False(exposure.IsSliderEnabled);

        // Kein Haken ohne Automatik.
        Assert.False(vm.GetControl(CameraControlKind.Contrast)!.IsAutoEnabled);
    }

    [Fact]
    public async Task UnsupportedControls_AreGrayedWithNote()
    {
        var vm = await OpenAsync();

        var pan = vm.GetControl(CameraControlKind.Pan)!;
        Assert.False(pan.Supported);
        Assert.True(pan.ShowNotSupported);
        Assert.False(pan.ShowControls);
        Assert.False(pan.IsSliderEnabled);
        Assert.Equal("Von dieser Kamera nicht unterstützt", AdminViewModel.ControlUnsupportedText);
        // Alle Regler erscheinen in den Gruppen.
        Assert.Equal(Enum.GetValues<CameraControlKind>().Length, vm.CameraControlGroups.Sum(g => g.Items.Count));
    }

    [Fact]
    public async Task OpenWithoutChanges_HasNoUnsavedChanges_AndNoLiveCalls()
    {
        var vm = await OpenAsync();

        Assert.False(vm.HasUnsavedChanges);
        Assert.Empty(_camera.LiveCalls);
        Assert.Empty(_camera.StartCalls);
    }

    [Fact]
    public async Task CameraNotRunning_ShowsHint_AndDoesNotRead()
    {
        _camera.SetStatus(CameraStatus.Stopped);

        var vm = await OpenAsync();

        Assert.Equal(AdminViewModel.ControlsNotRunningText, vm.ControlsHint);
        Assert.Equal("Die Kamera läuft nicht – die Regler sind nicht verfügbar.", vm.ControlsHint);
        Assert.False(vm.ControlGroupsVisible);
        Assert.False(vm.ControlsEditable);
        Assert.Equal(0, _camera.GetControlsCalls);
        Assert.False(vm.PowerlineEnabled);
    }

    [Fact]
    public async Task CameraUsedByOtherApp_ShowsHint_AndEverythingIsDisabled()
    {
        _camera.ControlsSnapshot = Snapshot(writable: false);

        var vm = await OpenAsync();

        Assert.Equal("Die Kamera wird gerade von einer anderen App genutzt – die Regler lassen sich nicht verstellen.", vm.ControlsHint);
        Assert.True(vm.ControlGroupsVisible);
        Assert.False(vm.ControlsEditable);
        Assert.All(vm.CameraControlItems, i => Assert.False(i.IsSliderEnabled || i.IsAutoEnabled));
        Assert.False(vm.PowerlineEnabled);

        vm.GetControl(CameraControlKind.Brightness)!.Value = 90;
        Assert.Empty(vm.Draft.Camera.ControlValues);
        Assert.Empty(_camera.LiveCalls);
    }

    [Fact]
    public async Task StatusChange_ToRunning_ReadsAgain()
    {
        var vm = await OpenAsync();
        Assert.Equal(60, vm.GetControl(CameraControlKind.Brightness)!.Value);

        _camera.SetStatus(CameraStatus.Starting);
        await WaitUntil(() => !vm.ControlGroupsVisible);
        Assert.Equal(AdminViewModel.ControlsNotRunningText, vm.ControlsHint);

        _camera.ControlsSnapshot = Snapshot(brightness: 33);
        _camera.SetStatus(CameraStatus.Running);
        await WaitUntil(() => vm.ControlGroupsVisible && vm.GetControl(CameraControlKind.Brightness)!.Value == 33);

        Assert.Equal(2, _camera.GetControlsCalls);
        Assert.Empty(_camera.LiveCalls);   // nichts verstellt: nichts zu senden
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Reconnect_AfterLiveChange_SendsTheDraftAgain()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;
        Assert.Single(_camera.LiveCalls);

        // Die Wiederverbindung bringt den Treiber auf seinen Stand zurück (der Dienst kennt nur das Gespeicherte).
        _camera.SetStatus(CameraStatus.Starting);
        await WaitUntil(() => !vm.ControlGroupsVisible);
        _camera.ControlsSnapshot = Snapshot(brightness: 60);
        _camera.SetStatus(CameraStatus.Running);

        await WaitUntil(() => _camera.LiveCalls.Count == 2);
        Assert.Equal(70, Find(_camera.LiveCalls[1].Values, CameraControlKind.Brightness).Value);
        Assert.Equal(70, vm.GetControl(CameraControlKind.Brightness)!.Value);
    }

    // ------------------------------------------------------------------ Verstellen

    [Fact]
    public async Task MovingSlider_SendsMergedValuesLive_AndStoresOnlyTheChangedControl()
    {
        var vm = await OpenAsync();

        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;

        var call = Assert.Single(_camera.LiveCalls);
        Assert.Equal(CameraPowerline.Hz50, call.Powerline);
        Assert.Equal(5, call.Values.Count);   // Entwurf über den Grundwerten: alle unterstützten Regler
        Assert.Equal(70, Find(call.Values, CameraControlKind.Brightness).Value);
        Assert.Equal(false, Find(call.Values, CameraControlKind.Brightness).Auto);
        Assert.Equal(40, Find(call.Values, CameraControlKind.Contrast).Value);
        Assert.Equal(true, Find(call.Values, CameraControlKind.Exposure).Auto);

        var stored = Assert.Single(vm.Draft.Camera.ControlValues);
        Assert.Equal(CameraControlKind.Brightness, stored.Kind);
        Assert.Equal(70, stored.Value);
        Assert.Equal(false, stored.Auto);
        Assert.Equal("cam1", vm.Draft.Camera.ControlsDeviceId);
        Assert.Equal("70 %", vm.GetControl(CameraControlKind.Brightness)!.DisplayValue);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Slider_SnapsToTheDriverStep_AndClamps()
    {
        var vm = await OpenAsync();

        var zoom = vm.GetControl(CameraControlKind.Zoom)!;
        zoom.Value = 251;
        Assert.Equal(250, zoom.Value);
        zoom.Value = 9999;
        Assert.Equal(400, zoom.Value);
        Assert.Equal(400, Find(vm.Draft.Camera.ControlValues, CameraControlKind.Zoom).Value);
        Assert.Equal("4,0×", zoom.DisplayValue);
    }

    [Fact]
    public async Task MovingTheSameControlTwice_ReplacesItsEntry()
    {
        var vm = await OpenAsync();
        var brightness = vm.GetControl(CameraControlKind.Brightness)!;

        brightness.Value = 70;
        brightness.Value = 80;
        vm.GetControl(CameraControlKind.Contrast)!.Value = 10;

        Assert.Equal(2, vm.Draft.Camera.ControlValues.Count);
        Assert.Equal(80, Find(vm.Draft.Camera.ControlValues, CameraControlKind.Brightness).Value);
        Assert.Equal(3, _camera.LiveCalls.Count);
    }

    [Fact]
    public async Task AutoCheckbox_WritesEntry_AndDisablesTheSlider()
    {
        var vm = await OpenAsync();
        var brightness = vm.GetControl(CameraControlKind.Brightness)!;

        brightness.Auto = true;

        Assert.False(brightness.IsSliderEnabled);
        var stored = Assert.Single(vm.Draft.Camera.ControlValues);
        Assert.Equal(true, stored.Auto);
        Assert.Equal(true, Find(_camera.LiveCalls.Single().Values, CameraControlKind.Brightness).Auto);

        // Automatik an: der Schieberegler wirkt nicht.
        brightness.Value = 10;
        Assert.Equal(60, brightness.Value);
        Assert.Single(_camera.LiveCalls);

        // Und wieder aus.
        brightness.Auto = false;
        Assert.True(brightness.IsSliderEnabled);
        Assert.Equal(false, Find(vm.Draft.Camera.ControlValues, CameraControlKind.Brightness).Auto);
    }

    [Fact]
    public async Task AutoCheckbox_WithoutAutomatic_DoesNothing()
    {
        var vm = await OpenAsync();

        vm.GetControl(CameraControlKind.Contrast)!.Auto = true;

        Assert.Empty(vm.Draft.Camera.ControlValues);
        Assert.Empty(_camera.LiveCalls);
    }

    [Fact]
    public async Task ControlChanges_NeverRestartTheCamera()
    {
        var vm = await OpenAsync();
        _ui.ConfirmResult = true;

        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;
        vm.GetControl(CameraControlKind.Exposure)!.Auto = false;
        vm.SelectedPowerline = vm.PowerlineOptions.First(o => o.Value == CameraPowerline.Hz60);
        vm.ControlProfileName = "Test";
        vm.SaveNewControlProfileCommand.Execute(null);
        vm.ResetControlsCommand.Execute(null);

        Assert.Empty(_camera.StartCalls);
        Assert.False(vm.CameraTouched);
    }

    // ------------------------------------------------------------------ Flimmerschutz

    [Fact]
    public async Task Powerline_ChangeIsSentLive_AndStoredInTheDraft()
    {
        var vm = await OpenAsync();

        Assert.Equal(["Nicht verändern", "Aus", "50 Hz", "60 Hz", "Automatisch"], vm.PowerlineOptions.Select(o => o.Display));
        Assert.Equal(CameraPowerline.Hz50, vm.SelectedPowerline!.Value);
        Assert.True(vm.PowerlineEnabled);

        vm.SelectedPowerline = vm.PowerlineOptions.First(o => o.Value == CameraPowerline.Hz60);

        Assert.Equal(CameraPowerline.Hz60, vm.Draft.Camera.Powerline);
        Assert.Equal(CameraPowerline.Hz60, Assert.Single(_camera.LiveCalls).Powerline);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Powerline_NotSupported_IsDisabledWithNote()
    {
        _camera.ControlsSnapshot = Snapshot(powerline: false);

        var vm = await OpenAsync();

        Assert.False(vm.PowerlineEnabled);
        Assert.True(vm.ShowPowerlineUnsupported);
        Assert.True(vm.ControlsEditable);
    }

    // ------------------------------------------------------------------ Zurücknehmen

    [Fact]
    public async Task Revert_WithoutLiveChange_SendsNothing()
    {
        var vm = await OpenAsync();

        await vm.RevertCameraControlsAsync();

        Assert.Empty(_camera.LiveCalls);
    }

    [Fact]
    public async Task Revert_SendsTheDriverBaseline_AndSavedPowerline_OnlyOnce()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;
        vm.SelectedPowerline = vm.PowerlineOptions.First(o => o.Value == CameraPowerline.Hz60);
        _camera.LiveCalls.Clear();

        await vm.RevertCameraControlsAsync();

        var call = Assert.Single(_camera.LiveCalls);
        Assert.Equal(CameraPowerline.Hz50, call.Powerline);   // gespeicherter Flimmerschutz
        Assert.Equal(5, call.Values.Count);
        Assert.Equal(60, Find(call.Values, CameraControlKind.Brightness).Value);
        Assert.Equal(false, Find(call.Values, CameraControlKind.Brightness).Auto);

        await vm.RevertCameraControlsAsync();
        Assert.Single(_camera.LiveCalls);
    }

    [Fact]
    public async Task Revert_UsesSavedValuesOfTheSameCameraOverTheDriverSnapshot()
    {
        _store.Current.Camera.ControlsDeviceId = "cam1";
        _store.Current.Camera.ControlValues = [Entry(CameraControlKind.Brightness, 80, false)];
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Contrast)!.Value = 10;
        _camera.LiveCalls.Clear();

        await vm.RevertCameraControlsAsync();

        var values = Assert.Single(_camera.LiveCalls).Values;
        Assert.Equal(80, Find(values, CameraControlKind.Brightness).Value);   // gespeichert
        Assert.Equal(40, Find(values, CameraControlKind.Contrast).Value);     // Treiber
    }

    [Fact]
    public async Task Revert_IgnoresSavedValuesOfAnotherCamera()
    {
        _store.Current.Camera.ControlsDeviceId = "cam2";
        _store.Current.Camera.ControlValues = [Entry(CameraControlKind.Brightness, 80, false)];
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Contrast)!.Value = 10;
        _camera.LiveCalls.Clear();

        await vm.RevertCameraControlsAsync();

        Assert.Equal(60, Find(Assert.Single(_camera.LiveCalls).Values, CameraControlKind.Brightness).Value);
    }

    [Fact]
    public async Task AfterApply_TheSavedStateIsTheNewBaseline_AndNothingIsPendingToRevert()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(1, _store.SaveCount);
        _camera.LiveCalls.Clear();
        await vm.RevertCameraControlsAsync();
        Assert.Empty(_camera.LiveCalls);   // seit dem Speichern nichts live verstellt

        vm.GetControl(CameraControlKind.Contrast)!.Value = 10;
        _camera.LiveCalls.Clear();
        await vm.RevertCameraControlsAsync();

        var values = Assert.Single(_camera.LiveCalls).Values;
        Assert.Equal(70, Find(values, CameraControlKind.Brightness).Value);   // gespeichert
        Assert.Equal(40, Find(values, CameraControlKind.Contrast).Value);
    }

    [Fact]
    public async Task AfterApplyOfAReset_TheBaselineIsTheDefaults_NotTheOldDriverState()
    {
        var vm = await OpenAsync();
        vm.ResetControlsCommand.Execute(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        vm.GetControl(CameraControlKind.Contrast)!.Value = 10;
        _camera.LiveCalls.Clear();
        await vm.RevertCameraControlsAsync();

        var values = Assert.Single(_camera.LiveCalls).Values;
        Assert.Equal(50, Find(values, CameraControlKind.Brightness).Value);   // Standard, nicht die 60 von vorher
        Assert.Equal(50, Find(values, CameraControlKind.Contrast).Value);
    }

    [Fact]
    public async Task RestoreAfterClose_WithoutCameraRestore_RevertsDirectly()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;
        _camera.LiveCalls.Clear();

        await vm.RestoreAfterCloseAsync(_store.Current.Clone(), restoreCamera: false);

        Assert.Single(_camera.LiveCalls);
        Assert.Empty(_camera.StartCalls);
    }

    [Fact]
    public async Task RestoreAfterClose_WithCameraRestore_RevertsOnlyAfterTheRestartCompleted()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;
        _camera.LiveCalls.Clear();
        var gate = new TaskCompletionSource();
        _camera.StartGate = _ => gate.Task;

        var task = vm.RestoreAfterCloseAsync(_store.Current.Clone(), restoreCamera: true);
        await WaitUntil(() => _camera.StartCalls.Count == 1);

        Assert.Empty(_camera.LiveCalls);   // wartet auf den Abschluss des Neustarts
        gate.SetResult();
        await task;
        // Das „Running“ der neuen Session sendet den verstellten Stand noch einmal; zuletzt kommen die Grundwerte zurück.
        Assert.Equal(60, Find(_camera.LiveCalls.Last().Values, CameraControlKind.Brightness).Value);
    }

    [Fact]
    public async Task RestoreAfterClose_WhenTheRestoreSwitchesCamera_RevertsBeforeTheRestart()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;
        _camera.LiveCalls.Clear();
        var gate = new TaskCompletionSource();
        _camera.StartGate = _ => gate.Task;
        var saved = _store.Current.Clone();
        saved.Camera.DeviceId = "cam2";

        var task = vm.RestoreAfterCloseAsync(saved, restoreCamera: true);
        await WaitUntil(() => _camera.StartCalls.Count == 1);

        Assert.Single(_camera.LiveCalls);   // die verstellte Kamera ist danach nicht mehr erreichbar
        gate.SetResult();
        await task;
        Assert.Single(_camera.LiveCalls);
    }

    // ------------------------------------------------------------------ Kamerawechsel

    [Fact]
    public async Task DeviceSwitch_DiscardsValues_KeepsProfiles_AndRevertsTheOldCamera()
    {
        _camera.ControlsSnapshotFor = id => Snapshot(id ?? "cam1");
        var vm = await OpenAsync();
        vm.ControlProfileName = "Hell";
        vm.SaveNewControlProfileCommand.Execute(null);
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;
        _camera.LiveCalls.Clear();

        await vm.SelectDeviceAsync(vm.Devices[1]);

        Assert.Empty(vm.Draft.Camera.ControlValues);
        Assert.Null(vm.Draft.Camera.ControlsDeviceId);
        Assert.Single(vm.Draft.Camera.ControlProfiles);
        Assert.Equal("cam2", _camera.StartCalls.Last().DeviceId);
        // Die bisherige Kamera bekam ihre Grundwerte zurück.
        Assert.Equal(60, Find(Assert.Single(_camera.LiveCalls).Values, CameraControlKind.Brightness).Value);
        // Die neue Kamera wurde gelesen (über den Statuswechsel, evtl. erst nach dem Dispatcher-Post); das Profil gehört zur ersten
        // und erscheint hier nicht.
        await WaitUntil(() => _camera.GetControlsCalls == 2);
        Assert.Empty(vm.ControlProfileOptions);

        await vm.SelectDeviceAsync(vm.Devices[0]);
        await WaitUntil(() => vm.ControlProfileOptions.Select(o => o.Name).SequenceEqual(["Hell"]));
    }

    [Fact]
    public async Task ResolutionChange_KeepsDraftValues_AndReadsAgain()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;

        vm.SelectedResolution = vm.Resolutions[1];
        await WaitUntil(() => _camera.StartCalls.Count == 1 && _camera.GetControlsCalls == 2);

        Assert.Equal(70, Find(vm.Draft.Camera.ControlValues, CameraControlKind.Brightness).Value);
        Assert.Equal("cam1", vm.Draft.Camera.ControlsDeviceId);
        // Nach dem Neustart gehen die Entwurfswerte erneut an die Kamera.
        await WaitUntil(() => _camera.LiveCalls.Count == 2);
        Assert.Equal(70, Find(_camera.LiveCalls[1].Values, CameraControlKind.Brightness).Value);
    }

    [Fact]
    public async Task Restart_ReadsTheControlsOnce_ViaTheStatusChange()
    {
        var vm = await OpenAsync();
        Assert.Equal(1, _camera.GetControlsCalls);

        await vm.RestartCameraAsync();
        await WaitUntil(() => _camera.GetControlsCalls >= 2);
        await Task.Delay(100);   // ein zweites Lesen hätte hier Zeit, sich zu zeigen

        Assert.Equal(2, _camera.GetControlsCalls);
    }

    [Fact]
    public async Task Import_WithAnotherPowerline_SendsItLive_AndRevertRestoresTheSavedOne()
    {
        var vm = await OpenAsync();
        _camera.LiveCalls.Clear();
        var imported = new AppSettings();
        imported.Camera.Powerline = CameraPowerline.Hz60;
        var file = Path.Combine(_dir, "import.json");
        File.WriteAllText(file, SettingsTransfer.Serialize(imported, null, "9.9.9", DateTimeOffset.UtcNow));
        _ui.ImportFileToPick = file;
        _ui.ConfirmResult = true;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal(CameraPowerline.Hz60, _camera.LiveCalls.Last().Powerline);
        Assert.Empty(_camera.StartCalls);

        _camera.LiveCalls.Clear();
        await vm.RevertCameraControlsAsync();
        Assert.Equal(CameraPowerline.Hz50, Assert.Single(_camera.LiveCalls).Powerline);
    }

    [Fact]
    public async Task Import_WhileTheCameraIsStopped_NeverSendsTheUnsavedPowerline_AfterClosingWithoutSaving()
    {
        var vm = await OpenAsync();
        _camera.SetStatus(CameraStatus.Stopped);
        var imported = new AppSettings();
        imported.Camera.Powerline = CameraPowerline.Hz60;
        var file = Path.Combine(_dir, "import.json");
        File.WriteAllText(file, SettingsTransfer.Serialize(imported, null, "9.9.9", DateTimeOffset.UtcNow));
        _ui.ImportFileToPick = file;
        _ui.ConfirmResult = true;
        await vm.ImportSettingsCommand.ExecuteAsync(null);
        _camera.LiveCalls.Clear();

        // „Schließen“ ohne Speichern: Die Kamera startet mit den gespeicherten Einstellungen neu und wird gelesen.
        await vm.RestoreAfterCloseAsync(_store.Current.Clone(), restoreCamera: true);
        await Task.Delay(100);

        Assert.DoesNotContain(_camera.LiveCalls, c => c.Powerline == CameraPowerline.Hz60);
    }

    [Fact]
    public async Task Import_WithTheSamePowerline_SendsNothing()
    {
        var vm = await OpenAsync();
        _camera.LiveCalls.Clear();
        var file = Path.Combine(_dir, "import.json");
        File.WriteAllText(file, SettingsTransfer.Serialize(new AppSettings(), null, "9.9.9", DateTimeOffset.UtcNow));
        _ui.ImportFileToPick = file;
        _ui.ConfirmResult = true;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Empty(_camera.LiveCalls);
    }

    [Fact]
    public async Task ControlDeviceIds_DifferingOnlyInCase_AreTheSameCamera()
    {
        _store.Current.Camera.ControlsDeviceId = "CAM1";
        _store.Current.Camera.ControlValues = [Entry(CameraControlKind.Brightness, 70)];
        var vm = await OpenAsync();   // läuft als "cam1"

        vm.GetControl(CameraControlKind.Contrast)!.Value = 10;

        // Die gespeicherten Werte der Kamera „CAM1“ bleiben im Entwurf (andere Schreibweise derselben Id).
        Assert.Equal(2, vm.Draft.Camera.ControlValues.Count);
        Assert.Contains(vm.Draft.Camera.ControlValues, v => v.Kind == CameraControlKind.Brightness);
    }

    // ------------------------------------------------------------------ Zurücksetzen

    [Fact]
    public async Task ResetAll_ClearsDraft_SendsDefaults_AndKeepsTheBaseline()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;
        vm.SelectedPowerline = vm.PowerlineOptions.First(o => o.Value == CameraPowerline.Hz60);
        _camera.LiveCalls.Clear();

        vm.ResetControlsCommand.Execute(null);

        Assert.Empty(vm.Draft.Camera.ControlValues);
        Assert.Null(vm.Draft.Camera.ControlsDeviceId);
        Assert.Equal(CameraPowerline.Hz50, vm.Draft.Camera.Powerline);

        var call = Assert.Single(_camera.LiveCalls);
        Assert.Equal(CameraPowerline.Hz50, call.Powerline);
        Assert.Equal(50, Find(call.Values, CameraControlKind.Brightness).Value);
        Assert.Equal(true, Find(call.Values, CameraControlKind.Brightness).Auto);   // Automatik, wo möglich
        Assert.Null(Find(call.Values, CameraControlKind.Contrast).Auto);            // keine Automatik vorhanden
        Assert.Equal(-5, Find(call.Values, CameraControlKind.Exposure).Value);
        Assert.Equal(100, Find(call.Values, CameraControlKind.Zoom).Value);

        // Die Anzeige folgt den Standardwerten.
        var brightness = vm.GetControl(CameraControlKind.Brightness)!;
        Assert.Equal(50, brightness.Value);
        Assert.True(brightness.Auto);
        Assert.False(brightness.IsSliderEnabled);

        // Schließen ohne Speichern nimmt auch das Zurücksetzen zurück.
        _camera.LiveCalls.Clear();
        await vm.RevertCameraControlsAsync();
        var reverted = Assert.Single(_camera.LiveCalls).Values;
        Assert.Equal(60, Find(reverted, CameraControlKind.Brightness).Value);
        Assert.Equal(false, Find(reverted, CameraControlKind.Brightness).Auto);
    }

    [Fact]
    public async Task ResetToDefaults_KeepsControlProfiles_AndSendsDefaults()
    {
        var vm = await OpenAsync();
        vm.ControlProfileName = "Hell";
        vm.SaveNewControlProfileCommand.Execute(null);
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;
        vm.CountdownSeconds = 9;
        _ui.ConfirmResult = true;
        _camera.LiveCalls.Clear();

        await vm.ResetToDefaultsAsync();

        Assert.Contains("Profile der Kamerasteuerung bleiben erhalten", _ui.LastConfirmMessage);
        Assert.Contains("außer der PIN", _ui.LastConfirmMessage);
        Assert.Equal(5, vm.Draft.Flow.CountdownSeconds);
        Assert.Equal("Hell", Assert.Single(vm.Draft.Camera.ControlProfiles).Name);
        Assert.Empty(vm.Draft.Camera.ControlValues);
        Assert.Null(vm.Draft.Camera.ControlsDeviceId);
        Assert.Equal(["Hell"], vm.ControlProfileOptions.Select(o => o.Name));
        Assert.Equal(50, Find(_camera.LiveCalls.Last().Values, CameraControlKind.Brightness).Value);
        Assert.Equal(50, vm.GetControl(CameraControlKind.Brightness)!.Value);
    }

    // ------------------------------------------------------------------ Profile

    [Fact]
    public async Task SaveNewProfile_CapturesAllSupportedControls()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 80;

        vm.ControlProfileName = "  Hell  ";
        vm.SaveNewControlProfileCommand.Execute(null);

        var profile = Assert.Single(vm.Draft.Camera.ControlProfiles);
        Assert.Equal("Hell", profile.Name);
        Assert.Equal("cam1", profile.DeviceId);
        Assert.Equal(5, profile.Values.Count);
        Assert.Equal(80, Find(profile.Values, CameraControlKind.Brightness).Value);
        Assert.Equal(40, Find(profile.Values, CameraControlKind.Contrast).Value);
        Assert.Equal(true, Find(profile.Values, CameraControlKind.Exposure).Auto);
        Assert.Equal("Hell", vm.SelectedControlProfile!.Name);
        Assert.Equal("", vm.ControlProfileModifiedText);
        Assert.True(vm.HasUnsavedChanges);
        // Das Profil enthält nur Regler, keinen Flimmerschutz.
        Assert.DoesNotContain(profile.Values, v => (int)v.Kind > (int)CameraControlKind.WhiteBalance);
    }

    [Fact]
    public async Task ProfileModified_ShowsWhenTheControlsDeviateFromTheSelectedProfile()
    {
        var vm = await OpenAsync();
        vm.ControlProfileName = "Hell";
        vm.SaveNewControlProfileCommand.Execute(null);
        Assert.Equal("", vm.ControlProfileModifiedText);

        vm.GetControl(CameraControlKind.Contrast)!.Value = 10;
        Assert.Equal("(geändert)", vm.ControlProfileModifiedText);

        vm.GetControl(CameraControlKind.Contrast)!.Value = 40;
        Assert.Equal("", vm.ControlProfileModifiedText);

        vm.GetControl(CameraControlKind.Exposure)!.Auto = false;
        Assert.Equal("(geändert)", vm.ControlProfileModifiedText);
    }

    [Fact]
    public async Task SelectingAProfile_LoadsItIntoTheControlsAndTheDraft_AndSendsItLive()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 80;
        vm.ControlProfileName = "Hell";
        vm.SaveNewControlProfileCommand.Execute(null);
        vm.GetControl(CameraControlKind.Brightness)!.Value = 20;
        vm.ControlProfileName = "Dunkel";
        vm.SaveNewControlProfileCommand.Execute(null);
        Assert.Equal("Dunkel", vm.SelectedControlProfile!.Name);
        _camera.LiveCalls.Clear();

        vm.SelectedControlProfile = vm.ControlProfileOptions.Single(o => o.Name == "Hell");

        Assert.Equal(80, vm.GetControl(CameraControlKind.Brightness)!.Value);
        Assert.Equal("Hell", vm.ControlProfileName);
        Assert.Equal(5, vm.Draft.Camera.ControlValues.Count);
        Assert.Equal(80, Find(vm.Draft.Camera.ControlValues, CameraControlKind.Brightness).Value);
        Assert.Equal("cam1", vm.Draft.Camera.ControlsDeviceId);
        Assert.Equal(80, Find(Assert.Single(_camera.LiveCalls).Values, CameraControlKind.Brightness).Value);
        Assert.Equal("", vm.ControlProfileModifiedText);
    }

    [Fact]
    public async Task OverwriteProfile_NeedsConfirmation()
    {
        var vm = await OpenAsync();
        vm.ControlProfileName = "Hell";
        vm.SaveNewControlProfileCommand.Execute(null);
        vm.GetControl(CameraControlKind.Brightness)!.Value = 90;
        Assert.Equal("(geändert)", vm.ControlProfileModifiedText);

        _ui.ConfirmResult = false;
        vm.OverwriteControlProfileCommand.Execute(null);
        Assert.Equal(60, Find(vm.Draft.Camera.ControlProfiles.Single().Values, CameraControlKind.Brightness).Value);
        Assert.Equal(1, _ui.ConfirmCalls);

        _ui.ConfirmResult = true;
        vm.OverwriteControlProfileCommand.Execute(null);
        Assert.Equal(90, Find(vm.Draft.Camera.ControlProfiles.Single().Values, CameraControlKind.Brightness).Value);
        Assert.Equal("", vm.ControlProfileModifiedText);
        Assert.Single(vm.Draft.Camera.ControlProfiles);
    }

    [Fact]
    public async Task RenameProfile_UsesTheNameFromTheTextBox()
    {
        var vm = await OpenAsync();
        vm.ControlProfileName = "Hell";
        vm.SaveNewControlProfileCommand.Execute(null);

        vm.ControlProfileName = "Heller";
        Assert.True(vm.RenameControlProfileCommand.CanExecute(null));
        vm.RenameControlProfileCommand.Execute(null);

        Assert.Equal("Heller", Assert.Single(vm.Draft.Camera.ControlProfiles).Name);
        Assert.Equal(["Heller"], vm.ControlProfileOptions.Select(o => o.Name));
        Assert.Equal("Heller", vm.SelectedControlProfile!.Name);

        // Nur die Schreibweise ändern ist erlaubt.
        vm.ControlProfileName = "HELLER";
        Assert.True(vm.RenameControlProfileCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteProfile_NeedsConfirmation()
    {
        var vm = await OpenAsync();
        vm.ControlProfileName = "Hell";
        vm.SaveNewControlProfileCommand.Execute(null);

        _ui.ConfirmResult = false;
        vm.DeleteControlProfileCommand.Execute(null);
        Assert.Single(vm.Draft.Camera.ControlProfiles);

        _ui.ConfirmResult = true;
        vm.DeleteControlProfileCommand.Execute(null);
        Assert.Empty(vm.Draft.Camera.ControlProfiles);
        Assert.Empty(vm.ControlProfileOptions);
        Assert.Null(vm.SelectedControlProfile);
        Assert.False(vm.DeleteControlProfileCommand.CanExecute(null));
    }

    [Fact]
    public async Task ProfileNames_MustBeUniquePerCamera_IgnoringCase()
    {
        var vm = await OpenAsync();
        vm.ControlProfileName = "Hell";
        vm.SaveNewControlProfileCommand.Execute(null);

        vm.ControlProfileName = "hell ";
        Assert.False(vm.SaveNewControlProfileCommand.CanExecute(null));
        // Gleicher Name wie das gewählte Profil: keine Warnung (Umbenennen der Schreibweise bleibt möglich).
        Assert.Equal("", vm.ControlProfileHint);

        vm.ControlProfileName = "Dunkel";
        vm.SaveNewControlProfileCommand.Execute(null);
        vm.SelectedControlProfile = vm.ControlProfileOptions.Single(o => o.Name == "Hell");
        vm.ControlProfileName = "DUNKEL";
        Assert.False(vm.RenameControlProfileCommand.CanExecute(null));
        Assert.False(vm.SaveNewControlProfileCommand.CanExecute(null));
        Assert.Equal(AdminViewModel.ControlProfileDuplicateText, vm.ControlProfileHint);
        Assert.True(vm.HasControlProfileHint);
        Assert.Equal(2, vm.Draft.Camera.ControlProfiles.Count);
    }

    [Fact]
    public async Task EmptyProfileName_BlocksSaving()
    {
        var vm = await OpenAsync();

        vm.ControlProfileName = "   ";

        Assert.False(vm.SaveNewControlProfileCommand.CanExecute(null));
    }

    [Fact]
    public async Task ProfileLimit_PerCamera_BlocksTheEleventhProfile()
    {
        var vm = await OpenAsync();
        for (var i = 1; i <= SettingsLimits.CameraProfilesMax; i++)
        {
            vm.ControlProfileName = "P" + i;
            Assert.True(vm.SaveNewControlProfileCommand.CanExecute(null));
            vm.SaveNewControlProfileCommand.Execute(null);
        }

        vm.ControlProfileName = "Zu viel";

        Assert.False(vm.SaveNewControlProfileCommand.CanExecute(null));
        Assert.Contains("höchstens 10 Profile", vm.ControlProfileHint);
        Assert.Equal(SettingsLimits.CameraProfilesMax, vm.Draft.Camera.ControlProfiles.Count);
    }

    [Fact]
    public async Task ProfileLimit_CountsOnlyTheActiveCamera()
    {
        for (var i = 1; i <= SettingsLimits.CameraProfilesMax; i++)
            _store.Current.Camera.ControlProfiles.Add(new CameraControlProfile { Name = "X" + i, DeviceId = "cam2", Values = [Entry(CameraControlKind.Zoom, 100)] });
        var vm = await OpenAsync();

        vm.ControlProfileName = "Neu";

        Assert.True(vm.SaveNewControlProfileCommand.CanExecute(null));
    }

    [Fact]
    public async Task Profiles_OfOtherCameras_AreHidden_DeviceIdIgnoresCase()
    {
        _store.Current.Camera.ControlProfiles =
        [
            new CameraControlProfile { Name = "Andere", DeviceId = "cam2", Values = [Entry(CameraControlKind.Zoom, 100)] },
            new CameraControlProfile { Name = "Meine", DeviceId = "CAM1", Values = [Entry(CameraControlKind.Zoom, 100)] },
        ];

        var vm = await OpenAsync();

        Assert.Equal(["Meine"], vm.ControlProfileOptions.Select(o => o.Name));
        Assert.Equal(2, vm.Draft.Camera.ControlProfiles.Count);   // die andere bleibt im Entwurf
    }

    [Fact]
    public async Task ProfileChanges_AreOnlyDraft_UntilSaved()
    {
        var vm = await OpenAsync();
        vm.ControlProfileName = "Hell";
        vm.SaveNewControlProfileCommand.Execute(null);

        Assert.Empty(_store.Current.Camera.ControlProfiles);
        Assert.True(vm.HasUnsavedChanges);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Hell", Assert.Single(_store.Current.Camera.ControlProfiles).Name);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Save_StoresOnlyTheChangedControls()
    {
        var vm = await OpenAsync();
        vm.GetControl(CameraControlKind.Brightness)!.Value = 70;
        vm.GetControl(CameraControlKind.Exposure)!.Auto = false;

        await vm.ApplyCommand.ExecuteAsync(null);

        var camera = _store.Current.Camera;
        Assert.Equal("cam1", camera.ControlsDeviceId);
        Assert.Equal([CameraControlKind.Brightness, CameraControlKind.Exposure], camera.ControlValues.Select(v => v.Kind));
        Assert.Empty(_camera.StartCalls);
    }

    // ------------------------------------------------------------------ Ansicht

    [StaFact]
    public void Window_BuildsTheControlCard_WithSlidersBoundToTheItems_AndRendersOptionally()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var vm = CreateViewModel();
        vm.InitializeAsync().GetAwaiter().GetResult();
        vm.ControlProfileName = "Hell";
        vm.SaveNewControlProfileCommand.Execute(null);

        var window = new AdminWindow(vm) { ShowInTaskbar = false, Width = 1100, Height = 1500 };
        var tabs = FindLogical<TabControl>(window)!;
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Kamera");
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1100, 1500));
        root.Arrange(new Rect(0, 0, 1100, 1500));
        root.UpdateLayout();

        var sliders = FindVisual<Slider>(root).Where(s => s.Visibility == Visibility.Visible).ToList();
        Assert.Equal(5, sliders.Count);   // nur unterstützte Regler zeigen einen Schieberegler
        var brightness = sliders.Single(s => (s.DataContext as CameraControlItem)?.Kind == CameraControlKind.Brightness);
        Assert.Equal(0, brightness.Minimum);
        Assert.Equal(100, brightness.Maximum);
        Assert.Equal(60, brightness.Value);
        Assert.True(brightness.IsEnabled);
        Assert.False(sliders.Single(s => (s.DataContext as CameraControlItem)?.Kind == CameraControlKind.Exposure).IsEnabled);

        // Das bloße Binden verändert den Entwurf nicht.
        Assert.Empty(vm.Draft.Camera.ControlValues);
        Assert.Empty(_camera.LiveCalls);

        // Bedienung über den Schieberegler erreicht den Entwurf.
        brightness.Value = 75;
        Assert.Equal(75, vm.GetControl(CameraControlKind.Brightness)!.Value);
        Assert.Equal(75, Find(vm.Draft.Camera.ControlValues, CameraControlKind.Brightness).Value);
        Assert.Single(vm.Draft.Camera.ControlValues);

        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            var bitmap = new RenderTargetBitmap(1100, 1500, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen())
                dc.DrawRectangle((Brush)window.Resources["BgBrush"], null, new Rect(0, 0, 1100, 1500));
            bitmap.Render(background);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(dir, "camera-controls.png"));
            encoder.Save(stream);
        }
    }

    private static T? FindLogical<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T self)
            return self;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (FindLogical<T>(child) is { } found)
                return found;
        }
        return null;
    }

    private static IEnumerable<T> FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T self)
            yield return self;
        if (parent is not Visual)
            yield break;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            foreach (var found in FindVisual<T>(VisualTreeHelper.GetChild(parent, i)))
                yield return found;
        }
    }
}
