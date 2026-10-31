using Fotobox.Core.Peace;

namespace Fotobox.Core;

/// <summary>Ein zu spielender Ablauf-Ton: Signal und Lautstärke in Prozent.</summary>
public readonly record struct BoothSound(BoothSoundCue Cue, int VolumePercent);

/// <summary>
/// Entscheidet rein aus Zustand, Stil und Einstellungen, welcher Ton des Ablaufs (Countdown-Piepen, Kamera-Sound) gespielt wird.
/// Ohne Zustand und ohne Abhängigkeiten, damit die Regeln einzeln prüfbar sind; das Merken des zuletzt gepiepten Werts liegt im
/// <c>BoothViewModel</c>.
/// </summary>
public static class BoothSoundCues
{
    /// <summary>
    /// Piep zur angezeigten Countdown-Ziffer <paramref name="value"/> (ab 1; bei 1 der höhere „letzte“ Piep) oder <c>null</c> = still.
    /// Gilt im Foto- und im Video-Countdown, aber nur bei den Stilen Normal und Peace (nicht bei Selbstzerstörung, dort tickt die
    /// Sirene, und nicht bei der Kisscam, dort läuft die Musik).
    /// </summary>
    public static BoothSound? Beep(BoothState state, CountdownStyle style, int value, FlowSettings? flow)
    {
        if (flow is not { CountdownBeepEnabled: true } || value < 1)
            return null;
        if (state is not (BoothState.Countdown or BoothState.VideoCountdown))
            return null;
        // Im Videocountdown gibt es keinen Stil (immer Normal).
        if (state == BoothState.Countdown && style is not (CountdownStyle.Normal or CountdownStyle.Peace))
            return null;
        return new BoothSound(BoothToneSynth.BeepCue(flow.CountdownBeepSound, final: value == 1), flow.CountdownBeepVolumePercent);
    }

    /// <summary>
    /// Kamera-Sound im Moment der Aufnahme oder <c>null</c> = still. Bei Normal, Peace und Kisscam; nicht bei der Selbstzerstörung
    /// (dort bleibt die Explosion). Im Video gibt es keinen Auslöser-Ton.
    /// </summary>
    public static BoothSound? Shutter(CountdownStyle style, FlowSettings? flow)
    {
        if (flow is not { ShutterSoundEnabled: true } || style == CountdownStyle.SelfDestruct)
            return null;
        return new BoothSound(BoothToneSynth.ShutterCue(flow.ShutterSound), flow.ShutterVolumePercent);
    }
}
