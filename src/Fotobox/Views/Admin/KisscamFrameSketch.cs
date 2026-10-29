using System.Windows;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.Views.Booth.Heart;

namespace Fotobox.Views.Admin;

/// <summary>
/// Skizze eines Kisscam-Rahmens für die Wahlkarten der Farbauswahl im Admin: Der Rahmen kommt vom <see cref="KisscamFrameRenderer"/>
/// (dieselbe Zeichnung wie im Livebild und im Foto) und liegt über einem neutralen grauen Verlauf, der das Foto andeutet.
/// Bewusst fest eingefärbt (wie die Gastansicht), nicht aus den Admin-Paletten.
/// </summary>
public sealed class KisscamFrameSketch : FrameworkElement
{
    private const double SketchWidth = 144, SketchHeight = 81;

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(KisscamColor), typeof(KisscamFrameSketch),
        new FrameworkPropertyMetadata(KisscamColor.PinkRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BannerTextProperty = DependencyProperty.Register(
        nameof(BannerText), typeof(string), typeof(KisscamFrameSketch),
        new FrameworkPropertyMetadata(HeartSettings.DefaultKisscamBannerText, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush PhotoBrush = CreatePhotoBrush();

    static KisscamFrameSketch()
    {
        WidthProperty.OverrideMetadata(typeof(KisscamFrameSketch), new FrameworkPropertyMetadata(SketchWidth));
        HeightProperty.OverrideMetadata(typeof(KisscamFrameSketch), new FrameworkPropertyMetadata(SketchHeight));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(KisscamFrameSketch), new UIPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(KisscamFrameSketch), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Dargestelltes Farbschema (nicht <c>Foreground</c> o. Ä.: eine eigene Eigenschaft, gebunden wie bei den anderen Skizzen).</summary>
    public KisscamColor Color
    {
        get => (KisscamColor)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Banner-Text der Skizze (Standard „KISS CAM“).</summary>
    public string BannerText
    {
        get => (string)GetValue(BannerTextProperty);
        set => SetValue(BannerTextProperty, value);
    }

    private static Brush CreatePhotoBrush()
    {
        var brush = new LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x62, 0x74),
            System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x3E), 90);
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        if (area.Width <= 0 || area.Height <= 0)
            return;
        drawingContext.PushClip(new RectangleGeometry(area, 4, 4));
        try
        {
            drawingContext.DrawRectangle(PhotoBrush, null, area);
            KisscamFrameRenderer.Draw(drawingContext, area, new PhotoFrame(PhotoFrameKind.Kisscam, BannerText ?? string.Empty, Color), 96);
        }
        finally
        {
            drawingContext.Pop();
        }
    }
}
