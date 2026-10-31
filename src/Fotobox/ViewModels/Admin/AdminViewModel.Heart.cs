using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;
using Fotobox.Core.Heart;
using Fotobox.Core.Music;
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

    /// <summary>Hinweis, wenn der Musikordner leer ist (keine Musikdateien der erlaubten Formate).</summary>
    public const string KisscamMusicEmptyText = "Der Ordner ist leer – kopieren Sie Musikdateien (MP3, WAV, M4A, WMA) hinein.";

    /// <summary>Hinweis, wenn Songs vorhanden sind, aber keiner angehakt ist.</summary>
    public const string KisscamNoSongSelectedText = "Kein Song ausgewählt – die Kisscam läuft ohne Musik.";

    /// <summary>Hinweis, wenn Songs angehakt sind, aber keiner davon abspielbar ist.</summary>
    public const string KisscamNoPlayableSongText = "Kein abspielbarer Song ausgewählt – die Kisscam läuft ohne Musik.";

    /// <summary>Hinweis, wenn der Musikordner nicht angelegt oder gelesen werden kann.</summary>
    public const string KisscamMusicFolderUnavailableText =
        "Der Musikordner ist nicht erreichbar oder lässt sich nicht anlegen. Prüfen Sie den Pfad und klicken Sie auf „Aktualisieren“. Die Kisscam läuft solange ohne Musik.";

    /// <summary>Hinweis, wenn die Config des Musikordners unlesbar ist.</summary>
    public const string KisscamMusicConfigBrokenText =
        "Die Datei kisscam-musik.json im Musikordner ist unlesbar, die Kisscam läuft deshalb ohne Musik. Sobald Sie hier etwas ändern, wird sie neu angelegt; die alte Datei bleibt als kisscam-musik.json.defekt erhalten.";

    /// <summary>Hinweis, wenn die Songeinstellungen nicht gespeichert werden konnten.</summary>
    public const string KisscamMusicSaveFailedText =
        "Die Songeinstellungen konnten nicht in den Musikordner gespeichert werden. Prüfen Sie die Schreibrechte des Ordners.";

    private readonly IMusicPlayer? _music;
    private IKisscamMusicLibrary? _library;
    private IReadOnlyList<HeartActionOption>? _heartActionOptions;

    // Anspielen eines Songs: die Zeile, die Wiedergabe wurde gestartet (danach bedeutet „läuft nicht mehr“ = Ende), die zuletzt
    // gemeldete Position und der Timer, der nach der Kisscam-Dauer ausblendet.
    private KisscamSongRowViewModel? _previewRow;
    private bool _previewSawPlaying;
    private TimeSpan _previewPosition;
    private ITimer? _previewTimer;
    private int _previewGeneration;
    private string? _kisscamTestMusicPath;

    /// <summary>Quelle der Zufallswahl des Songs im Kisscam-Test. Für Tests austauschbar.</summary>
    internal Random KisscamRandom { get; set; } = Random.Shared;

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
            SetProperty(_draft.Heart.KisscamSeconds,
                Math.Clamp(value, SettingsLimits.KisscamSecondsMin, SettingsLimits.KisscamSecondsMax),
                v => _draft.Heart.KisscamSeconds = v);
        }
    }

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

    /// <summary>Gesamtlautstärke der Musik in Prozent (<see cref="SettingsLimits.KisscamMusicVolumeMin"/> bis <c>Max</c>); die Lautstärke eines Songs wirkt als Prozentsatz davon.</summary>
    public int KisscamMusicVolumePercent
    {
        get => _draft.Heart.KisscamMusicVolumePercent;
        set => SetProperty(_draft.Heart.KisscamMusicVolumePercent,
            Math.Clamp(value, SettingsLimits.KisscamMusicVolumeMin, SettingsLimits.KisscamMusicVolumeMax),
            v => _draft.Heart.KisscamMusicVolumePercent = v);
    }

    /// <summary>Anzuzeigender Musikordner (Standard: Musik\Fotobox+\Kisscam); gerätespezifisch, wird beim Teilen der Einstellungen nicht übertragen.</summary>
    public string KisscamMusicFolderDisplay => EffectiveKisscamMusicFolder;

    public bool IsDefaultKisscamMusicFolder => string.IsNullOrWhiteSpace(_draft.Heart.KisscamMusicFolder);

    public bool HasCustomKisscamMusicFolder => !IsDefaultKisscamMusicFolder;

    private string EffectiveKisscamMusicFolder =>
        string.IsNullOrWhiteSpace(_draft.Heart.KisscamMusicFolder) ? AppInfo.DefaultKisscamMusicFolder : _draft.Heart.KisscamMusicFolder;

    /// <summary>Die Songs des Musikordners (nach Dateinamen abgeglichen, nicht bei jeder Änderung neu erzeugt).</summary>
    public ObservableCollection<KisscamSongRowViewModel> KisscamSongs { get; } = [];

    /// <summary>Stand der Bibliothek (Laden, bereit, Ordner nicht erreichbar, Config defekt).</summary>
    public KisscamLibraryStatus KisscamMusicStatus => _library?.Current.Status ?? KisscamLibraryStatus.Loading;

    /// <summary>Hinweise zur Musik (leerer Ordner, kein Song gewählt, Ordner nicht erreichbar, Config defekt, Speichern fehlgeschlagen).</summary>
    public IReadOnlyList<string> KisscamMusicHints => BuildKisscamMusicHints(_library?.Current);

    public string KisscamMusicHintsText => string.Join(Environment.NewLine, KisscamMusicHints);

    public bool HasKisscamMusicHints => KisscamMusicHints.Count > 0;

    /// <summary>Öffnet die Ordnerauswahl für den Musikordner.</summary>
    public IRelayCommand ChooseKisscamMusicFolderCommand { get; private set; } = null!;

    /// <summary>Setzt den Musikordner auf den Standard zurück.</summary>
    public IRelayCommand ResetKisscamMusicFolderCommand { get; private set; } = null!;

    /// <summary>Öffnet den Musikordner im Explorer.</summary>
    public IAsyncRelayCommand OpenKisscamMusicFolderCommand { get; private set; } = null!;

    /// <summary>Liest den Musikordner und die Config neu.</summary>
    public IRelayCommand RefreshKisscamMusicCommand { get; private set; } = null!;

    private void InitHeart(IMusicPlayer? music, IKisscamMusicLibrary? library)
    {
        // Wird im Konstruktor aufgerufen (die get-only-Auto-Properties der Befehle sind dort noch nicht belegt).
        _library = library;
        ChooseKisscamMusicFolderCommand = new RelayCommand(ChooseKisscamMusicFolder);
        ResetKisscamMusicFolderCommand = new RelayCommand(ResetKisscamMusicFolder);
        OpenKisscamMusicFolderCommand = new AsyncRelayCommand(OpenKisscamMusicFolderAsync);
        RefreshKisscamMusicCommand = new RelayCommand(() => _library?.Refresh(), () => _library is not null);
        ToggleKisscamTestCommand = new RelayCommand(ToggleKisscamTest, () => !_disposed);
        if (music is not null)
        {
            music.PlaybackChanged += OnMusicPlaybackChanged;
            music.PlaybackFailed += OnMusicPlaybackFailed;
            music.PositionChanged += OnMusicPositionChanged;
        }
        if (_library is not null)
        {
            _library.Changed += OnKisscamLibraryChanged;
            // Speichert etwas anderes den Store (z. B. die PIN-Änderung), setzt die Anwendung die Bibliothek auf den gespeicherten Ordner;
            // dieser Handler (später angemeldet) lenkt sie danach wieder auf den Ordner des Entwurfs.
            _store.SettingsChanged += OnStoreSettingsChanged;
            // Der Admin arbeitet auf dem Ordner des Entwurfs; beim Schließen geht die Bibliothek zurück auf den gespeicherten Ordner.
            _library.SetFolder(_draft.Heart.KisscamMusicFolder);
            ReconcileKisscamSongs();
        }
    }

    private void OnStoreSettingsChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
            _library?.SetFolder(_draft.Heart.KisscamMusicFolder);
    }

    /// <summary>Wendet den Ordner des Entwurfs auf die Bibliothek an (nach Wahl, Zurücksetzen oder Ersetzen des Entwurfs).</summary>
    private void ApplyKisscamMusicFolderToLibrary()
    {
        StopKisscamMusicPreview();
        RaiseKisscamMusicFolder();
        _library?.SetFolder(_draft.Heart.KisscamMusicFolder);
        ReconcileKisscamSongs();
    }

    private void RaiseKisscamMusicFolder()
    {
        OnPropertyChanged(nameof(KisscamMusicFolderDisplay));
        OnPropertyChanged(nameof(IsDefaultKisscamMusicFolder));
        OnPropertyChanged(nameof(HasCustomKisscamMusicFolder));
    }

    private void ChooseKisscamMusicFolder()
    {
        string? folder;
        try
        {
            folder = _ui.PickFolder(EffectiveKisscamMusicFolder, "Musikordner für die Kisscam auswählen");
        }
        catch (Exception ex)
        {
            AppLog.Error("Ordnerauswahl der Kisscam-Musik fehlgeschlagen.", ex);
            return;
        }

        if (string.IsNullOrWhiteSpace(folder))
            return;
        // Der Normalisierer würde zu lange Pfade stillschweigend verwerfen: lieber gleich melden.
        if (folder.Length > SettingsLimits.KisscamMusicFolderMaxLength || folder.Any(char.IsControl))
        {
            _ui.ShowInfo("Musikordner", $"Der Pfad des Ordners ist zu lang oder ungültig (höchstens {SettingsLimits.KisscamMusicFolderMaxLength} Zeichen). Bitte wählen Sie einen Ordner an einem kürzeren Ort aus.");
            return;
        }

        _draft.Heart.KisscamMusicFolder = folder;
        ApplyKisscamMusicFolderToLibrary();
    }

    private void ResetKisscamMusicFolder()
    {
        if (_draft.Heart.KisscamMusicFolder is null)
            return;
        _draft.Heart.KisscamMusicFolder = null;
        ApplyKisscamMusicFolderToLibrary();
    }

    // Öffnet den Musikordner im Explorer, aber nur, wenn er existiert (die Bibliothek legt ihn selbst an); mit Frist.
    private async Task OpenKisscamMusicFolderAsync()
    {
        var folder = EffectiveKisscamMusicFolder;
        var exists = await FolderProbe.ExistsAsync(folder, FolderProbeTimeout);
        if (_disposed)
            return;
        if (exists is null)
        {
            ValidationMessage = "Der Ordner ist nicht erreichbar (Zeitüberschreitung).";
            return;
        }

        if (!exists.Value)
        {
            ValidationMessage = "Der Ordner existiert noch nicht. Klicken Sie auf „Aktualisieren“, um ihn anzulegen.";
            return;
        }

        try
        {
            _ui.OpenFolder(folder);
        }
        catch (Exception ex)
        {
            AppLog.Error("Musikordner konnte nicht geöffnet werden.", ex);
            ValidationMessage = "Der Ordner konnte nicht geöffnet werden.";
        }
    }

    // Die Bibliothek meldet auf einem beliebigen Thread: in den UI-Kontext holen (wie PostPeace); ohne Kontext (Tests) sofort.
    private void OnKisscamLibraryChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        PostPeace(() =>
        {
            if (!_disposed)
                ReconcileKisscamSongs();
        });
    }

    // Gleicht die Zeilen mit dem Stand der Bibliothek ab (nach Dateinamen): vorhandene Zeilen werden aktualisiert und bei Bedarf
    // verschoben, neue eingefügt, fehlende entfernt. So bleibt der Fokus erhalten.
    private void ReconcileKisscamSongs()
    {
        var snapshot = _library?.Current ?? KisscamLibrarySnapshot.Empty;
        // Namen, die sich nur in der Groß-/Kleinschreibung unterscheiden (case-sensitive Ordner), erscheinen einmal: der erste gewinnt.
        var wanted = snapshot.Songs.DistinctBy(s => s.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        var byName = new Dictionary<string, KisscamSongRowViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in KisscamSongs)
            byName.TryAdd(existing.FileName, existing);

        for (var i = 0; i < wanted.Count; i++)
        {
            var song = wanted[i];
            if (byName.TryGetValue(song.FileName, out var row))
            {
                row.Apply(song);
                var current = KisscamSongs.IndexOf(row);
                if (current != i)
                    KisscamSongs.Move(current, i);
            }
            else
            {
                KisscamSongs.Insert(i, new KisscamSongRowViewModel(this, song));
            }
        }

        while (KisscamSongs.Count > wanted.Count)
        {
            var last = KisscamSongs[^1];
            if (ReferenceEquals(last, _previewRow))
                StopKisscamMusicPreview();
            KisscamSongs.RemoveAt(KisscamSongs.Count - 1);
        }

        OnPropertyChanged(nameof(KisscamMusicStatus));
        OnPropertyChanged(nameof(KisscamMusicHints));
        OnPropertyChanged(nameof(KisscamMusicHintsText));
        OnPropertyChanged(nameof(HasKisscamMusicHints));
    }

    private static IReadOnlyList<string> BuildKisscamMusicHints(KisscamLibrarySnapshot? snapshot)
    {
        var hints = new List<string>();
        if (snapshot is null)
            return hints;
        switch (snapshot.Status)
        {
            case KisscamLibraryStatus.FolderUnavailable:
                hints.Add(KisscamMusicFolderUnavailableText);
                break;
            case KisscamLibraryStatus.ConfigBroken:
                hints.Add(KisscamMusicConfigBrokenText);
                break;
            case KisscamLibraryStatus.Ready when snapshot.Songs.Count == 0:
                hints.Add(KisscamMusicEmptyText);
                break;
            case KisscamLibraryStatus.Ready when !snapshot.HasEnabledSong:
                hints.Add(KisscamNoSongSelectedText);
                break;
            case KisscamLibraryStatus.Ready when !snapshot.Songs.Any(s => s.Enabled && s.Playable):
                hints.Add(KisscamNoPlayableSongText);
                break;
        }
        if (snapshot.SaveFailed)
            hints.Add(KisscamMusicSaveFailedText);
        return hints;
    }

    // ---- Songliste: Änderungen, Anspielen

    // Eine Zeile hat sich geändert: in die Bibliothek (Cache sofort, Config entprellt) – unabhängig vom „Speichern“ des Admins.
    internal void UpdateKisscamSong(KisscamSongRowViewModel row, bool? enabled = null, int? startSeconds = null, int? volumePercent = null)
    {
        if (_disposed)
            return;
        try
        {
            _library?.UpdateSong(row.FileName, enabled, startSeconds, volumePercent);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kisscam-Songeinstellung konnte nicht übernommen werden.", ex);
        }
    }

    /// <summary>
    /// „Anspielen“: spielt den Song ab seiner Startzeit für die Dauer der Kisscam und blendet dann aus. Es läuft nur ein Anspielen;
    /// ein anderer Song, der Kisscam-Test, ein Ordnerwechsel, ein Tabwechsel und das Schließen beenden es.
    /// </summary>
    internal void ToggleKisscamSongPreview(KisscamSongRowViewModel row)
    {
        if (_disposed || _music is null)
            return;
        if (ReferenceEquals(_previewRow, row))
        {
            StopKisscamMusicPreview();
            return;
        }

        StopKisscamMusicPreview();
        EndKisscamTest(completed: false);
        var generation = ++_previewGeneration;
        _previewRow = row;
        _previewSawPlaying = false;
        _previewPosition = TimeSpan.Zero;
        row.IsPreviewing = true;
        var volume = KisscamSongPicker.EffectiveVolume(_draft.Heart.KisscamMusicVolumePercent, row.VolumePercent);
        try
        {
            _music.Play(row.FullPath, volume, loop: true, row.Song.EffectiveStart);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kisscam-Song konnte nicht angespielt werden.", ex);
            ClearKisscamPreviewState();
            return;
        }

        var seconds = Math.Clamp(_draft.Heart.KisscamSeconds, SettingsLimits.KisscamSecondsMin, SettingsLimits.KisscamSecondsMax);
        _previewTimer = _time.CreateTimer(_ => PostPeace(() => KisscamPreviewElapsed(generation)), null,
            TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    // Die Kisscam-Dauer ist um: ausblenden; das Ende der Wiedergabe räumt den Zustand auf.
    private void KisscamPreviewElapsed(int generation)
    {
        if (_disposed || generation != _previewGeneration || _previewRow is null)
            return;
        DisposeKisscamPreviewTimer();
        try
        {
            _music?.FadeOut(KisscamTestMusicFadeOut);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kisscam-Song konnte nicht ausgeblendet werden.", ex);
        }
        // Falls der Player kein Ende meldet (z. B. nie gestartet), räumt die Frist nach dem Ausblenden auf.
        _previewTimer = _time.CreateTimer(_ => PostPeace(() =>
        {
            if (!_disposed && generation == _previewGeneration)
                ClearKisscamPreviewState();
        }), null, KisscamTestMusicFadeOut + TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
    }

    /// <summary>„Position übernehmen“: Die zuletzt gemeldete Position des laufenden Anspielens wird zur Startzeit des Songs.</summary>
    internal void TakeKisscamSongPreviewPosition(KisscamSongRowViewModel row)
    {
        if (_disposed || !ReferenceEquals(_previewRow, row))
            return;
        row.StartSeconds = (int)Math.Floor(_previewPosition.TotalSeconds);
    }

    /// <summary>
    /// Beendet das Anspielen eines Songs – aber nur, wenn diese Ansicht es gestartet hat (der Player gehört auch der Gastansicht).
    /// Wird beim Schließen, bei Tab- und Seitenwechsel, bei Ordnerwechsel, beim Kisscam-Test und beim Ersetzen des Entwurfs aufgerufen.
    /// </summary>
    internal void StopKisscamMusicPreview()
    {
        if (_previewRow is null)
            return;
        ClearKisscamPreviewState();
        try
        {
            _music?.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kisscam-Song konnte nicht gestoppt werden.", ex);
        }
    }

    private void ClearKisscamPreviewState()
    {
        ++_previewGeneration;
        DisposeKisscamPreviewTimer();
        var row = _previewRow;
        _previewRow = null;
        _previewSawPlaying = false;
        if (row is not null)
            row.IsPreviewing = false;
    }

    private void DisposeKisscamPreviewTimer()
    {
        var timer = _previewTimer;
        _previewTimer = null;
        timer?.Dispose();
    }

    // Kann auf jedem Thread kommen: in den UI-Kontext holen (wie PostPeace); ohne Kontext (Tests) sofort.
    private void OnMusicPlaybackChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        PostPeace(HandleMusicPlaybackChanged);
    }

    // Der Player meldet einen gescheiterten Start (Datei fehlt/kaputt, Öffnungsfrist verpasst): kein PlaybackChanged, weil nie gespielt
    // wurde – ohne diese Meldung bliebe der Knopf auf „Stopp“ stehen. Die Bibliothek markiert den Song selbst als nicht abspielbar.
    private void OnMusicPlaybackFailed(object? sender, MusicPlaybackFailedEventArgs e)
    {
        if (_disposed)
            return;
        PostPeace(() => HandleMusicPlaybackFailed(e.Path));
    }

    private void OnMusicPositionChanged(object? sender, MusicPositionEventArgs e)
    {
        if (_disposed)
            return;
        PostPeace(() => HandleMusicPosition(e));
    }

    private void HandleMusicPlaybackFailed(string path)
    {
        if (_disposed)
            return;
        // Der Player gehört auch der Gastansicht: nur reagieren, wenn diese Ansicht die Musik gestartet hat.
        if (_previewRow is { } row && string.Equals(row.FullPath, path, StringComparison.OrdinalIgnoreCase))
            ClearKisscamPreviewState();
        if (_kisscamTestMusic && string.Equals(_kisscamTestMusicPath, path, StringComparison.OrdinalIgnoreCase))
            _kisscamTestMusic = false;
    }

    private void HandleMusicPlaybackChanged()
    {
        if (_disposed || _previewRow is null || _music is null)
            return;
        if (_music.IsPlaying)
            _previewSawPlaying = true;
        else if (_previewSawPlaying)
            ClearKisscamPreviewState(); // Die Wiedergabe ist zu Ende (oder wurde von anderer Stelle gestoppt).
    }

    private void HandleMusicPosition(MusicPositionEventArgs e)
    {
        if (_disposed || _previewRow is not { } row || !string.Equals(row.FullPath, e.Path, StringComparison.OrdinalIgnoreCase))
            return;
        _previewPosition = e.Position;
        row.PositionText = KisscamTimeFormat.Format((int)Math.Floor(e.Position.TotalSeconds));
    }

    private void DisposeHeart()
    {
        if (_music is not null)
        {
            _music.PlaybackChanged -= OnMusicPlaybackChanged;
            _music.PlaybackFailed -= OnMusicPlaybackFailed;
            _music.PositionChanged -= OnMusicPositionChanged;
        }
        EndKisscamTest(completed: false);
        StopKisscamMusicPreview();
        if (_library is not null)
        {
            _library.Changed -= OnKisscamLibraryChanged;
            _store.SettingsChanged -= OnStoreSettingsChanged;
            // Die Bibliothek wieder auf den gespeicherten Ordner zurücksetzen (auch beim Abbrechen: ein ungespeicherter Entwurfsordner gilt nicht).
            _library.SetFolder(_store.Current.Heart.KisscamMusicFolder);
        }
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

    /// <summary>Gasttext einblenden: den ganzen Test über, bis das Abzeichen „Foto würde jetzt ausgelöst“ kommt (wie in der Gastansicht).</summary>
    public bool KisscamTestShowText => _kisscamTestRunning && !_kisscamTestPhotoBadge;

    /// <summary>Die großen Ziffern stehen in der Vorschau, solange noch mindestens eine Sekunde übrig ist (wie in der Gastansicht).</summary>
    public bool KisscamTestShowDigits => _kisscamTestRunning && !_kisscamTestPhotoBadge && _kisscamTestSeconds >= 1;

    /// <summary>Die aktuelle Ziffer des Countdowns (… 3, 2, 1).</summary>
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

        // Der Player gehört beiden Wegen: das Anspielen eines Songs endet, bevor der Test Musik spielt.
        StopKisscamMusicPreview();
        var generation = ++_kisscamTestGeneration;
        StartKisscamTestMusic();
        SetKisscamTestState(running: true,
            Math.Clamp(_draft.Heart.KisscamSeconds, SettingsLimits.KisscamSecondsMin, SettingsLimits.KisscamSecondsMax), photoBadge: false);
        _kisscamTestTimer = _time.CreateTimer(_ => PostPeace(() => KisscamTestTickElapsed(generation)), null, KisscamTestTick, KisscamTestTick);
        AppLog.Info("Admin-Vorschau: Kisscam-Test gestartet.");
    }

    // Wie in der Gastansicht: rein zufällige Wahl (Wiederholung erlaubt) unter den angehakten, abspielbaren Songs aus dem Stand der
    // Bibliothek (kein Dateizugriff); keiner = Stille.
    private void StartKisscamTestMusic()
    {
        if (_music is null)
            return;
        var pick = KisscamSongPicker.Pick(_library?.Current, _draft.Heart.KisscamMusicVolumePercent, KisscamRandom);
        if (pick is null)
        {
            AppLog.Info("Admin-Vorschau: Kisscam-Test ohne Musik (kein abspielbarer Song ausgewählt).");
            return;
        }

        // Vor dem Play setzen: Ein Fehlschlag kann schon während des Aufrufs gemeldet werden.
        _kisscamTestMusic = true;
        _kisscamTestMusicPath = pick.Path;
        try
        {
            _music.Play(pick.Path, pick.VolumePercent, loop: true, pick.StartAt);
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
            or nameof(KisscamMusicStatus) or nameof(KisscamMusicHints) or nameof(KisscamMusicHintsText) or nameof(HasKisscamMusicHints)
            or nameof(KisscamTextProblems) or nameof(KisscamTextProblemsText) or nameof(HasKisscamTextProblems)
            or nameof(KisscamFileNameExample)
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

        if ((h.KisscamMusicFolder?.Length ?? 0) > SettingsLimits.KisscamMusicFolderMaxLength)
            errors.Add($"Der Pfad des Musikordners darf höchstens {SettingsLimits.KisscamMusicFolderMaxLength} Zeichen lang sein.");
    }
}
