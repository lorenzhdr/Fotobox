using System.Runtime.InteropServices;
using Fotobox.Core;

namespace Fotobox.Services.Audio;

/// <summary>
/// Spielt Countdown-Piepen und Kamera-Sound (<see cref="BoothToneSynth"/>) über winmm <c>PlaySound</c> aus nativem Speicher; die
/// Technik ist die des <see cref="FunSoundPlayer"/> (<c>SoundPlayer</c> wäre ungeeignet: winmm liest asynchron weiter, das
/// verwaltete Array könnte der GC währenddessen verschieben). Wirft nie; Fehler (z. B. kein Audiogerät) werden nur einmal geloggt.
/// Anders als der <see cref="FunSoundPlayer"/> gibt es keine Warteschlange und keine Wartezeit: Der Ton startet sofort und ersetzt
/// einen laufenden (auch einen Fun-Ton, <c>PlaySound</c> kennt nur eine Wiedergabe je Prozess). Die Puffer liegen je Signal und
/// 5-%-Lautstärkestufe im Cache, entstehen beim ersten <see cref="Prepare"/> oder <see cref="Play"/> und werden nie freigegeben,
/// weil winmm asynchron liest.
/// </summary>
public sealed class BoothSoundPlayer : IBoothSoundPlayer
{
    private const int WavHeaderBytes = 44;

    // Flags von PlaySound (mmsystem.h).
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    private readonly object _gate = new();
    // IntPtr.Zero = nicht verfügbar (Synthese oder Speicher fehlgeschlagen).
    private readonly Dictionary<(BoothSoundCue Cue, int Step), IntPtr> _buffers = [];
    private bool _failureLogged;
    private bool _prepareFailureLogged;

    public void Play(BoothSoundCue cue, int volumePercent)
    {
        try
        {
            var step = BoothToneSynth.VolumeStep(volumePercent);
            if (step <= 0)
                return; // 0 % ist still: Es soll auch keinen laufenden Ton abschneiden.

            lock (_gate)
            {
                var buffer = GetBufferLocked((cue, step));
                if (buffer == IntPtr.Zero)
                    return;
                if (!PlaySound(buffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT)) // asynchron
                {
                    if (!_failureLogged)
                    {
                        _failureLogged = true;
                        AppLog.Warn("Ablauf-Ton konnte nicht gespielt werden (kein Audiogerät?).");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                AppLog.Warn("Ablauf-Ton konnte nicht gespielt werden.", ex);
            }
        }
    }

    public void Prepare(BoothSoundCue cue, int volumePercent)
    {
        try
        {
            var step = BoothToneSynth.VolumeStep(volumePercent);
            if (step <= 0)
                return;
            lock (_gate)
                GetBufferLocked((cue, step));
        }
        catch (Exception ex)
        {
            if (!_prepareFailureLogged)
            {
                _prepareFailureLogged = true;
                AppLog.Warn("Ablauf-Ton konnte nicht vorbereitet werden.", ex);
            }
        }
    }

    private IntPtr GetBufferLocked((BoothSoundCue Cue, int Step) key)
    {
        if (_buffers.TryGetValue(key, out var existing))
            return existing;

        var buffer = IntPtr.Zero;
        try
        {
            var bytes = BoothToneSynth.Create(key.Cue, key.Step);
            if (bytes.Length < WavHeaderBytes)
            {
                AppLog.Warn("Ablauf-Ton konnte nicht vorbereitet werden (ungültige Wave-Daten).");
            }
            else
            {
                buffer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ablauf-Ton konnte nicht vorbereitet werden.", ex);
            if (buffer != IntPtr.Zero)
            {
                // Noch nie abgespielt, also gefahrlos freizugeben.
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }
        }
        _buffers[key] = buffer;
        return buffer;
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);
}
