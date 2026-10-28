using System.IO;
using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Fotobox.ViewModels.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Admin „Einstellungen teilen“: Export des Entwurfs, Import in den Entwurf, importierte PIN, Overlay-Rückfall, Fehlerpfade.</summary>
[Collection(WpfCollection.Name)]
public sealed class AdminSettingsTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-transfer-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AdminViewModelTests.FakeStore _store;
    private readonly AdminViewModelTests.FakePinService _pin;
    private readonly AdminViewModelTests.FakeCamera _camera = new();
    private readonly AdminViewModelTests.FakePrint _print = new();
    private readonly AdminViewModelTests.FakeUi _ui = new();
    private readonly AdminViewModelTests.FakeDisplays _displays = new(AdminViewModelTests.AdminMonitors.Primary);

    public AdminSettingsTransferTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "overlays"));
        _store = new AdminViewModelTests.FakeStore(_dir);
        _pin = new AdminViewModelTests.FakePinService(_store);
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

    private async Task<AdminViewModel> CreateInitializedAsync()
    {
        var vm = new AdminViewModel(_store, _pin, _camera, _print, _ui, null, _displays);
        await vm.InitializeAsync();
        return vm;
    }

    private string TempFile(string name) => Path.Combine(_dir, name);

    private static SecuritySettings FilePin() => new()
    {
        PinHash = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
        PinSalt = Convert.ToBase64String(Enumerable.Range(50, 16).Select(i => (byte)i).ToArray()),
        PinIterations = 123_456,
    };

    private string WriteImportFile(AppSettings settings, SecuritySettings? pin = null, string fileName = "import.json")
    {
        var path = TempFile(fileName);
        File.WriteAllText(path, SettingsTransfer.Serialize(settings, pin, "9.9.9", DateTimeOffset.UtcNow));
        return path;
    }

    private void SetStorePin()
    {
        _store.Current.Security.PinHash = "hash:4711";
        _store.Current.Security.PinSalt = "salt";
        _store.Current.Security.PinIterations = 123_456;
    }

    /// <summary>Geräte-, Pfad- und Mikrofonwerte des Zielrechners (in den Speicher gesetzt, bevor die Ansicht öffnet).</summary>
    private void SetTargetDevices()
    {
        _store.Current.Camera.DeviceId = "cam2";
        _store.Current.Camera.ResolutionWidth = 1280;
        _store.Current.Camera.ResolutionHeight = 720;
        _store.Current.Storage.PhotoFolder = Path.Combine(_dir, "fotos");
        _store.Current.Video.MicrophoneId = "mic-b";
        _store.Current.Video.MicrophoneName = "Testmikrofon B";
    }

    private static AppSettings DifferentSettings()
    {
        var s = new AppSettings();
        s.Flow.CountdownSeconds = 11;
        s.Layout.Kind = LayoutKind.Strip;
        s.Layout.StripStyle = StripStyle.Single;
        s.Layout.CaptionText = "Hochzeit";
        s.Storage.FilePrefix = "Feier";
        // Gerätefelder der Quelle: dürfen nie ankommen.
        s.Camera.DeviceId = "cam1";
        s.Camera.ResolutionWidth = 640;
        s.Camera.ResolutionHeight = 480;
        s.Storage.PhotoFolder = Path.Combine(Path.GetTempPath(), "quelle");
        s.Video.MicrophoneId = "mic-a";
        return s;
    }

    // ------------------------------------------------------------------ Export

    [Fact]
    public async Task Export_WithoutPinTick_WritesFileWithoutDeviceFieldsAndSecurity()
    {
        SetStorePin();
        SetTargetDevices();
        var vm = await CreateInitializedAsync();
        var path = TempFile("export.json");
        _ui.ExportFileToPick = path;

        await vm.ExportSettingsCommand.ExecuteAsync(null);

        Assert.True(File.Exists(path));
        Assert.StartsWith("FotoboxPlus-Einstellungen-", _ui.LastSuggestedExportName);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("PinHash", json);
        Assert.DoesNotContain("hash:4711", json);
        Assert.DoesNotContain("cam2", json);
        Assert.DoesNotContain("mic-b", json);
        Assert.DoesNotContain("fotos", json);
        var parsed = SettingsTransfer.Parse(json);
        Assert.Null(parsed.Pin);
        Assert.Null(parsed.Sections.Camera.DeviceId);

        Assert.Equal("Einstellungen exportiert", _ui.LastInfo?.Title);
        Assert.Contains(path, _ui.LastInfo!.Value.Message);
        Assert.Contains("keine PIN", _ui.LastInfo.Value.Message);
        Assert.False(vm.IsTransferring);
    }

    [Fact]
    public async Task Export_ContainsTheUnsavedDraft()
    {
        var vm = await CreateInitializedAsync();
        vm.CountdownSeconds = 7;
        var path = TempFile("draft.json");
        _ui.ExportFileToPick = path;

        await vm.ExportSettingsCommand.ExecuteAsync(null);

        Assert.Equal(5, _store.Current.Flow.CountdownSeconds);
        Assert.Equal(7, SettingsTransfer.Parse(File.ReadAllText(path)).Sections.Flow.CountdownSeconds);
    }

    [Fact]
    public async Task Export_WithPinTickAndPin_ContainsTheStoredPin()
    {
        SetStorePin();
        var vm = await CreateInitializedAsync();
        vm.ExportIncludePin = true;
        var path = TempFile("pin.json");
        _ui.ExportFileToPick = path;

        await vm.ExportSettingsCommand.ExecuteAsync(null);

        var json = File.ReadAllText(path);
        Assert.Contains("hash:4711", json);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("Settings").TryGetProperty("Security", out _));
        Assert.Contains("enthält die Admin-PIN", _ui.LastInfo!.Value.Message);
    }

    [Fact]
    public async Task Export_WithPinTickButNoPin_ContainsNoSecurity_AndSaysSo()
    {
        var vm = await CreateInitializedAsync();
        vm.ExportIncludePin = true;
        var path = TempFile("nopin.json");
        _ui.ExportFileToPick = path;

        await vm.ExportSettingsCommand.ExecuteAsync(null);

        Assert.DoesNotContain("PinHash", File.ReadAllText(path));
        Assert.Contains("keine PIN festgelegt", _ui.LastInfo!.Value.Message);
    }

    [Fact]
    public async Task Export_PinIsTakenFromTheStore_NotFromTheDraft()
    {
        SetStorePin();
        var vm = await CreateInitializedAsync();
        vm.Draft.Security.PinHash = "draft-hash";
        vm.ExportIncludePin = true;
        var path = TempFile("store-pin.json");
        _ui.ExportFileToPick = path;

        await vm.ExportSettingsCommand.ExecuteAsync(null);

        var json = File.ReadAllText(path);
        Assert.Contains("hash:4711", json);
        Assert.DoesNotContain("draft-hash", json);
    }

    [Fact]
    public async Task Export_Cancelled_WritesNothingAndShowsNothing()
    {
        var vm = await CreateInitializedAsync();
        _ui.ExportFileToPick = null;

        await vm.ExportSettingsCommand.ExecuteAsync(null);

        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
        Assert.Empty(_ui.InfoCalls);
        Assert.False(vm.IsTransferring);
    }

    [Fact]
    public async Task Export_ToUnwritablePath_ShowsError()
    {
        var vm = await CreateInitializedAsync();
        _ui.ExportFileToPick = Path.Combine(_dir, "gibt-es-nicht", "unter", "export.json");

        await vm.ExportSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Export fehlgeschlagen", _ui.LastInfo?.Title);
        Assert.False(vm.IsTransferring);
    }

    // ------------------------------------------------------------------ Import

    [Fact]
    public async Task Import_ReplacesSectionsInDraft_KeepsDevices_DoesNotRestartCamera()
    {
        SetTargetDevices();
        var vm = await CreateInitializedAsync();
        var starts = _camera.StartCalls.Count;
        _ui.ImportFileToPick = WriteImportFile(DifferentSettings());
        _ui.ConfirmResult = true;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        // Werte der Datei.
        Assert.Equal(11, vm.Draft.Flow.CountdownSeconds);
        Assert.Equal(11, vm.CountdownSeconds);
        Assert.Equal(LayoutKind.Strip, vm.Draft.Layout.Kind);
        Assert.Equal("Hochzeit", vm.Draft.Layout.CaptionText);
        Assert.Equal("Feier", vm.Draft.Storage.FilePrefix);
        // Fehlende Werte der Datei = Standard.
        Assert.True(vm.Draft.Camera.MirrorPreview);
        // Geräte und Ordner des Zielrechners bleiben.
        Assert.Equal("cam2", vm.Draft.Camera.DeviceId);
        Assert.Equal(1280, vm.Draft.Camera.ResolutionWidth);
        Assert.Equal(720, vm.Draft.Camera.ResolutionHeight);
        Assert.Equal(Path.Combine(_dir, "fotos"), vm.Draft.Storage.PhotoFolder);
        Assert.Equal("mic-b", vm.Draft.Video.MicrophoneId);
        Assert.Equal("cam2", vm.SelectedDevice?.Id);
        // Kamera nicht neu gestartet.
        Assert.Equal(starts, _camera.StartCalls.Count);
        Assert.False(vm.CameraTouched);

        Assert.Equal("Einstellungen importieren", _ui.ConfirmLog[0].Title);
        Assert.Equal("Einstellungen importiert", _ui.LastInfo?.Title);
        Assert.Contains("Übernehmen", _ui.LastInfo!.Value.Message);
        Assert.False(vm.IsTransferring);
    }

    [Fact]
    public async Task Import_IsDirtyUntilSaved_StoreUnchangedBeforeSave_SavePersists()
    {
        SetTargetDevices();
        var vm = await CreateInitializedAsync();
        Assert.False(vm.HasUnsavedChanges);
        _ui.ImportFileToPick = WriteImportFile(DifferentSettings());
        _ui.ConfirmResult = true;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(5, _store.Current.Flow.CountdownSeconds);
        Assert.Equal(0, _store.SaveCount);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, _store.SaveCount);
        Assert.Equal(11, _store.Current.Flow.CountdownSeconds);
        Assert.Equal(LayoutKind.Strip, _store.Current.Layout.Kind);
        Assert.Equal("cam2", _store.Current.Camera.DeviceId);
        Assert.Equal(Path.Combine(_dir, "fotos"), _store.Current.Storage.PhotoFolder);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Import_ThenClose_AsksToDiscard()
    {
        var vm = await CreateInitializedAsync();
        _ui.ImportFileToPick = WriteImportFile(DifferentSettings());
        _ui.ConfirmResult = true;
        await vm.ImportSettingsCommand.ExecuteAsync(null);
        var before = _ui.ConfirmCalls;

        _ui.ConfirmResult = false;
        Assert.False(vm.ConfirmDiscardChanges());

        Assert.Equal(before + 1, _ui.ConfirmCalls);
        Assert.Equal("Ungespeicherte Änderungen", _ui.LastConfirmTitle);
    }

    [Fact]
    public async Task Import_DeclinedConfirmation_LeavesDraftUnchanged()
    {
        var vm = await CreateInitializedAsync();
        var before = JsonSerializer.Serialize(vm.Draft, AppSettings.JsonOptions);
        _ui.ImportFileToPick = WriteImportFile(DifferentSettings());
        _ui.ConfirmResult = false;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal(before, JsonSerializer.Serialize(vm.Draft, AppSettings.JsonOptions));
        Assert.False(vm.HasUnsavedChanges);
        Assert.Empty(_ui.InfoCalls);
    }

    [Fact]
    public async Task Import_Cancelled_DoesNothing()
    {
        var vm = await CreateInitializedAsync();
        _ui.ImportFileToPick = null;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal(0, _ui.ConfirmCalls);
        Assert.Empty(_ui.InfoCalls);
        Assert.False(vm.HasUnsavedChanges);
    }

    private static void SetIsSaving(AdminViewModel vm, bool value) =>
        typeof(AdminViewModel).GetProperty(nameof(AdminViewModel.IsSaving))!.SetValue(vm, value);

    [Fact]
    public async Task Import_Running_DisablesSaveAndApply_AndSaveCoreRefuses()
    {
        var vm = await CreateInitializedAsync();
        _ui.ImportFileToPick = WriteImportFile(DifferentSettings());
        bool? applyDuringPick = null, saveDuringPick = null, transferringDuringPick = null;
        _ui.OnPickImportFile = () =>
        {
            transferringDuringPick = vm.IsTransferring;
            applyDuringPick = vm.ApplyCommand.CanExecute(null);
            saveDuringPick = vm.SaveCommand.CanExecute(null);
        };
        bool? applyDuringConfirm = null;
        var storeWritesDuringConfirm = -1;
        _ui.OnConfirm = () =>
        {
            applyDuringConfirm = vm.ApplyCommand.CanExecute(null);
            // Ein erzwungener Aufruf (an CanExecute vorbei) darf nicht speichern.
            vm.ApplyCommand.Execute(null);
            storeWritesDuringConfirm = _store.SaveCount;
        };
        var changedNotifications = 0;
        vm.ApplyCommand.CanExecuteChanged += (_, _) => changedNotifications++;

        Assert.True(vm.ApplyCommand.CanExecute(null));
        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.True(transferringDuringPick);
        Assert.False(applyDuringPick);
        Assert.False(saveDuringPick);
        Assert.False(applyDuringConfirm);
        Assert.Equal(0, storeWritesDuringConfirm);
        Assert.True(changedNotifications >= 2);   // gesperrt und wieder frei
        Assert.True(vm.ApplyCommand.CanExecute(null));
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_Running_DisablesSaveAndApply()
    {
        var vm = await CreateInitializedAsync();
        _ui.ExportFileToPick = TempFile("export-busy.json");
        bool? apply = null;
        // PickExportFile hat keinen Hook: Der Zustand wird beim Setzen von IsTransferring geprüft.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AdminViewModel.IsTransferring) && vm.IsTransferring)
                apply = vm.ApplyCommand.CanExecute(null);
        };

        await vm.ExportSettingsCommand.ExecuteAsync(null);

        Assert.False(apply);
        Assert.True(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Import_AbortsWhenASaveStartedMeanwhile_AfterTheConfirmation()
    {
        SetStorePin();
        var vm = await CreateInitializedAsync();
        var before = JsonSerializer.Serialize(vm.Draft, AppSettings.JsonOptions);
        _ui.ImportFileToPick = WriteImportFile(DifferentSettings(), FilePin());
        _ui.ConfirmResults.Enqueue(true);   // Import
        _ui.ConfirmResults.Enqueue(true);   // PIN ersetzen
        var confirms = 0;
        _ui.OnConfirm = () =>
        {
            if (++confirms == 2)
                SetIsSaving(vm, true);   // während der PIN-Rückfrage beginnt ein Speichern
        };

        await vm.ImportSettingsCommand.ExecuteAsync(null);
        SetIsSaving(vm, false);

        Assert.Equal(before, JsonSerializer.Serialize(vm.Draft, AppSettings.JsonOptions));
        Assert.Equal("Import abgebrochen", _ui.LastInfo?.Title);
        // Keine PIN vorgemerkt: Ein Speichern schreibt die alte PIN.
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("hash:4711", _store.Current.Security.PinHash);
    }

    [Fact]
    public async Task Import_AbortsWhenASaveStartedMeanwhile_AfterTheRead()
    {
        var vm = await CreateInitializedAsync();
        var before = JsonSerializer.Serialize(vm.Draft, AppSettings.JsonOptions);
        _ui.ImportFileToPick = WriteImportFile(DifferentSettings());
        _ui.OnPickImportFile = () => SetIsSaving(vm, true);

        await vm.ImportSettingsCommand.ExecuteAsync(null);
        SetIsSaving(vm, false);

        Assert.Equal(before, JsonSerializer.Serialize(vm.Draft, AppSettings.JsonOptions));
        Assert.Equal(0, _ui.ConfirmCalls);
        Assert.Equal("Import abgebrochen", _ui.LastInfo?.Title);
    }

    [Fact]
    public async Task Import_InvalidFile_ShowsError_DraftUnchanged_NoConfirmation()
    {
        var vm = await CreateInitializedAsync();
        var before = JsonSerializer.Serialize(vm.Draft, AppSettings.JsonOptions);
        var path = TempFile("kaputt.json");
        File.WriteAllText(path, "das ist kein json {");
        _ui.ImportFileToPick = path;
        _ui.ConfirmResult = true;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Import fehlgeschlagen", _ui.LastInfo?.Title);
        Assert.False(string.IsNullOrWhiteSpace(_ui.LastInfo!.Value.Message));
        Assert.Equal(0, _ui.ConfirmCalls);
        Assert.Equal(before, JsonSerializer.Serialize(vm.Draft, AppSettings.JsonOptions));
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.IsTransferring);
    }

    [Fact]
    public async Task Import_MissingFile_ShowsError()
    {
        var vm = await CreateInitializedAsync();
        _ui.ImportFileToPick = TempFile("gibt-es-nicht.json");
        _ui.ConfirmResult = true;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Import fehlgeschlagen", _ui.LastInfo?.Title);
        Assert.False(vm.HasUnsavedChanges);
    }

    // ------------------------------------------------------------------ Overlay

    [Fact]
    public async Task Import_OverlayMissingAtTarget_FallsBackAndNotes()
    {
        var vm = await CreateInitializedAsync();
        var source = new AppSettings();
        source.Layout.Kind = LayoutKind.Overlay;
        source.Layout.OverlayFileName = "rahmen.png";
        source.Layout.GuestChoices = [GuestLayout.Overlay, GuestLayout.Single];
        _ui.ImportFileToPick = WriteImportFile(source);
        _ui.ConfirmResult = true;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.NotEqual(LayoutKind.Overlay, vm.Draft.Layout.Kind);
        Assert.Null(vm.Draft.Layout.OverlayFileName);
        Assert.False(vm.HasOverlay);
        Assert.DoesNotContain(GuestLayout.Overlay, vm.Draft.Layout.GuestChoices);
        Assert.Contains(SettingsTransfer.OverlayMissingNote, _ui.LastInfo!.Value.Message);
    }

    [Fact]
    public async Task Import_OverlayExistsAtTarget_IsKept()
    {
        File.WriteAllBytes(Path.Combine(_store.OverlayDirectory, "rahmen.png"), [1]);
        var vm = await CreateInitializedAsync();
        var source = new AppSettings();
        source.Layout.Kind = LayoutKind.Overlay;
        source.Layout.OverlayFileName = "rahmen.png";
        _ui.ImportFileToPick = WriteImportFile(source);
        _ui.ConfirmResult = true;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal(LayoutKind.Overlay, vm.Draft.Layout.Kind);
        Assert.Equal("rahmen.png", vm.Draft.Layout.OverlayFileName);
        Assert.DoesNotContain(SettingsTransfer.OverlayMissingNote, _ui.LastInfo!.Value.Message);
    }

    // ------------------------------------------------------------------ PIN

    [Fact]
    public async Task Import_PinConfirmed_IsAppliedOnSave()
    {
        SetStorePin();
        var vm = await CreateInitializedAsync();
        var pin = FilePin();
        _ui.ImportFileToPick = WriteImportFile(new AppSettings(), pin);
        _ui.ConfirmResults.Enqueue(true);   // Import
        _ui.ConfirmResults.Enqueue(true);   // PIN ersetzen

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Admin-PIN importieren", _ui.ConfirmLog[1].Title);
        Assert.Contains("Soll sie die aktuelle PIN ersetzen?", _ui.ConfirmLog[1].Message);
        Assert.Equal("hash:4711", _store.Current.Security.PinHash);   // noch nicht gespeichert
        Assert.True(vm.HasUnsavedChanges);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(pin.PinHash, _store.Current.Security.PinHash);
        Assert.Equal(pin.PinSalt, _store.Current.Security.PinSalt);
        Assert.Equal(pin.PinIterations, _store.Current.Security.PinIterations);
        Assert.False(vm.HasUnsavedChanges);

        // Ein zweites Speichern behält die neue PIN.
        vm.CountdownSeconds = 8;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(pin.PinHash, _store.Current.Security.PinHash);
    }

    [Fact]
    public async Task Import_PinDeclined_KeepsCurrentPin_AndIsNotDirtyByItself()
    {
        SetStorePin();
        var vm = await CreateInitializedAsync();
        _ui.ImportFileToPick = WriteImportFile(new AppSettings(), FilePin());
        _ui.ConfirmResults.Enqueue(true);
        _ui.ConfirmResults.Enqueue(false);

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.False(vm.HasUnsavedChanges);
        Assert.Contains("nicht übernommen", _ui.LastInfo!.Value.Message);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("hash:4711", _store.Current.Security.PinHash);
    }

    [Fact]
    public async Task SecondImport_WithDeclinedPin_DropsPinPendingFromFirstImport()
    {
        SetStorePin();
        var vm = await CreateInitializedAsync();
        _ui.ImportFileToPick = WriteImportFile(new AppSettings(), FilePin());
        _ui.ConfirmResults.Enqueue(true);   // Import 1
        _ui.ConfirmResults.Enqueue(true);   // PIN 1 ersetzen
        await vm.ImportSettingsCommand.ExecuteAsync(null);

        _ui.ConfirmResults.Enqueue(true);   // Import 2
        _ui.ConfirmResults.Enqueue(false);  // PIN 2 ablehnen
        await vm.ImportSettingsCommand.ExecuteAsync(null);

        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("hash:4711", _store.Current.Security.PinHash);
    }

    [Fact]
    public async Task Import_SamePinAsCurrent_DoesNotAsk()
    {
        var pin = FilePin();
        _store.Current.Security.PinHash = pin.PinHash;
        _store.Current.Security.PinSalt = pin.PinSalt;
        _store.Current.Security.PinIterations = pin.PinIterations;
        var vm = await CreateInitializedAsync();
        _ui.ImportFileToPick = WriteImportFile(new AppSettings(), pin);
        _ui.ConfirmResult = true;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal(1, _ui.ConfirmCalls);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Import_PendingPinAlone_MakesHasUnsavedChangesTrue_AndCloseAsks()
    {
        SetStorePin();
        var vm = await CreateInitializedAsync();
        // Datei mit genau den aktuellen Einstellungen: Nur die PIN unterscheidet sich.
        var path = WriteImportFile(_store.Current.Clone(), FilePin());
        _ui.ImportFileToPick = path;
        _ui.ConfirmResults.Enqueue(true);
        _ui.ConfirmResults.Enqueue(true);

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.True(vm.HasUnsavedChanges);
        _ui.ConfirmResult = false;
        Assert.False(vm.ConfirmDiscardChanges());
    }

    [Fact]
    public async Task Import_TypedPinWinsOverImportedPin()
    {
        SetStorePin();
        var vm = await CreateInitializedAsync();
        _ui.ImportFileToPick = WriteImportFile(new AppSettings(), FilePin());
        _ui.ConfirmResults.Enqueue(true);
        _ui.ConfirmResults.Enqueue(true);
        await vm.ImportSettingsCommand.ExecuteAsync(null);

        vm.NewPin = "2468";
        vm.RepeatPin = "2468";
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("2468", _pin.LastPin);
        Assert.Equal("hash:2468", _store.Current.Security.PinHash);
        Assert.False(vm.HasUnsavedChanges);

        // Die importierte PIN ist verbraucht: Sie kommt beim nächsten Speichern nicht zurück.
        vm.CountdownSeconds = 8;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("hash:2468", _store.Current.Security.PinHash);
    }

    [Fact]
    public async Task Reset_ClearsPendingImportedPin()
    {
        SetStorePin();
        var vm = await CreateInitializedAsync();
        _ui.ImportFileToPick = WriteImportFile(new AppSettings(), FilePin());
        _ui.ConfirmResults.Enqueue(true);
        _ui.ConfirmResults.Enqueue(true);
        await vm.ImportSettingsCommand.ExecuteAsync(null);
        Assert.True(vm.HasUnsavedChanges);

        _ui.ConfirmResult = true;
        await vm.ResetToDefaultsCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("hash:4711", _store.Current.Security.PinHash);
    }

    [Fact]
    public async Task SaveWithoutImport_KeepsStorePin()
    {
        SetStorePin();
        var vm = await CreateInitializedAsync();
        vm.CountdownSeconds = 8;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("hash:4711", _store.Current.Security.PinHash);
    }
}
