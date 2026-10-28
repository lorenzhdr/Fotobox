using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Booth;

namespace Fotobox.ViewModels.Admin;

// Peaceerkennung – Entwurfs-Properties, Vorschau und Testknöpfe
public sealed partial class AdminViewModel
{
    /// <summary>Dauer des Test-Countdowns „Selbstzerstörung testen“ in Sekunden (unabhängig von der eingestellten Dauer).</summary>
    internal const int PeaceTestSelfDestructSeconds = 5;

    private static readonly TimeSpan PeaceTestTick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PeacePhotoBadgeDuration = TimeSpan.FromSeconds(2);

    // ---------------------------------------------------------------- Entwurfswerte

    /// <summary>Peaceerkennung aktiv; die Einstellungen darunter sind sonst ausgegraut.</summary>
    public bool PeaceEnabled
    {
        get => _draft.Peace.Enabled;
        set
        {
            if (SetProperty(_draft.Peace.Enabled, value, v => _draft.Peace.Enabled = v))
            {
                OnPropertyChanged(nameof(PeaceStatusText));
                OnPropertyChanged(nameof(PeaceHasNoAction));
            }
        }
    }

    public bool PeaceActionSelfDestruct
    {
        get => _draft.Peace.ActionSelfDestruct;
        set
        {
            if (SetProperty(_draft.Peace.ActionSelfDestruct, value, v => _draft.Peace.ActionSelfDestruct = v))
                OnPropertyChanged(nameof(PeaceHasNoAction));
        }
    }

    public bool PeaceActionPhoto
    {
        get => _draft.Peace.ActionPhoto;
        set
        {
            if (SetProperty(_draft.Peace.ActionPhoto, value, v => _draft.Peace.ActionPhoto = v))
                OnPropertyChanged(nameof(PeaceHasNoAction));
        }
    }

    public bool PeaceActionMoustache
    {
        get => _draft.Peace.ActionMoustache;
        set
        {
            if (SetProperty(_draft.Peace.ActionMoustache, value, v => _draft.Peace.ActionMoustache = v))
            {
                OnPropertyChanged(nameof(PeaceHasNoAction));
                OnPropertyChanged(nameof(HasPeacePreviewMoustacheWarning));
                OnPropertyChanged(nameof(PeacePreviewMoustacheWarning));
            }
        }
    }

    /// <summary>Peaceerkennung ist an, aber keine Aktion gewählt (Warnung; blockiert das Speichern).</summary>
    public bool PeaceHasNoAction => _draft.Peace.Enabled && !_draft.Peace.HasAnyAction;

    public int PeaceHoldMilliseconds
    {
        get => _draft.Peace.HoldMilliseconds;
        set => SetProperty(_draft.Peace.HoldMilliseconds, value, v => _draft.Peace.HoldMilliseconds = v);
    }

    public bool PeaceShowHoldProgress
    {
        get => _draft.Peace.ShowHoldProgress;
        set => SetProperty(_draft.Peace.ShowHoldProgress, value, v => _draft.Peace.ShowHoldProgress = v);
    }

    public int PeaceCooldownSeconds
    {
        get => _draft.Peace.CooldownSeconds;
        set => SetProperty(_draft.Peace.CooldownSeconds, value, v => _draft.Peace.CooldownSeconds = v);
    }

    public int PeaceSensitivityPercent
    {
        get => _draft.Peace.SensitivityPercent;
        set => SetProperty(_draft.Peace.SensitivityPercent, value, v => _draft.Peace.SensitivityPercent = v);
    }

    public int PeaceAnalysesPerSecond
    {
        get => _draft.Peace.AnalysesPerSecond;
        set => SetProperty(_draft.Peace.AnalysesPerSecond, value, v => _draft.Peace.AnalysesPerSecond = v);
    }

    public int PeaceSelfDestructSeconds
    {
        get => _draft.Peace.SelfDestructSeconds;
        set => SetProperty(_draft.Peace.SelfDestructSeconds, value, v => _draft.Peace.SelfDestructSeconds = v);
    }

    public bool PeaceSelfDestructSoundEnabled
    {
        get => _draft.Peace.SelfDestructSoundEnabled;
        set => SetProperty(_draft.Peace.SelfDestructSoundEnabled, value, v => _draft.Peace.SelfDestructSoundEnabled = v);
    }

    public int PeacePhotoCountdownSeconds
    {
        get => _draft.Peace.PhotoCountdownSeconds;
        set => SetProperty(_draft.Peace.PhotoCountdownSeconds, value, v => _draft.Peace.PhotoCountdownSeconds = v);
    }

    public MoustacheTargets PeaceMoustacheTargets
    {
        get => _draft.Peace.MoustacheTargets;
        set => SetProperty(_draft.Peace.MoustacheTargets, value, v => _draft.Peace.MoustacheTargets = v);
    }

    public MoustacheStyle PeaceMoustacheStyle
    {
        get => _draft.Peace.MoustacheStyle;
        set
        {
            if (SetProperty(_draft.Peace.MoustacheStyle, value, v => _draft.Peace.MoustacheStyle = v))
                OnPropertyChanged(nameof(PeaceRandomStylesVisible));
        }
    }

    /// <summary>Die Wahl „Beim Zufall verwenden“ ist nur bei „Zufällig je Person“ sichtbar.</summary>
    public bool PeaceRandomStylesVisible => _draft.Peace.MoustacheStyle == MoustacheStyle.Random;

    // ---- Zufallsliste: je Form ein Haken; der letzte Haken lässt sich nicht entfernen (Kontrollkästchen ist dann deaktiviert).

    public bool PeaceRandomTwirl { get => IsRandomStyle(MoustacheStyle.Twirl); set => SetRandomStyle(MoustacheStyle.Twirl, value); }
    public bool PeaceRandomWalrus { get => IsRandomStyle(MoustacheStyle.Walrus); set => SetRandomStyle(MoustacheStyle.Walrus, value); }
    public bool PeaceRandomChevron { get => IsRandomStyle(MoustacheStyle.Chevron); set => SetRandomStyle(MoustacheStyle.Chevron, value); }
    public bool PeaceRandomBrush { get => IsRandomStyle(MoustacheStyle.Brush); set => SetRandomStyle(MoustacheStyle.Brush, value); }
    public bool PeaceRandomHandlebar { get => IsRandomStyle(MoustacheStyle.Handlebar); set => SetRandomStyle(MoustacheStyle.Handlebar, value); }
    public bool PeaceRandomFuManchu { get => IsRandomStyle(MoustacheStyle.FuManchu); set => SetRandomStyle(MoustacheStyle.FuManchu, value); }
    public bool PeaceRandomHorseshoe { get => IsRandomStyle(MoustacheStyle.Horseshoe); set => SetRandomStyle(MoustacheStyle.Horseshoe, value); }

    public bool PeaceRandomTwirlEnabled => CanToggleRandomStyle(MoustacheStyle.Twirl);
    public bool PeaceRandomWalrusEnabled => CanToggleRandomStyle(MoustacheStyle.Walrus);
    public bool PeaceRandomChevronEnabled => CanToggleRandomStyle(MoustacheStyle.Chevron);
    public bool PeaceRandomBrushEnabled => CanToggleRandomStyle(MoustacheStyle.Brush);
    public bool PeaceRandomHandlebarEnabled => CanToggleRandomStyle(MoustacheStyle.Handlebar);
    public bool PeaceRandomFuManchuEnabled => CanToggleRandomStyle(MoustacheStyle.FuManchu);
    public bool PeaceRandomHorseshoeEnabled => CanToggleRandomStyle(MoustacheStyle.Horseshoe);

    private IReadOnlyList<MoustacheStyle> RandomStyleList => MoustacheStyles.NormalizeRandomStyles(_draft.Peace.RandomStyles);

    private bool IsRandomStyle(MoustacheStyle style) => RandomStyleList.Contains(style);

    // Der letzte gesetzte Haken ist gesperrt: Es muss immer mindestens eine Form übrig bleiben.
    private bool CanToggleRandomStyle(MoustacheStyle style) => !(IsRandomStyle(style) && RandomStyleList.Count <= 1);

    private void SetRandomStyle(MoustacheStyle style, bool on)
    {
        var current = RandomStyleList;
        if (current.Contains(style) != on)
        {
            var wanted = current.Where(s => s != style).ToList();
            if (on)
                wanted.Add(style);
            // Das Entfernen des letzten Hakens ist wirkungslos (die Ansicht liest den Haken danach neu).
            if (wanted.Count > 0)
                _draft.Peace.RandomStyles = MoustacheStyles.NormalizeRandomStyles(wanted);
        }
        RaiseRandomStyles();
    }

    private void RaiseRandomStyles()
    {
        foreach (var name in new[]
        {
            nameof(PeaceRandomTwirl), nameof(PeaceRandomWalrus), nameof(PeaceRandomChevron), nameof(PeaceRandomBrush),
            nameof(PeaceRandomHandlebar), nameof(PeaceRandomFuManchu), nameof(PeaceRandomHorseshoe),
            nameof(PeaceRandomTwirlEnabled), nameof(PeaceRandomWalrusEnabled), nameof(PeaceRandomChevronEnabled), nameof(PeaceRandomBrushEnabled),
            nameof(PeaceRandomHandlebarEnabled), nameof(PeaceRandomFuManchuEnabled), nameof(PeaceRandomHorseshoeEnabled),
        })
            OnPropertyChanged(name);
    }

    public MoustacheVisibility PeaceMoustacheVisibility
    {
        get => _draft.Peace.MoustacheVisibility;
        set
        {
            if (SetProperty(_draft.Peace.MoustacheVisibility, value, v => _draft.Peace.MoustacheVisibility = v))
                OnPropertyChanged(nameof(PeaceMoustacheDurationEnabled));
        }
    }

    /// <summary>Die feste Anzeigedauer des Schnurrbarts ist nur bei „Feste Dauer“ wirksam.</summary>
    public bool PeaceMoustacheDurationEnabled => _draft.Peace.MoustacheVisibility == MoustacheVisibility.FixedDuration;

    public int PeaceMoustacheDurationSeconds
    {
        get => _draft.Peace.MoustacheDurationSeconds;
        set => SetProperty(_draft.Peace.MoustacheDurationSeconds, value, v => _draft.Peace.MoustacheDurationSeconds = v);
    }

    public bool PeaceMoustacheAutoPhoto
    {
        get => _draft.Peace.MoustacheAutoPhoto;
        set => SetProperty(_draft.Peace.MoustacheAutoPhoto, value, v => _draft.Peace.MoustacheAutoPhoto = v);
    }

    public int PeaceMoustacheAutoPhotoCountdownSeconds
    {
        get => _draft.Peace.MoustacheAutoPhotoCountdownSeconds;
        set => SetProperty(_draft.Peace.MoustacheAutoPhotoCountdownSeconds, value, v => _draft.Peace.MoustacheAutoPhotoCountdownSeconds = v);
    }

    // ---------------------------------------------------------------- Startseite: Status der Kacheln

    /// <summary>„An“ bzw. „Aus“ für die Kachel „Personenerkennung“.</summary>
    public string FunPersonsStatusText => _draft.Fun.Enabled ? "An" : "Aus";

    /// <summary>„An“ bzw. „Aus“ für die Kachel „Peaceerkennung“.</summary>
    public string PeaceStatusText => _draft.Peace.Enabled ? "An" : "Aus";

    // ---------------------------------------------------------------- Vorschau

    /// <summary>Eigene Peaceerkennung der Vorschau (ohne Bart-Zustand der Gastansicht, damit ein Test nie in ein echtes Foto gelangt).</summary>
    public PeaceOverlayViewModel PeacePreview { get; }

    private bool _peacePreviewShowFacePoints;

    /// <summary>
    /// Zeigt in der Vorschau die fünf Gesichtspunkte (Augen, Nase, Mundwinkel) jedes Gesichts, um die Lage des Bartes zu beurteilen.
    /// Nur Anzeige: nicht gespeichert, nicht Teil des Entwurfs, Standard aus, nie in der Gastansicht.
    /// </summary>
    public bool PeacePreviewShowFacePoints
    {
        get => _peacePreviewShowFacePoints;
        set => SetProperty(ref _peacePreviewShowFacePoints, value);
    }

    /// <summary>Test-Knopf: Selbstzerstörungs-Countdown mit Explosion in der Vorschau, ohne Foto.</summary>
    public IRelayCommand TestSelfDestructCommand { get; }

    /// <summary>Test-Knopf: Schnurrbart in der Vorschau (ohne Foto oder Druck).</summary>
    public IRelayCommand TestMoustacheCommand { get; }

    private bool _peaceTestSelfDestructVisible;
    private int _peaceTestSecondsRemaining;
    private bool _peacePhotoBadgeVisible;
    private ITimer? _peaceTestTimer;
    private ITimer? _peaceBadgeTimer;
    private int _peaceTestGeneration;
    private int _peaceBadgeGeneration;

    /// <summary>Der Test-Countdown der Selbstzerstörung ist sichtbar.</summary>
    public bool PeaceTestSelfDestructVisible
    {
        get => _peaceTestSelfDestructVisible;
        private set => SetProperty(ref _peaceTestSelfDestructVisible, value);
    }

    /// <summary>Verbleibende Sekunden des Test-Countdowns.</summary>
    public int PeaceTestSecondsRemaining
    {
        get => _peaceTestSecondsRemaining;
        private set => SetProperty(ref _peaceTestSecondsRemaining, value);
    }

    /// <summary>Abzeichen „Foto würde ausgelöst“ (2 s), wenn in der Vorschau die Aktion Foto gewählt wurde.</summary>
    public bool PeacePhotoBadgeVisible
    {
        get => _peacePhotoBadgeVisible;
        private set => SetProperty(ref _peacePhotoBadgeVisible, value);
    }

    /// <summary>Der Test-Countdown erreicht null: Die Ansicht spielt die Explosion ab (<c>SelfDestructOverlay.PlayExplosion</c>).</summary>
    public event EventHandler? PeaceTestExplosionRequested;

    public string PeacePreviewStatusText => PeacePreviewStatus.Text;

    public string PeacePreviewWarningText => PeacePreviewStatus.Warning ?? string.Empty;

    public bool HasPeacePreviewWarning => PeacePreviewStatus.Warning is not null;

    /// <summary>Hinweis, wenn der Schnurrbart gewählt ist, die Vorschau aber meldet, dass die Gesichtserkennung nicht bereitsteht.</summary>
    public const string PeaceMoustacheUnavailableText =
        "Die Gesichtserkennung ist nicht verfügbar – der Schnurrbart wird nicht ausgeführt.";

    public string PeacePreviewMoustacheWarning => HasPeacePreviewMoustacheWarning ? PeaceMoustacheUnavailableText : string.Empty;

    // Nur bei laufender Vorschau: Ohne Anforderung (Modus aus) ist MoustacheAvailable noch unbekannt, nicht „nicht verfügbar“.
    public bool HasPeacePreviewMoustacheWarning =>
        _draft.Peace.ActionMoustache && PeacePreview.Mode != PeaceMode.Off && !PeacePreview.MoustacheAvailable;

    private PeacePreviewStatusLines PeacePreviewStatus =>
        PeacePreview.Mode == PeaceMode.Off
            ? new PeacePreviewStatusLines(string.Empty, null)
            : PeacePreviewStatusFormatter.Format(PeacePreview.Status, PeacePreview.Hands.Count, PeacePreview.FaceCount,
                PeacePreview.PeaceDetectedNow, FunPreviewCameraRunning);

    // Läuft die Peace-Seite, gelten die Entwurfswerte sofort. Nur auf der Seite „Peaceerkennung“ ist die Vorschau aktiv.
    private void UpdatePeacePreview()
    {
        if (_disposed)
            return;
        var active = _isFunTabSelected && _funPage == FunPage.Peace;
        if (!active)
            StopPeaceTests();
        PeacePreview.SetMode(active ? PeaceMode.Watching : PeaceMode.Off, _draft.Peace.Clone(), MirrorPreview);
        RaisePeacePreviewStatus();
    }

    private static bool AffectsPeacePreview(string? propertyName) =>
        string.IsNullOrEmpty(propertyName) // alle Eigenschaften (Standardwerte wiederhergestellt)
        || propertyName == nameof(MirrorPreview)
        || (propertyName.StartsWith("Peace", StringComparison.Ordinal)
            && !propertyName.StartsWith("PeacePreview", StringComparison.Ordinal)
            && !propertyName.StartsWith("PeaceTest", StringComparison.Ordinal)
            && !propertyName.StartsWith("PeacePhotoBadge", StringComparison.Ordinal)
            && propertyName is not (nameof(PeaceStatusText) or nameof(PeaceHasNoAction) or nameof(PeaceMoustacheDurationEnabled)));

    private void RaisePeacePreviewStatus()
    {
        OnPropertyChanged(nameof(PeacePreviewStatusText));
        OnPropertyChanged(nameof(PeacePreviewWarningText));
        OnPropertyChanged(nameof(HasPeacePreviewWarning));
        OnPropertyChanged(nameof(PeacePreviewMoustacheWarning));
        OnPropertyChanged(nameof(HasPeacePreviewMoustacheWarning));
    }

    private void OnPeacePreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PeaceOverlayViewModel.Status) or nameof(PeaceOverlayViewModel.Hands) or nameof(PeaceOverlayViewModel.FaceCount)
            or nameof(PeaceOverlayViewModel.MoustacheAvailable)
            or nameof(PeaceOverlayViewModel.PeaceDetectedNow) or nameof(PeaceOverlayViewModel.Mode) or nameof(PeaceOverlayViewModel.IsShown)
            or null or "")
            RaisePeacePreviewStatus();
    }

    // Aktion in der Vorschau: nie ein echtes Foto oder Druck.
    private void OnPeacePreviewAction(object? sender, PeaceTrigger trigger)
    {
        if (_disposed)
            return;
        switch (trigger.Action)
        {
            case PeaceAction.SelfDestruct:
                StartPeaceTestSelfDestruct();
                break;
            case PeaceAction.Moustache:
                PeacePreview.StartMoustache(trigger);
                break;
            case PeaceAction.Photo:
                ShowPeacePhotoBadge();
                break;
        }
    }

    private void StartPeaceTestMoustache()
    {
        if (_disposed)
            return;
        PeacePreview.StartTestMoustache();
    }

    private void StartPeaceTestSelfDestruct()
    {
        if (_disposed)
            return;
        StopPeaceSelfDestructTest();
        PeaceTestSecondsRemaining = PeaceTestSelfDestructSeconds;
        PeaceTestSelfDestructVisible = true;
        PlayPeaceTick(PeaceTestSecondsRemaining);
        var generation = ++_peaceTestGeneration;
        _peaceTestTimer = _time.CreateTimer(_ => PostPeace(() => PeaceTestTickElapsed(generation)), null, PeaceTestTick, PeaceTestTick);
    }

    private void PeaceTestTickElapsed(int generation)
    {
        if (_disposed || generation != _peaceTestGeneration || _peaceTestTimer is null)
            return;

        if (PeaceTestSecondsRemaining > 0)
        {
            PeaceTestSecondsRemaining--;
            if (PeaceTestSecondsRemaining > 0)
            {
                PlayPeaceTick(PeaceTestSecondsRemaining);
            }
            else
            {
                // Die Explosion ist der Moment, in dem in der Gastansicht das Foto entstünde; hier entsteht keines.
                if (_draft.Peace.SelfDestructSoundEnabled)
                    PeacePreview.PlayExplosion();
                PeaceTestExplosionRequested?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        // Eine Sekunde nach der Explosion endet der Test.
        StopPeaceSelfDestructTest();
    }

    private void PlayPeaceTick(int secondsRemaining)
    {
        if (_draft.Peace.SelfDestructSoundEnabled)
            PeacePreview.PlaySelfDestructTick(secondsRemaining);
    }

    private void StopPeaceSelfDestructTest()
    {
        _peaceTestGeneration++;
        var timer = _peaceTestTimer;
        _peaceTestTimer = null;
        timer?.Dispose();
        if (PeaceTestSelfDestructVisible)
        {
            PeaceTestSelfDestructVisible = false;
            PeacePreview.StopSounds();
        }
    }

    private void ShowPeacePhotoBadge()
    {
        StopPeaceBadge();
        PeacePhotoBadgeVisible = true;
        var generation = ++_peaceBadgeGeneration;
        _peaceBadgeTimer = _time.CreateTimer(
            _ => PostPeace(() =>
            {
                if (!_disposed && generation == _peaceBadgeGeneration)
                    StopPeaceBadge();
            }), null, PeacePhotoBadgeDuration, Timeout.InfiniteTimeSpan);
    }

    private void StopPeaceBadge()
    {
        _peaceBadgeGeneration++;
        var timer = _peaceBadgeTimer;
        _peaceBadgeTimer = null;
        timer?.Dispose();
        PeacePhotoBadgeVisible = false;
    }

    private void StopPeaceTests()
    {
        StopPeaceSelfDestructTest();
        StopPeaceBadge();
    }

    // Timer-Thread (bzw. bei FakeTimeProvider der Aufrufer): zurück auf den UI-Kontext.
    private void PostPeace(Action action)
    {
        if (_sync is null)
            action();
        else
            _sync.Post(_ => action(), null);
    }

    private void DisposePeacePreview()
    {
        StopPeaceTests();
        PeacePreview.PropertyChanged -= OnPeacePreviewPropertyChanged;
        PeacePreview.ActionRequested -= OnPeacePreviewAction;
        PeacePreview.Dispose();
    }
}
