using System.Windows;
using System.Windows.Controls;
using Fotobox.Core.Heart;
using Fotobox.Core.Peace;

namespace Fotobox.Views.Booth.Heart;

/// <summary>
/// Kisscam-Ansicht: der Jumbotron-Rahmen (<see cref="Frame"/>) über die ganze Fläche und, solange <see cref="ShowText"/> gilt, der
/// Gasttext (<see cref="Text"/>) unten zwischen den unteren Eckherzen – groß, weiß und fett auf einem dunklen Band. Banner und Rahmen
/// bleiben frei. Wird in der Gastansicht und in der Admin-Vorschau verwendet; nimmt keine Eingaben an.
/// </summary>
public sealed partial class KisscamOverlay : UserControl
{
    /// <summary>Schriftgröße des Gasttextes relativ zur kürzeren Kante der Fläche.</summary>
    public const double FontFactor = 0.058;

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(PhotoFrame), typeof(KisscamOverlay),
        new PropertyMetadata(null, (d, e) => ((KisscamOverlay)d).FrameElement.Frame = (PhotoFrame?)e.NewValue));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(KisscamOverlay),
        new PropertyMetadata(string.Empty, (d, _) => ((KisscamOverlay)d).UpdateText()));

    /// <summary>Gasttext einblenden (in den letzten Sekunden zeigt die Gastansicht stattdessen die Ziffern).</summary>
    public static readonly DependencyProperty ShowTextProperty = DependencyProperty.Register(
        nameof(ShowText), typeof(bool), typeof(KisscamOverlay),
        new PropertyMetadata(true, (d, _) => ((KisscamOverlay)d).UpdateText()));

    public KisscamOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateMetrics();
        UpdateText();
    }

    /// <summary>Rahmen (Banner-Text und Farbschema); null = kein Rahmen.</summary>
    public PhotoFrame? Frame
    {
        get => (PhotoFrame?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>Gasttext, fertig formatiert (z. B. mit <see cref="KisscamTextFormatter"/>).</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool ShowText
    {
        get => (bool)GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    private void UpdateText()
    {
        GuestText.Text = Text ?? string.Empty;
        TextBand.Visibility = ShowText && !string.IsNullOrWhiteSpace(Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    // Der Text steht unten zwischen den unteren Eckherzen und über dem Rahmenband; sein Platz hängt nur von der Fläche ab.
    private void UpdateMetrics()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (!(width > 0) || !(height > 0))
            return;

        var geometry = KisscamFrameGeometry.For(width, height);
        if (geometry.CornerHearts.Count < 4)
            return;

        var m = Math.Min(width, height);
        var gap = KisscamFrameGeometry.HeartGapFactor * m;
        var side = Math.Max(0, geometry.CornerHearts[2].Right + gap);
        var bottom = geometry.Thickness + 2 * gap;
        TextBand.Margin = new Thickness(side, 0, side, bottom);
        TextBand.MaxWidth = Math.Max(1, width - 2 * side);
        TextBand.CornerRadius = new CornerRadius(0.025 * m);
        TextBand.Padding = new Thickness(0.026 * m, 0.013 * m, 0.026 * m, 0.013 * m);
        GuestText.FontSize = Math.Max(12, FontFactor * m);
    }
}
