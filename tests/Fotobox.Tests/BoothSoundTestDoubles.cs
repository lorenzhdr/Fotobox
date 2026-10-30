using Fotobox.Core;

namespace Fotobox.Tests;

/// <summary>Zeichnet die Aufrufe des Ablauf-Ton-Spielers auf (Reihenfolge wie im Aufruf; threadsicher, der Ablauf läuft teils auf dem Threadpool).</summary>
internal sealed class FakeBoothSoundPlayer : IBoothSoundPlayer
{
    private readonly object _gate = new();
    private readonly List<(BoothSoundCue Cue, int Volume)> _played = [];
    private readonly List<(BoothSoundCue Cue, int Volume)> _prepared = [];

    /// <summary>Wirft bei jedem Aufruf (der Ablauf darf davon nicht beeinträchtigt werden).</summary>
    public bool Throw { get; set; }

    public IReadOnlyList<(BoothSoundCue Cue, int Volume)> Played
    {
        get { lock (_gate) return _played.ToArray(); }
    }

    public IReadOnlyList<(BoothSoundCue Cue, int Volume)> Prepared
    {
        get { lock (_gate) return _prepared.ToArray(); }
    }

    public IReadOnlyList<BoothSoundCue> PlayedCues => Played.Select(p => p.Cue).ToArray();

    public void Play(BoothSoundCue cue, int volumePercent)
    {
        lock (_gate)
            _played.Add((cue, volumePercent));
        if (Throw)
            throw new InvalidOperationException("Testfehler beim Abspielen");
    }

    public void Prepare(BoothSoundCue cue, int volumePercent)
    {
        lock (_gate)
            _prepared.Add((cue, volumePercent));
        if (Throw)
            throw new InvalidOperationException("Testfehler beim Vorbereiten");
    }
}
