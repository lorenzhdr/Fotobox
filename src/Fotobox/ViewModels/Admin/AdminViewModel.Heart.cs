using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;
using Fotobox.Services.Storage;
using Fotobox.ViewModels.Booth;

namespace Fotobox.ViewModels.Admin;

/// <summary>Eine wählbare Herz-Aktion als Wahlkarte (Katalog <see cref="HeartActions"/>); die Auswahl liegt im <see cref="AdminViewModel"/>.</summary>
public sealed class HeartActionOption(AdminViewModel owner, HeartActionInfo info) : ObservableObject
{
    public HeartAction Action => info.Action;

    public string DisplayName => info.DisplayName;

    public string Description => info.Description;

    /// <summary>Diese Aktion ist gewählt (das Setzen auf <c>false</c> wird ignoriert: Gewählt wird immer durch eine andere Aktion).</summary>
    public bool IsSelected
    {
        get => owner.HeartSelectedAction == info.Action;
        set
        {
            if (value)
                owner.HeartSelectedAction = info.Action;
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

// Herzerkennung (Fun → Herz) – Entwurfs-Properties, Kisscam-Einstellungen, Musik-Anhören, Live-Vorschau mit Kisscam-Test und Validierung.
// Die Vorschau teilt sich die Kamera-Vorschau (PeacePreview) mit den Seiten Peace und Mittelfinger (siehe PushPeacePreviewMode).
public sealed partial class AdminViewModel
{
    /// <summary>Takt des Kisscam-Tests in der Vorschau.</summary>
    private static readonly TimeSpan KisscamTestTick = TimeSpan.FromSeconds(1);

    /// <summary>So lange steht nach dem Ablauf des Timers das Abzeichen „Foto würde jetzt ausgelöst“, dann endet der Test.</summary>
    internal static readonly TimeSpan KisscamTestPhotoBadgeDuration = TimeSpan.FromMilliseconds(1500);

    /// <summary>Ausblenddauer der Musik am Ende des Tests (wie in der Gastansicht beim Blitz).</summary>
    internal static readonly TimeSpan KisscamTestMusicFadeOut = TimeSpan.FromMilliseconds(800);

    private bool _kisscamTestRunning;
    private int _kisscamTestSeconds;
    private bool _kisscamTestPhotoBadge;
    // Der Test hat die Musik gestartet (nur dann darf er sie stoppen; der Player gehört auch der Gastansicht).
    private bool _kisscamTestMusic;
    private ITimer? _kisscamTestTimer;
    private int _kisscamTestGeneration;

    /// <summary>Hinweis, wenn die Musikdatei fehlt oder nicht erreichbar ist (nur Warnung, blockiert das Speichern nicht).</summary>
    public const string KisscamMusicMissingText =
        "Die Musikdatei wurde nicht gefunden oder ist nicht erreichbar. Die Kisscam läuft dann ohne Ton.";

    /// <summary>Hinweis, wenn die Musik beim Anhören oder im Kisscam-Test nicht abgespielt werden konnte.</summary>
    public const string KisscamMusicPlayFailedText = "Die Datei konnte nicht abgespielt werden.";

    /// <summary>Hinweis unter dem Timer, wenn die Kisscam nur die letzten Sekunden dauert (kein Gasttext).</summary>
    public const string KisscamOnlyCountdownHintText = "Bei 3 Sekunden erscheint nur der Countdown 3 – 2 – 1, kein Text.";

    /// <summary>Text der Anzeige, solange keine Musikdatei gewählt ist.</summary>
    public const string KisscamNoMusicText = "Keine Datei gewählt";

    private readonly IMusicPlayer? _music;
    private IReadOnlyList<HeartActionOption>? _heartActionOptions;
    // Die Vorschau der Musik wurde von dieser Ansicht gestartet (nur dann darf sie den geteilten Player wieder stoppen).
    private bool _kisscamPreviewStarted;
    // Der Player hat die Wiedergabe tatsächlich begonnen (danach bedeutet „läuft nicht mehr“ = Ende).
    private bool _kisscamPreviewSawPlaying;
    private bool _kisscamMusicMissing;
    private bool _kisscamMusicPlayFailed;
    private int _kisscamMusicCheckVersion;

    /// <summary>
    /// Prüfung, ob die Musikdatei existiert (läuft auf dem Threadpool, siehe <see cref="FolderProbeTimeout"/>). Für Tests austauschbar.
    /// </summary>
    internal Func<string, bool> KisscamMusicFileExists { get; set; } = File.Exists;

    /// <summary>Die zuletzt gestartete Prüfung der Musikdatei (für Tests abwartbar; wirft nie).</summary>
    internal Task KisscamMusicCheck { get; private set; } = Task.CompletedTask;

    // ---------------------------------------------------------------- Schalter, Formen, Erkennung

    /// <summary>Herzerkennung aktiv; die Einstellungen darunter sind sonst ausgegraut.</summary>
    public bool HeartEnabled
    {
        get => _draft.Heart.Enabled;
        set
        {
            if (!SetProperty(_draft.Heart.Enabled, value, v => _draft.Heart.Enabled = v))
                return;
            if (!value)
            {
                StopKisscamMusicPreview();
                EndKisscamTest(completed: false);
            }
            OnPropertyChanged(nameof(HeartStatusText));
            OnPropertyChanged(nameof(HeartHasNoShape));
            OnPropertyChanged(nameof(MiddleFingerPeaceDisabledWarning));
            OnPropertyChanged(nameof(HasMiddleFingerPeaceDisabledWarning));
        }
    }

    /// <summary>Klassisches Herz (zwei Hände, Daumen und Zeigefinger).</summary>
    public bool HeartShapeClassic
    {
        get => _draft.Heart.ShapeClassic;
        set
        {
            if (SetProperty(_draft.Heart.ShapeClassic, value, v => _draft.Heart.ShapeClassic = v))
                RaiseHeartShapeState();
        }
    }

    /// <summary>Dach-Herz (Fingerspitzen beider Hände oben zusammen).</summary>
    public bool HeartShapeRoof
    {
        get => _draft.Heart.ShapeRoof;
        set
        {
            if (SetProperty(_draft.Heart.ShapeRoof, value, v => _draft.Heart.ShapeRoof = v))
                RaiseHeartShapeState();
        }
    }

    /// <summary>Auf den Kopf gestelltes Herz.</summary>
    public bool HeartShapeInverted
    {
        get => _draft.Heart.ShapeInverted;
        set
        {
            if (SetProperty(_draft.Heart.ShapeInverted, value, v => _draft.Heart.ShapeInverted = v))
                RaiseHeartShapeState();
        }
    }

    /// <summary>Fingerherz (eine Hand, experimentell).</summary>
    public bool HeartShapeFingerHeart
    {
        get => _draft.Heart.ShapeFingerHeart;
        set
        {
            if (SetProperty(_draft.Heart.ShapeFingerHeart, value, v => _draft.Heart.ShapeFingerHeart = v))
                RaiseHeartShapeState();
        }
    }

    // Der letzte gesetzte Haken ist gesperrt (wie bei den Aktionen des Mittelfingers): Es soll immer mindestens eine Form übrig bleiben.
    public bool HeartShapeClassicEnabled => !(_draft.Heart.ShapeClassic && HeartShapeCount <= 1);

    public bool HeartShapeRoofEnabled => !(_draft.Heart.ShapeRoof && HeartShapeCount <= 1);

    public bool HeartShapeInvertedEnabled => !(_draft.Heart.ShapeInverted && HeartShapeCount <= 1);

    public bool HeartShapeFingerHeartEnabled => !(_draft.Heart.ShapeFingerHeart && HeartShapeCount <= 1);

    private int HeartShapeCount =>
        (_draft.Heart.ShapeClassic ? 1 : 0) + (_draft.Heart.ShapeRoof ? 1 : 0)
        + (_draft.Heart.ShapeInverted ? 1 : 0) + (_draft.Heart.ShapeFingerHeart ? 1 : 0);

    /// <summary>Herzerkennung ist an, aber keine Form gewählt (Warnung; blockiert das Speichern).</summary>
    public bool HeartHasNoShape => _draft.Heart.Enabled && !_draft.Heart.HasAnyShape;

    private void RaiseHeartShapeState()
    {
        OnPropertyChanged(nameof(HeartShapeClassicEnabled));
        OnPropertyChanged(nameof(HeartShapeRoofEnabled));
        OnPropertyChanged(nameof(HeartShapeInvertedEnabled));
        OnPropertyChanged(nameof(HeartShapeFingerHeartEnabled));
        OnPropertyChanged(nameof(HeartHasNoShape));
    }

    /// <summary>Haltedauer in Millisekunden (<see cref="SettingsLimits.HeartHoldMillisecondsMin"/> bis <c>Max</c>).</summary>
    public int HeartHoldMilliseconds
    {
        get => _draft.Heart.HoldMilliseconds;
        set => SetProperty(_draft.Heart.HoldMilliseconds,
            Math.Clamp(value, SettingsLimits.HeartHoldMillisecondsMin, SettingsLimits.HeartHoldMillisecondsMax),
            v => _draft.Heart.HoldMilliseconds = v);
    }

    /// <summary>Empfindlichkeit in Prozent (<see cref="SettingsLimits.HeartSensitivityMin"/> bis <c>Max</c>).</summary>
    public int HeartSensitivityPercent
    {
        get => _draft.Heart.SensitivityPercent;
        set => SetProperty(_draft.Heart.SensitivityPercent,
            Math.Clamp(value, SettingsLimits.HeartSensitivityMin, SettingsLimits.HeartSensitivityMax),
            v => _draft.Heart.SensitivityPercent = v);
    }

    /// <summary>Fortschrittsanzeige am Herz während des Haltens.</summary>
    public bool HeartShowHoldFeedback
    {
        get => _draft.Heart.ShowHoldFeedback;
        set => SetProperty(_draft.Heart.ShowHoldFeedback, value, v => _draft.Heart.ShowHoldFeedback = v);
    }

    // ---------------------------------------------------------------- Aktion

    /// <summary>Die wählbaren Herz-Aktionen (aus dem Katalog <see cref="HeartActions.All"/>), jede als Wahlkarte.</summary>
    public IReadOnlyList<HeartActionOption> HeartActionOptions =>
        _heartActionOptions ??= HeartActions.All.Select(i => new HeartActionOption(this, i)).ToList();

    /// <summary>Die gewählte Herz-Aktion (genau eine).</summary>
    public HeartAction HeartSelectedAction
    {
        get => _draft.Heart.Action;
        set
        {
            if (!Enum.IsDefined(value))
                return;
            if (SetProperty(_draft.Heart.Action, value, v => _draft.Heart.Action = v))
            {
                RefreshHeartActionOptions();
                OnPropertyChanged(nameof(IsKisscamActionSelected));
                OnPropertyChanged(nameof(HeartStatusText));
            }
        }
    }

    /// <summary>Die Aktion „Kisscam“ ist gewählt (nur dann zeigt die Seite die Karte „Kisscam“).</summary>
    public bool IsKisscamActionSelected => _draft.Heart.Action == HeartAction.Kisscam;

    private void RefreshHeartActionOptions()
    {
        foreach (var option in HeartActionOptions)
            option.Refresh();
    }

    // ---------------------------------------------------------------- Startseite

    /// <summary>„Aus“ bzw. „An · Kisscam“ (Name der Aktion) für die Kachel „Herz“.</summary>
    public string HeartStatusText =>
        _draft.Heart.Enabled
            ? "An · " + (HeartActions.Find(_draft.Heart.Action)?.DisplayName ?? _draft.Heart.Action.ToString())
            : "Aus";

    // ---------------------------------------------------------------- Kisscam: Timer, Texte, Farbe

    /// <summary>Dauer des Kisscam-Countdowns in Sekunden (<see cref="SettingsLimits.KisscamSecondsMin"/> bis <c>Max</c>).</summary>
    public int KisscamSeconds
    {
        get => _draft.Heart.KisscamSeconds;
        set
        {
            if (SetProperty(_draft.Heart.KisscamSeconds,
                    Math.Clamp(value, SettingsLimits.KisscamSecondsMin, SettingsLimits.KisscamSecondsMax),
                    v => _draft.Heart.KisscamSeconds = v))
                OnPropertyChanged(nameof(HasKisscamOnlyCountdownHint));
        }
    }

    /// <summary>
    /// Bei höchstens <see cref="SettingsLimits.KisscamFinalDigitsSeconds"/> Sekunden zeigt die Gastansicht von Anfang an nur die großen
    /// Ziffern, nie den Gasttext (Hinweis unter dem Timer).
    /// </summary>
    public bool HasKisscamOnlyCountdownHint => _draft.Heart.KisscamSeconds <= SettingsLimits.KisscamFinalDigitsSeconds;

    /// <summary>Hinweistext des Gastes mit Platzhalter <c>{Sekunden}</c>; leer = <see cref="KisscamTextPlaceholder"/>.</summary>
    public string KisscamText
    {
        get => _draft.Heart.KisscamText ?? string.Empty;
        set
        {
            if (SetProperty(_draft.Heart.KisscamText ?? string.Empty, value ?? string.Empty,
                    v => _draft.Heart.KisscamText = string.IsNullOrWhiteSpace(v) ? null : v))
            {
                OnPropertyChanged(nameof(KisscamTextProblems));
                OnPropertyChanged(nameof(KisscamTextProblemsText));
                OnPropertyChanged(nameof(HasKisscamTextProblems));
                OnPropertyChanged(nameof(KisscamTestText));
            }
        }
    }

    /// <summary>Der Standardtext (grauer Platzhalter im Eingabefeld, solange es leer ist).</summary>
    public string KisscamTextPlaceholder => HeartSettings.DefaultKisscamText;

    /// <summary>Unbekannte Platzhalter im Gasttext, je einer als Hinweiszeile (nicht blockierend; leer = alles in Ordnung).</summary>
    public IReadOnlyList<string> KisscamTextProblems =>
        KisscamTextFormatter.FindUnknownPlaceholders(_draft.Heart.KisscamText)
            .Select(p => $"Unbekannter Platzhalter {p} – er wird unverändert angezeigt. Bekannt ist nur {{Sekunden}}.")
            .ToList();

    public string KisscamTextProblemsText => string.Join(Environment.NewLine, KisscamTextProblems);

    public bool HasKisscamTextProblems => KisscamTextProblems.Count > 0;

    /// <summary>Banner-Text im Rahmen (höchstens <see cref="SettingsLimits.KisscamBannerMaxLength"/> Zeichen); leer = <see cref="KisscamBannerTextPlaceholder"/>.</summary>
    public string KisscamBannerText
    {
        get => _draft.Heart.KisscamBannerText ?? string.Empty;
        set
        {
            if (SetProperty(_draft.Heart.KisscamBannerText ?? string.Empty, value ?? string.Empty,
                    v => _draft.Heart.KisscamBannerText = string.IsNullOrWhiteSpace(v) ? null : v))
                OnPropertyChanged(nameof(KisscamTestFrame));
        }
    }

    /// <summary>Der Standard-Banner-Text (grauer Platzhalter im Eingabefeld).</summary>
    public string KisscamBannerTextPlaceholder => HeartSettings.DefaultKisscamBannerText;

    /// <summary>Farbschema des Jumbotron-Rahmens (Wahlkarten mit Skizze; gebunden über <c>EnumMatchConverter</c>).</summary>
    public KisscamColor KisscamColor
    {
        get => _draft.Heart.KisscamColor;
        set
        {
            if (Enum.IsDefined(value) && SetProperty(_draft.Heart.KisscamColor, value, v => _draft.Heart.KisscamColor = v))
                OnPropertyChanged(nameof(KisscamTestFrame));
        }
    }

    // ---------------------------------------------------------------- Kisscam: Zoom, Dateiname

    /// <summary>Digitalzoom auf die Herz-Personen während der Kisscam.</summary>
    public bool KisscamZoomEnabled
    {
        get => _draft.Heart.KisscamZoomEnabled;
        set => SetProperty(_draft.Heart.KisscamZoomEnabled, value, v => _draft.Heart.KisscamZoomEnabled = v);
    }

    /// <summary>Zoomstärke in Prozent (<see cref="SettingsLimits.KisscamZoomPercentMin"/> bis <c>Max</c>).</summary>
    public int KisscamZoomPercent
    {
        get => _draft.Heart.KisscamZoomPercent;
        set => SetProperty(_draft.Heart.KisscamZoomPercent,
            Math.Clamp(value, SettingsLimits.KisscamZoomPercentMin, SettingsLimits.KisscamZoomPercentMax),
            v => _draft.Heart.KisscamZoomPercent = v);
    }

    /// <summary>Dateiname-Präfix des Kisscam-Fotos.</summary>
    public string KisscamFilePrefix
    {
        get => _draft.Heart.KisscamFilePrefix;
        set
        {
            if (SetProperty(_draft.Heart.KisscamFilePrefix, value ?? string.Empty, v => _draft.Heart.KisscamFilePrefix = v))
                OnPropertyChanged(nameof(KisscamFileNameExample));
        }
    }

    /// <summary>Beispiel für einen Dateinamen mit dem aktuellen Präfix (leeres Präfix: Standard).</summary>
    public string KisscamFileNameExample =>
        $"Dateien heißen zum Beispiel „{SettingsNormalizer.SanitizePrefix(_draft.Heart.KisscamFilePrefix, HeartSettings.DefaultKisscamFilePrefix)}_20261029_153000.jpg“.";

    // ---------------------------------------------------------------- Kisscam: Musik

    /// <summary>Gewählter Pfad der Musikdatei (leer = keine); gerätespezifisch, wird beim Teilen der Einstellungen nicht übertragen.</summary>
    public string KisscamMusicPath => _draft.Heart.KisscamMusicPath ?? string.Empty;

    public bool HasKisscamMusic => !string.IsNullOrEmpty(_draft.Heart.KisscamMusicPath);

    /// <summary>Anzeige: nur der Dateiname der Musik (oder „Keine Datei gewählt“).</summary>
    public string KisscamMusicFileName
    {
        get
        {
            var path = _draft.Heart.KisscamMusicPath;
            if (string.IsNullOrEmpty(path))
                return KisscamNoMusicText;
            var name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }
    }

    /// <summary>Lautstärke der Musik in Prozent (<see cref="SettingsLimits.KisscamMusicVolumeMin"/> bis <c>Max</c>).</summary>
    public int KisscamMusicVolumePercent
    {
        get => _draft.Heart.KisscamMusicVolumePercent;
        set => SetProperty(_draft.Heart.KisscamMusicVolumePercent,
            Math.Clamp(value, SettingsLimits.KisscamMusicVolumeMin, SettingsLimits.KisscamMusicVolumeMax),
            v => _draft.Heart.KisscamMusicVolumePercent = v);
    }

    /// <summary>Die gewählte Musikdatei fehlt oder ist nicht erreichbar (Warnung; blockiert das Speichern nicht).</summary>
    public bool KisscamMusicMissing
    {
        get => _kisscamMusicMissing;
        private set
        {
            if (SetProperty(ref _kisscamMusicMissing, value))
                RaiseKisscamMusicCommands();
        }
    }

    /// <summary>Die Musik konnte beim letzten Anhören bzw. Kisscam-Test nicht gespielt werden (Hinweis; Zustand, keine Einstellung).</summary>
    public bool KisscamMusicPlayFailed
    {
        get => _kisscamMusicPlayFailed;
        private set => SetProperty(ref _kisscamMusicPlayFailed, value);
    }

    /// <summary>Die Musik wird gerade zum Anhören gespielt (Zustand, keine Einstellung).</summary>
    public bool KisscamMusicPreviewPlaying => _kisscamPreviewStarted;

    /// <summary>„Stopp“ während des Anhörens, sonst „Anhören“.</summary>
    public string KisscamMusicPreviewButtonText => _kisscamPreviewStarted ? "Stopp" : "Anhören";

    /// <summary>Wählt eine Musikdatei (WAV/MP3) über den Dateidialog.</summary>
    public IRelayCommand PickKisscamMusicCommand { get; private set; } = null!;

    /// <summary>Entfernt die Musikdatei (die Kisscam läuft dann ohne Ton).</summary>
    public IRelayCommand RemoveKisscamMusicCommand { get; private set; } = null!;

    /// <summary>Spielt die Musik zum Anhören bzw. beendet das Anhören. Ohne Player oder Datei nicht ausführbar.</summary>
    public IRelayCommand ToggleKisscamMusicPreviewCommand { get; private set; } = null!;

    private void InitHeart(IMusicPlayer? music)
    {
        // Wird im Konstruktor aufgerufen (die get-only-Auto-Properties der Befehle sind dort noch nicht belegt).
        PickKisscamMusicCommand = new RelayCommand(PickKisscamMusic);
        RemoveKisscamMusicCommand = new RelayCommand(RemoveKisscamMusic, () => HasKisscamMusic);
        ToggleKisscamMusicPreviewCommand = new RelayCommand(ToggleKisscamMusicPreview, CanToggleKisscamMusicPreview);
        ToggleKisscamTestCommand = new RelayCommand(ToggleKisscamTest, () => !_disposed);
        if (music is not null)
        {
            music.PlaybackChanged += OnMusicPlaybackChanged;
            music.PlaybackFailed += OnMusicPlaybackFailed;
        }
        RefreshKisscamMusicCheck();
    }

    private bool CanToggleKisscamMusicPreview() =>
        !_disposed && _music is not null && !_kisscamTestRunning
        && (_kisscamPreviewStarted || (HasKisscamMusic && !_kisscamMusicMissing));

    private void RaiseKisscamMusicCommands()
    {
        RemoveKisscamMusicCommand.NotifyCanExecuteChanged();
        ToggleKisscamMusicPreviewCommand.NotifyCanExecuteChanged();
    }

    private void PickKisscamMusic()
    {
        string? file;
        try
        {
            file = _ui.PickAudioFile();
        }
        catch (Exception ex)
        {
            AppLog.Error("Dateiauswahl der Kisscam-Musik fehlgeschlagen.", ex);
            return;
        }

        if (string.IsNullOrWhiteSpace(file))
            return;
        // Der Normalisierer würde zu lange Pfade stillschweigend verwerfen: lieber gleich melden.
        if (file.Length > SettingsLimits.KisscamMusicPathMaxLength || file.Any(char.IsControl))
        {
            _ui.ShowInfo("Musikdatei", $"Der Pfad der Datei ist zu lang oder ungültig (höchstens {SettingsLimits.KisscamMusicPathMaxLength} Zeichen). Bitte wählen Sie die Datei an einem kürzeren Ort aus.");
            return;
        }

        SetKisscamMusicPath(file);
    }

    private void RemoveKisscamMusic() => SetKisscamMusicPath(null);

    private void SetKisscamMusicPath(string? path)
    {
        if (string.Equals(_draft.Heart.KisscamMusicPath, path, StringComparison.Ordinal))
            return;
        StopKisscamMusicPreview();
        _draft.Heart.KisscamMusicPath = path;
        OnPropertyChanged(nameof(KisscamMusicPath));
        OnPropertyChanged(nameof(KisscamMusicFileName));
        OnPropertyChanged(nameof(HasKisscamMusic));
        RefreshKisscamMusicCheck();
    }

    // Prüft die Musikdatei im Hintergrund (ein hängender Netzpfad friert die Oberfläche nicht ein); ältere Ergebnisse verfallen.
    private void RefreshKisscamMusicCheck()
    {
        var version = ++_kisscamMusicCheckVersion;
        var path = _draft.Heart.KisscamMusicPath;
        KisscamMusicMissing = false;
        RaiseKisscamMusicCommands();
        KisscamMusicCheck = string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : CheckKisscamMusicAsync(path, version);
    }

    private async Task CheckKisscamMusicAsync(string path, int version)
    {
        var exists = KisscamMusicFileExists;
        bool missing;
        try
        {
            missing = !await Task.Run(() => exists(path)).WaitAsync(FolderProbeTimeout, _time);
        }
        catch (TimeoutException)
        {
            AppLog.Warn("Prüfung der Kisscam-Musikdatei: Zeitüberschreitung.");
            missing = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Prüfung der Kisscam-Musikdatei fehlgeschlagen.", ex);
            missing = true;
        }

        if (_disposed || version != _kisscamMusicCheckVersion)
            return;
        KisscamMusicMissing = missing;
    }

    private void ToggleKisscamMusicPreview()
    {
        if (_disposed || _music is null)
            return;
        if (_kisscamPreviewStarted)
        {
            StopKisscamMusicPreview();
            return;
        }

        var path = _draft.Heart.KisscamMusicPath;
        if (string.IsNullOrWhiteSpace(path))
            return;
        KisscamMusicPlayFailed = false;
        SetKisscamPreviewStarted(true);
        try
        {
            _music.Play(path, _draft.Heart.KisscamMusicVolumePercent, loop: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kisscam-Musik konnte nicht zum Anhören gestartet werden.", ex);
            SetKisscamPreviewStarted(false);
        }
    }

    /// <summary>
    /// Beendet das Anhören der Kisscam-Musik – aber nur, wenn diese Ansicht es gestartet hat (der Player gehört auch der
    /// Gastansicht). Wird beim Schließen, bei Tab- und Seitenwechsel, bei geänderter Datei und beim Ersetzen des Entwurfs aufgerufen.
    /// </summary>
    internal void StopKisscamMusicPreview()
    {
        // Seiten-/Tabwechsel, Dateiwechsel usw. räumen auch einen alten Fehlerhinweis ab.
        KisscamMusicPlayFailed = false;
        if (!_kisscamPreviewStarted)
            return;
        SetKisscamPreviewStarted(false);
        try
        {
            _music?.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kisscam-Musik konnte nicht gestoppt werden.", ex);
        }
    }

    private void SetKisscamPreviewStarted(bool started)
    {
        _kisscamPreviewSawPlaying = false;
        if (_kisscamPreviewStarted == started)
            return;
        _kisscamPreviewStarted = started;
        OnPropertyChanged(nameof(KisscamMusicPreviewPlaying));
        OnPropertyChanged(nameof(KisscamMusicPreviewButtonText));
        ToggleKisscamMusicPreviewCommand.NotifyCanExecuteChanged();
    }

    // Kann auf jedem Thread kommen: in den UI-Kontext holen (wie PostPeace); ohne Kontext (Tests) sofort.
    private void OnMusicPlaybackChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (_sync is null)
            HandleMusicPlaybackChanged();
        else
            _sync.Post(_ => HandleMusicPlaybackChanged(), null);
    }

    // Der Player meldet einen gescheiterten Start (Datei fehlt/kaputt, Öffnungsfrist verpasst): kein PlaybackChanged, weil nie gespielt
    // wurde – ohne diese Meldung bliebe der Knopf auf „Stopp“ stehen. Kann auf jedem Thread kommen.
    private void OnMusicPlaybackFailed(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (_sync is null)
            HandleMusicPlaybackFailed();
        else
            _sync.Post(_ => HandleMusicPlaybackFailed(), null);
    }

    private void HandleMusicPlaybackFailed()
    {
        if (_disposed)
            return;
        // Der Player gehört auch der Gastansicht: nur reagieren, wenn diese Ansicht die Musik gestartet hat.
        var ours = false;
        if (_kisscamPreviewStarted)
        {
            SetKisscamPreviewStarted(false);
            ours = true;
        }
        if (_kisscamTestMusic)
        {
            _kisscamTestMusic = false;
            ours = true;
        }
        if (ours)
            KisscamMusicPlayFailed = true;
    }

    private void HandleMusicPlaybackChanged()
    {
        if (_disposed || !_kisscamPreviewStarted || _music is null)
            return;
        if (_music.IsPlaying)
            _kisscamPreviewSawPlaying = true;
        else if (_kisscamPreviewSawPlaying)
            SetKisscamPreviewStarted(false); // Die Wiedergabe ist zu Ende (oder wurde von anderer Stelle gestoppt).
    }

    private void DisposeHeart()
    {
        if (_music is not null)
        {
            _music.PlaybackChanged -= OnMusicPlaybackChanged;
            _music.PlaybackFailed -= OnMusicPlaybackFailed;
        }
        EndKisscamTest(completed: false);
        StopKisscamMusicPreview();
    }

    // ---------------------------------------------------------------- Live-Vorschau: Kisscam-Test

    /// <summary>Test-Knopf: startet den Kisscam-Test in der Vorschau (ohne Foto, ohne Druck); während des Tests bricht er ihn ab.</summary>
    public IRelayCommand ToggleKisscamTestCommand { get; private set; } = null!;

    /// <summary>„Kisscam testen“ bzw. während des Tests „Test abbrechen“.</summary>
    public string KisscamTestButtonText => _kisscamTestRunning ? "Test abbrechen" : "Kisscam testen";

    /// <summary>Ein Kisscam-Test läuft (Rahmen und Timer in der Vorschau; bis zum Ende des Abzeichens „Foto würde jetzt ausgelöst“).</summary>
    public bool KisscamTestRunning => _kisscamTestRunning;

    /// <summary>Verbleibende Sekunden des Tests (0 = Auslösemoment).</summary>
    public int KisscamTestSeconds => _kisscamTestSeconds;

    /// <summary>Gasttext des Tests, mit der Restzeit formatiert (wie in der Gastansicht).</summary>
    public string KisscamTestText => KisscamTextFormatter.Format(_draft.Heart.KisscamText, _kisscamTestSeconds);

    /// <summary>Gasttext einblenden: solange mehr als <see cref="SettingsLimits.KisscamFinalDigitsSeconds"/> Sekunden übrig sind.</summary>
    public bool KisscamTestShowText =>
        _kisscamTestRunning && !_kisscamTestPhotoBadge && _kisscamTestSeconds > SettingsLimits.KisscamFinalDigitsSeconds;

    /// <summary>Die großen Ziffern der letzten Sekunden stehen in der Vorschau.</summary>
    public bool KisscamTestShowDigits =>
        _kisscamTestRunning && !_kisscamTestPhotoBadge && _kisscamTestSeconds >= 1 && _kisscamTestSeconds <= SettingsLimits.KisscamFinalDigitsSeconds;

    /// <summary>Die Ziffer der letzten Sekunden (3, 2, 1).</summary>
    public string KisscamTestDigits => KisscamTestShowDigits ? _kisscamTestSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Abzeichen „Foto würde jetzt ausgelöst 📷“ am Ende des Tests (kurz sichtbar, danach endet der Test).</summary>
    public bool KisscamTestPhotoBadgeVisible => _kisscamTestPhotoBadge;

    /// <summary>Rahmen aus dem Entwurf (Banner und Farbe) für die Vorschau; null, solange kein Test läuft.</summary>
    public PhotoFrame? KisscamTestFrame
    {
        get
        {
            if (!_kisscamTestRunning)
                return null;
            var banner = string.IsNullOrWhiteSpace(_draft.Heart.KisscamBannerText) ? HeartSettings.DefaultKisscamBannerText : _draft.Heart.KisscamBannerText;
            return new PhotoFrame(PhotoFrameKind.Kisscam, banner, _draft.Heart.KisscamColor);
        }
    }

    private void ToggleKisscamTest()
    {
        if (_kisscamTestRunning)
            EndKisscamTest(completed: false);
        else
            StartKisscamTest();
    }

    // Der Test lebt nur auf der Seite „Herz“ (Seiten- und Tabwechsel beenden ihn); er löst nie ein Foto, einen Druck oder eine Aufnahme aus.
    private void StartKisscamTest()
    {
        if (_disposed || _kisscamTestRunning || !(_isFunTabSelected && _funPage == FunPage.Heart))
            return;

        // Der Player gehört beiden Anhör-Wegen: das Anhören der Musik endet, bevor der Test sie spielt.
        StopKisscamMusicPreview();
        var generation = ++_kisscamTestGeneration;
        StartKisscamTestMusic();
        SetKisscamTestState(running: true,
            Math.Clamp(_draft.Heart.KisscamSeconds, SettingsLimits.KisscamSecondsMin, SettingsLimits.KisscamSecondsMax), photoBadge: false);
        _kisscamTestTimer = _time.CreateTimer(_ => PostPeace(() => KisscamTestTickElapsed(generation)), null, KisscamTestTick, KisscamTestTick);
        AppLog.Info("Admin-Vorschau: Kisscam-Test gestartet.");
    }

    private void StartKisscamTestMusic()
    {
        var path = _draft.Heart.KisscamMusicPath;
        if (_music is null || string.IsNullOrWhiteSpace(path) || _kisscamMusicMissing)
            return;
        // Vor dem Play setzen: Ein Fehlschlag kann schon während des Aufrufs gemeldet werden.
        _kisscamTestMusic = true;
        try
        {
            _music.Play(path, _draft.Heart.KisscamMusicVolumePercent, loop: true);
        }
        catch (Exception ex)
        {
            _kisscamTestMusic = false;
            AppLog.Warn("Kisscam-Musik konnte im Test nicht gestartet werden.", ex);
        }
    }

    private void KisscamTestTickElapsed(int generation)
    {
        if (_disposed || generation != _kisscamTestGeneration || !_kisscamTestRunning || _kisscamTestPhotoBadge)
            return;

        var next = _kisscamTestSeconds - 1;
        if (next > 0)
        {
            SetKisscamTestState(running: true, next, photoBadge: false);
            return;
        }

        // Der Auslösemoment: In der Gastansicht entstünde jetzt das Foto; hier erscheint nur das Abzeichen. Die Musik blendet aus.
        DisposeKisscamTestTimer();
        if (_kisscamTestMusic)
        {
            _kisscamTestMusic = false;
            try
            {
                _music?.FadeOut(KisscamTestMusicFadeOut);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Kisscam-Musik konnte im Test nicht ausgeblendet werden.", ex);
            }
        }
        SetKisscamTestState(running: true, 0, photoBadge: true);
        _kisscamTestTimer = _time.CreateTimer(
            _ => PostPeace(() =>
            {
                if (!_disposed && generation == _kisscamTestGeneration)
                    EndKisscamTest(completed: true);
            }), null, KisscamTestPhotoBadgeDuration, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Beendet den Kisscam-Test (Ablauf, Knopf, Mittelfinger, Seiten-/Tabwechsel, Herz-Schalter aus, Entwurfswechsel, Schließen):
    /// Timer weg, Musik gestoppt (falls der Test sie gestartet hat), Rahmen und Abzeichen verschwinden. Ohne laufenden Test wirkungslos.
    /// </summary>
    internal void EndKisscamTest(bool completed)
    {
        _kisscamTestGeneration++;
        DisposeKisscamTestTimer();
        if (!_kisscamTestRunning)
            return;

        if (_kisscamTestMusic)
        {
            _kisscamTestMusic = false;
            try
            {
                _music?.Stop();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Kisscam-Musik konnte im Test nicht gestoppt werden.", ex);
            }
        }
        SetKisscamTestState(running: false, 0, photoBadge: false);
        AppLog.Info(completed ? "Admin-Vorschau: Kisscam-Test beendet." : "Admin-Vorschau: Kisscam-Test abgebrochen.");
    }

    private void DisposeKisscamTestTimer()
    {
        var timer = _kisscamTestTimer;
        _kisscamTestTimer = null;
        timer?.Dispose();
    }

    private void SetKisscamTestState(bool running, int seconds, bool photoBadge)
    {
        var runningChanged = _kisscamTestRunning != running;
        _kisscamTestRunning = running;
        _kisscamTestSeconds = seconds;
        _kisscamTestPhotoBadge = photoBadge;
        foreach (var name in new[]
        {
            nameof(KisscamTestRunning), nameof(KisscamTestSeconds), nameof(KisscamTestText), nameof(KisscamTestShowText),
            nameof(KisscamTestShowDigits), nameof(KisscamTestDigits), nameof(KisscamTestPhotoBadgeVisible),
            nameof(KisscamTestFrame), nameof(KisscamTestButtonText),
        })
            OnPropertyChanged(name);
        if (!runningChanged)
            return;
        ToggleKisscamMusicPreviewCommand.NotifyCanExecuteChanged();
        // Der laufende Test ist für die Mittelfingererkennung ein aufhebbares Ziel (external im SetMode).
        if (PeacePreview.Mode != PeaceMode.Off)
            PushPeacePreviewMode();
    }

    // ---------------------------------------------------------------- Live-Vorschau: Statuszeile

    /// <summary>Statuszeile unter der Vorschau der Seite „Herz“ (Hände, Gesichter, Rate, „Herz erkannt ❤“).</summary>
    public string HeartPreviewStatusText => HeartPreviewStatus.Text;

    public string HeartPreviewWarningText => HeartPreviewStatus.Warning ?? string.Empty;

    public bool HasHeartPreviewWarning => HeartPreviewStatus.Warning is not null;

    /// <summary>Text des Abzeichens in der Vorschau: „Herz erkannt ❤ (Klassisch)“ (leer, solange kein Herz erkannt ist).</summary>
    public string HeartPreviewBadgeText =>
        PeacePreview.Mode != PeaceMode.Off && PeacePreview.HeartDetectedNow
            ? PeacePreviewStatusFormatter.HeartBadge(PeacePreview.HeartDetectedShape)
            : string.Empty;

    private PeacePreviewStatusLines HeartPreviewStatus =>
        PeacePreview.Mode == PeaceMode.Off
            ? new PeacePreviewStatusLines(string.Empty, null)
            : PeacePreviewStatusFormatter.Format(PeacePreview.Status, PeacePreview.Hands.Count, PeacePreview.FaceCount,
                peaceNow: false, FunPreviewCameraRunning, heartNow: PeacePreview.HeartDetectedNow);

    private void RaiseHeartPreviewStatus()
    {
        OnPropertyChanged(nameof(HeartPreviewStatusText));
        OnPropertyChanged(nameof(HeartPreviewWarningText));
        OnPropertyChanged(nameof(HasHeartPreviewWarning));
        OnPropertyChanged(nameof(HeartPreviewBadgeText));
    }

    // Ein Herz wurde in der Vorschau lange genug gehalten: startet den Kisscam-Test (nur auf der Seite „Herz“, nie ein Foto).
    private void OnPeacePreviewHeart(object? sender, HeartTrigger trigger)
    {
        if (_disposed || !(_isFunTabSelected && _funPage == FunPage.Heart))
            return;
        StartKisscamTest();
    }

    // Zustands-Properties der Herz-Seite: sie sind keine Einstellungen und löschen „Einstellungen gespeichert“ nicht.
    private static bool IsHeartViewStateProperty(string? propertyName) =>
        propertyName is nameof(HeartStatusText) or nameof(HeartHasNoShape) or nameof(IsKisscamActionSelected)
            or nameof(KisscamMusicPreviewPlaying) or nameof(KisscamMusicPreviewButtonText) or nameof(KisscamMusicMissing)
            or nameof(KisscamTextProblems) or nameof(KisscamTextProblemsText) or nameof(HasKisscamTextProblems)
            or nameof(KisscamFileNameExample) or nameof(KisscamMusicPlayFailed) or nameof(HasKisscamOnlyCountdownHint)
        || (propertyName is not null && propertyName.StartsWith("HeartShape", StringComparison.Ordinal)
            && propertyName.EndsWith("Enabled", StringComparison.Ordinal))
        // Live-Vorschau und Kisscam-Test: reiner Zustand, keine Einstellung
        || (propertyName is not null && (propertyName.StartsWith("KisscamTest", StringComparison.Ordinal)
            || propertyName.StartsWith("HeartPreview", StringComparison.Ordinal)
            || propertyName.StartsWith("HasHeartPreview", StringComparison.Ordinal)));

    // ---------------------------------------------------------------- Validierung

    /// <summary>Herzerkennung: mindestens eine Form, gültige Texte und Dateinamen, alle Zahlen im Wertebereich.</summary>
    private void ValidateHeart(List<string> errors)
    {
        var h = _draft.Heart;
        // Nur bei eingeschalteter Erkennung, sonst ließen sich Fehler bei ausgegrauten Feldern nicht beheben.
        if (h.Enabled && !h.HasAnyShape)
            errors.Add("Wählen Sie für die Herzerkennung mindestens eine Herzform aus.");

        CheckPeaceRange(h.HoldMilliseconds, SettingsLimits.HeartHoldMillisecondsMin, SettingsLimits.HeartHoldMillisecondsMax, "Die Haltedauer der Herzerkennung", "Millisekunden", errors);
        CheckPeaceRange(h.SensitivityPercent, SettingsLimits.HeartSensitivityMin, SettingsLimits.HeartSensitivityMax, "Die Empfindlichkeit der Herzerkennung", "Prozent", errors);
        CheckPeaceRange(h.KisscamSeconds, SettingsLimits.KisscamSecondsMin, SettingsLimits.KisscamSecondsMax, "Die Dauer der Kisscam", "Sekunden", errors);
        CheckPeaceRange(h.KisscamZoomPercent, SettingsLimits.KisscamZoomPercentMin, SettingsLimits.KisscamZoomPercentMax, "Der Zoom der Kisscam", "Prozent", errors);
        CheckPeaceRange(h.KisscamMusicVolumePercent, SettingsLimits.KisscamMusicVolumeMin, SettingsLimits.KisscamMusicVolumeMax, "Die Lautstärke der Kisscam-Musik", "Prozent", errors);

        if (!h.Enabled || h.Action != HeartAction.Kisscam)
            return;

        if ((h.KisscamText?.Length ?? 0) > SettingsLimits.HintTextMaxLength)
            errors.Add($"Der Kisscam-Text darf höchstens {SettingsLimits.HintTextMaxLength} Zeichen lang sein.");
        if ((h.KisscamBannerText?.Length ?? 0) > SettingsLimits.KisscamBannerMaxLength)
            errors.Add($"Der Banner-Text der Kisscam darf höchstens {SettingsLimits.KisscamBannerMaxLength} Zeichen lang sein.");

        var prefix = h.KisscamFilePrefix;
        if (string.IsNullOrWhiteSpace(prefix))
            errors.Add("Das Dateipräfix der Kisscam darf nicht leer sein.");
        else if (prefix.Length > SettingsLimits.FilePrefixMaxLength || prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add($"Das Dateipräfix der Kisscam darf höchstens {SettingsLimits.FilePrefixMaxLength} Zeichen lang sein und keine Zeichen wie \\ / : * ? \" < > | enthalten.");

        if ((h.KisscamMusicPath?.Length ?? 0) > SettingsLimits.KisscamMusicPathMaxLength)
            errors.Add($"Der Pfad der Musikdatei darf höchstens {SettingsLimits.KisscamMusicPathMaxLength} Zeichen lang sein.");
    }
}
