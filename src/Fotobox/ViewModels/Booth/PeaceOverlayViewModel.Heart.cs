using Fotobox.Core;
using Fotobox.Core.Heart;

namespace Fotobox.ViewModels.Booth;

// Herzerkennung: Die Engine sitzt hier, weil der Gestendienst nur die oberste Anforderung bedient – sie nutzt die Anforderung der
// Peaceerkennung (Hände, Analyserate) mit.
public sealed partial class PeaceOverlayViewModel
{
    private readonly HeartEngine _heartEngine;

    // Herz-Einstellungen (null = aus) und ob die Peace-Gesten ausgewertet werden (false: Herz ohne Peace).
    private HeartSettings? _heart;
    private bool _peaceGestures = true;
    private HeartView? _heartView;
    private bool _heartDetectedNow;
    private HeartShape? _heartDetectedShape;

    /// <summary>Ein Herz wurde lange genug gehalten (auf dem UI-Thread; nur im Leerlauf, nie während eines Durchgangs).</summary>
    public event EventHandler<HeartTrigger>? HeartTriggered;

    /// <summary>
    /// Das beste Herz im Bild für die Haltefortschrittsanzeige (ungespiegelte, normierte Koordinaten) oder null, wenn gerade keines zu
    /// sehen ist bzw. die Erkennung aus ist.
    /// </summary>
    public HeartView? Heart
    {
        get => _heartView;
        private set => SetProperty(ref _heartView, value);
    }

    /// <summary>Zeigt gerade (innerhalb der Aussetzer-Toleranz) irgendeine Hand-Kombination ein Herz? Für die Admin-Vorschau.</summary>
    public bool HeartDetectedNow
    {
        get => _heartDetectedNow;
        private set => SetProperty(ref _heartDetectedNow, value);
    }

    /// <summary>Form des erkannten Herzens, solange <see cref="HeartDetectedNow"/> gilt, sonst null.</summary>
    public HeartShape? HeartDetectedShape
    {
        get => _heartDetectedShape;
        private set => SetProperty(ref _heartDetectedShape, value);
    }

    /// <summary>Ist die Herzerkennung eingeschaltet (Einstellung), gleich in welcher Betriebsart?</summary>
    private bool HeartActive => _heart is { Enabled: true };

    private HeartEngine CreateHeartEngine(TimeProvider time)
    {
        var engine = new HeartEngine(time);
        engine.Triggered += OnHeartEngineTriggered;
        engine.Changed += (_, _) => RefreshHeart();
        return engine;
    }

    // Stellt die Engine auf die aktuellen Einstellungen und die Betriebsart ein (ausgelöst wird nur im Leerlauf).
    private void ConfigureHeart(PeaceMode mode)
    {
        _heartEngine.Configure(_heart ?? new HeartSettings { Enabled = false }, _settings.AnalysesPerSecond);
        _heartEngine.GesturesEnabled = mode == PeaceMode.Watching && HeartActive;
    }

    private void ResetHeart()
    {
        _heartEngine.GesturesEnabled = false;
        _heartEngine.Reset();
        RefreshHeart();
    }

    private void RefreshHeart()
    {
        var hearts = _heartEngine.Snapshot.Hearts;
        Heart = hearts.Count > 0 ? hearts[0] : null;
        HeartDetectedNow = _heartEngine.DetectedNow;
        HeartDetectedShape = _heartEngine.DetectedShape;
    }

    private void OnHeartEngineTriggered(object? sender, HeartTrigger trigger)
    {
        try
        {
            HeartTriggered?.Invoke(this, trigger);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Herzerkennung: Aktion konnte nicht ausgeführt werden.", ex);
        }
    }
}
