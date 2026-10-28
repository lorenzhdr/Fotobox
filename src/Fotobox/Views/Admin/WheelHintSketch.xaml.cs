using System.Windows;
using System.Windows.Controls;
using Fotobox.Core;

namespace Fotobox.Views.Admin;

/// <summary>Skizze einer Darstellung der Mausradzeile (<see cref="WheelHintStyle"/>) für die Wahlkarten im Admin.</summary>
public sealed partial class WheelHintSketch : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(WheelHintStyle), typeof(WheelHintSketch), new PropertyMetadata(WheelHintStyle.Matching));

    public WheelHintSketch()
    {
        InitializeComponent();
    }

    public WheelHintStyle Kind
    {
        get => (WheelHintStyle)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
