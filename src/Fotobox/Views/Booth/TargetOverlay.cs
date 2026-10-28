using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Fotobox.Core;
using Fotobox.Core.Fun;

namespace Fotobox.Views.Booth;

/// <summary>
/// Zeichnet die Zielboxen „TARGET n“ der Zielerfassung über dem Livebild (nur Darstellung, nimmt keine Eingaben an). Je Ziel-Id gibt es
/// ein wiederverwendetes Element, sodass laufende Animationen stabil bleiben; Position und Größe folgen dem Snapshot weich (150 ms),
/// das erste Platzieren und jede Änderung der Ansichtsgröße/Spiegelung geschieht sofort. Neue Ziele rasten mit der eingestellten
/// Animation ein (<see cref="TargetVisual.StartLockOn"/>). Der sichtbare Bildausschnitt (<see cref="VisibleRegion"/>) wird
/// zurückgemeldet, damit die Engine Personen außerhalb des Beschnitts ignoriert.
/// </summary>
public sealed class TargetOverlay : UserControl
{
    private static readonly Duration MoveDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(150));

    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(FunSnapshot), typeof(TargetOverlay),
        new PropertyMetadata(FunSnapshot.Empty, (d, _) => ((TargetOverlay)d).Rebuild(immediate: false)));

    /// <summary>Auflösung des Kamerabilds; 0 = unbekannt (dann 16:9).</summary>
    public static readonly DependencyProperty ImageWidthProperty = DependencyProperty.Register(
        nameof(ImageWidth), typeof(double), typeof(TargetOverlay),
        new PropertyMetadata(0.0, (d, _) => ((TargetOverlay)d).OnGeometryChanged()));

    public static readonly DependencyProperty ImageHeightProperty = DependencyProperty.Register(
        nameof(ImageHeight), typeof(double), typeof(TargetOverlay),
        new PropertyMetadata(0.0, (d, _) => ((TargetOverlay)d).OnGeometryChanged()));

    /// <summary>Wie das Kamerabild in die Ansicht passt (Standard wie das Livebild der Gastansicht: füllend).</summary>
    public static readonly DependencyProperty FitProperty = DependencyProperty.Register(
        nameof(Fit), typeof(OverlayFit), typeof(TargetOverlay),
        new PropertyMetadata(OverlayFit.UniformToFill, (d, _) => ((TargetOverlay)d).OnGeometryChanged()));

    public static readonly DependencyProperty MirrorProperty = DependencyProperty.Register(
        nameof(Mirror), typeof(bool), typeof(TargetOverlay),
        new PropertyMetadata(false, (d, _) => ((TargetOverlay)d).Rebuild(immediate: true)));

    public static readonly DependencyProperty BoxStyleProperty = DependencyProperty.Register(
        nameof(BoxStyle), typeof(TargetBoxStyle), typeof(TargetOverlay),
        new PropertyMetadata(TargetBoxStyle.Corners, (d, _) => ((TargetOverlay)d).Rebuild(immediate: true)));

    public static readonly DependencyProperty BoxColorProperty = DependencyProperty.Register(
        nameof(BoxColor), typeof(TargetColor), typeof(TargetOverlay),
        new PropertyMetadata(TargetColor.Red, (d, _) => ((TargetOverlay)d).Rebuild(immediate: true)));

    public static readonly DependencyProperty AnimationProperty = DependencyProperty.Register(
        nameof(Animation), typeof(LockOnAnimation), typeof(TargetOverlay),
        new PropertyMetadata(LockOnAnimation.ShrinkPulse));

    /// <summary>Sichtbarer Ausschnitt des (ungespiegelten) Kamerabilds in der Ansicht; per OneWayToSource an das Ansichtsmodell.</summary>
    public static readonly DependencyProperty VisibleRegionProperty = DependencyProperty.Register(
        nameof(VisibleRegion), typeof(NormRect), typeof(TargetOverlay),
        new FrameworkPropertyMetadata(NormRect.Full, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private readonly Canvas _canvas = new() { ClipToBounds = true, IsHitTestVisible = false, Focusable = false };
    private readonly Dictionary<int, TargetVisual> _visuals = [];

    public TargetOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        Content = _canvas;
        SizeChanged += (_, _) => OnGeometryChanged();
        IsVisibleChanged += (_, e) =>
        {
            // Unsichtbar: nichts weiterlaufen lassen (Animationen kosten Render-Zeit über dem Livebild).
            if (!(bool)e.NewValue)
                Clear();
            else
                Rebuild(immediate: true);
        };
    }

    public FunSnapshot Snapshot { get => (FunSnapshot)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public double ImageWidth { get => (double)GetValue(ImageWidthProperty); set => SetValue(ImageWidthProperty, value); }
    public double ImageHeight { get => (double)GetValue(ImageHeightProperty); set => SetValue(ImageHeightProperty, value); }
    public OverlayFit Fit { get => (OverlayFit)GetValue(FitProperty); set => SetValue(FitProperty, value); }
    public bool Mirror { get => (bool)GetValue(MirrorProperty); set => SetValue(MirrorProperty, value); }
    public TargetBoxStyle BoxStyle { get => (TargetBoxStyle)GetValue(BoxStyleProperty); set => SetValue(BoxStyleProperty, value); }
    public TargetColor BoxColor { get => (TargetColor)GetValue(BoxColorProperty); set => SetValue(BoxColorProperty, value); }
    public LockOnAnimation Animation { get => (LockOnAnimation)GetValue(AnimationProperty); set => SetValue(AnimationProperty, value); }
    public NormRect VisibleRegion { get => (NormRect)GetValue(VisibleRegionProperty); set => SetValue(VisibleRegionProperty, value); }

    /// <summary>Aktuell geführte Ziele je Id (für Tests); ausblendende Elemente zählen nicht mehr mit.</summary>
    internal IReadOnlyDictionary<int, TargetVisual> Visuals => _visuals;

    private void OnGeometryChanged()
    {
        UpdateVisibleRegion();
        Rebuild(immediate: true);
    }

    private void UpdateVisibleRegion()
    {
        var region = OverlayMapper.VisibleRegion(ImageWidth, ImageHeight, ActualWidth, ActualHeight, Fit);
        if (region != VisibleRegion)
            SetCurrentValue(VisibleRegionProperty, region);
    }

    private void Clear()
    {
        foreach (var visual in _visuals.Values)
            visual.StopAnimations();
        _visuals.Clear();
        _canvas.Children.Clear();
    }

    private void Rebuild(bool immediate)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var snapshot = Snapshot ?? FunSnapshot.Empty;
        var unit = height > 0 ? Math.Clamp(height / 1080.0, 0.45, 2.5) : 1;
        var labelHeight = TargetVisual.LabelHeight(unit);
        var seen = new HashSet<int>();

        if (width > 0 && height > 0)
        {
            foreach (var target in snapshot.Targets)
            {
                var rect = OverlayMapper.Map(target.Box, ImageWidth, ImageHeight, width, height, Fit, Mirror);
                if (rect.IsEmpty)
                    continue;

                seen.Add(target.Id);
                var created = false;
                if (!_visuals.TryGetValue(target.Id, out var visual))
                {
                    visual = new TargetVisual(target.Id);
                    _visuals[target.Id] = visual;
                    _canvas.Children.Add(visual);
                    created = true;
                }

                visual.BoxStyle = BoxStyle;
                visual.ColorKind = BoxColor;
                visual.Unit = unit;
                visual.LabelText = target.Label;
                visual.LabelInside = rect.Y < labelHeight + 2;
                Place(visual, rect, immediate || created);

                // Einrasten beginnt einmal je Beginn-Zeitpunkt; der Zustand steuert „LOCKING…“ und das Blinken.
                var key = target.LockStartedAt.UtcTicks;
                var start = target.IsLockingOn && visual.LockKey != key;
                if (start)
                    visual.LockKey = key;
                visual.SetLocking(target.IsLockingOn);
                if (!target.IsLockingOn)
                    visual.DisplayedLabel = target.Label;
                if (start)
                    visual.StartLockOn(Animation);
            }
        }

        // Verschwundene Ziele kurz ausblenden und entfernen.
        foreach (var id in _visuals.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            var gone = _visuals[id];
            _visuals.Remove(id);
            FadeOut(gone);
        }
    }

    private static void Place(TargetVisual visual, ViewRect rect, bool immediate)
    {
        visual.Target = rect;
        if (immediate)
        {
            visual.BeginAnimation(Canvas.LeftProperty, null);
            visual.BeginAnimation(Canvas.TopProperty, null);
            visual.BeginAnimation(FrameworkElement.WidthProperty, null);
            visual.BeginAnimation(FrameworkElement.HeightProperty, null);
            Canvas.SetLeft(visual, rect.X);
            Canvas.SetTop(visual, rect.Y);
            visual.Width = rect.Width;
            visual.Height = rect.Height;
            return;
        }

        Move(visual, Canvas.LeftProperty, rect.X);
        Move(visual, Canvas.TopProperty, rect.Y);
        Move(visual, FrameworkElement.WidthProperty, rect.Width);
        Move(visual, FrameworkElement.HeightProperty, rect.Height);
    }

    private static void Move(TargetVisual visual, DependencyProperty property, double to)
    {
        // Der Endwert steht sofort als Basiswert fest; die Animation läuft vom bisher angezeigten Wert dorthin.
        var from = (double)visual.GetValue(property);
        visual.SetValue(property, to);
        if (double.IsNaN(from) || Math.Abs(from - to) < 0.5)
        {
            visual.BeginAnimation(property, null);
            return;
        }
        visual.BeginAnimation(property, new DoubleAnimation(from, to, MoveDuration) { FillBehavior = FillBehavior.Stop });
    }

    private void FadeOut(TargetVisual visual)
    {
        visual.SetLocking(false);
        var fade = new DoubleAnimation(0, FadeOutDuration);
        fade.Completed += (_, _) =>
        {
            visual.StopAnimations();
            _canvas.Children.Remove(visual);
        };
        visual.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
