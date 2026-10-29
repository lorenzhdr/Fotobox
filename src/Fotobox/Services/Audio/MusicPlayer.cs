using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Fotobox.Core;

namespace Fotobox.Services.Audio;

/// <summary>
/// Spielt die Kisscam-Musik (WAV oder MP3) über den WPF-<see cref="MediaPlayer"/> (Media Foundation). Der Player lebt auf einem eigenen
/// STA-Thread mit <see cref="Dispatcher"/>, der erst beim ersten <see cref="Play"/> entsteht; Aufrufer posten nur per
/// <c>BeginInvoke</c> und warten nie. Wirft nie: Fehlende Datei, kaputte Datei, fehlendes Media Foundation (Windows N) oder eine
/// verpasste Öffnungsfrist ergeben nur eine Log-Warnung und Stille. Jeder <see cref="Play"/>/<see cref="Stop"/> erhöht einen
/// Generationszähler; Ereignisse und Timer einer älteren Generation (oder eines bereits ersetzten <see cref="MediaPlayer"/>s) werden
/// verworfen. Das Öffnen ist mit <see cref="OpenTimeout"/> befristet, <see cref="Dispose"/> wartet höchstens <see cref="DisposeWait"/>.
/// </summary>
public sealed class MusicPlayer : IMusicPlayer, IDisposable
{
    /// <summary>Frist zum Öffnen der Datei (<c>MediaOpened</c>); danach wird still aufgegeben.</summary>
    internal static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Längste Wartezeit von <see cref="Dispose"/> auf das Ende des Musik-Threads.</summary>
    internal static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(1);
    /// <summary>Schrittweite der Ausblendung.</summary>
    internal static readonly TimeSpan FadeStep = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    // Aufträge, die vor dem Start des Dispatchers eingehen (Thread entsteht lazy); werden beim Start in die Warteschlange gestellt.
    private readonly List<Action> _pending = [];
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private bool _disposed;

    private int _generation;
    private int _isPlaying;

    // Nur auf dem Musik-Thread.
    private MediaPlayer? _player;
    private DispatcherTimer? _openTimer;
    private DispatcherTimer? _fadeTimer;
    private bool _opened;
    private bool _fading;

    public bool IsPlaying => Volatile.Read(ref _isPlaying) != 0;

    public event EventHandler? PlaybackChanged;

    public event EventHandler? PlaybackFailed;

    public void Play(string? path, int volumePercent, bool loop)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Stop();
                return;
            }
            var generation = Interlocked.Increment(ref _generation);
            var volume = Math.Clamp(volumePercent, 0, 100) / 100.0;
            Post(() => PlayCore(path, volume, loop, generation));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Musik konnte nicht gestartet werden.", ex);
        }
    }

    public void FadeOut(TimeSpan duration)
    {
        try
        {
            // Ohne laufende Wiedergabe nichts tun (und keinen Thread anlegen).
            if (!HasThread())
                return;
            Post(() => FadeOutCore(duration));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Musik konnte nicht ausgeblendet werden.", ex);
        }
    }

    public void Stop()
    {
        try
        {
            Interlocked.Increment(ref _generation);
            if (!HasThread())
                return;
            Post(StopCore);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Musik konnte nicht gestoppt werden.", ex);
        }
    }

    public void Dispose()
    {
        Thread? thread;
        Dispatcher? dispatcher;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending.Clear();
            thread = _thread;
            dispatcher = _dispatcher;
        }
        Interlocked.Increment(ref _generation);
        try
        {
            if (dispatcher is not null)
            {
                // Zuerst aufräumen, dann den Dispatcher beenden (Reihenfolge der Warteschlange).
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Safe(StopCore, "Musik konnte beim Beenden nicht gestoppt werden.")));
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
            if (thread is not null && thread != Thread.CurrentThread && !thread.Join(DisposeWait))
                AppLog.Warn("Der Musik-Thread wurde nicht rechtzeitig beendet.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Musik-Player konnte nicht sauber beendet werden.", ex);
        }
    }

    // ---- Threading ----

    private bool HasThread()
    {
        lock (_gate)
            return !_disposed && _thread is not null;
    }

    // Stellt einen Auftrag auf den Musik-Thread (legt ihn beim ersten Mal an). Blockiert nie.
    private void Post(Action action)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_dispatcher is not null)
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Safe(action, "Musik-Auftrag fehlgeschlagen.")));
                return;
            }
            _pending.Add(action);
            if (_thread is not null)
                return;
            var thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "Musik",
            };
            thread.SetApartmentState(ApartmentState.STA);
            try
            {
                thread.Start();
                _thread = thread;
            }
            catch (Exception ex)
            {
                _pending.Clear();
                AppLog.Warn("Musik-Thread konnte nicht gestartet werden.", ex);
            }
        }
    }

    private void ThreadMain()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += (_, e) =>
            {
                AppLog.Warn("Unbehandelte Ausnahme im Musik-Thread.", e.Exception);
                e.Handled = true;
            };
            lock (_gate)
            {
                if (_disposed)
                    return;
                _dispatcher = dispatcher;
                foreach (var action in _pending)
                {
                    var captured = action;
                    dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Safe(captured, "Musik-Auftrag fehlgeschlagen.")));
                }
                _pending.Clear();
            }
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Musik-Thread ist abgestürzt.", ex);
        }
    }

    private static void Safe(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Warn(message, ex);
        }
    }

    // ---- Auf dem Musik-Thread ----

    private void PlayCore(string path, double volume, bool loop, int generation)
    {
        // Ein neuerer Play/Stop hat dieses Starten bereits überholt.
        if (generation != Volatile.Read(ref _generation))
            return;
        StopCore();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                AppLog.Warn($"Musikdatei nicht gefunden: {Path.GetFileName(path)}");
                RaiseFailed(generation);
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Musikpfad ungültig.", ex);
            RaiseFailed(generation);
            return;
        }

        MediaPlayer player;
        try
        {
            // Wirft ohne Media Foundation (Windows N) möglicherweise schon hier.
            player = new MediaPlayer();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Musik nicht verfügbar (Media Foundation fehlt?).", ex);
            RaiseFailed(generation);
            return;
        }

        _player = player;
        _opened = false;
        _fading = false;

        player.MediaOpened += (_, _) => Safe(() => OnOpened(player, volume, generation), "MediaOpened der Musik fehlgeschlagen.");
        player.MediaFailed += (_, e) => Safe(() => OnFailed(player, generation, e.ErrorException), "MediaFailed der Musik fehlgeschlagen.");
        player.MediaEnded += (_, _) => Safe(() => OnEnded(player, loop, generation), "MediaEnded der Musik fehlgeschlagen.");

        try
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher) { Interval = OpenTimeout };
            timer.Tick += (_, _) => Safe(() => OnOpenTimeout(player, timer, generation), "Öffnungsfrist der Musik fehlgeschlagen.");
            _openTimer = timer;
            timer.Start();
            player.Open(new Uri(fullPath, UriKind.Absolute));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Musikdatei konnte nicht geöffnet werden.", ex);
            RaiseFailed(generation);
            StopCore();
        }
    }

    private bool IsCurrent(MediaPlayer player, int generation) =>
        ReferenceEquals(player, _player) && generation == Volatile.Read(ref _generation);

    private void OnOpened(MediaPlayer player, double volume, int generation)
    {
        if (!IsCurrent(player, generation))
            return;
        StopTimer(ref _openTimer);
        _opened = true;
        player.Volume = volume;
        player.Play();
        SetPlaying(true);
    }

    private void OnFailed(MediaPlayer player, int generation, Exception? error)
    {
        if (!IsCurrent(player, generation))
            return;
        AppLog.Warn("Musikdatei konnte nicht abgespielt werden.", error);
        RaiseFailed(generation);
        StopCore();
    }

    private void OnEnded(MediaPlayer player, bool loop, int generation)
    {
        if (!IsCurrent(player, generation) || _fading)
            return;
        if (loop)
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        else
        {
            StopCore();
        }
    }

    private void OnOpenTimeout(MediaPlayer player, DispatcherTimer timer, int generation)
    {
        timer.Stop();
        if (!IsCurrent(player, generation) || _opened)
            return;
        AppLog.Warn("Musikdatei wurde nicht rechtzeitig geöffnet.");
        RaiseFailed(generation);
        StopCore();
    }

    private void FadeOutCore(TimeSpan duration)
    {
        var player = _player;
        // Noch nicht geöffnet (oder nichts da): nichts zum Ausblenden, Wiedergabe gar nicht erst beginnen lassen. Läuft schon eine
        // Ausblendung, bleibt sie unverändert (idempotent).
        if (player is null || _fading)
            return;
        if (!_opened || duration <= FadeStep)
        {
            StopCore();
            return;
        }

        _fading = true;
        var generation = Volatile.Read(ref _generation);
        var steps = (int)Math.Ceiling(duration.TotalMilliseconds / FadeStep.TotalMilliseconds);
        var startVolume = player.Volume;
        var step = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher) { Interval = FadeStep };
        timer.Tick += (_, _) => Safe(() =>
        {
            if (!IsCurrent(player, generation))
            {
                timer.Stop();
                return;
            }
            step++;
            if (step >= steps)
            {
                StopCore();
                return;
            }
            player.Volume = startVolume * (1.0 - (double)step / steps);
        }, "Ausblendung der Musik fehlgeschlagen.");
        _fadeTimer = timer;
        timer.Start();
    }

    // Beendet alles sofort und idempotent: Timer, Player (Handler hängen an der Instanz und werden mit ihr verworfen), Zustand.
    private void StopCore()
    {
        StopTimer(ref _openTimer);
        StopTimer(ref _fadeTimer);
        var player = _player;
        _player = null;
        _opened = false;
        _fading = false;
        if (player is not null)
        {
            try
            {
                player.Stop();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Musik konnte nicht angehalten werden.", ex);
            }
            try
            {
                player.Close();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Musik konnte nicht freigegeben werden.", ex);
            }
        }
        SetPlaying(false);
    }

    private static void StopTimer(ref DispatcherTimer? timer)
    {
        var t = timer;
        timer = null;
        try
        {
            t?.Stop();
        }
        catch
        {
            // Timer-Stopp darf nie werfen.
        }
    }

    // Meldet einen gescheiterten Start oder Abbruch der Wiedergabe (nur für die aktuelle Generation: ein neuerer Play/Stop macht die
    // Meldung gegenstandslos). PlaybackChanged feuert dabei nicht, weil nie gespielt wurde.
    private void RaiseFailed(int generation)
    {
        if (generation != Volatile.Read(ref _generation))
            return;
        try
        {
            PlaybackFailed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("PlaybackFailed-Handler der Musik ist fehlgeschlagen.", ex);
        }
    }

    private void SetPlaying(bool playing)
    {
        if (Interlocked.Exchange(ref _isPlaying, playing ? 1 : 0) == (playing ? 1 : 0))
            return;
        try
        {
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn("PlaybackChanged-Handler der Musik ist fehlgeschlagen.", ex);
        }
    }
}
