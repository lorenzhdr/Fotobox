using System.Windows;
using Fotobox.Core.Fun;
using Fotobox.Core.Peace;

namespace Fotobox.Views.Booth.Peace;

/// <summary>
/// Zeichnet Bärte, den Haltefortschritt der Peace-Hand und (im Admin) die Handpunkte über dem Livebild (nur Darstellung, nimmt keine
/// Eingaben an). Platzhalter: zeichnet noch nichts; die Eigenschaften sind endgültig.
/// </summary>
public sealed class PeaceOverlay : FrameworkElement
{
    private static FrameworkPropertyMetadata RenderMetadata(object? defaultValue) =>
        new(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty MoustachesProperty = DependencyProperty.Register(
        nameof(Moustaches), typeof(IReadOnlyList<MoustacheView>), typeof(PeaceOverlay), RenderMetadata(null));

    public static readonly DependencyProperty HandsProperty = DependencyProperty.Register(
        nameof(Hands), typeof(IReadOnlyList<HandView>), typeof(PeaceOverlay), RenderMetadata(null));

    /// <summary>Auflösung des Kamerabilds; 0 = unbekannt (dann 16:9).</summary>
    public static readonly DependencyProperty ImageWidthProperty = DependencyProperty.Register(
        nameof(ImageWidth), typeof(double), typeof(PeaceOverlay), RenderMetadata(0.0));

    public static readonly DependencyProperty ImageHeightProperty = DependencyProperty.Register(
        nameof(ImageHeight), typeof(double), typeof(PeaceOverlay), RenderMetadata(0.0));

    /// <summary>Wie das Kamerabild in die Ansicht passt (Standard wie das Livebild der Gastansicht: füllend).</summary>
    public static readonly DependencyProperty FitProperty = DependencyProperty.Register(
        nameof(Fit), typeof(OverlayFit), typeof(PeaceOverlay), RenderMetadata(OverlayFit.UniformToFill));

    public static readonly DependencyProperty MirrorProperty = DependencyProperty.Register(
        nameof(Mirror), typeof(bool), typeof(PeaceOverlay), RenderMetadata(false));

    public static readonly DependencyProperty ShowHoldProgressProperty = DependencyProperty.Register(
        nameof(ShowHoldProgress), typeof(bool), typeof(PeaceOverlay), RenderMetadata(false));

    public static readonly DependencyProperty ShowLandmarksProperty = DependencyProperty.Register(
        nameof(ShowLandmarks), typeof(bool), typeof(PeaceOverlay), RenderMetadata(false));

    public PeaceOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
    }

    public IReadOnlyList<MoustacheView>? Moustaches
    {
        get => (IReadOnlyList<MoustacheView>?)GetValue(MoustachesProperty);
        set => SetValue(MoustachesProperty, value);
    }

    public IReadOnlyList<HandView>? Hands
    {
        get => (IReadOnlyList<HandView>?)GetValue(HandsProperty);
        set => SetValue(HandsProperty, value);
    }

    public double ImageWidth
    {
        get => (double)GetValue(ImageWidthProperty);
        set => SetValue(ImageWidthProperty, value);
    }

    public double ImageHeight
    {
        get => (double)GetValue(ImageHeightProperty);
        set => SetValue(ImageHeightProperty, value);
    }

    public OverlayFit Fit
    {
        get => (OverlayFit)GetValue(FitProperty);
        set => SetValue(FitProperty, value);
    }

    public bool Mirror
    {
        get => (bool)GetValue(MirrorProperty);
        set => SetValue(MirrorProperty, value);
    }

    public bool ShowHoldProgress
    {
        get => (bool)GetValue(ShowHoldProgressProperty);
        set => SetValue(ShowHoldProgressProperty, value);
    }

    public bool ShowLandmarks
    {
        get => (bool)GetValue(ShowLandmarksProperty);
        set => SetValue(ShowLandmarksProperty, value);
    }

    protected override void OnRender(System.Windows.Media.DrawingContext drawingContext)
    {
        // Platzhalter: Zeichnung folgt.
    }
}
