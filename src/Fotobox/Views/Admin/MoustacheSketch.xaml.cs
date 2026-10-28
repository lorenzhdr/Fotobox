using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Fotobox.Core;
using Fotobox.Views.Booth.Peace;

namespace Fotobox.Views.Admin;

/// <summary>
/// Skizze einer Bartform (<see cref="MoustacheStyle"/>) für die Wahlkarten im Admin; <see cref="MoustacheStyle.Random"/> zeigt alle
/// vier Formen klein. Gezeichnet werden die Geometrien aus <see cref="MoustacheShapes"/>.
/// </summary>
public sealed partial class MoustacheSketch : UserControl
{
    private static readonly Brush Fill = CreateBrush();

    public static readonly DependencyProperty StyleKindProperty = DependencyProperty.Register(
        nameof(StyleKind), typeof(MoustacheStyle), typeof(MoustacheSketch),
        new PropertyMetadata(MoustacheStyle.Twirl, (d, _) => ((MoustacheSketch)d).Rebuild()));

    public MoustacheSketch()
    {
        InitializeComponent();
        Rebuild();
    }

    /// <summary>Dargestellte Bartform (nicht <c>Style</c>, das ist die WPF-Eigenschaft für Stile).</summary>
    public MoustacheStyle StyleKind
    {
        get => (MoustacheStyle)GetValue(StyleKindProperty);
        set => SetValue(StyleKindProperty, value);
    }

    private static Brush CreateBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xF2, 0xD3, 0xA0));
        brush.Freeze();
        return brush;
    }

    private void Rebuild()
    {
        Host.Children.Clear();
        if (StyleKind == MoustacheStyle.Random)
        {
            // 2 × 2 kleine Bärte
            MoustacheStyle[] all = [MoustacheStyle.Twirl, MoustacheStyle.Walrus, MoustacheStyle.Chevron, MoustacheStyle.Brush];
            for (var i = 0; i < all.Length; i++)
                Add(all[i], 30 + (i % 2) * 60, 18 + (i / 2) * 32, 46);
        }
        else
        {
            Add(StyleKind, 60, 34, 92);
        }
    }

    private void Add(MoustacheStyle style, double centerX, double centerY, double width)
    {
        var path = new Path
        {
            Data = MoustacheShapes.Get(style),
            Fill = Fill,
            RenderTransform = new TransformGroup
            {
                Children = [new ScaleTransform(width, width), new TranslateTransform(centerX, centerY)],
            },
        };
        Host.Children.Add(path);
    }
}
