using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fotobox.Core;

namespace Fotobox.Views.Admin;

/// <summary>Feste Farben der Zielerfassung für die Skizzen im Admin (gleiche Werte wie in der Gastansicht, nicht themenabhängig).</summary>
internal static class AdminTargetColors
{
    public static Color Of(TargetColor color) => color switch
    {
        TargetColor.Green => Color.FromRgb(0x39, 0xFF, 0x14),
        TargetColor.Cyan => Color.FromRgb(0x00, 0xE5, 0xFF),
        TargetColor.Yellow => Color.FromRgb(0xFF, 0xD6, 0x0A),
        TargetColor.White => Color.FromRgb(0xFF, 0xFF, 0xFF),
        _ => Color.FromRgb(0xFF, 0x3B, 0x30),
    };

    public static Brush BrushOf(TargetColor color)
    {
        var brush = new SolidColorBrush(Of(color));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Skizze eines Box-Stils (<see cref="TargetBoxStyle"/>) in der gewählten <see cref="Color"/> für die Wahlkarten im Admin.</summary>
public sealed partial class TargetStyleSketch : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(TargetBoxStyle), typeof(TargetStyleSketch), new PropertyMetadata(TargetBoxStyle.Corners));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(TargetColor), typeof(TargetStyleSketch),
        new PropertyMetadata(TargetColor.Red, (d, e) => ((TargetStyleSketch)d).BoxBrush = AdminTargetColors.BrushOf((TargetColor)e.NewValue)));

    public static readonly DependencyProperty BoxBrushProperty = DependencyProperty.Register(
        nameof(BoxBrush), typeof(Brush), typeof(TargetStyleSketch), new PropertyMetadata(AdminTargetColors.BrushOf(TargetColor.Red)));

    public TargetStyleSketch()
    {
        InitializeComponent();
    }

    public TargetBoxStyle Kind
    {
        get => (TargetBoxStyle)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public TargetColor Color
    {
        get => (TargetColor)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Aus <see cref="Color"/> abgeleiteter Pinsel (nur für die Bindungen der Skizze).</summary>
    public Brush BoxBrush
    {
        get => (Brush)GetValue(BoxBrushProperty);
        private set => SetValue(BoxBrushProperty, value);
    }
}
