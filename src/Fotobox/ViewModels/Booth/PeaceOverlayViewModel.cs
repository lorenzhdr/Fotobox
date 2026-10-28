using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;
using Fotobox.Services.Detection.Peace;

namespace Fotobox.ViewModels.Booth;

/// <summary>Betriebsart der Peaceerkennung in der Gastansicht.</summary>
public enum PeaceMode
{
    /// <summary>Keine Erkennung (z. B. Admin offen, Kamera aus, Aufnahme läuft).</summary>
    Off,
    /// <summary>Hände und Gesichter beobachten (Leerlauf).</summary>
    Watching,
    /// <summary>Nur Gesichter verfolgen, damit der Bart dem Gesicht folgt (Countdown, Vorschau).</summary>
    FollowMoustache,
}

/// <summary>
/// Bindbare Sicht auf die Peaceerkennung (Gastansicht und Admin-Vorschau, je eine Instanz). Nur auf dem UI-Thread verwenden.
/// Platzhalter: Listen bleiben leer, es werden keine Aktionen ausgelöst; die öffentliche Oberfläche ist endgültig.
/// </summary>
public sealed class PeaceOverlayViewModel : ObservableObject, IDisposable
{
    private readonly IGestureDetectionService _service;
    private readonly TimeProvider _time;
    private readonly IFunSoundPlayer _sound;
    private readonly GuestMoustacheState? _guestState;
    private readonly Func<int, int>? _random;

    private PeaceMode _mode;
    private IReadOnlyList<MoustacheView> _moustaches = [];
    private IReadOnlyList<HandView> _hands = [];
    private double _imageWidth;
    private double _imageHeight;
    private bool _peaceDetectedNow;
    private bool _moustacheActive;
    private GestureStatus _status = GestureStatus.Starting;
    private bool _moustacheAvailable;

    /// <param name="guestState">Gemeinsamer Bart-Zustand für die Foto-Planung; nur die Gastansicht übergibt ihn (die Admin-Vorschau nie).</param>
    /// <param name="random">Zufall für Aktionswahl und Bartstil: liefert für eine Obergrenze n einen Wert in 0..n-1 (null = Standard).</param>
    public PeaceOverlayViewModel(IGestureDetectionService service, TimeProvider time, IFunSoundPlayer sound,
        GuestMoustacheState? guestState = null, Func<int, int>? random = null)
    {
        _service = service;
        _time = time;
        _sound = sound;
        _guestState = guestState;
        _random = random;
    }

    /// <summary>Instanz ohne Erkennung und Ton (für Ansichtsmodelle, die das Feature nie aktivieren, z. B. in Tests).</summary>
    public static PeaceOverlayViewModel CreateInert() => new(new InertGestureDetectionService(), TimeProvider.System, new InertSound());

    public PeaceMode Mode => _mode;

    /// <summary>Bärte im Livebild (ungespiegelte Posen; Spiegelung übernimmt <c>PeaceOverlay</c>).</summary>
    public IReadOnlyList<MoustacheView> Moustaches
    {
        get => _moustaches;
        private set => SetProperty(ref _moustaches, value);
    }

    /// <summary>Verfolgte Hände (Admin-Vorschau, Fortschrittsring).</summary>
    public IReadOnlyList<HandView> Hands
    {
        get => _hands;
        private set => SetProperty(ref _hands, value);
    }

    /// <summary>Auflösung des Kamerabilds; 0 = unbekannt.</summary>
    public double ImageWidth
    {
        get => _imageWidth;
        private set => SetProperty(ref _imageWidth, value);
    }

    public double ImageHeight
    {
        get => _imageHeight;
        private set => SetProperty(ref _imageHeight, value);
    }

    /// <summary>Gibt es etwas zu zeichnen?</summary>
    public bool IsShown => Moustaches.Count > 0 || Hands.Count > 0;

    /// <summary>Zeigt gerade mindestens eine Hand das Peace-Zeichen (Abzeichen in der Admin-Vorschau)?</summary>
    public bool PeaceDetectedNow
    {
        get => _peaceDetectedNow;
        private set => SetProperty(ref _peaceDetectedNow, value);
    }

    /// <summary>Ist ein Schnurrbart aktiv (Bärte werden verfolgt)?</summary>
    public bool MoustacheActive
    {
        get => _moustacheActive;
        private set => SetProperty(ref _moustacheActive, value);
    }

    public GestureStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Steht die Gesichtserkennung bereit (sonst fällt die Aktion Schnurrbart aus der Auswahl)?</summary>
    public bool MoustacheAvailable
    {
        get => _moustacheAvailable;
        private set => SetProperty(ref _moustacheAvailable, value);
    }

    /// <summary>Eine Peace-Aktion soll ausgeführt werden (auf dem UI-Thread).</summary>
#pragma warning disable CS0067 // Platzhalter: Die Engine löst das Ereignis erst mit der echten Implementierung aus.
    public event EventHandler<PeaceTrigger>? ActionRequested;
#pragma warning restore CS0067

    /// <summary>Betriebsart und Einstellungen setzen. Ein Wechsel ändert nur die Anforderung (kein Zurücksetzen), damit ein Bart den Countdown übersteht.</summary>
    public void SetMode(PeaceMode mode, PeaceSettings settings, bool mirror)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        OnPropertyChanged(nameof(Mode));
    }

    /// <summary>Startet den Schnurrbart für die auslösende Hand (Aktion „Schnurrbart“).</summary>
    public void StartMoustache(PeaceTrigger trigger)
    {
    }

    /// <summary>Admin „Bart testen“: Bart ohne Kamera auf einem Platzhaltergesicht bzw. am erkannten Gesicht (nie im echten Foto).</summary>
    public void StartTestMoustache()
    {
    }

    /// <summary>Das Foto ist fertig (Zustand Vorschau): Ein Bart „bis zum nächsten Foto“ endet.</summary>
    public void OnPhotoCompleted()
    {
    }

    /// <summary>Tick der Selbstzerstörung (<paramref name="secondsRemaining"/> Sekunden übrig).</summary>
    public void PlaySelfDestructTick(int secondsRemaining)
    {
    }

    public void PlayExplosion()
    {
    }

    public void StopSounds()
    {
    }

    public void Dispose()
    {
    }

    private sealed class InertSound : IFunSoundPlayer
    {
        public void Play(FunSoundCue cue) { }
        public void Stop() { }
    }
}
