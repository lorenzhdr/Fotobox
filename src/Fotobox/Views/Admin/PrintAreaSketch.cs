using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Views.Booth;

namespace Fotobox.Views.Admin;

/// <summary>
/// Skizze des Druckbereichs für den Admin: ein angedeutetes 16:9-Livebild (bewusst fest eingefärbt wie die Gastansicht, keine Admin-Farben)
/// mit demselben <see cref="PrintAreaOverlay"/> wie in der Gastansicht. Gezeichnet wird im Maßstab 1920 × 1080 und auf die Größe des
/// Elements skaliert, die Linienstärke verhält sich also wie am Gerät. Mit <see cref="UseSample"/> zeigt sie einen festen Beispielausschnitt
/// (seitlicher Zuschnitt) statt <see cref="Area"/> – für die Wahlkarten der Form.
/// </summary>
public sealed class PrintAreaSketch : Border
{
    /// <summary>Beispielausschnitt der Wahlkarten (seitlicher Zuschnitt, etwa Hochformat auf 16:9).</summary>
    public static readonly NormRect SampleArea = new(0.2, 0, 0.6, 1);

    public static readonly DependencyProperty AreaProperty = DependencyProperty.Register(
        nameof(Area), typeof(NormRect?), typeof(PrintAreaSketch), new PropertyMetadata(null, (d, _) => ((PrintAreaSketch)d).UpdateArea()));

    public static readonly DependencyProperty UseSampleProperty = DependencyProperty.Register(
        nameof(UseSample), typeof(bool), typeof(PrintAreaSketch), new PropertyMetadata(false, (d, _) => ((PrintAreaSketch)d).UpdateArea()));

    public static readonly DependencyProperty AreaStyleProperty = DependencyProperty.Register(
        nameof(AreaStyle), typeof(PrintAreaStyle), typeof(PrintAreaSketch), new PropertyMetadata(PrintAreaStyle.LinesShaded));

    public static readonly DependencyProperty AreaColorProperty = DependencyProperty.Register(
        nameof(AreaColor), typeof(PrintAreaColor), typeof(PrintAreaSketch), new PropertyMetadata(PrintAreaColor.White));

    public static readonly DependencyProperty OpacityPercentProperty = DependencyProperty.Register(
        nameof(OpacityPercent), typeof(int), typeof(PrintAreaSketch), new PropertyMetadata(SettingsLimits.PrintAreaOpacityDefault));

    public static readonly DependencyProperty ThicknessPxProperty = DependencyProperty.Register(
        nameof(ThicknessPx), typeof(int), typeof(PrintAreaSketch),
        new PropertyMetadata(SettingsLimits.PrintAreaThicknessDefault, (d, _) => ((PrintAreaSketch)d).UpdateThickness()));

    public static readonly DependencyProperty ThicknessScaleProperty = DependencyProperty.Register(
        nameof(ThicknessScale), typeof(double), typeof(PrintAreaSketch), new PropertyMetadata(1.0, (d, _) => ((PrintAreaSketch)d).UpdateThickness()));

    public static readonly DependencyProperty ShadePercentProperty = DependencyProperty.Register(
        nameof(ShadePercent), typeof(int), typeof(PrintAreaSketch), new PropertyMetadata(SettingsLimits.PrintAreaShadeDefault));

    private readonly PrintAreaOverlay _overlay;

    public PrintAreaSketch()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        CornerRadius = new CornerRadius(4);

        _overlay = new PrintAreaOverlay { ImageWidth = 1920, ImageHeight = 1080, Mirror = false };
        Bind(_overlay, PrintAreaOverlay.AreaStyleProperty, AreaStyleProperty);
        Bind(_overlay, PrintAreaOverlay.AreaColorProperty, AreaColorProperty);
        Bind(_overlay, PrintAreaOverlay.OpacityPercentProperty, OpacityPercentProperty);
        Bind(_overlay, PrintAreaOverlay.ShadePercentProperty, ShadePercentProperty);

        var photo = new Grid { Width = 1920, Height = 1080, ClipToBounds = true };
        photo.Background = new LinearGradientBrush(Color.FromRgb(0x5A, 0x62, 0x74), Color.FromRgb(0x2A, 0x30, 0x3E), 90);
        // Angedeutete Person als Platzhalter für das Livebild.
        var person = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        photo.Children.Add(new Ellipse { Width = 270, Height = 270, Fill = person, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 170, 0, 0) });
        photo.Children.Add(new Ellipse { Width = 780, Height = 620, Fill = person, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 470, 0, 0) });
        photo.Children.Add(_overlay);

        Child = new Viewbox { Stretch = Stretch.Uniform, Child = photo };
        UpdateArea();
        UpdateThickness();
    }

    /// <summary>Anzuzeigender Ausschnitt (normiert); null = keine Markierung. Wird bei <see cref="UseSample"/> ignoriert.</summary>
    public NormRect? Area
    {
        get => (NormRect?)GetValue(AreaProperty);
        set => SetValue(AreaProperty, value);
    }

    /// <summary>Statt <see cref="Area"/> den festen Beispielausschnitt zeigen.</summary>
    public bool UseSample
    {
        get => (bool)GetValue(UseSampleProperty);
        set => SetValue(UseSampleProperty, value);
    }

    public PrintAreaStyle AreaStyle
    {
        get => (PrintAreaStyle)GetValue(AreaStyleProperty);
        set => SetValue(AreaStyleProperty, value);
    }

    public PrintAreaColor AreaColor
    {
        get => (PrintAreaColor)GetValue(AreaColorProperty);
        set => SetValue(AreaColorProperty, value);
    }

    public int OpacityPercent
    {
        get => (int)GetValue(OpacityPercentProperty);
        set => SetValue(OpacityPercentProperty, value);
    }

    public int ThicknessPx
    {
        get => (int)GetValue(ThicknessPxProperty);
        set => SetValue(ThicknessPxProperty, value);
    }

    /// <summary>
    /// Vergrößerung der Linienstärke in der Skizze (Standard 1): In der verkleinerten Vorschau wäre eine Linie von 1–10 Punkten kaum zu
    /// sehen; mit dem Faktor bleiben die Unterschiede der Stärken sichtbar.
    /// </summary>
    public double ThicknessScale
    {
        get => (double)GetValue(ThicknessScaleProperty);
        set => SetValue(ThicknessScaleProperty, value);
    }

    public int ShadePercent
    {
        get => (int)GetValue(ShadePercentProperty);
        set => SetValue(ShadePercentProperty, value);
    }

    private void UpdateArea() => _overlay.Area = UseSample ? SampleArea : Area;

    private void UpdateThickness() => _overlay.ThicknessPx = (int)Math.Round(ThicknessPx * Math.Max(0.1, ThicknessScale));

    private void Bind(DependencyObject target, DependencyProperty targetProperty, DependencyProperty source) =>
        BindingOperations.SetBinding(target, targetProperty, new Binding { Source = this, Path = new PropertyPath(source) });
}
