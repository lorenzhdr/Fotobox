using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fotobox.Core.Music;

namespace Fotobox.ViewModels.Admin;

/// <summary>
/// Eine Zeile der Songliste im Tab „Herz“: Häkchen, Startzeit, Song-Lautstärke, Länge, Warnungen sowie „Anspielen“ und „Position
/// übernehmen“. Die Werte stehen in der Musikbibliothek (Config im Musikordner); die Zeile ändert sie dort sofort und wird bei jeder
/// Änderung der Bibliothek abgeglichen (<see cref="Apply"/>), nicht neu erzeugt – so bleibt der Fokus erhalten.
/// </summary>
public sealed class KisscamSongRowViewModel : ObservableObject
{
    /// <summary>Warnung, wenn die Startzeit hinter dem Songende liegt.</summary>
    public const string StartBeyondEndText = "Startzeit liegt hinter dem Songende – es wird ab 0:00 gespielt";

    /// <summary>Warnung, wenn der Player die Datei nicht abspielen konnte.</summary>
    public const string NotPlayableText = "Datei lässt sich nicht abspielen";

    private readonly AdminViewModel _owner;
    private KisscamSong _song;
    private bool _isPreviewing;
    private string _positionText = string.Empty;

    internal KisscamSongRowViewModel(AdminViewModel owner, KisscamSong song)
    {
        _owner = owner;
        _song = song;
        StartEarlierCommand = new RelayCommand(() => StartSeconds -= 1);
        StartLaterCommand = new RelayCommand(() => StartSeconds += 1);
        PreviewCommand = new RelayCommand(() => _owner.ToggleKisscamSongPreview(this));
        TakePositionCommand = new RelayCommand(() => _owner.TakeKisscamSongPreviewPosition(this));
    }

    /// <summary>Reiner Dateiname (Schlüssel in der Config).</summary>
    public string FileName => _song.FileName;

    public string FullPath => _song.FullPath;

    /// <summary>Häkchen: Der Song darf in der Kisscam gespielt werden.</summary>
    public bool Enabled
    {
        get => _song.Enabled;
        set
        {
            if (value == _song.Enabled)
                return;
            _song = _song with { Enabled = value };
            OnPropertyChanged();
            _owner.UpdateKisscamSong(this, enabled: value);
        }
    }

    /// <summary>Startzeit in Sekunden (<see cref="Fotobox.Core.SettingsLimits.KisscamSongStartSecondsMin"/> bis <c>Max</c>).</summary>
    public int StartSeconds
    {
        get => _song.StartSeconds;
        set
        {
            var clamped = KisscamMusicConfigCodec.ClampStart(value);
            if (clamped == _song.StartSeconds)
                return;
            _song = _song with { StartSeconds = clamped };
            RaiseStart();
            _owner.UpdateKisscamSong(this, startSeconds: clamped);
        }
    }

    /// <summary>Startzeit als „m:ss“; die Eingabe versteht auch eine reine Sekundenzahl, ungültiger Text wird verworfen.</summary>
    public string StartText
    {
        get => KisscamTimeFormat.Format(_song.StartSeconds);
        set
        {
            if (KisscamTimeFormat.TryParse(value, out var seconds))
                StartSeconds = seconds;
            // Die Anzeige immer zurück auf die gültige Schreibweise bzw. den alten Wert setzen.
            OnPropertyChanged();
        }
    }

    /// <summary>Song-Lautstärke in Prozent der Gesamtlautstärke (<see cref="Fotobox.Core.SettingsLimits.KisscamSongVolumeMin"/> bis <c>Max</c>).</summary>
    public int VolumePercent
    {
        get => _song.VolumePercent;
        set
        {
            var clamped = KisscamMusicConfigCodec.ClampVolume(value);
            if (clamped == _song.VolumePercent)
                return;
            _song = _song with { VolumePercent = clamped };
            OnPropertyChanged();
            _owner.UpdateKisscamSong(this, volumePercent: clamped);
        }
    }

    /// <summary>Länge des Songs als „m:ss“ (unbekannt: „–“).</summary>
    public string DurationText => KisscamTimeFormat.FormatDuration(_song.Duration);

    /// <summary>Die Startzeit liegt hinter dem Songende (Warnung).</summary>
    public bool StartBeyondEnd => _song.StartBeyondEnd;

    /// <summary>Der Player konnte die Datei nicht abspielen (Warnung).</summary>
    public bool NotPlayable => !_song.Playable;

    /// <summary>Der Song wird gerade angespielt.</summary>
    public bool IsPreviewing
    {
        get => _isPreviewing;
        internal set
        {
            if (!SetProperty(ref _isPreviewing, value))
                return;
            if (!value)
                PositionText = string.Empty;
            OnPropertyChanged(nameof(PreviewButtonText));
        }
    }

    /// <summary>„Stopp“ während des Anspielens, sonst „Anspielen“.</summary>
    public string PreviewButtonText => _isPreviewing ? "Stopp" : "Anspielen";

    /// <summary>Aktuelle Position beim Anspielen als „m:ss“ (leer, solange nichts läuft).</summary>
    public string PositionText
    {
        get => _positionText;
        internal set => SetProperty(ref _positionText, value);
    }

    public IRelayCommand StartEarlierCommand { get; }

    public IRelayCommand StartLaterCommand { get; }

    /// <summary>Spielt den Song ab der Startzeit an bzw. beendet das Anspielen.</summary>
    public IRelayCommand PreviewCommand { get; }

    /// <summary>Übernimmt die Position des laufenden Anspielens als Startzeit.</summary>
    public IRelayCommand TakePositionCommand { get; }

    internal KisscamSong Song => _song;

    /// <summary>Übernimmt den Stand der Bibliothek, ohne ihn zurückzumelden.</summary>
    internal void Apply(KisscamSong song)
    {
        var old = _song;
        if (old == song)
            return;
        _song = song;
        if (old.Enabled != song.Enabled)
            OnPropertyChanged(nameof(Enabled));
        if (old.StartSeconds != song.StartSeconds || old.Duration != song.Duration)
            RaiseStart();
        if (old.VolumePercent != song.VolumePercent)
            OnPropertyChanged(nameof(VolumePercent));
        if (old.Duration != song.Duration)
            OnPropertyChanged(nameof(DurationText));
        if (old.Playable != song.Playable)
            OnPropertyChanged(nameof(NotPlayable));
        if (old.FullPath != song.FullPath)
            OnPropertyChanged(nameof(FullPath));
    }

    private void RaiseStart()
    {
        OnPropertyChanged(nameof(StartSeconds));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(StartBeyondEnd));
    }
}
