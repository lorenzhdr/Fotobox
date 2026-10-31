using Fotobox.Core;

namespace Fotobox.ViewModels.Admin;

/// <summary>
/// Tab „Fun“, Seite „Tracking-Overlay“ (<see cref="TrackingOverlaySettings"/>): welche Ebenen die Einblendung in der Gastansicht zeigt,
/// Beschriftungen, Statuszeile, Warum-Info, Stil und Größe. Die Einstellungen wirken wie alle anderen erst nach „Übernehmen“.
/// Die Seite hat keine Vorschau und startet keine Erkennung (<see cref="FunPage.TrackingOverlay"/> zählt weder für die Zielerfassungs-
/// noch für die Peace-/Herz-Vorschau). Ein/Aus per Taste: Aktion <see cref="KeyAction.TrackingOverlay"/> im Tab „Tasten“.
/// </summary>
public sealed partial class AdminViewModel
{
    // ---------------------------------------------------------------- Ebenen

    public bool TrackingOverlayShowPersons
    {
        get => _draft.TrackingOverlay.ShowPersons;
        set => SetProperty(_draft.TrackingOverlay.ShowPersons, value, v => _draft.TrackingOverlay.ShowPersons = v);
    }

    public bool TrackingOverlayShowFaces
    {
        get => _draft.TrackingOverlay.ShowFaces;
        set => SetProperty(_draft.TrackingOverlay.ShowFaces, value, v => _draft.TrackingOverlay.ShowFaces = v);
    }

    public bool TrackingOverlayShowHands
    {
        get => _draft.TrackingOverlay.ShowHands;
        set => SetProperty(_draft.TrackingOverlay.ShowHands, value, v => _draft.TrackingOverlay.ShowHands = v);
    }

    public bool TrackingOverlayShowGestures
    {
        get => _draft.TrackingOverlay.ShowGestures;
        set => SetProperty(_draft.TrackingOverlay.ShowGestures, value, v => _draft.TrackingOverlay.ShowGestures = v);
    }

    public bool TrackingOverlayShowMoustaches
    {
        get => _draft.TrackingOverlay.ShowMoustaches;
        set => SetProperty(_draft.TrackingOverlay.ShowMoustaches, value, v => _draft.TrackingOverlay.ShowMoustaches = v);
    }

    // ---------------------------------------------------------------- Zusatzangaben

    public bool TrackingOverlayShowLabels
    {
        get => _draft.TrackingOverlay.ShowLabels;
        set => SetProperty(_draft.TrackingOverlay.ShowLabels, value, v => _draft.TrackingOverlay.ShowLabels = v);
    }

    public bool TrackingOverlayShowStatusLine
    {
        get => _draft.TrackingOverlay.ShowStatusLine;
        set => SetProperty(_draft.TrackingOverlay.ShowStatusLine, value, v => _draft.TrackingOverlay.ShowStatusLine = v);
    }

    public bool TrackingOverlayShowVerdicts
    {
        get => _draft.TrackingOverlay.ShowVerdicts;
        set => SetProperty(_draft.TrackingOverlay.ShowVerdicts, value, v => _draft.TrackingOverlay.ShowVerdicts = v);
    }

    // ---------------------------------------------------------------- Stil und Größe

    /// <summary>Stil „Technik“ (Wahlkarte; Zwilling von <see cref="IsTrackingOverlaySciFi"/>).</summary>
    public bool IsTrackingOverlayTechnik
    {
        get => _draft.TrackingOverlay.Style == TrackingOverlayStyle.Technik;
        set => SetTrackingOverlayStyle(TrackingOverlayStyle.Technik, value);
    }

    /// <summary>Stil „Sci-Fi“.</summary>
    public bool IsTrackingOverlaySciFi
    {
        get => _draft.TrackingOverlay.Style == TrackingOverlayStyle.SciFi;
        set => SetTrackingOverlayStyle(TrackingOverlayStyle.SciFi, value);
    }

    private void SetTrackingOverlayStyle(TrackingOverlayStyle style, bool selected)
    {
        // Ein Abwählen meldet die Wahlkarte beim Umschalten selbst; nur das Anwählen ändert den Wert.
        if (!selected || _draft.TrackingOverlay.Style == style)
            return;

        _draft.TrackingOverlay.Style = style;
        OnPropertyChanged(nameof(IsTrackingOverlayTechnik));
        OnPropertyChanged(nameof(IsTrackingOverlaySciFi));
    }

    /// <summary>Größe von Punkten, Linien und Schrift in Prozent.</summary>
    public int TrackingOverlayScalePercent
    {
        get => _draft.TrackingOverlay.ScalePercent;
        set
        {
            if (SetProperty(_draft.TrackingOverlay.ScalePercent, value, v => _draft.TrackingOverlay.ScalePercent = v))
                OnPropertyChanged(nameof(TrackingOverlayScaleText));
        }
    }

    public string TrackingOverlayScaleText => $"{TrackingOverlayScalePercent} %";

    // ---------------------------------------------------------------- Taste

    /// <summary>Hinweis auf der Seite: mit welcher Taste das Overlay in der Gastansicht ein- und ausgeblendet wird (aktueller Stand im Tab „Tasten“).</summary>
    public string TrackingOverlayKeyHint
    {
        get
        {
            var keys = string.Join(" oder ", Editor(KeyAction.TrackingOverlay).Keys.Select(k => k.DisplayName));
            return keys.Length == 0
                ? "Für das Tracking-Overlay ist noch keine Taste festgelegt. Legen Sie im Tab „Tasten“ unter „Tracking-Overlay ein/aus“ eine Taste fest, um das Overlay in der Gastansicht ein- und auszublenden. Nach dem Start der Fotobox ist das Overlay ausgeblendet."
                : $"Blenden Sie das Overlay in der Gastansicht mit {keys} ein und aus. Die Taste ändern Sie im Tab „Tasten“. Nach dem Start der Fotobox ist das Overlay ausgeblendet.";
        }
    }

    /// <summary>Status-Pille der Kachel: die erste Taste des Overlays, ohne Taste „Aus“ (dann lässt es sich nicht einblenden).</summary>
    public string TrackingOverlayStatusText =>
        Editor(KeyAction.TrackingOverlay).Keys.FirstOrDefault()?.DisplayName ?? "Aus";

    /// <summary>Wertebereich der Größe.</summary>
    private void ValidateTrackingOverlay(List<string> errors)
    {
        CheckPeaceRange(_draft.TrackingOverlay.ScalePercent, SettingsLimits.TrackingOverlayScaleMin, SettingsLimits.TrackingOverlayScaleMax,
            "Die Größe des Tracking-Overlays", "Prozent", errors);
    }
}
