using System.Globalization;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Services.Layout;
using Fotobox.Services.Printing;

namespace Fotobox.ViewModels.Admin;

/// <summary>
/// Tab „Gastansicht“, Karte „Druckbereich anzeigen“ (<see cref="PrintAreaSettings"/>): Schalter, Form, Farbe, Deckkraft, Linienstärke,
/// Abdunklung und eine Vorschau-Skizze (16:9-Platzhalter mit der Markierung für das Standard-Layout des Entwurfs).
/// </summary>
public sealed partial class AdminViewModel
{
    /// <summary>Platzhalter-Livebild der Skizze (16:9, die Gastansicht rechnet mit 1920 × 1080).</summary>
    public const double PrintAreaPreviewImageWidth = 1920, PrintAreaPreviewImageHeight = 1080;

    /// <summary>Druckbereich in der Gastansicht anzeigen (Standard aus).</summary>
    public bool PrintAreaEnabled
    {
        get => _draft.PrintArea.Enabled;
        set
        {
            if (SetProperty(_draft.PrintArea.Enabled, value, v => _draft.PrintArea.Enabled = v))
            {
                OnPropertyChanged(nameof(IsPrintAreaDetailEnabled));
                OnPropertyChanged(nameof(IsPrintAreaShadeEnabled));
            }
        }
    }

    /// <summary>Form der Markierung (Wahlkarten; gebunden über <c>EnumMatchConverter</c>).</summary>
    public PrintAreaStyle PrintAreaShape
    {
        get => _draft.PrintArea.Style;
        set
        {
            if (SetProperty(_draft.PrintArea.Style, value, v => _draft.PrintArea.Style = v))
                OnPropertyChanged(nameof(IsPrintAreaShadeEnabled));
        }
    }

    /// <summary>Farbe der Markierung (Farbpunkte; gebunden über <c>EnumMatchConverter</c>).</summary>
    public PrintAreaColor PrintAreaLineColor
    {
        get => _draft.PrintArea.Color;
        set => SetProperty(_draft.PrintArea.Color, value, v => _draft.PrintArea.Color = v);
    }

    /// <summary>Deckkraft der Linien und Eckmarken in Prozent.</summary>
    public int PrintAreaOpacityPercent
    {
        get => _draft.PrintArea.OpacityPercent;
        set
        {
            if (SetProperty(_draft.PrintArea.OpacityPercent, value, v => _draft.PrintArea.OpacityPercent = v))
                OnPropertyChanged(nameof(PrintAreaOpacityText));
        }
    }

    public string PrintAreaOpacityText => $"{PrintAreaOpacityPercent} %";

    /// <summary>Linienstärke in Bildschirmpunkten.</summary>
    public int PrintAreaThickness
    {
        get => _draft.PrintArea.ThicknessPx;
        set => SetProperty(_draft.PrintArea.ThicknessPx, value, v => _draft.PrintArea.ThicknessPx = v);
    }

    /// <summary>Abdunklung außerhalb des Druckbereichs in Prozent (nur bei der Form „Linien und Abdunklung“).</summary>
    public int PrintAreaShadePercent
    {
        get => _draft.PrintArea.ShadePercent;
        set
        {
            if (SetProperty(_draft.PrintArea.ShadePercent, value, v => _draft.PrintArea.ShadePercent = v))
                OnPropertyChanged(nameof(PrintAreaShadeText));
        }
    }

    public string PrintAreaShadeText => $"{PrintAreaShadePercent} %";

    /// <summary>Der Regler „Abdunklung“ wirkt nur bei der Form „Linien und Abdunklung“.</summary>
    public bool IsPrintAreaShadeEnabled => PrintAreaEnabled && PrintAreaShape == PrintAreaStyle.LinesShaded;

    /// <summary>Alle Felder der Karte außer dem Schalter sind bei ausgeschalteter Funktion ausgegraut.</summary>
    public bool IsPrintAreaDetailEnabled => PrintAreaEnabled;

    /// <summary>
    /// Der gedruckte Ausschnitt für das Standard-Layout des Entwurfs auf einem 16:9-Platzhalterbild; <c>null</c> = kein Zuschnitt
    /// (dann zeigt die Skizze keine Markierung). Rechnet ohne Druckerabfrage mit dem im Entwurf gewählten Papier bzw. 10 × 15 cm
    /// (<see cref="PrintLayoutResolver.ResolvePageSpec"/>).
    /// </summary>
    public NormRect? PrintAreaPreviewRect
    {
        get
        {
            var (guest, page) = PrintAreaPreviewSetup();
            return PrintAreaPreview.Compute(guest, _draft.Layout, page, PrintAreaPreviewImageWidth, PrintAreaPreviewImageHeight);
        }
    }

    /// <summary>Erklärung unter der Skizze: für welches Layout und Papier sie gilt bzw. warum es keine Markierung gibt.</summary>
    public string PrintAreaPreviewText
    {
        get
        {
            var (guest, page) = PrintAreaPreviewSetup();
            var name = GuestLayouts.DisplayName(guest, _draft.Layout.StripPhotoCount);
            var paper = string.Create(CultureInfo.GetCultureInfo("de-DE"),
                $"{page.WidthMm:0.#} × {page.HeightMm:0.#} mm, {(page.IsLandscape ? "Querformat" : "Hochformat")}");
            if (guest == GuestLayout.StripSingle)
                return $"Standard-Layout „{name}“: Die Fotos werden ganz gedruckt, es gibt keinen Zuschnitt und deshalb keine Markierung.";
            if (PrintAreaPreviewRect is null)
                return $"Standard-Layout „{name}“ ({paper}): Das ganze Kamerabild wird gedruckt, es gibt keinen Zuschnitt und deshalb keine Markierung.";
            return $"Skizze für das Standard-Layout „{name}“ ({paper}) mit einem Kamerabild im Format 16:9. Gäste, die per Mausrad ein anderes Layout wählen, sehen die Markierung dieses Layouts.";
        }
    }

    private (GuestLayout Guest, PageSpec Page) PrintAreaPreviewSetup()
    {
        var guest = DefaultGuestLayout;
        var applied = new LayoutSettings();
        GuestLayouts.ApplyTo(applied, guest);
        return (guest, PrintLayoutResolver.ResolvePageSpec(_draft.Printer, applied.Kind, [], null));
    }

    /// <summary>Welche Änderungen betreffen die Skizze (Standard-Layout, Streifenzahl, Text im Fuß, Papier, Ausrichtung)? Ein Ersetzen des Entwurfs meldet alles.</summary>
    private static bool AffectsPrintAreaPreview(string? name) =>
        name is nameof(StripPhotoCount) or nameof(IsStripDoubleCut) or nameof(IsStripSingle) or nameof(CaptionText)
            or nameof(ShowDate) or nameof(SelectedPaper) or nameof(SelectedOrientation) or nameof(IsGuestSingleEnabled)
            or nameof(IsGuestStripDoubleEnabled) or nameof(IsGuestStripSingleEnabled) or nameof(IsGuestOverlayEnabled);

    /// <summary>Wertebereiche der Karte (auch bei ausgeschalteter Funktion, die Felder sind dann nur ausgegraut).</summary>
    private void ValidatePrintArea(List<string> errors)
    {
        var p = _draft.PrintArea;
        CheckPeaceRange(p.OpacityPercent, SettingsLimits.PrintAreaOpacityMin, SettingsLimits.PrintAreaOpacityMax, "Die Deckkraft des Druckbereichs", "Prozent", errors);
        CheckPeaceRange(p.ThicknessPx, SettingsLimits.PrintAreaThicknessMin, SettingsLimits.PrintAreaThicknessMax, "Die Linienstärke des Druckbereichs", "Punkten", errors);
        CheckPeaceRange(p.ShadePercent, SettingsLimits.PrintAreaShadeMin, SettingsLimits.PrintAreaShadeMax, "Die Abdunklung des Druckbereichs", "Prozent", errors);
    }
}
