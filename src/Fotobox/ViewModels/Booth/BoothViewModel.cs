using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Fotobox.Services.Camera;
using Fotobox.Services.Storage;

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
    private bool _isShutDown;
    // Peaceerkennung: Foto-Vorschau schon gemeldet, Selbstzerstörungs-Countdown gesehen (Ton läuft), Explosionsfenster vorbei.
    private bool _peaceWasPreview;
    private bool _selfDestructPending;
    private bool _explosionOver;
    private readonly DispatcherTimer _explosionTimer;
    // Herz/Kisscam: Musik, Rahmen und Zoom-Ausschnitt des laufenden Durchgangs; „pending“ = Kisscam-Countdown gesehen (Musik läuft).
    private readonly IMusicPlayer? _music;

    /// <summary>Hat die Gastansicht einen Musikspieler (DI)? Für Tests der Zusammensetzung.</summary>
    internal bool HasMusic => _music is not null;

    private bool _kisscamPending;
    private PhotoFrame? _kisscamFrame;
    private NormRect? _kisscamRegion;

    /// <summary>So lange bleibt die Alarm-Ansicht nach dem Blitz sichtbar, damit die Explosion zu Ende spielt.</summary>
    private static readonly TimeSpan ExplosionDuration = TimeSpan.FromMilliseconds(1200);

    /// <summary>Geparste Tastenbelegung (Reihenfolge der Auslöser-Tasten bleibt für den Hinweistext erhalten).</summary>
    private sealed record KeyMap(IReadOnlyList<KeyChord> Trigger, HashSet<KeyChord> Admin, HashSet<KeyChord> Cancel,
        IReadOnlyList<KeyChord> VideoStart, HashSet<KeyChord> VideoStop);

    /// <param name="fun">Zielerfassung der gesamten Gastansicht (DI); ohne Angabe eine untätige Instanz (Tests).</param>
    /// <param name="peace">Peaceerkennung der Gastansicht (DI, mit dem Bart-Zustand für die Foto-Planung); ohne Angabe eine untätige Instanz (Tests).</param>
    /// <param name="music">Musik der Kisscam (DI); ohne Angabe läuft die Kisscam stumm.</param>
    public BoothViewModel(BoothController controller, ICameraService camera, ISettingsStore settings,
        FunOverlayViewModel? fun = null, PeaceOverlayViewModel? peace = null, IMusicPlayer? music = null)
    {
        Controller = controller;
        Camera = camera;
        Settings = settings;
        Fun = fun ?? FunOverlayViewModel.CreateInert();
        Peace = peace ?? PeaceOverlayViewModel.CreateInert();
        _music = music;
        Peace.ActionRequested += (_, trigger) => OnPeaceAction(trigger);
        Peace.MiddleFingerTriggered += (_, _) => OnMiddleFinger();
        Peace.HeartTriggered += (_, trigger) => OnHeartAction(trigger);
        Peace.PropertyChanged += (_, e) =>
        {
            // Die Fortschrittsanzeige am Herz folgt dem besten Herz der Erkennung.
            if (e.PropertyName == nameof(PeaceOverlayViewModel.Heart))
            {
                OnPropertyChanged(nameof(ShowHeartFeedback));
                OnPropertyChanged(nameof(HeartFeedback));
            }
        };
        Fun.PropertyChanged += (_, e) =>
        {
            // Der Fun-Text ersetzt die Hinweiszeile an derselben Stelle (unten = Foto-Hinweis, oben = Video-Hinweis).
            if (e.PropertyName is nameof(FunOverlayViewModel.TextVisible) or nameof(FunOverlayViewModel.TextPosition)
                or nameof(FunOverlayViewModel.IsShown))
            {
                OnPropertyChanged(nameof(ShowHint));
                OnPropertyChanged(nameof(ShowLayoutHint));
                OnPropertyChanged(nameof(ShowVideoHint));
            }
        };
        _dispatcher = Dispatcher.CurrentDispatcher;
        _layoutPreviewTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = LayoutPreviewDuration };
        _layoutPreviewTimer.Tick += (_, _) => HideLayoutPreview();
        _explosionTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = ExplosionDuration };
        _explosionTimer.Tick += (_, _) =>
        {
            _explosionTimer.Stop();
            _explosionOver = true;
            OnPropertyChanged(nameof(ShowSelfDestruct));
        };

        controller.StateChanged += (_, _) => OnUi(Refresh);
        controller.Changed += (_, _) => OnUi(Refresh);
        controller.FlashRequested += (_, _) =>
        {
            // Der Stil wird hier gelesen, bevor der Aufruf auf den UI-Thread wechselt: Er gilt nur bis zum Ende des Durchgangs.
            var selfDestruct = controller.CountdownStyle == CountdownStyle.SelfDestruct;
            var kisscam = controller.CountdownStyle == CountdownStyle.Kisscam;
            OnUi(() =>
            {
                if (selfDestruct)
                {
                    RaiseExplosion();
                    return;
                }
                if (kisscam)
                    EndKisscamMusic();
                FlashRequested?.Invoke(this, EventArgs.Empty);
            });
        };
        camera.StatusChanged += (_, _) => OnUi(Refresh);
        settings.SettingsChanged += (_, _) =>
        {
            _keyMap = null;
            try
            {
                // Gespeicherte Reglerwerte für spätere Starts/Wiederverbindungen (wendet nichts sofort an).
                camera.ConfigureControls(settings.Current.Camera);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Kamerareglerwerte konnten nicht übernommen werden.", ex);
            }
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

    /// <summary>
    /// Fun „Zielerfassung“ (Zielboxen und Text über dem Livebild). Aktiv genau dann, wenn eingeschaltet, im Leerlauf, Kamera läuft
    /// und die Admin-Ansicht zu ist (<see cref="UpdateFun"/>).
    /// </summary>
    public FunOverlayViewModel Fun { get; }

    /// <summary>
    /// Peaceerkennung (Easter Egg): Im Leerlauf beobachtet sie Hände und Gesichter, während eines Fotos folgt sie nur noch einem
    /// aktiven Schnurrbart (<see cref="UpdatePeace"/>). Die Gastansicht verrät nichts davon.
    /// </summary>
    public PeaceOverlayViewModel Peace { get; }

    /// <summary>Wird bei jedem neuen Countdown-Wert (&gt; 0) ausgelöst – für die Zahlen-Animation.</summary>
    public event EventHandler? CountdownTicked;
    /// <summary>Weißer Blitz unmittelbar vor der Aufnahme (UI-Thread); bei der Selbstzerstörung stattdessen <see cref="ExplosionRequested"/>.</summary>
    public event EventHandler? FlashRequested;
    /// <summary>Explosion der Selbstzerstörung im Moment der Aufnahme (UI-Thread; ersetzt <see cref="FlashRequested"/>).</summary>
    public event EventHandler? ExplosionRequested;

    public bool MirrorPreview => Settings.Current.Camera.MirrorPreview;

    /// <summary>Fortschrittsring an der Hand beim Halten des Zeichens (<see cref="PeaceSettings.ShowHoldProgress"/>).</summary>
    public bool PeaceShowHoldProgress => Settings.Current.Peace?.ShowHoldProgress == true;

    public bool IsIdle => Controller.State == BoothState.Idle;
    public bool IsCountdown => Controller.State == BoothState.Countdown;
    public string CountdownText => Controller.CountdownValue > 0 ? Controller.CountdownValue.ToString() : "";
    /// <summary>Zwischen zwei Streifenfotos: kein Countdown-Wert, nächste Aufnahme folgt.</summary>
    public bool IsPause => IsCountdown && Controller.CountdownValue == 0
        && Controller.CountdownStyle is not (CountdownStyle.SelfDestruct or CountdownStyle.Kisscam);

    /// <summary>Darstellung des Countdowns des laufenden Durchgangs (Normal, Peace oder Selbstzerstörung).</summary>
    public CountdownStyle CountdownStyle => Controller.CountdownStyle;
    /// <summary>Countdown der Selbstzerstörung (rote Alarm-Anzeige statt der Ziffern).</summary>
    public bool IsSelfDestructCountdown => IsCountdown && Controller.CountdownStyle == CountdownStyle.SelfDestruct;
    /// <summary>Countdown nach Peace-Zeichen (normale Ziffern, daneben ein kleines ✌).</summary>
    public bool IsPeaceCountdown =>
        IsCountdown && Controller.CountdownStyle == CountdownStyle.Peace && Controller.CountdownValue > 0;
    /// <summary>Die gewohnten großen Ziffern sind sichtbar (nicht bei der Selbstzerstörung).</summary>
    public bool ShowNormalCountdownDigits => ShowCountdown && !IsSelfDestructCountdown;
    public int SelfDestructSecondsRemaining => Controller.CountdownValue;
    /// <summary>
    /// Alarm-Ansicht der Selbstzerstörung sichtbar: im Countdown und danach noch im Zustand Aufnahme, bis die Explosion zu Ende
    /// gespielt hat – das Overlay muss beim Blitz sichtbar sein, sonst wirkt <c>PlayExplosion</c> nicht.
    /// </summary>
    public bool ShowSelfDestruct =>
        Controller.CountdownStyle == CountdownStyle.SelfDestruct
        && (Controller.State == BoothState.Countdown || (Controller.State == BoothState.Capturing && !_explosionOver));

    // ---- Herz / Kisscam

    private HeartSettings HeartConfig => Settings.Current.Heart ?? new HeartSettings();

    /// <summary>Kisscam-Countdown läuft (Jumbotron-Rahmen; Gasttext bzw. in den letzten Sekunden die großen Ziffern).</summary>
    public bool IsKisscamCountdown => IsCountdown && Controller.CountdownStyle == CountdownStyle.Kisscam;

    /// <summary>Jumbotron-Rahmen sichtbar: im Kisscam-Countdown und beim Auslösen, bis das Foto fertig ist.</summary>
    public bool ShowKisscamFrame =>
        Controller.CountdownStyle == CountdownStyle.Kisscam && Controller.State is BoothState.Countdown or BoothState.Capturing;

    /// <summary>Gasttext sichtbar: im Kisscam-Countdown, bis die letzten <see cref="SettingsLimits.KisscamFinalDigitsSeconds"/> Sekunden die Ziffern zeigen.</summary>
    public bool ShowKisscamText => IsKisscamCountdown && Controller.CountdownValue > SettingsLimits.KisscamFinalDigitsSeconds;

    /// <summary>Gasttext mit der live herunterzählenden Restzeit (<c>{Sekunden}</c>).</summary>
    public string KisscamText => KisscamTextFormatter.Format(HeartConfig.KisscamText, Controller.CountdownValue);

    /// <summary>Rahmen des laufenden Kisscam-Durchgangs (nur solange <see cref="ShowKisscamFrame"/>).</summary>
    public PhotoFrame? KisscamFrame => ShowKisscamFrame ? _kisscamFrame : null;

    /// <summary>Digitalzoom auf die Herz-Gäste (normiert, ungespiegelt) – null = kein Zoom; nur solange <see cref="ShowKisscamFrame"/>.</summary>
    public NormRect? KisscamZoomRegion => ShowKisscamFrame ? _kisscamRegion : null;

    /// <summary>Herz mit Haltefortschritt zwischen den Händen sichtbar (Leerlauf, eingeschaltet, ein Herz im Bild).</summary>
    public bool ShowHeartFeedback =>
        IsIdle && HeartConfig is { Enabled: true, ShowHoldFeedback: true } && Peace.Heart is not null;

    /// <summary>Das Herz der Erkennung für die Fortschrittsanzeige (ungespiegelt, normiert).</summary>
    public HeartView? HeartFeedback => Peace.Heart;

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

    /// <summary>Leerlauf mit laufender Kamera: Grundlage der Hinweiszeilen.</summary>
    private bool IdleWithCamera => IsIdle && Camera.Status == CameraStatus.Running;

    /// <summary>
    /// Hinweis „Drücke … für ein Foto“ – nur wenn ein Foto auch wirklich möglich ist. Zeigt die Zielerfassung ihren Text unten,
    /// entfällt er (der Fun-Text nimmt den Platz ein).
    /// </summary>
    public bool ShowHint => IdleWithCamera && !(Fun.TextVisible && Fun.TextPosition == FunTextPosition.Bottom);

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
                click: TriggersOnClick, photoKeys: map.Trigger);
        }
    }

    public HintStyle VideoHintStyle => Settings.Current.GuestScreen?.VideoHintStyle ?? HintStyle.Pill;

    /// <summary>
    /// Video-Hinweiszeile sichtbar: Leerlauf mit laufender Kamera (wie die Fotozeile), Videomodus an, Text nicht leer.
    /// Solange die Layout-Vorschauliste eingeblendet ist, entfällt sie (die Liste ist mittig und hoch, sie würde sonst bei
    /// kleinen Bildschirmen überlappen).
    /// </summary>
    public bool ShowVideoHint => IdleWithCamera && !IsLayoutPreviewVisible && VideoHintText.Length > 0
        && !(Fun.TextVisible && Fun.TextPosition == FunTextPosition.Top);

    public bool IsVideoCountdown => Controller.State == BoothState.VideoCountdown;
    /// <summary>
    /// Countdown-Anzeige (Zahl, Abdunklung) für Foto- und Videodurchgang. In der Kisscam entfällt sie, solange der Gasttext läuft
    /// (<see cref="ShowKisscamText"/>): Die Abdunklung würde den Jumbotron-Rahmen verdecken; in den letzten Sekunden kommen die Ziffern.
    /// </summary>
    public bool ShowCountdown => (IsCountdown && !ShowKisscamText) || IsVideoCountdown;
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
    public bool ShowLayoutHint => ShowHint && LayoutChoices.Count >= 2 && (Settings.Current.GuestScreen?.ShowWheelHint ?? true);

    public string LayoutHintText => GuestLayouts.WheelHintText(Settings.Current.GuestScreen, SelectedLayout, Settings.Current.Layout.StripPhotoCount);

    /// <summary>Darstellung der Mausradzeile (<see cref="WheelHintStyle"/>).</summary>
    public WheelHintStyle LayoutHintStyle => Settings.Current.GuestScreen?.WheelHintStyle ?? WheelHintStyle.Matching;

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
            UpdateFun();
            UpdatePeace();
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
        _isShutDown = true;
        _layoutPreviewTimer.Stop();
        _explosionTimer.Stop();
        StopMusic();
        Fun.SetActive(false, new FunSettings(), false);
        UpdatePeace(); // Shutdown schaltet die Peaceerkennung aus
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

        UpdateFun();
        UpdatePeace();
        UpdateSelfDestructSound();
        UpdateKisscamMusic();
        OnPropertyChanged(string.Empty);

        var value = Controller.CountdownValue;
        if (value != _lastCountdown)
        {
            _lastCountdown = value;
            if (value > 0)
            {
                CountdownTicked?.Invoke(this, EventArgs.Empty);
                // Die Töne prüfen den Schalter der Einstellung selbst.
                if (IsSelfDestructCountdown)
                    Peace.PlaySelfDestructTick(value);
            }
        }
    }

    /// <summary>
    /// Verlässt der Zustand den Selbstzerstörungs-Countdown ohne Aufnahme (Abbrechen), verstummt die Sirene sofort. Geht es in die
    /// Aufnahme, bleibt der Ton (die Explosion klingt noch).
    /// </summary>
    private void UpdateSelfDestructSound()
    {
        if (IsSelfDestructCountdown)
        {
            _selfDestructPending = true;
            if (_explosionOver || _explosionTimer.IsEnabled)
            {
                _explosionTimer.Stop();
                _explosionOver = false;
            }
        }
        else if (_selfDestructPending)
        {
            _selfDestructPending = false;
            if (Controller.State != BoothState.Capturing)
                Peace.StopSounds();
        }
    }

    /// <summary>
    /// Verlässt der Zustand den Kisscam-Countdown ohne Aufnahme (Abbrechen per Taste oder Mittelfinger, Fehler), verstummt die Musik
    /// sofort und die Kisscam-Merker verfallen. Geht es in die Aufnahme, blendet <see cref="EndKisscamMusic"/> sie beim Blitz aus.
    /// </summary>
    private void UpdateKisscamMusic()
    {
        if (IsKisscamCountdown)
        {
            _kisscamPending = true;
            return;
        }

        if (_kisscamPending && Controller.State is BoothState.Idle or BoothState.Error or BoothState.Starting)
        {
            _kisscamPending = false;
            StopMusic();
            AppLog.Info("Kisscam abgebrochen.");
        }

        if (Controller.CountdownStyle != CountdownStyle.Kisscam && Controller.State is BoothState.Idle or BoothState.Error or BoothState.Starting)
        {
            _kisscamFrame = null;
            _kisscamRegion = null;
        }
    }

    /// <summary>Der Blitz der Kisscam: Die Musik blendet in 0,8 s aus.</summary>
    private void EndKisscamMusic()
    {
        _kisscamPending = false;
        try
        {
            _music?.FadeOut(TimeSpan.FromMilliseconds(800));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kisscam-Musik konnte nicht ausgeblendet werden.", ex);
        }
    }

    private void StopMusic()
    {
        try
        {
            _music?.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kisscam-Musik konnte nicht beendet werden.", ex);
        }
    }

    /// <summary>Der Blitz der Selbstzerstörung: Explosion (Bild und Ton) statt des weißen Blitzes.</summary>
    private void RaiseExplosion()
    {
        _selfDestructPending = false;
        _explosionOver = false;
        _explosionTimer.Stop();
        _explosionTimer.Start();
        OnPropertyChanged(nameof(ShowSelfDestruct));
        try
        {
            ExplosionRequested?.Invoke(this, EventArgs.Empty);
            Peace.PlayExplosion();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Explosion konnte nicht abgespielt werden.", ex);
        }
    }

    /// <summary>
    /// Entscheidet neu, in welcher Betriebsart die Peaceerkennung läuft: Im Leerlauf bei laufender Kamera beobachtet sie
    /// (<see cref="PeaceMode.Watching"/>); während eines Fotodurchgangs folgt sie nur einem aktiven Schnurrbart
    /// (<see cref="PeaceMode.FollowMoustache"/>), sonst ist sie aus. Aus ist sie auch bei Shutdown, offener Admin-Ansicht,
    /// ausgeschaltetem Schalter, Video und Fehler. Billig und wiederholbar; beim Eintritt in die Vorschau endet ein Bart „bis zum
    /// nächsten Foto“. Mittelfinger: Er wird nur im Leerlauf und im Countdown beobachtet. Im Selbstzerstörungs-Countdown (nichts sonst)
    /// und solange ein Bart (auch dessen Verschwinden-Animation) zu sehen ist, bleibt die Erkennung im Countdown an
    /// (<see cref="PeaceMode.FollowMoustache"/>, mit Händen nur wenn es etwas aufzuheben gibt); in Aufnahme, Vorschau und Druck folgt sie
    /// nur dem Bart (nur Gesichter, nie Hände); ohne das Feature (<see cref="MiddleFingerSettings.Enabled"/> aus) bleibt alles wie zuvor.
    /// Herz: Die Erkennung läuft, sobald Peace oder <see cref="HeartSettings.Enabled"/> an ist (bei ausgeschaltetem Peace ohne Peace-Gesten);
    /// im Kisscam-Countdown zählt sie als Mittelfinger-Ziel (<see cref="MiddleFingerTargets.Kisscam"/>).
    /// </summary>
    private void UpdatePeace()
    {
        try
        {
            var peace = Settings.Current.Peace ?? new PeaceSettings();
            var state = Controller.State;
            if (state == BoothState.Preview && !_peaceWasPreview)
                Peace.OnPhotoCompleted();
            _peaceWasPreview = state == BoothState.Preview;

            // Der Mittelfinger wird nur im Leerlauf und im Countdown beobachtet; in allen späteren Fotozuständen
            // (Aufnahme, Vorschau, Druck …) folgt die Erkennung nur dem Bart und fordert wie vor dem Feature nur Gesichter an.
            MiddleFingerSettings? middleFinger = state is BoothState.Idle or BoothState.Countdown
                ? Settings.Current.MiddleFinger ?? new MiddleFingerSettings()
                : null;
            var external = MiddleFingerTargets.None;
            if (state == BoothState.Countdown)
            {
                external = Controller.CountdownStyle switch
                {
                    CountdownStyle.SelfDestruct => MiddleFingerTargets.SelfDestruct,
                    CountdownStyle.Kisscam => MiddleFingerTargets.Kisscam,
                    _ => MiddleFingerTargets.None,
                };
            }

            // Herz und Peace sind unabhängig voneinander ein- und ausschaltbar; die Erkennung läuft, sobald eines davon an ist.
            var heart = Settings.Current.Heart ?? new HeartSettings();
            var mode = PeaceMode.Off;
            if (!_isShutDown && (peace.Enabled || heart.Enabled) && !IsAdminOpen && Camera.Status == CameraStatus.Running)
            {
                mode = state switch
                {
                    BoothState.Idle => PeaceMode.Watching,
                    BoothState.Countdown =>
                        Peace.HasMoustacheVisuals || middleFinger!.Wants(external) ? PeaceMode.FollowMoustache : PeaceMode.Off,
                    BoothState.Capturing or BoothState.Preview or BoothState.Printing
                        or BoothState.Printed or BoothState.PrintCancelled =>
                        Peace.HasMoustacheVisuals ? PeaceMode.FollowMoustache : PeaceMode.Off,
                    _ => PeaceMode.Off,
                };
            }
            Peace.SetMode(mode, peace, MirrorPreview, middleFinger, external, heart, peaceGestures: peace.Enabled);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Peaceerkennung konnte nicht umgeschaltet werden.", ex);
        }
    }

    /// <summary>
    /// Ein Peace-Zeichen wurde gehalten: führt die gewählte Aktion aus. Gesperrt wie <see cref="TryTrigger()"/> bei offener
    /// Admin-Ansicht und startender Kamera; alles Weitere (Leerlauf, Kamera läuft) prüft der <see cref="BoothController"/>.
    /// Peace-Fotos sind immer Einzelfotos (Streifen werden zum Einzelfoto), mit dem vom Gast gewählten Layout.
    /// </summary>
    internal bool OnPeaceAction(PeaceTrigger trigger)
    {
        try
        {
            if (_isShutDown || IsAdminOpen || IsCameraBusy || Controller.State != BoothState.Idle)
                return false;

            var settings = Settings.Current.Peace ?? new PeaceSettings();
            switch (trigger.Action)
            {
                case PeaceAction.SelfDestruct:
                    return TriggerPeacePhoto(settings.SelfDestructSeconds, CountdownStyle.SelfDestruct);
                case PeaceAction.Photo:
                    return TriggerPeacePhoto(settings.PhotoCountdownSeconds, CountdownStyle.Peace);
                case PeaceAction.Moustache:
                    Peace.StartMoustache(trigger);
                    if (settings.MoustacheAutoPhoto)
                        TriggerPeacePhoto(settings.MoustacheAutoPhotoCountdownSeconds, CountdownStyle.Peace);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Peace-Aktion konnte nicht ausgeführt werden.", ex);
            return false;
        }
    }

    /// <summary>
    /// Ein Mittelfinger wurde gehalten: hebt Peace-Effekte auf. Eine laufende Selbstzerstörung (Countdown) bricht still ab wie mit
    /// der Abbrechen-Taste (nur <see cref="BoothController.TryCancelCountdown"/>, nie Video oder Druck; ist schon die Aufnahme
    /// gestartet, läuft das Foto weiter), alle sichtbaren Bärte verschwinden (0,3-s-Animation, der Bart-Zustand fürs Foto ist sofort
    /// leer). Andere Countdowns (normal, Peace-Foto, Auto-Foto zum Bart) laufen weiter. Die Peace-Wirkungen gelten nur bei
    /// <see cref="MiddleFingerSettings.ActionCancelPeace"/>. Mit <see cref="MiddleFingerSettings.ActionCancelKisscam"/> bricht er einen
    /// laufenden Kisscam-Countdown ab (wie die Abbrechen-Taste, ohne Foto). Ohne Effekt bei Shutdown, offener Admin-Ansicht und
    /// ausgeschalteter Erkennung.
    /// </summary>
    /// <returns>true, wenn etwas aufgehoben wurde.</returns>
    internal bool OnMiddleFinger()
    {
        try
        {
            var middleFinger = Settings.Current.MiddleFinger;
            if (_isShutDown || IsAdminOpen || middleFinger is not { Enabled: true, HasAnyAction: true })
                return false;

            var selfDestruct = middleFinger.ActionCancelPeace
                && Controller.State == BoothState.Countdown
                && Controller.CountdownStyle == CountdownStyle.SelfDestruct
                && Controller.TryCancelCountdown();
            var moustache = middleFinger.ActionCancelPeace && Peace.VanishMoustaches();
            var kisscam = middleFinger.ActionCancelKisscam
                && Controller.State == BoothState.Countdown
                && Controller.CountdownStyle == CountdownStyle.Kisscam
                && Controller.TryCancelCountdown();

            AppLog.Info(kisscam ? "Mittelfinger: Kisscam abgebrochen."
                : selfDestruct && moustache ? "Mittelfinger: Selbstzerstörung abgebrochen, Bart entfernt."
                : selfDestruct ? "Mittelfinger: Selbstzerstörung abgebrochen."
                : moustache ? "Mittelfinger: Bart entfernt."
                : "Mittelfinger: nichts aufzuheben.");
            return selfDestruct || moustache || kisscam;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Mittelfinger-Aktion konnte nicht ausgeführt werden.", ex);
            return false;
        }
    }

    /// <summary>
    /// Ein Herz wurde gehalten: führt die gewählte Herz-Aktion aus. Gesperrt wie <see cref="OnPeaceAction"/> bei offener Admin-Ansicht,
    /// startender Kamera, Shutdown und außerhalb des Leerlaufs. Neue Aktionen (<see cref="HeartAction"/>) kommen als weiterer Fall hinzu.
    /// </summary>
    /// <returns>true, wenn die Aktion gestartet wurde.</returns>
    internal bool OnHeartAction(HeartTrigger trigger)
    {
        try
        {
            if (_isShutDown || IsAdminOpen || IsCameraBusy || Controller.State != BoothState.Idle)
                return false;

            switch (trigger.Action)
            {
                case HeartAction.Kisscam:
                    return StartKisscam(trigger);
                default:
                    AppLog.Info($"Herzerkennung: Aktion {trigger.Action} ist nicht verfügbar.");
                    return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Herz-Aktion konnte nicht ausgeführt werden.", ex);
            return false;
        }
    }

    /// <summary>
    /// Startet die Kisscam: langer Countdown mit Jumbotron-Rahmen, dann ein Einzelfoto mit Rahmen (eigenes Präfix, optional auf die
    /// Herz-Gäste herangezoomt). Rahmen und Ausschnitt werden vor dem Start gemerkt, weil der Zustandswechsel die Anzeige sofort
    /// aktualisiert.
    /// </summary>
    private bool StartKisscam(HeartTrigger trigger)
    {
        var heart = HeartConfig;
        var region = heart.KisscamZoomEnabled
            ? KisscamZoom.Plan(trigger.HeartBox, trigger.FaceBoxes, heart.KisscamZoomPercent)
            : null;
        var banner = string.IsNullOrWhiteSpace(heart.KisscamBannerText) ? HeartSettings.DefaultKisscamBannerText : heart.KisscamBannerText;
        var frame = new PhotoFrame(PhotoFrameKind.Kisscam, banner, heart.KisscamColor);

        _kisscamFrame = frame;
        _kisscamRegion = region;
        var started = Controller.TryTrigger(new TriggerOptions
        {
            SingleShotOnly = true,
            CountdownSeconds = heart.KisscamSeconds,
            Style = CountdownStyle.Kisscam,
            CropRegion = region,
            FilePrefix = SettingsNormalizer.SanitizePrefix(heart.KisscamFilePrefix, HeartSettings.DefaultKisscamFilePrefix),
            Frame = frame,
        });
        if (!started)
        {
            _kisscamFrame = null;
            _kisscamRegion = null;
            return false;
        }

        ResetLayoutSelection();
        var music = !string.IsNullOrWhiteSpace(heart.KisscamMusicPath);
        if (music)
        {
            try
            {
                _music?.Play(heart.KisscamMusicPath, heart.KisscamMusicVolumePercent, loop: true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Kisscam-Musik konnte nicht gestartet werden.", ex);
            }
        }
        AppLog.Info($"Kisscam gestartet ({heart.KisscamSeconds} s, Zoom {(region is null ? "aus" : $"{heart.KisscamZoomPercent} %")}, Musik {(music ? "an" : "aus")}).");
        return true;
    }

    private bool TriggerPeacePhoto(int countdownSeconds, CountdownStyle style)
    {
        // Wie TryTrigger(): Nur bei echter Auswahlmöglichkeit überschreibt die Wahl des Gastes das Standard-Layout.
        GuestLayout? layout = LayoutChoices.Count >= 2 ? SelectedLayout : null;
        var options = new TriggerOptions
        {
            Layout = layout,
            SingleShotOnly = true,
            CountdownSeconds = countdownSeconds,
            Style = style,
        };
        if (!Controller.TryTrigger(options))
            return false;

        ResetLayoutSelection();
        return true;
    }

    /// <summary>
    /// Entscheidet neu, ob die Zielerfassung läuft: eingeschaltet, Leerlauf, Kamera läuft, Admin zu. Billig und wiederholbar
    /// (<see cref="FunOverlayViewModel.SetActive"/> tut bei unveränderten Einstellungen nichts); mit dem Auslösen ist sie sofort aus.
    /// </summary>
    private void UpdateFun()
    {
        try
        {
            var fun = Settings.Current.Fun;
            var active = !_isShutDown && fun is { Enabled: true } && IsIdle && !IsAdminOpen
                && Camera.Status == CameraStatus.Running;
            Fun.SetActive(active, fun ?? new FunSettings(), MirrorPreview);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Zielerfassung konnte nicht umgeschaltet werden.", ex);
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
