using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fotobox.Core;

namespace Fotobox.Views.Admin;

/// <summary>Skizze einer Einrast-Animation (<see cref="LockOnAnimation"/>) in der gewählten <see cref="Color"/> für die Wahlkarten im Admin.</summary>
public sealed partial class LockOnSketch : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(LockOnAnimation), typeof(LockOnSketch), new PropertyMetadata(LockOnAnimation.ShrinkPulse));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(TargetColor), typeof(LockOnSketch),
        new PropertyMetadata(TargetColor.Red, (d, e) => ((LockOnSketch)d).BoxBrush = AdminTargetColors.BrushOf((TargetColor)e.NewValue)));

    public static readonly DependencyProperty BoxBrushProperty = DependencyProperty.Register(
        nameof(BoxBrush), typeof(Brush), typeof(LockOnSketch), new PropertyMetadata(AdminTargetColors.BrushOf(TargetColor.Red)));

    public LockOnSketch()
    {
        InitializeComponent();
    }

    public LockOnAnimation Kind
    {
        get => (LockOnAnimation)GetValue(KindProperty);
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
