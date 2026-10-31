using System.Globalization;
using Fotobox.Core;

namespace Fotobox.ViewModels.Admin;

/// <summary>
/// Tab „Leistung“, Karte „Leistungsanzeige“ (<see cref="PerformanceOverlaySettings"/>): welche Werte die Einblendung in der Gastansicht
/// zeigt, Darstellung, Aktualisierung, Schriftgröße und Hintergrund-Deckkraft. Die Einstellungen wirken wie alle anderen erst nach
/// „Übernehmen“; sie lösen nie den Neustart-Hinweis der Hardwarebeschleunigung aus (<c>PerformanceRestartHint</c>).
/// Ein/Aus per Taste: Aktion <see cref="KeyAction.PerformanceOverlay"/> im Tab „Tasten“.
/// </summary>
public sealed partial class AdminViewModel
{
    // ---------------------------------------------------------------- System

    public bool PerformanceOverlayShowCpu
    {
        get => _draft.PerformanceOverlay.ShowCpu;
        set => SetProperty(_draft.PerformanceOverlay.ShowCpu, value, v => _draft.PerformanceOverlay.ShowCpu = v);
    }

    public bool PerformanceOverlayShowCpuClock
    {
        get => _draft.PerformanceOverlay.ShowCpuClock;
        set => SetProperty(_draft.PerformanceOverlay.ShowCpuClock, value, v => _draft.PerformanceOverlay.ShowCpuClock = v);
    }

    public bool PerformanceOverlayShowCpuTemperature
    {
        get => _draft.PerformanceOverlay.ShowCpuTemperature;
        set => SetProperty(_draft.PerformanceOverlay.ShowCpuTemperature, value, v => _draft.PerformanceOverlay.ShowCpuTemperature = v);
    }

    public bool PerformanceOverlayShowGpu
    {
        get => _draft.PerformanceOverlay.ShowGpu;
        set => SetProperty(_draft.PerformanceOverlay.ShowGpu, value, v => _draft.PerformanceOverlay.ShowGpu = v);
    }

    public bool PerformanceOverlayShowGpuClock
    {
        get => _draft.PerformanceOverlay.ShowGpuClock;
        set => SetProperty(_draft.PerformanceOverlay.ShowGpuClock, value, v => _draft.PerformanceOverlay.ShowGpuClock = v);
    }

    public bool PerformanceOverlayShowGpuTemperature
    {
        get => _draft.PerformanceOverlay.ShowGpuTemperature;
        set => SetProperty(_draft.PerformanceOverlay.ShowGpuTemperature, value, v => _draft.PerformanceOverlay.ShowGpuTemperature = v);
    }

    public bool PerformanceOverlayShowGpuMemory
    {
        get => _draft.PerformanceOverlay.ShowGpuMemory;
        set => SetProperty(_draft.PerformanceOverlay.ShowGpuMemory, value, v => _draft.PerformanceOverlay.ShowGpuMemory = v);
    }

    public bool PerformanceOverlayShowNpu
    {
        get => _draft.PerformanceOverlay.ShowNpu;
        set => SetProperty(_draft.PerformanceOverlay.ShowNpu, value, v => _draft.PerformanceOverlay.ShowNpu = v);
    }

    public bool PerformanceOverlayShowRam
    {
        get => _draft.PerformanceOverlay.ShowRam;
        set => SetProperty(_draft.PerformanceOverlay.ShowRam, value, v => _draft.PerformanceOverlay.ShowRam = v);
    }

    // ---------------------------------------------------------------- Kamera

    public bool PerformanceOverlayShowCameraFps
    {
        get => _draft.PerformanceOverlay.ShowCameraFps;
        set => SetProperty(_draft.PerformanceOverlay.ShowCameraFps, value, v => _draft.PerformanceOverlay.ShowCameraFps = v);
    }

    public bool PerformanceOverlayShowDisplayFps
    {
        get => _draft.PerformanceOverlay.ShowDisplayFps;
        set => SetProperty(_draft.PerformanceOverlay.ShowDisplayFps, value, v => _draft.PerformanceOverlay.ShowDisplayFps = v);
    }

    // ---------------------------------------------------------------- KI

    public bool PerformanceOverlayShowAnalysisRate
    {
        get => _draft.PerformanceOverlay.ShowAnalysisRate;
        set => SetProperty(_draft.PerformanceOverlay.ShowAnalysisRate, value, v => _draft.PerformanceOverlay.ShowAnalysisRate = v);
    }

    public bool PerformanceOverlayShowCycleTime
    {
        get => _draft.PerformanceOverlay.ShowCycleTime;
        set => SetProperty(_draft.PerformanceOverlay.ShowCycleTime, value, v => _draft.PerformanceOverlay.ShowCycleTime = v);
    }

    public bool PerformanceOverlayShowModelDevices
    {
        get => _draft.PerformanceOverlay.ShowModelDevices;
        set => SetProperty(_draft.PerformanceOverlay.ShowModelDevices, value, v => _draft.PerformanceOverlay.ShowModelDevices = v);
    }

    // ---------------------------------------------------------------- Darstellung

    /// <summary>Darstellung „Nur Text“ (Wahlkarte; Zwilling von <see cref="IsPerformanceOverlayBars"/>).</summary>
    public bool IsPerformanceOverlayText
    {
        get => _draft.PerformanceOverlay.Style == PerformanceOverlayStyle.Text;
        set => SetPerformanceOverlayStyle(PerformanceOverlayStyle.Text, value);
    }

    /// <summary>Darstellung „Text mit Balken“.</summary>
    public bool IsPerformanceOverlayBars
    {
        get => _draft.PerformanceOverlay.Style == PerformanceOverlayStyle.Bars;
        set => SetPerformanceOverlayStyle(PerformanceOverlayStyle.Bars, value);
    }

    private void SetPerformanceOverlayStyle(PerformanceOverlayStyle style, bool selected)
    {
        // Ein Abwählen meldet die Wahlkarte beim Umschalten selbst; nur das Anwählen ändert den Wert.
        if (!selected || _draft.PerformanceOverlay.Style == style)
            return;

        _draft.PerformanceOverlay.Style = style;
        OnPropertyChanged(nameof(IsPerformanceOverlayText));
        OnPropertyChanged(nameof(IsPerformanceOverlayBars));
    }

    /// <summary>Aktualisierungsintervall in Millisekunden; der Regler zeigt Sekunden (<see cref="PerformanceOverlayRefreshText"/>).</summary>
    public int PerformanceOverlayRefreshMs
    {
        get => _draft.PerformanceOverlay.RefreshMs;
        set
        {
            if (SetProperty(_draft.PerformanceOverlay.RefreshMs, value, v => _draft.PerformanceOverlay.RefreshMs = v))
                OnPropertyChanged(nameof(PerformanceOverlayRefreshText));
        }
    }

    public string PerformanceOverlayRefreshText =>
        string.Create(CultureInfo.GetCultureInfo("de-DE"), $"{PerformanceOverlayRefreshMs / 1000.0:0.0} s");

    /// <summary>Schriftgröße der Anzeige in Bildschirmpunkten.</summary>
    public int PerformanceOverlayFontSize
    {
        get => _draft.PerformanceOverlay.FontSize;
        set
        {
            if (SetProperty(_draft.PerformanceOverlay.FontSize, value, v => _draft.PerformanceOverlay.FontSize = v))
                OnPropertyChanged(nameof(PerformanceOverlayFontSizeText));
        }
    }

    public string PerformanceOverlayFontSizeText => $"{PerformanceOverlayFontSize} px";

    /// <summary>Deckkraft des Hintergrunds in Prozent.</summary>
    public int PerformanceOverlayBackgroundOpacity
    {
        get => _draft.PerformanceOverlay.BackgroundOpacityPercent;
        set
        {
            if (SetProperty(_draft.PerformanceOverlay.BackgroundOpacityPercent, value, v => _draft.PerformanceOverlay.BackgroundOpacityPercent = v))
                OnPropertyChanged(nameof(PerformanceOverlayBackgroundOpacityText));
        }
    }

    public string PerformanceOverlayBackgroundOpacityText => $"{PerformanceOverlayBackgroundOpacity} %";

    // ---------------------------------------------------------------- Taste

    /// <summary>Hinweis unter der Karte: mit welcher Taste die Anzeige in der Gastansicht ein- und ausgeblendet wird (aktueller Stand im Tab „Tasten“).</summary>
    public string PerformanceOverlayKeyHint
    {
        get
        {
            var keys = string.Join(" oder ", Editor(KeyAction.PerformanceOverlay).Keys.Select(k => k.DisplayName));
            return keys.Length == 0
                ? "Für die Leistungsanzeige ist noch keine Taste festgelegt. Legen Sie im Tab „Tasten“ unter „Leistungsanzeige ein/aus“ eine Taste fest, um die Anzeige in der Gastansicht ein- und auszublenden. Nach dem Start der Fotobox ist die Anzeige ausgeblendet."
                : $"Blenden Sie die Anzeige in der Gastansicht mit {keys} ein und aus. Die Taste ändern Sie im Tab „Tasten“. Nach dem Start der Fotobox ist die Anzeige ausgeblendet.";
        }
    }

    /// <summary>Wertebereiche der Karte.</summary>
    private void ValidatePerformanceOverlay(List<string> errors)
    {
        var p = _draft.PerformanceOverlay;
        CheckPeaceRange(p.RefreshMs, SettingsLimits.PerformanceOverlayRefreshMsMin, SettingsLimits.PerformanceOverlayRefreshMsMax, "Die Aktualisierung der Leistungsanzeige", "Millisekunden", errors);
        CheckPeaceRange(p.FontSize, SettingsLimits.PerformanceOverlayFontSizeMin, SettingsLimits.PerformanceOverlayFontSizeMax, "Die Schriftgröße der Leistungsanzeige", "Punkten", errors);
        CheckPeaceRange(p.BackgroundOpacityPercent, SettingsLimits.PerformanceOverlayBackgroundOpacityMin, SettingsLimits.PerformanceOverlayBackgroundOpacityMax, "Die Hintergrund-Deckkraft der Leistungsanzeige", "Prozent", errors);
    }
}
