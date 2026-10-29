using System.Windows;
using System.Windows.Media;
using Fotobox.Core.Peace;

namespace Fotobox.Views.Booth.Heart;

/// <summary>
/// Zeichnet den Jumbotron-Rahmen der Kisscam über die ganze Fläche (<see cref="KisscamFrameRenderer"/>, dieselbe Zeichnung wie im
/// Foto). Ohne <see cref="Frame"/> bleibt es leer; nimmt keine Eingaben an.
/// </summary>
public sealed class KisscamFrameElement : FrameworkElement
{
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(PhotoFrame), typeof(KisscamFrameElement),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public KisscamFrameElement()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>Rahmen (Banner-Text und Farbschema); null = nichts zeichnen.</summary>
    public PhotoFrame? Frame
    {
        get => (PhotoFrame?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Frame is not { } frame)
            return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip * 96;
        KisscamFrameRenderer.Draw(drawingContext, new Rect(RenderSize), frame, dpi);
    }
}
