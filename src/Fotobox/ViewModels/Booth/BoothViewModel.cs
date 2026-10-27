using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;
using Fotobox.Services.Camera;

namespace Fotobox.ViewModels.Booth;

/// <summary>Aktion, die einer Taste in der Gastansicht zugeordnet ist.</summary>
/// <remarks>
/// Video: <see cref="VideoToggle"/> = die Taste steht in der Start- und in der Stopp-Liste (ein Druck schaltet um),
/// <see cref="VideoStart"/>/<see cref="VideoStop"/> = nur in einer der beiden Listen.
/// </remarks>
public enum BoothKeyAction { None, Trigger, OpenAdmin, Cancel, VideoStart, VideoStop, VideoToggle }

/// <summary>
/// Eintrag der Layout-Vorschauliste: gewählte Variante, Anzeigename, Hervorhebung und Platzhalter 1..n für die Fotofelder
/// einer Streifen-Skizze.
/// </summary>
public sealed record LayoutOption(GuestLayout Layout, string Name, bool IsSelected, IReadOnlyList<int> StripSlots);

/// <summary>Bindbare Sicht auf <see cref="BoothController"/> und Kamera für die Gastansicht.</summary>
public sealed class BoothViewModel : ObservableObject
{
    /// <summary>So lange bleibt die Layout-Vorschauliste nach dem letzten Mausrad-Schritt sichtbar.</summary>
    private static readonly TimeSpan LayoutPreviewDuration = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    private int _lastCountdown;
    private bool _isAdminOpen;
    // Geparste Tastenbelegung; wird beim Einstellungswechsel verworfen und beim nächsten Zugriff neu gelesen.
    private volatile KeyMap? _keyMap;
    // Vom Gast gewähltes Layout; null = Standard aus den Einstellungen (für den nächsten Gast gilt wieder der Standard).
    private GuestLayout? _selectedLayout;
    private readonly DispatcherTimer _layoutPreviewTimer;
    private bool _isLayoutPreviewVisible;
    private LayoutOption[] _layoutOptions = [];
    private int[] _stripSlots = [];

    /// <summary>Geparste Tastenbelegung (Reihenfolge der Auslöser-Tasten bleibt für den Hinweistext erhalten).</summary>
    private sealed record KeyMap(IReadOnlyList<KeyChord> Trigger, HashSet<KeyChord> Admin, HashSet<KeyChord> Cancel,
        IReadOnlyList<KeyChord> VideoStart, HashSet<KeyChord> VideoStop);

    public BoothViewModel(BoothController controller, ICameraService camera, ISettingsStore settings)
    {
        Controller = controller;
        Camera = camera;
        Settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _layoutPreviewTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = LayoutPreviewDuration };
        _layoutPreviewTimer.Tick += (_, _) => HideLayoutPreview();

        controller.StateChanged += (_, _) => OnUi(Refresh);
        controller.Changed += (_, _) => OnUi(Refresh);
        controller.FlashRequested += (_, _) => OnUi(() => FlashRequested?.Invoke(this, EventArgs.Empty));
        camera.StatusChanged += (_, _) => OnUi(Refresh);
        settings.SettingsChanged += (_, _) =>
        {
            _keyMap = null;
            OnUi(() =>
            {
                ResetLayoutSelection();
                Refresh();
            });
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

    /// <summary>Foto-Ansicht (Layout-Vorschau bzw. Druckfortschritt) sichtbar; „Fertig“ hat einen eigenen Bildschirm.</summary>
    public bool ShowResult => IsPreview || IsPrinting;
    public bool IsPreview => Controller.State == BoothState.Preview;
    public BitmapSource? PreviewImage => Controller.PreviewImage;

    public bool IsPrinting => Controller.State == BoothState.Printing;
    /// <summary>Druck fertig: Gast soll das Foto aus dem Drucker nehmen.</summary>
    public bool IsPrinted => Controller.State == BoothState.Printed;
    /// <summary>Foto wird zusammengestellt/gespeichert (nach der letzten Aufnahme).</summary>
    public bool IsProcessing => Controller.State == BoothState.Capturing && Controller.ShotIndex >= Controller.ShotCount;
    public string StatusText => IsPrinting
        ? Controller.StatusMessage ?? "Wird gedruckt…"
        : IsProcessing ? "Einen Moment, dein Foto wird erstellt…"
        : IsVideoSaving ? "Video wird gespeichert …" : "";
    public bool ShowProcessingBanner => IsProcessing || IsVideoSaving;

    /// <summary>Bereich mit Fortschrittsbalken (Druck läuft).</summary>
    public bool ShowPrintInfo => IsPrinting;

    /// <summary>Eigener „Fertig“-Bildschirm (Gast soll das Foto aus dem Drucker nehmen).</summary>
    public bool ShowDone => IsPrinted;

    /// <summary>Ruhige Meldung nach einem Druckabbruch (bzw. wenn sich der Druck nicht mehr abbrechen ließ).</summary>
    public bool ShowPrintCancelled => Controller.State == BoothState.PrintCancelled;
    public string PrintCancelledTitle => Controller.PrintCancelTitle ?? "";
    public string PrintCancelledText => Controller.PrintCancelText ?? "";

    /// <summary>Druckfortschritt 0..1 für den Balken.</summary>
    public double PrintProgress => Controller.PrintProgress;
    /// <summary>Erwartete Zeit ist um, der Druck läuft noch.</summary>
    public bool IsPrintOverdue => IsPrinting && Controller.PrintOverdue;
    public string PrintWaitText => Controller.PrintCancelRequested
        ? "Einen Moment …"
        : IsPrintOverdue
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
    /// Hinweistext: der eigene Text aus den Einstellungen mit eingesetzten Platzhaltern, sonst automatisch mit den eingestellten
    /// Auslöser-Tasten („Drücke die Leertaste für ein Foto 📸“, siehe <see cref="HintTextFormatter"/>).
    /// </summary>
    public string HintText
    {
        get
        {
            var map = GetKeyMap();
            // {Videotaste_N} ist in der Fotozeile nur bei aktivem Videomodus belegt.
            return HintTextFormatter.Format(Settings.Current.GuestScreen?.HintText, map.Trigger, TriggersOnClick,
                VideoEnabled ? map.VideoStart : []);
        }
    }

    /// <summary>Darstellung des Hinweistexts (Pille, schlichter Text oder Band).</summary>
    public HintStyle HintStyle => Settings.Current.GuestScreen?.HintStyle ?? HintStyle.Pill;

    // ---- Video

    /// <summary>Videomodus ist eingeschaltet (<see cref="VideoSettings.Enabled"/>).</summary>
    public bool VideoEnabled => Settings.Current.Video?.Enabled == true;

    /// <summary>
    /// Text der Video-Hinweiszeile (oben im Bild): eigener Text mit Platzhaltern, sonst automatisch aus den Video-Starttasten;
    /// leer, wenn keine Starttaste belegt ist oder der Videomodus aus ist.
    /// </summary>
    public string VideoHintText
    {
        get
        {
            if (!VideoEnabled)
                return "";
            var map = GetKeyMap();
            return HintTextFormatter.FormatVideo(Settings.Current.GuestScreen?.VideoHintText, map.VideoStart,
                click: false, photoKeys: map.Trigger);
        }
    }

    public HintStyle VideoHintStyle => Settings.Current.GuestScreen?.VideoHintStyle ?? HintStyle.Pill;

    /// <summary>
    /// Video-Hinweiszeile sichtbar: Leerlauf mit laufender Kamera (wie die Fotozeile), Videomodus an, Text nicht leer.
    /// Solange die Layout-Vorschauliste eingeblendet ist, entfällt sie (die Liste ist mittig und hoch, sie würde sonst bei
    /// kleinen Bildschirmen überlappen).
    /// </summary>
    public bool ShowVideoHint => ShowHint && !IsLayoutPreviewVisible && VideoHintText.Length > 0;

    public bool IsVideoCountdown => Controller.State == BoothState.VideoCountdown;
    /// <summary>Countdown-Anzeige (Zahl, Abdunklung) für Foto- und Videodurchgang.</summary>
    public bool ShowCountdown => IsCountdown || IsVideoCountdown;
    /// <summary>Video-Countdown ohne Zahl: Vorbereitung bzw. Kamera-Start (kann bei Zeitüberschreitungen bis ~25 s dauern).</summary>
    public bool IsVideoStarting => IsVideoCountdown && Controller.CountdownValue == 0;

    public bool IsRecording => Controller.State == BoothState.Recording;
    public bool IsVideoSaving => Controller.State == BoothState.VideoSaving;
    /// <summary>Eigener Bildschirm „Video gespeichert“.</summary>
    public bool ShowVideoSaved => Controller.State == BoothState.VideoSaved;
    public string VideoSavedText => Controller.VideoSavedText;

    private VideoSettings VideoConfig => Settings.Current.Video ?? new VideoSettings();

    /// <summary>Roter blinkender Punkt samt „REC“.</summary>
    public bool ShowRecDot => IsRecording && VideoConfig.ShowRecDot;
    /// <summary>Laufzeit „mm:ss“.</summary>
    public bool ShowElapsed => IsRecording && VideoConfig.ShowElapsed;
    /// <summary>Restzeit „noch mm:ss“; bei Warnung ersetzt sie die große Warnanzeige.</summary>
    public bool ShowRemaining => IsRecording && VideoConfig.ShowRemaining && !IsRecordingWarning;
    /// <summary>Mindestens ein Element der Infoleiste (Punkt, Laufzeit, Restzeit) ist sichtbar.</summary>
    public bool ShowRecordingInfo => ShowRecDot || ShowElapsed || ShowRemaining;
    /// <summary>Große Warnung („Noch 10 s“) in den letzten <see cref="VideoSettings.WarningSeconds"/> Sekunden.</summary>
    public bool IsRecordingWarning => IsRecording && Controller.IsRecordingWarning;
    /// <summary>Das Overlay zeigt überhaupt etwas (sonst bleibt es leer).</summary>
    public bool ShowRecordingOverlay => ShowRecordingInfo || IsRecordingWarning || ShowNoAudioHint;
    /// <summary>Ton war gewünscht, die Aufnahme läuft aber ohne (Mikrofon nicht verfügbar).</summary>
    public bool ShowNoAudioHint => IsRecording && VideoConfig.UsesAudio && !Controller.RecordingHasAudio;

    public string ElapsedText => FormatClock(Controller.RecordingElapsed, roundUp: false);
    public string RemainingText => "noch " + FormatClock(Controller.RecordingRemaining, roundUp: true);

    public string RecordingWarningText
    {
        get
        {
            var seconds = (int)Math.Ceiling(Math.Max(0, Controller.RecordingRemaining.TotalSeconds));
            return seconds < 60 ? $"Noch {seconds} s" : $"Noch {FormatClock(TimeSpan.FromSeconds(seconds), roundUp: false)} min";
        }
    }

    /// <summary>„mm:ss“ (Minuten können über 59 liegen); die Restzeit wird aufgerundet, damit Laufzeit + Restzeit = Höchstdauer bleibt.</summary>
    public static string FormatClock(TimeSpan value, bool roundUp)
    {
        var seconds = roundUp ? Math.Ceiling(value.TotalSeconds) : Math.Floor(value.TotalSeconds);
        var total = (int)Math.Max(0, seconds);
        return $"{total / 60:00}:{total % 60:00}";
    }

    /// <summary>Mindestens eine Maustaste löst die Aufnahme aus.</summary>
    private bool TriggersOnClick =>
        Settings.Current.Input is { } input && (input.TriggerOnLeftClick || input.TriggerOnRightClick);

    /// <summary>
    /// Ordnet eine gedrückte Maustaste der Einstellung zu (links/rechts, je nach <see cref="InputSettings"/>).
    /// Reine Zuordnung ohne Seiteneffekt; ausgeführt wird sie vom Fenster.
    /// </summary>
    public BoothKeyAction HandleMouseButton(MouseButton button)
    {
        var input = Settings.Current.Input;
        return button switch
        {
            MouseButton.Left when input?.TriggerOnLeftClick == true => BoothKeyAction.Trigger,
            MouseButton.Right when input?.TriggerOnRightClick == true => BoothKeyAction.Trigger,
            _ => BoothKeyAction.None,
        };
    }

    // ---- Layoutwahl per Mausrad

    /// <summary>Layouts, zwischen denen der Gast wechseln darf (leer oder ein Eintrag = kein Wechsel).</summary>
    public IReadOnlyList<GuestLayout> LayoutChoices => Settings.Current.Layout.GuestChoices ?? [];

    /// <summary>Standard-Layout aus den Einstellungen.</summary>
    private GuestLayout DefaultLayout => GuestLayouts.From(Settings.Current.Layout);

    /// <summary>Aktuell gewähltes Layout (nach Start, Foto oder Einstellungswechsel: der Standard).</summary>
    public GuestLayout SelectedLayout => _selectedLayout ?? DefaultLayout;

    public string SelectedLayoutName => GuestLayouts.DisplayName(SelectedLayout, Settings.Current.Layout.StripPhotoCount);

    /// <summary>
    /// Ein Eintrag je Wahlmöglichkeit für die Vorschauliste (Skizze, Name, Hervorhebung). Die Liste bleibt dieselbe Instanz,
    /// solange sich Auswahl, Wahlmöglichkeiten und Fotozahl nicht ändern – <see cref="Refresh"/> meldet sonst alle
    /// Eigenschaften neu (auch im Drucktakt), und die Liste würde jedes Mal neu aufgebaut.
    /// </summary>
    public IReadOnlyList<LayoutOption> LayoutOptions
    {
        get
        {
            var choices = LayoutChoices;
            var selected = SelectedLayout;
            var slots = StripSlots;
            var photoCount = Settings.Current.Layout.StripPhotoCount;

            if (!OptionsMatch(choices, selected, slots, photoCount))
            {
                _layoutOptions = choices
                    .Select(c => new LayoutOption(c, GuestLayouts.DisplayName(c, photoCount), c == selected, slots))
                    .ToArray();
            }
            return _layoutOptions;
        }
    }

    private bool OptionsMatch(IReadOnlyList<GuestLayout> choices, GuestLayout selected, IReadOnlyList<int> slots, int photoCount)
    {
        if (_layoutOptions.Length != choices.Count)
            return false;

        for (var i = 0; i < choices.Count; i++)
        {
            var option = _layoutOptions[i];
            if (option.Layout != choices[i] ||
                option.IsSelected != (choices[i] == selected) ||
                !ReferenceEquals(option.StripSlots, slots) ||
                option.Name != GuestLayouts.DisplayName(choices[i], photoCount))
                return false;
        }
        return true;
    }

    /// <summary>Platzhalter 1..n für die schematischen Fotofelder eines Streifens in der Vorschauliste.</summary>
    public IReadOnlyList<int> StripSlots
    {
        get
        {
            var count = Math.Max(1, Settings.Current.Layout.StripPhotoCount);
            if (_stripSlots.Length != count)
                _stripSlots = Enumerable.Range(1, count).ToArray();
            return _stripSlots;
        }
    }

    /// <summary>Der Gast darf das Layout wechseln: mindestens zwei Möglichkeiten, Leerlauf mit laufender Kamera, nichts blockiert.</summary>
    public bool CanChooseLayout =>
        LayoutChoices.Count >= 2 && IsIdle && Camera.Status == CameraStatus.Running && !IsAdminOpen && !IsCameraBusy;

    /// <summary>Vorschauliste mit allen Layouts ist (noch) eingeblendet.</summary>
    public bool IsLayoutPreviewVisible
    {
        get => _isLayoutPreviewVisible;
        private set
        {
            if (SetProperty(ref _isLayoutPreviewVisible, value))
                OnPropertyChanged(nameof(ShowVideoHint));
        }
    }

    /// <summary>Zusatzzeile unter dem Hinweistext; nur, wenn der Gast überhaupt wählen kann.</summary>
    public bool ShowLayoutHint => ShowHint && LayoutChoices.Count >= 2;

    public string LayoutHintText => GuestLayouts.WheelHint(SelectedLayout, Settings.Current.Layout.StripPhotoCount);

    /// <summary>
    /// Wählt das nächste (<paramref name="direction"/> &gt; 0) bzw. vorherige Layout, zyklisch, und blendet die Vorschauliste
    /// für kurze Zeit ein. Wirkt nur, wenn <see cref="CanChooseLayout"/> gilt.
    /// </summary>
    public void CycleLayout(int direction)
    {
        if (direction == 0 || !CanChooseLayout)
            return;

        var choices = LayoutChoices;
        var index = IndexOfSelected(choices);
        // Nicht in der Liste (z. B. Standard fehlt): bei Index 0 beginnen.
        var next = index < 0 ? 0 : (index + Math.Sign(direction) + choices.Count) % choices.Count;
        _selectedLayout = choices[next];

        IsLayoutPreviewVisible = true;
        _layoutPreviewTimer.Stop();
        _layoutPreviewTimer.Start();
        Refresh();
    }

    /// <summary>
    /// Eine Mausrad-Stufe: nach oben = vorheriges Layout, nach unten = nächstes; mit
    /// <see cref="InputSettings.InvertMouseWheel"/> umgekehrt.
    /// </summary>
    public void ScrollLayout(bool wheelUp)
    {
        var direction = wheelUp ? -1 : 1;
        if (Settings.Current.Input?.InvertMouseWheel == true)
            direction = -direction;
        CycleLayout(direction);
    }

    private int IndexOfSelected(IReadOnlyList<GuestLayout> choices)
    {
        var selected = SelectedLayout;
        for (var i = 0; i < choices.Count; i++)
        {
            if (choices[i] == selected)
                return i;
        }
        return -1;
    }

    private void HideLayoutPreview()
    {
        _layoutPreviewTimer.Stop();
        IsLayoutPreviewVisible = false;
    }

    /// <summary>Zurück zum Standard-Layout und Vorschauliste ausblenden (nächster Gast, Einstellungswechsel).</summary>
    private void ResetLayoutSelection()
    {
        _selectedLayout = null;
        HideLayoutPreview();
    }

    /// <summary>
    /// Ordnet eine gedrückte Taste der eingestellten Belegung zu (Vorrang: Admin, Abbrechen, Auslösen).
    /// Reine Zuordnung ohne Seiteneffekt; ausgeführt wird sie vom Fenster.
    /// </summary>
    public BoothKeyAction HandleKey(KeyChord chord)
    {
        var map = GetKeyMap();
        if (map.Admin.Contains(chord)) return BoothKeyAction.OpenAdmin;
        if (map.Cancel.Contains(chord)) return BoothKeyAction.Cancel;
        if (map.Trigger.Contains(chord)) return BoothKeyAction.Trigger;

        // Videotasten wirken nur bei aktivem Videomodus. Genau eine Aktion je Tastendruck: Steht die Taste in beiden
        // Listen, schaltet ein Druck um (Start im Leerlauf, Stopp in der Aufnahme), nie beides zugleich.
        if (VideoEnabled)
        {
            var start = map.VideoStart.Contains(chord);
            var stop = map.VideoStop.Contains(chord);
            if (start && stop) return BoothKeyAction.VideoToggle;
            if (start) return BoothKeyAction.VideoStart;
            if (stop) return BoothKeyAction.VideoStop;
        }
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

        // Video-Tasten haben keinen Ersatzwert: leer = keine Taste belegt.
        map = new KeyMap(trigger, [.. admin], [.. ParseKeys(input?.CancelCountdownKeys)],
            ParseKeys(input?.VideoStartKeys), [.. ParseKeys(input?.VideoStopKeys)]);
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

    /// <summary>
    /// Die Gastansicht darf auf einen anderen Bildschirm verschoben werden: nur im Leerlauf, beim Start und im
    /// Fehlerzustand, nie mitten in Countdown, Aufnahme, Vorschau, Druck oder Videodurchgang (Video-Countdown, Aufnahme,
    /// Speichern, „gespeichert“).
    /// </summary>
    public bool CanMoveWindow => Controller.State is BoothState.Idle or BoothState.Starting or BoothState.Error;

    /// <summary>Ein Kamerastart/-neustart dieser Ansicht läuft gerade.</summary>
    public bool IsCameraBusy => _cameraGate.CurrentCount == 0;

    /// <summary>
    /// Löst einen Durchgang aus (mit dem gewählten Layout, danach gilt wieder der Standard). Gesperrt, solange die
    /// Admin-Ansicht offen ist oder die Kamera gerade (neu) startet; alles Weitere (Idle, Kamera läuft) prüft der
    /// <see cref="BoothController"/>.
    /// </summary>
    public bool TryTrigger()
    {
        if (IsAdminOpen || IsCameraBusy)
            return false;

        // Nur bei echter Auswahlmöglichkeit überschreibt die Wahl des Gastes das Standard-Layout.
        GuestLayout? layout = LayoutChoices.Count >= 2 ? SelectedLayout : null;
        if (!Controller.TryTrigger(layout))
            return false;

        ResetLayoutSelection();
        return true;
    }

    /// <summary>
    /// Bricht den Countdown (Foto oder Video) oder einen laufenden Druck ab bzw. beendet eine Videoaufnahme (sonst false).
    /// Bei offener Admin-Ansicht wirkungslos.
    /// </summary>
    public bool TryCancel() => !IsAdminOpen && Controller.TryCancel();

    /// <summary>Startet ein Video (nur im Leerlauf bei aktivem Videomodus). Gesperrt wie <see cref="TryTrigger"/>: bei offener Admin-Ansicht oder startender Kamera.</summary>
    public bool TryStartVideo() => VideoEnabled && !IsAdminOpen && !IsCameraBusy && Controller.TryStartVideo();

    /// <summary>Beendet die laufende Aufnahme (erst ab der Mindestlänge, siehe <see cref="BoothController.CanStopVideo"/>).</summary>
    public bool TryStopVideo() => VideoEnabled && !IsAdminOpen && Controller.TryStopVideo();

    /// <summary>Taste für Start und Stopp: im Leerlauf starten, in der Aufnahme beenden, sonst nichts.</summary>
    public bool TryToggleVideo() => Controller.State switch
    {
        BoothState.Idle => TryStartVideo(),
        BoothState.Recording => TryStopVideo(),
        _ => false,
    };

    /// <summary>Führt eine Video-Tastenaktion aus (Rückgabe: es wurde etwas ausgelöst); andere Aktionen bleiben wirkungslos.</summary>
    public bool ExecuteVideoAction(BoothKeyAction action) => action switch
    {
        BoothKeyAction.VideoToggle => TryToggleVideo(),
        BoothKeyAction.VideoStart => TryStartVideo(),
        BoothKeyAction.VideoStop => TryStopVideo(),
        _ => false,
    };

    /// <summary>Beim Laden des Fensters: Controller freigeben und Kamera starten.</summary>
    public async Task InitializeAsync()
    {
        Controller.MarkReady();
        Refresh();
        await StartCameraAsync();
    }

    /// <summary>Startet die Kamera mit den aktuellen Einstellungen neu (z. B. nach Admin-Änderungen).</summary>
    public Task RestartCameraAsync() => StartCameraAsync();

    /// <summary>
    /// Bricht einen Ablauf ab; die Kamera wird hier nicht gestoppt, das macht <c>App.OnExit</c> mit dem Freigeben
    /// des Dienstes (Dispose stoppt ohnehin, so wartet das Beenden nur einmal und nicht doppelt im UI-Thread).
    /// </summary>
    public void Shutdown()
    {
        _layoutPreviewTimer.Stop();
        Controller.Cancel();
    }

    private async Task StartCameraAsync()
    {
        await _cameraGate.WaitAsync();
        try
        {
            // Ton wird beim Sessionstart festgelegt: immer über den zentralen Helfer starten (ConfigureAudio + StartAsync).
            await Camera.StartWithAudioAsync(Settings.Current);
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
        // Die Vorschauliste gehört in den Leerlauf; beginnt ein Durchgang oder ein Fehler, verschwindet sie sofort.
        if (_isLayoutPreviewVisible && !CanChooseLayout)
            HideLayoutPreview();

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
