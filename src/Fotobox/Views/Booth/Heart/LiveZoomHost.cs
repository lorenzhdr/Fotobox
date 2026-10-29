using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Fotobox.Core.Fun;
using Fotobox.Core.Heart;

namespace Fotobox.Views.Booth.Heart;

/// <summary>
/// Digitalzoom der Kisscam im Livebild: Das einzige Kind (Livebild samt Zielmarken, Bärten und Herz-Anzeige) wird so vergrößert und
/// verschoben, dass der geplante <see cref="Region"/> (normiert, ungespiegelt, siehe <see cref="KisscamZoom"/>) die Ansicht füllt. Weil
/// alle Overlays im selben Kind liegen, bleiben sie deckungsgleich mit dem Bild. Der Wechsel dauert <see cref="AnimationMilliseconds"/>
/// ms; ohne Region (null) ist es die Identität. Nur Darstellung, nimmt keine Eingaben an.
/// </summary>
public sealed class LiveZoomHost : Decorator
{
    /// <summary>Dauer des Hinein- und Herauszoomens (Millisekunden).</summary>
    public const int AnimationMilliseconds = 400;

    private static readonly (double Scale, double TranslateX, double TranslateY) Identity = (1, 0, 0);

    public static readonly DependencyProperty RegionProperty = DependencyProperty.Register(
        nameof(Region), typeof(NormRect?), typeof(LiveZoomHost), new PropertyMetadata(null, (d, _) => ((LiveZoomHost)d).Update(animate: true)));

    /// <summary>Auflösung des Kamerabilds; 0 = unbekannt (dann 16:9).</summary>
    public static readonly DependencyProperty ImageWidthProperty = DependencyProperty.Register(
        nameof(ImageWidth), typeof(double), typeof(LiveZoomHost), new PropertyMetadata(0.0, (d, _) => ((LiveZoomHost)d).Update(animate: false)));

    public static readonly DependencyProperty ImageHeightProperty = DependencyProperty.Register(
        nameof(ImageHeight), typeof(double), typeof(LiveZoomHost), new PropertyMetadata(0.0, (d, _) => ((LiveZoomHost)d).Update(animate: false)));

    /// <summary>Wie das Kamerabild in die Ansicht passt (Standard wie das Livebild der Gastansicht: füllend).</summary>
    public static readonly DependencyProperty FitProperty = DependencyProperty.Register(
        nameof(Fit), typeof(OverlayFit), typeof(LiveZoomHost), new PropertyMetadata(OverlayFit.UniformToFill, (d, _) => ((LiveZoomHost)d).Update(animate: false)));

    public static readonly DependencyProperty MirrorProperty = DependencyProperty.Register(
        nameof(Mirror), typeof(bool), typeof(LiveZoomHost), new PropertyMetadata(false, (d, _) => ((LiveZoomHost)d).Update(animate: false)));

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new();
    private readonly TransformGroup _group = new();
    private (double Scale, double TranslateX, double TranslateY) _target = Identity;

    public LiveZoomHost()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        _group.Children.Add(_scale);
        _group.Children.Add(_translate);
        SizeChanged += (_, _) => Update(animate: false);
    }

    /// <summary>Anzuzeigender Ausschnitt (normiert, ungespiegelt); null = ganzes Bild.</summary>
    public NormRect? Region
    {
        get => (NormRect?)GetValue(RegionProperty);
        set => SetValue(RegionProperty, value);
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

    /// <summary>Zieltransformation (<c>p' = Scale · p + Translate</c>) der aktuellen Region; zum Prüfen in Tests.</summary>
    internal (double Scale, double TranslateX, double TranslateY) Target => _target;

    /// <summary>
    /// Zieltransformation für den Ausschnitt <paramref name="region"/> in einer Ansicht <paramref name="viewWidth"/> × <paramref name="viewHeight"/>.
    /// Die Verschiebung wird zusätzlich auf die <b>Ansicht</b> begrenzt (das Livebild und die Overlays sind an den Rändern der Ansicht
    /// beschnitten; bei füllender Darstellung ragt das Kamerabild darüber hinaus, dort gäbe es nach dem Vergrößern nur Leere).
    /// </summary>
    public static (double Scale, double TranslateX, double TranslateY) ComputeTarget(NormRect? region, double imageWidth, double imageHeight,
        double viewWidth, double viewHeight, OverlayFit fit, bool mirror)
    {
        if (region is not { } r || !(viewWidth > 0) || !(viewHeight > 0))
            return Identity;

        var (scale, tx, ty) = KisscamZoom.ViewTransform(r, imageWidth, imageHeight, viewWidth, viewHeight, fit, mirror);
        if (!double.IsFinite(scale) || !double.IsFinite(tx) || !double.IsFinite(ty) || scale <= 1 + 1e-9)
            return Identity;

        tx = Math.Clamp(tx, viewWidth * (1 - scale), 0);
        ty = Math.Clamp(ty, viewHeight * (1 - scale), 0);
        return (scale, tx, ty);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is { } child && !ReferenceEquals(child.RenderTransform, _group))
            child.RenderTransform = _group;
        return base.ArrangeOverride(arrangeSize);
    }

    private void Update(bool animate)
    {
        var target = ComputeTarget(Region, ImageWidth, ImageHeight, ActualWidth, ActualHeight, Fit, Mirror);
        if (NearlyEqual(target, _target))
            return;

        _target = target;
        if (animate)
        {
            Animate(_scale, ScaleTransform.ScaleXProperty, target.Scale);
            Animate(_scale, ScaleTransform.ScaleYProperty, target.Scale);
            Animate(_translate, TranslateTransform.XProperty, target.TranslateX);
            Animate(_translate, TranslateTransform.YProperty, target.TranslateY);
            return;
        }

        Set(_scale, ScaleTransform.ScaleXProperty, target.Scale);
        Set(_scale, ScaleTransform.ScaleYProperty, target.Scale);
        Set(_translate, TranslateTransform.XProperty, target.TranslateX);
        Set(_translate, TranslateTransform.YProperty, target.TranslateY);
    }

    private static void Set(Animatable transform, DependencyProperty property, double value)
    {
        transform.BeginAnimation(property, null);
        transform.SetValue(property, value);
    }

    // Vom aktuellen (ggf. noch laufenden) Wert zum Ziel.
    private static void Animate(Animatable transform, DependencyProperty property, double to)
    {
        var from = (double)transform.GetValue(property);
        transform.BeginAnimation(property, null);
        transform.SetValue(property, to);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(AnimationMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        transform.BeginAnimation(property, animation);
    }

    private static bool NearlyEqual((double Scale, double TranslateX, double TranslateY) a, (double Scale, double TranslateX, double TranslateY) b) =>
        Math.Abs(a.Scale - b.Scale) < 1e-6 && Math.Abs(a.TranslateX - b.TranslateX) < 0.01 && Math.Abs(a.TranslateY - b.TranslateY) < 0.01;
}
