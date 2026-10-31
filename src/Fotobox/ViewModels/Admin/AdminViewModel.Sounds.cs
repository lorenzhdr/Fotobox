using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;

namespace Fotobox.ViewModels.Admin;

/// <summary>Eine wählbare Klangvariante in der Admin-Ansicht: Wert der Einstellung und deutscher Name.</summary>
public sealed record SoundOption<T>(T Value, string Name);

// Tab „Ablauf“, Karte „Töne“ – Countdown-Piepen und Kamera-Sound: Entwurfs-Properties und „Anhören“ mit den Werten des Entwurfs
// (ohne zu speichern). Die Auswahlen lesen immer aus dem Entwurf, so bleiben sie nach „Standardwerte“ und Import korrekt.
public sealed partial class AdminViewModel
{
    /// <summary>Abstand der drei Piepe beim Anhören (normal, normal, hoch).</summary>
    internal static readonly TimeSpan BeepPreviewInterval = TimeSpan.FromSeconds(1);

    private IBoothSoundPlayer? _boothSound;
    private ITimer? _beepPreviewTimer;
    private int _beepPreviewGeneration;
    private int _beepPreviewStep;

    /// <summary>Wählbare Klangvarianten des Countdown-Piepens.</summary>
    public IReadOnlyList<SoundOption<CountdownBeepSound>> CountdownBeepSounds { get; } =
    [
        new(CountdownBeepSound.Soft, "Weich"),
        new(CountdownBeepSound.Bright, "Hell"),
        new(CountdownBeepSound.WoodBlock, "Holzblock"),
    ];

    /// <summary>Wählbare Klangvarianten des Kamera-Sounds.</summary>
    public IReadOnlyList<SoundOption<ShutterSound>> ShutterSounds { get; } =
    [
        new(ShutterSound.Slr, "Spiegelreflex"),
        new(ShutterSound.Compact, "Kompaktkamera"),
        new(ShutterSound.Retro, "Retro (mit Filmtransport)"),
    ];

    /// <summary>„Anhören“ des Countdown-Piepens: drei Piepe im Abstand von etwa einer Sekunde (normal, normal, hoch).</summary>
    public IRelayCommand PreviewCountdownBeepCommand { get; private set; } = null!;

    /// <summary>„Anhören“ des Kamera-Sounds.</summary>
    public IRelayCommand PreviewShutterSoundCommand { get; private set; } = null!;

    private void InitSounds(IBoothSoundPlayer? boothSound)
    {
        _boothSound = boothSound;
        PreviewCountdownBeepCommand = new RelayCommand(PreviewCountdownBeep);
        PreviewShutterSoundCommand = new RelayCommand(PreviewShutterSound);
    }

    /// <summary>Countdown-Piepen einschalten; die Einstellungen darunter sind sonst ausgegraut.</summary>
    public bool CountdownBeepEnabled
    {
        get => _draft.Flow.CountdownBeepEnabled;
        set
        {
            if (SetProperty(_draft.Flow.CountdownBeepEnabled, value, v => _draft.Flow.CountdownBeepEnabled = v) && !value)
                StopBeepPreview();
        }
    }

    public CountdownBeepSound CountdownBeepSound
    {
        get => _draft.Flow.CountdownBeepSound;
        set => SetProperty(_draft.Flow.CountdownBeepSound, value, v => _draft.Flow.CountdownBeepSound = v);
    }

    /// <summary>Lautstärke des Countdown-Piepens (<see cref="SettingsLimits.SoundVolumeMin"/>–<see cref="SettingsLimits.SoundVolumeMax"/>).</summary>
    public int CountdownBeepVolumePercent
    {
        get => _draft.Flow.CountdownBeepVolumePercent;
        set
        {
            if (SetProperty(_draft.Flow.CountdownBeepVolumePercent, value, v => _draft.Flow.CountdownBeepVolumePercent = v))
                OnPropertyChanged(nameof(CountdownBeepVolumeText));
        }
    }

    public string CountdownBeepVolumeText => $"{_draft.Flow.CountdownBeepVolumePercent} %";

    /// <summary>Kamera-Sound einschalten; die Einstellungen darunter sind sonst ausgegraut.</summary>
    public bool ShutterSoundEnabled
    {
        get => _draft.Flow.ShutterSoundEnabled;
        set => SetProperty(_draft.Flow.ShutterSoundEnabled, value, v => _draft.Flow.ShutterSoundEnabled = v);
    }

    public ShutterSound ShutterSound
    {
        get => _draft.Flow.ShutterSound;
        set => SetProperty(_draft.Flow.ShutterSound, value, v => _draft.Flow.ShutterSound = v);
    }

    /// <summary>Lautstärke des Kamera-Sounds (<see cref="SettingsLimits.SoundVolumeMin"/>–<see cref="SettingsLimits.SoundVolumeMax"/>).</summary>
    public int ShutterVolumePercent
    {
        get => _draft.Flow.ShutterVolumePercent;
        set
        {
            if (SetProperty(_draft.Flow.ShutterVolumePercent, value, v => _draft.Flow.ShutterVolumePercent = v))
                OnPropertyChanged(nameof(ShutterVolumeText));
        }
    }

    public string ShutterVolumeText => $"{_draft.Flow.ShutterVolumePercent} %";

    // Der erste Piep klingt sofort, der zweite und dritte folgen im Sekundentakt (Timer statt Warten: Die Oberfläche bleibt frei).
    // Ein erneuter Druck beginnt von vorn; Schalter aus und Schließen der Admin-Ansicht beenden die Folge.
    private void PreviewCountdownBeep()
    {
        if (_disposed)
            return;

        StopBeepPreview();
        var generation = ++_beepPreviewGeneration;
        // Werte des Entwurfs jetzt festhalten: Spätere Änderungen während der Folge ändern sie nicht mehr mitten drin.
        var sound = _draft.Flow.CountdownBeepSound;
        var volume = _draft.Flow.CountdownBeepVolumePercent;
        _beepPreviewStep = 0;
        PlayBeepPreviewStep(sound, volume);
        _beepPreviewTimer = _time.CreateTimer(_ => PostPeace(() => BeepPreviewElapsed(generation, sound, volume)), null,
            BeepPreviewInterval, BeepPreviewInterval);
    }

    private void BeepPreviewElapsed(int generation, CountdownBeepSound sound, int volume)
    {
        if (_disposed || generation != _beepPreviewGeneration)
            return;
        PlayBeepPreviewStep(sound, volume);
        if (_beepPreviewStep >= 3)
            DisposeBeepPreviewTimer();
    }

    private void PlayBeepPreviewStep(CountdownBeepSound sound, int volume)
    {
        var last = _beepPreviewStep == 2;
        _beepPreviewStep++;
        try
        {
            _boothSound?.Play(BoothToneSynth.BeepCue(sound, last), volume);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Countdown-Piepen konnte nicht angehört werden.", ex);
        }
    }

    private void PreviewShutterSound()
    {
        if (_disposed)
            return;
        try
        {
            _boothSound?.Play(BoothToneSynth.ShutterCue(_draft.Flow.ShutterSound), _draft.Flow.ShutterVolumePercent);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kamera-Sound konnte nicht angehört werden.", ex);
        }
    }

    private void StopBeepPreview()
    {
        _beepPreviewGeneration++;
        DisposeBeepPreviewTimer();
    }

    private void DisposeBeepPreviewTimer()
    {
        var timer = _beepPreviewTimer;
        _beepPreviewTimer = null;
        timer?.Dispose();
    }

    private void DisposeSounds() => StopBeepPreview();
}
