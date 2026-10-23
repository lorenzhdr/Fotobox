using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;
using Fotobox.Services.Camera;

namespace Fotobox.ViewModels.Booth;

/// <summary>Aktion, die einer Taste in der Gastansicht zugeordnet ist.</summary>
public enum BoothKeyAction { None, Trigger, OpenAdmin, CancelCountdown }

/// <summary>Bindbare Sicht auf <see cref="BoothController"/> und Kamera für die Gastansicht.</summary>
public sealed class BoothViewModel : ObservableObject
{
    /// <summary>Höchstens so viele Tastennamen nennt der Hinweistext.</summary>
    private const int HintKeysMax = 3;

    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    private int _lastCountdown;
    private bool _isAdminOpen;
    // Geparste Tastenbelegung; wird beim Einstellungswechsel verworfen und beim nächsten Zugriff neu gelesen.
    private volatile KeyMap? _keyMap;

    /// <summary>Geparste Tastenbelegung (Reihenfolge der Auslöser-Tasten bleibt für den Hinweistext erhalten).</summary>
    private sealed record KeyMap(IReadOnlyList<KeyChord> Trigger, HashSet<KeyChord> Admin, HashSet<KeyChord> Cancel);

    public BoothViewModel(BoothController controller, ICameraService camera, ISettingsStore settings)
    {
        Controller = controller;
        Camera = camera;
        Settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        controller.StateChanged += (_, _) => OnUi(Refresh);
        controller.Changed += (_, _) => OnUi(Refresh);
        controller.FlashRequested += (_, _) => OnUi(() => FlashRequested?.Invoke(this, EventArgs.Empty));
        camera.StatusChanged += (_, _) => OnUi(Refresh);
        settings.SettingsChanged += (_, _) =>
        {
            _keyMap = null;
            OnUi(Refresh);
        };
    }

    public BoothController Controller { get; }
    public ICameraService Camera { get; }
    public ISettingsStore Settings { get; }

    /// <summary>Wird bei jedem neuen Countdown-Wert (&gt; 0) ausgelöst – für die Zahlen-Animation.</summary>
    public event EventHandler? CountdownTicked;
    /// <summary>Weißer Blitz unmittelbar vor der Aufnahme (UI-Thread).</summary>
    public event EventHandler? FlashRequested;

    public bool MirrorPreview => Settings.Current.Camera.MirrorPreview;

    public bool IsIdle => Controller.State == BoothState.Idle;
    public bool IsCountdown => Controller.State == BoothState.Countdown;
    public string CountdownText => Controller.CountdownValue > 0 ? Controller.CountdownValue.ToString() : "";
    /// <summary>Zwischen zwei Streifenfotos: kein Countdown-Wert, nächste Aufnahme folgt.</summary>
    public bool IsPause => IsCountdown && Controller.CountdownValue == 0;
    public bool ShowShotInfo => Controller.ShotCount > 1 && Controller.State is BoothState.Countdown or BoothState.Capturing;
    public string ShotInfoText => ShowShotInfo ? $"Foto {Controller.ShotIndex} von {Controller.ShotCount}" : "";

    /// <summary>Layout-Vorschau bzw. Druck-/Fertig-Anzeige sichtbar.</summary>
    public bool ShowResult => IsPreview || IsPrinting || IsPrinted;
    public bool IsPreview => Controller.State == BoothState.Preview;
    public BitmapSource? PreviewImage => Controller.PreviewImage;

    public bool IsPrinting => Controller.State == BoothState.Printing;
    /// <summary>Druck fertig: Gast soll das Foto aus dem Drucker nehmen.</summary>
    public bool IsPrinted => Controller.State == BoothState.Printed;
    /// <summary>Foto wird zusammengestellt/gespeichert (nach der letzten Aufnahme).</summary>
    public bool IsProcessing => Controller.State == BoothState.Capturing && Controller.ShotIndex >= Controller.ShotCount;
    public string StatusText => IsPrinting
        ? Controller.StatusMessage ?? "Wird gedruckt…"
        : IsProcessing ? "Einen Moment, dein Foto wird erstellt…" : "";
    /// <summary>Banner „Foto wird erstellt“ (der Druck hat einen eigenen Bereich mit Fortschrittsbalken).</summary>
    public bool ShowStatusBanner => IsPrinting || IsProcessing;
    public bool ShowProcessingBanner => IsProcessing;

    /// <summary>Bereich mit Fortschrittsbalken (Druck läuft bzw. „Fertig“).</summary>
    public bool ShowPrintInfo => IsPrinting || IsPrinted;

    /// <summary>Druckfortschritt 0..1 für den Balken (während „Fertig“ voll).</summary>
    public double PrintProgress => Controller.PrintProgress;
    /// <summary>Erwartete Zeit ist um, der Druck läuft noch.</summary>
    public bool IsPrintOverdue => IsPrinting && Controller.PrintOverdue;
    public bool IsPrintOnTime => IsPrinting && !Controller.PrintOverdue;
    public string PrintWaitText => IsPrintOverdue
        ? "Gleich geschafft …"
        : $"Der Drucker ist etwas langsam – dein Foto kommt in ca. {FormatDuration(Controller.ExpectedPrintSeconds)}.";

    /// <summary>„2 Minuten“, „1 Minute“, „45 Sekunden“ – Minuten kaufmännisch gerundet (90 s → 2 Minuten).</summary>
    public static string FormatDuration(int seconds)
    {
        if (seconds < 60)
            return $"{Math.Max(1, seconds)} Sekunden";

        var minutes = (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);
        return minutes == 1 ? "1 Minute" : $"{minutes} Minuten";
    }

    public bool IsError => Controller.State == BoothState.Error;
    public string ErrorText => Controller.ErrorMessage ?? "";

    public bool HasCameraProblem =>
        Controller.State is BoothState.Idle or BoothState.Starting &&
        Camera.Status is CameraStatus.NoDevice or CameraStatus.AccessDenied or CameraStatus.Error;

    public bool IsCameraStarting =>
        Controller.State is BoothState.Idle or BoothState.Starting &&
        Camera.Status is CameraStatus.Starting or CameraStatus.Stopped;

    public string CameraProblemTitle => Camera.Status switch
    {
        CameraStatus.AccessDenied => CameraMessages.AccessDeniedTitle,
        CameraStatus.NoDevice => CameraMessages.NoDeviceTitle,
        _ => CameraMessages.UnavailableTitle,
    };

    /// <summary>
    /// Handlungsorientierter Text unter dem Titel: die Dienstmeldung, soweit sie mehr sagt als der Titel,
    /// sonst ein fester Hinweis. Der Wiederhol-Hinweis steht nur im XAML.
    /// </summary>
    public string CameraProblemText
    {
        get
        {
            var detail = DetailBeyondTitle(Camera.StatusMessage, CameraProblemTitle);
            if (detail is not null)
                return detail;

            return Camera.Status switch
            {
                CameraStatus.AccessDenied => CameraMessages.AccessDeniedHint,
                CameraStatus.NoDevice => CameraMessages.NoDeviceHint,
                CameraStatus.Error => CameraMessages.UnavailableHint,
                _ => "",
            };
        }
    }

    /// <summary>Hinweis „Drücke … für ein Foto“ – nur wenn ein Foto auch wirklich möglich ist.</summary>
    public bool ShowHint => IsIdle && Camera.Status == CameraStatus.Running;

    /// <summary>
    /// Hinweistext mit den eingestellten Auslöser-Tasten: „Drücke die Leertaste für ein Foto 📸“,
    /// bei mehreren Tasten „Drücke Leertaste oder Eingabetaste für ein Foto 📸“ (höchstens drei Namen).
    /// </summary>
    public string HintText
    {
        get
        {
            var keys = GetKeyMap().Trigger;
            if (keys.Count == 1)
            {
                var single = keys[0];
                var name = single == new KeyChord(Key.Space, ModifierKeys.None) ? "die Leertaste" : single.DisplayName;
                return $"Drücke {name} für ein Foto 📸";
            }

            var names = keys.Take(HintKeysMax).Select(k => k.DisplayName).ToList();
            var list = names.Count <= 1
                ? string.Concat(names)
                : $"{string.Join(", ", names.Take(names.Count - 1))} oder {names[^1]}";
            return $"Drücke {list} für ein Foto 📸";
        }
    }

    /// <summary>
    /// Ordnet eine gedrückte Taste der eingestellten Belegung zu (Vorrang: Admin, Countdown abbrechen, Auslösen).
    /// Reine Zuordnung ohne Seiteneffekt; ausgeführt wird sie vom Fenster.
    /// </summary>
    public BoothKeyAction HandleKey(KeyChord chord)
    {
        var map = GetKeyMap();
        if (map.Admin.Contains(chord)) return BoothKeyAction.OpenAdmin;
        if (map.Cancel.Contains(chord)) return BoothKeyAction.CancelCountdown;
        if (map.Trigger.Contains(chord)) return BoothKeyAction.Trigger;
        return BoothKeyAction.None;
    }

    private KeyMap GetKeyMap()
    {
        var map = _keyMap;
        if (map is not null)
            return map;

        var input = Settings.Current.Input;
        var trigger = ParseKeys(input?.TriggerKeys);
        if (trigger.Count == 0 && KeyChord.TryParse(InputSettings.DefaultTriggerKey, out var space))
            trigger.Add(space);
        var admin = ParseKeys(input?.AdminKeys);
        if (admin.Count == 0 && KeyChord.TryParse(InputSettings.DefaultAdminKey, out var adminDefault))
            admin.Add(adminDefault);

        map = new KeyMap(trigger, [.. admin], [.. ParseKeys(input?.CancelCountdownKeys)]);
        _keyMap = map;
        return map;
    }

    private static List<KeyChord> ParseKeys(IEnumerable<string>? entries)
    {
        var result = new List<KeyChord>();
        foreach (var entry in entries ?? [])
        {
            if (KeyChord.TryParse(entry, out var chord) && !result.Contains(chord))
                result.Add(chord);
        }
        return result;
    }

    /// <summary>Die modale Admin-Ansicht (inklusive PIN-Abfrage) ist offen; das Hauptfenster setzt das Flag.</summary>
    public bool IsAdminOpen
    {
        get => _isAdminOpen;
        set
        {
            if (_isAdminOpen == value) return;
            _isAdminOpen = value;
            OnPropertyChanged(nameof(IsAdminOpen));
            OnPropertyChanged(nameof(CanOpenAdmin));
        }
    }

    /// <summary>Admin-Ansicht darf geöffnet werden: im Leerlauf, beim Start und im Fehlerzustand, nicht mitten im Ablauf.</summary>
    public bool CanOpenAdmin =>
        !IsAdminOpen && Controller.State is BoothState.Idle or BoothState.Starting or BoothState.Error;

    /// <summary>Ein Kamerastart/-neustart dieser Ansicht läuft gerade.</summary>
    public bool IsCameraBusy => _cameraGate.CurrentCount == 0;

    /// <summary>
    /// Löst einen Durchgang aus. Gesperrt, solange die Admin-Ansicht offen ist oder die Kamera gerade (neu) startet;
    /// alles Weitere (Idle, Kamera läuft) prüft der <see cref="BoothController"/>.
    /// </summary>
    public bool TryTrigger() => !IsAdminOpen && !IsCameraBusy && Controller.TryTrigger();

    /// <summary>Bricht den Countdown ab (nur im Zustand Countdown wirksam, sonst false).</summary>
    public bool TryCancelCountdown() => !IsAdminOpen && Controller.TryCancelCountdown();

    /// <summary>Beim Laden des Fensters: Controller freigeben und Kamera starten.</summary>
    public async Task InitializeAsync()
    {
        Controller.MarkReady();
        Refresh();
        await StartCameraAsync();
    }

    /// <summary>Startet die Kamera mit den aktuellen Einstellungen neu (z. B. nach Admin-Änderungen).</summary>
    public Task RestartCameraAsync() => StartCameraAsync();

    /// <summary>Bricht einen Ablauf ab und stoppt die Kamera (blockiert max. 3 s).</summary>
    public void Shutdown()
    {
        Controller.Cancel();
        try
        {
            // Auf dem Threadpool ausführen, damit kein UI-Thread-Deadlock entsteht.
            if (!Task.Run(() => Camera.StopAsync()).Wait(TimeSpan.FromSeconds(3)))
                AppLog.Warn("Kamera konnte nicht rechtzeitig gestoppt werden.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Fehler beim Stoppen der Kamera.", ex);
        }
    }

    private async Task StartCameraAsync()
    {
        await _cameraGate.WaitAsync();
        try
        {
            await Camera.StartAsync(Settings.Current.Camera);
        }
        catch (Exception ex)
        {
            AppLog.Error("Kamera konnte nicht gestartet werden.", ex);
        }
        finally
        {
            _cameraGate.Release();
            Refresh();
        }
    }

    /// <summary>Dienstmeldung ohne den (ggf. vorangestellten) Titel; null, wenn nichts darüber hinaus gesagt wird.</summary>
    private static string? DetailBeyondTitle(string? message, string title)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var text = message.Trim();
        var heading = title.TrimEnd('.', ' ');
        if (text.StartsWith(heading, StringComparison.OrdinalIgnoreCase))
            text = text[heading.Length..].TrimStart('.', ':', ' ');

        return text.Length == 0 ? null : text;
    }

    private void Refresh()
    {
        OnPropertyChanged(string.Empty);

        var value = Controller.CountdownValue;
        if (value != _lastCountdown)
        {
            _lastCountdown = value;
            if (value > 0)
                CountdownTicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }
}
