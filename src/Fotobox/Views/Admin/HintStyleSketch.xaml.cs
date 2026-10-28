using System.Windows;
using System.Windows.Controls;
using Fotobox.Core;

namespace Fotobox.Views.Admin;

/// <summary>Skizze eines Hinweis-Stils (<see cref="HintStyle"/>) für die Wahlkarten im Admin; <see cref="AtTop"/> für die Video-Zeile oben.</summary>
public sealed partial class HintStyleSketch : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(HintStyle), typeof(HintStyleSketch), new PropertyMetadata(HintStyle.Pill));

    public static readonly DependencyProperty AtTopProperty = DependencyProperty.Register(
        nameof(AtTop), typeof(bool), typeof(HintStyleSketch), new PropertyMetadata(false));

    public HintStyleSketch()
    {
        InitializeComponent();
    }

    public HintStyle Kind
    {
        get => (HintStyle)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Hinweis oben im Bild (Video) statt unten (Foto).</summary>
    public bool AtTop
    {
        get => (bool)GetValue(AtTopProperty);
        set => SetValue(AtTopProperty, value);
    }
}
