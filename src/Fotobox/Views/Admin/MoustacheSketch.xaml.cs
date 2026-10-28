using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.Views.Booth.Peace;

namespace Fotobox.Views.Admin;

/// <summary>
/// Skizze einer Bartform (<see cref="MoustacheStyle"/>) für die Wahlkarten im Admin; <see cref="MoustacheStyle.Random"/> zeigt alle
/// sieben Formen klein in wechselnden Haarfarben. Gezeichnet wird die Haartextur aus <see cref="MoustacheTextures"/>
/// (dieselbe wie im Livebild und im Foto), jede Form so eingepasst, dass sie ganz sichtbar ist (auch Fu Manchu und Hufeisen).
/// </summary>
public sealed partial class MoustacheSketch : UserControl
{
    private const double CanvasWidth = 120, CanvasHeight = 68;

    public static readonly DependencyProperty StyleKindProperty = DependencyProperty.Register(
        nameof(StyleKind), typeof(MoustacheStyle), typeof(MoustacheSketch),
        new PropertyMetadata(MoustacheStyle.Twirl, (d, _) => ((MoustacheSketch)d).Rebuild()));

    public static readonly DependencyProperty HairColorProperty = DependencyProperty.Register(
        nameof(HairColor), typeof(MoustacheColor), typeof(MoustacheSketch),
        new PropertyMetadata(MoustacheColor.DarkBrown, (d, _) => ((MoustacheSketch)d).Rebuild()));

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

    /// <summary>Haarfarbe der Skizze (bei „Zufällig“ wechselt sie je Form).</summary>
    public MoustacheColor HairColor
    {
        get => (MoustacheColor)GetValue(HairColorProperty);
        set => SetValue(HairColorProperty, value);
    }

    private void Rebuild()
    {
        Host.Children.Clear();
        if (StyleKind == MoustacheStyle.Random)
        {
            // 4 + 3 kleine Bärte in zwei Reihen
            var all = MoustacheStyles.All;
            MoustacheColor[] colors =
            [
                MoustacheColor.DarkBrown, MoustacheColor.Ginger, MoustacheColor.Gray, MoustacheColor.Black,
                MoustacheColor.Blond, MoustacheColor.Brown, MoustacheColor.DarkBrown,
            ];
            for (var i = 0; i < all.Count; i++)
            {
                var row = i < 4 ? 0 : 1;
                var perRow = row == 0 ? 4 : all.Count - 4;
                var column = row == 0 ? i : i - 4;
                var cellWidth = CanvasWidth / perRow;
                var cellHeight = CanvasHeight / 2;
                Add(all[i], colors[i % colors.Length], column * cellWidth, row * cellHeight, cellWidth, cellHeight, maxWidth: 30);
            }
        }
        else
        {
            Add(StyleKind, HairColor, 0, 0, CanvasWidth, CanvasHeight, maxWidth: 92);
        }
    }

    // Passt die Textur in die Zelle (Breite der Form höchstens maxWidth); die Bounding-Box der Form liegt mittig in der Zelle.
    private void Add(MoustacheStyle style, MoustacheColor color, double cellX, double cellY, double cellWidth, double cellHeight, double maxWidth)
    {
        var shape = MoustacheShapes.Get(style).Bounds;
        var fit = Math.Min((cellWidth - 6) / shape.Width, (cellHeight - 6) / shape.Height);
        var width = Math.Min(maxWidth, fit);
        var texture = MoustacheTextures.Get(style, color, width);

        var originX = cellX + cellWidth / 2;
        var originY = cellY + cellHeight / 2 - (shape.Y + shape.Height / 2) * width;
        var image = new Image
        {
            Source = texture.Bitmap,
            Width = texture.UnitRect.Width * width,
            Height = texture.UnitRect.Height * width,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(image, originX + texture.UnitRect.X * width);
        Canvas.SetTop(image, originY + texture.UnitRect.Y * width);
        Host.Children.Add(image);
    }
}
