using System.Windows;
using System.Windows.Media;
using Fotobox.Core.Fun;
using Fotobox.Core.Tracking;

namespace Fotobox.Views.Booth;

/// <summary>
/// Tracking-Overlay im Livebild: zeichnet, was die Erkennungen sehen (Personen-Boxen, Gesichter mit fünf Punkten, Handpunkte, Gesten,
/// Bärte), roh und ohne Glätten (siehe <see cref="TrackingOverlayPainter"/>). Nur Darstellung, nimmt keine Eingaben an, ohne Effekte und
/// ohne Animation. Sitzt im selben Raster wie die übrigen Overlays im Zoom-Host, damit es bei der Kisscam-Vergrößerung deckungsgleich mit
/// dem Livebild bleibt; zeichnet nur, wenn <see cref="Frame"/> gesetzt ist. Koordinaten wie die übrigen Overlays (ungespiegelt, Spiegelung
/// über <see cref="Mirror"/>, Standard füllend wie das Livebild).
/// </summary>
public sealed class TrackingOverlay : FrameworkElement
{
    private static FrameworkPropertyMetadata RenderMetadata(object? defaultValue) =>
        new(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(TrackingOverlayFrame), typeof(TrackingOverlay), RenderMetadata(null));

    public static readonly DependencyProperty MirrorProperty = DependencyProperty.Register(
        nameof(Mirror), typeof(bool), typeof(TrackingOverlay), RenderMetadata(false));

    /// <summary>Wie das Kamerabild in die Ansicht passt (Standard wie das Livebild der Gastansicht: füllend).</summary>
    public static readonly DependencyProperty FitProperty = DependencyProperty.Register(
        nameof(Fit), typeof(OverlayFit), typeof(TrackingOverlay), RenderMetadata(OverlayFit.UniformToFill));

    public TrackingOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
    }

    /// <summary>Was gezeichnet wird; null = nichts.</summary>
    public TrackingOverlayFrame? Frame
    {
        get => (TrackingOverlayFrame?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public bool Mirror
    {
        get => (bool)GetValue(MirrorProperty);
        set => SetValue(MirrorProperty, value);
    }

    public OverlayFit Fit
    {
        get => (OverlayFit)GetValue(FitProperty);
        set => SetValue(FitProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Frame is not { } frame)
            return;
        TrackingOverlayPainter.Paint(drawingContext, frame, ActualWidth, ActualHeight, Mirror, Fit, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
