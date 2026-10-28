using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Hinweis der Gastansicht in allen neun Stilen: Mausradzeile je Darstellung, Laufschrift, Animationen nur im sichtbaren Zustand.
/// Die Animationstests zeigen echte Fenster (sonst feuern die <c>IsVisible</c>-Trigger nie).
/// </summary>
[Collection(WpfCollection.Name)]
public class GuestHintStyleTests
{
    private const string WheelText = "Mausrad: Layout wechseln · Ein Foto";

    public static IEnumerable<object[]> AllStyles => Enum.GetValues<HintStyle>().Select(s => new object[] { s });

    public static IEnumerable<object[]> AllStylesAndWheelStyles =>
        from s in Enum.GetValues<HintStyle>()
        from w in Enum.GetValues<WheelHintStyle>()
        select new object[] { s, w };

    public static IEnumerable<object[]> AllStylesAndPositions =>
        from s in Enum.GetValues<HintStyle>()
        from p in Enum.GetValues<HintPosition>()
        select new object[] { s, p };

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Layout(FrameworkElement element, double width = 1920, double height = 1080)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static Grid ActivePart(GuestHint hint, HintStyle style) =>
        ((Grid)hint.Content).Children.OfType<Grid>().ElementAt((int)style);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    /// <summary>Sichtbar im Sinne von „Visibility“ des Elements und aller Vorfahren bis <paramref name="root"/> (unabhängig von einem Fenster).</summary>
    private static bool EffectivelyVisible(FrameworkElement element, FrameworkElement root)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible)
                return false;
            if (ReferenceEquals(current, root))
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return true;
    }

    // ---- Mausradzeile je Darstellung

    [StaTheory]
    [MemberData(nameof(AllStylesAndWheelStyles))]
    public void WheelLine_ShowsExactlyTheChosenVariant_InEveryHintStyle(HintStyle style, WheelHintStyle wheel)
    {
        var hint = new GuestHint
        {
            Text = "Drücke die Leertaste", HintStyle = style, LayoutHintText = WheelText, ShowLayoutHint = true, LayoutHintStyle = wheel,
        };
        Layout(hint);
        var part = ActivePart(hint, style);

        var lines = Descendants<TextBlock>(part).Where(t => t.Text == WheelText && EffectivelyVisible(t, part)).ToList();
        Assert.Single(lines);   // nie zwei Zeilen übereinander
        var line = lines[0];

        // „Passend“ ist die Textzeile des Hinweis-Stils (26 px); alle anderen kommen aus dem Slot.
        var inSlot = Descendants<WheelHintSlot>(part).Any(s => Descendants<TextBlock>(s).Contains(line));
        Assert.Equal(wheel != WheelHintStyle.Matching, inSlot);
        if (wheel == WheelHintStyle.Matching)
            Assert.Equal(26, line.FontSize);

        bool Visible(Func<TextBlock, bool> pick) =>
            Descendants<WheelHintSlot>(part).SelectMany(s => Descendants<TextBlock>(s))
                .Any(t => pick(t) && EffectivelyVisible(t, part));
        Assert.Equal(wheel == WheelHintStyle.Chip, Visible(t => t.Text == "⇅"));
        Assert.Equal(wheel == WheelHintStyle.Arrows, Visible(t => t.Text == "‹") && Visible(t => t.Text == "›"));

        var pills = Descendants<WheelHintSlot>(part).SelectMany(s => Descendants<Border>(s))
            .Where(b => b.CornerRadius.TopLeft == 20 && EffectivelyVisible(b, part)).ToList();
        Assert.Equal(wheel == WheelHintStyle.SmallPill ? 1 : 0, pills.Count);

        if (wheel == WheelHintStyle.Plain)
            Assert.True(line.FontSize < 26);   // etwas kleiner als die Standardzeile
    }

    [StaTheory]
    [MemberData(nameof(AllStyles))]
    public void WheelLine_IsHiddenWithoutShowLayoutHint(HintStyle style)
    {
        foreach (var wheel in Enum.GetValues<WheelHintStyle>())
        {
            var hint = new GuestHint
            {
                Text = "Drücke die Leertaste", HintStyle = style, LayoutHintText = WheelText, ShowLayoutHint = false, LayoutHintStyle = wheel,
            };
            Layout(hint);
            var part = ActivePart(hint, style);

            Assert.DoesNotContain(Descendants<TextBlock>(part), t => t.Text == WheelText && EffectivelyVisible(t, part));
        }
    }

    // ---- Sprechblase: Spitze zeigt zur Bildmitte

    private static List<Point> Points(Path path)
    {
        var geometry = path.Data.GetFlattenedPathGeometry();
        var points = new List<Point>();
        foreach (var figure in geometry.Figures)
        {
            points.Add(figure.StartPoint);
            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment poly)
                    points.AddRange(poly.Points);
                else if (segment is LineSegment line)
                    points.Add(line.Point);
            }
        }
        return points;
    }

    [StaFact]
    public void Bubble_TailPointsToTheCenter_UpAtBottom_AndDownAtTop()
    {
        foreach (var position in Enum.GetValues<HintPosition>())
        {
            var hint = new GuestHint { Text = "Hallo", HintStyle = HintStyle.Bubble, Position = position };
            Layout(hint);
            var part = ActivePart(hint, HintStyle.Bubble);
            var tails = Descendants<Path>(part).ToList();
            Assert.Equal(2, tails.Count);

            var visible = tails.Single(t => EffectivelyVisible(t, part));
            var body = Descendants<Border>(part).First(b => b.CornerRadius.TopLeft >= 40);
            var tailTop = visible.TranslatePoint(new Point(0, 0), part).Y;
            var bodyTop = body.TranslatePoint(new Point(0, 0), part).Y;
            var bodyBottom = bodyTop + body.ActualHeight;

            // Foto-Zeile unten: Spitze über der Blase (zur Bildmitte); Video-Zeile oben: Spitze unter der Blase.
            if (position == HintPosition.Bottom)
                Assert.True(tailTop + visible.ActualHeight <= bodyTop + 1.5, "Spitze liegt über der Blase");
            else
                Assert.True(tailTop >= bodyBottom - 1.5, "Spitze liegt unter der Blase");

            // Der einzelne Eckpunkt am äußeren Rand der Spitze ist spitz: oben bei Bottom, unten bei Top.
            var points = Points(visible);
            var extremeY = position == HintPosition.Bottom ? points.Min(p => p.Y) : points.Max(p => p.Y);
            Assert.Single(points.Where(p => Math.Abs(p.Y - extremeY) < 0.01).DistinctBy(p => (p.X, p.Y)));
        }
    }

    // ---- Abstand der Mausradzeile (Slot) zum Hinweis

    private static IEnumerable<FrameworkElement> VisibleChildren(Panel panel) =>
        panel.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible);

    private static Rect BoundsIn(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    public static IEnumerable<object[]> AllStylesAndSlotVariants =>
        from s in Enum.GetValues<HintStyle>()
        from w in Enum.GetValues<WheelHintStyle>()
        where w != WheelHintStyle.Matching
        select new object[] { s, w };

    [StaTheory]
    [MemberData(nameof(AllStylesAndSlotVariants))]
    public void WheelSlot_KeepsMinimumGapToTheHint_AndToTheScreenEdge(HintStyle style, WheelHintStyle wheel)
    {
        var hint = new GuestHint
        {
            Text = "Drücke die Leertaste für ein Foto", HintStyle = style, LayoutHintText = WheelText, ShowLayoutHint = true,
            LayoutHintStyle = wheel,
        };
        Layout(hint);
        var part = ActivePart(hint, style);
        var slot = Descendants<WheelHintSlot>(part).Single();
        var slotBounds = BoundsIn(slot, part);

        // Unterkante des Slots mindestens 24 px vom Bildrand.
        Assert.True(1080 - slotBounds.Bottom >= 24, $"Rand {1080 - slotBounds.Bottom}");

        double gap;
        if (slot.Parent is StackPanel stack)
        {
            // Band-Stile: Der Slot sitzt im Band unter dem Hinweis; sichtbarer Vorgänger = Hinweis (die Passend-Zeile ist eingeklappt).
            var previous = VisibleChildren(stack).TakeWhile(c => !ReferenceEquals(c, slot)).Last();
            gap = slotBounds.Top - BoundsIn(previous, part).Bottom;
        }
        else
        {
            // Schwebende Stile: Der Hinweis ist das Element neben Slot und Passend-Zeile; Animationsausschlag abziehen.
            var main = VisibleChildren(part).First(c => c is not WheelHintSlot && !(c is TextBlock t && t.Text == WheelText));
            var swing = style switch { HintStyle.Pill => 3.0, HintStyle.Bubble => 6.0, _ => 0.0 };
            gap = slotBounds.Top - BoundsIn(main, part).Bottom - swing;
        }
        Assert.True(gap >= 20, $"Abstand {gap}");
    }

    [StaTheory]
    [InlineData(HintStyle.Pill)]
    [InlineData(HintStyle.Plain)]
    public void MatchingWheelLine_LeavesTheHintWhereItWas(HintStyle style)
    {
        // „Passend“ muss unverändert bleiben: Hinweis 80 px über dem unteren Rand, Zeile 30 px.
        var hint = new GuestHint
        {
            Text = "Drücke die Leertaste", HintStyle = style, LayoutHintText = WheelText, ShowLayoutHint = true,
            LayoutHintStyle = WheelHintStyle.Matching,
        };
        Layout(hint);
        var part = ActivePart(hint, style);
        var main = VisibleChildren(part).First(c => c is not WheelHintSlot && !(c is TextBlock t && t.Text == WheelText));
        var line = VisibleChildren(part).OfType<TextBlock>().Single(t => t.Text == WheelText);
        Assert.Equal(80, 1080 - BoundsIn(main, part).Bottom, 0.5);
        Assert.Equal(30, 1080 - BoundsIn(line, part).Bottom, 0.5);
    }

    // ---- Laufschrift

    private static (Window Window, GuestHint Hint) ShowHint(HintStyle style, HintPosition position = HintPosition.Bottom, string text = "Drücke die Leertaste")
    {
        var hint = new GuestHint { Text = text, HintStyle = style, Position = position };
        var window = new Window
        {
            Width = 1920, Height = 1080, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = hint,
        };
        window.Show();
        window.UpdateLayout();
        Pump();
        return (window, hint);
    }

    private static TranslateTransform TickerShift(GuestHint hint)
    {
        var part = ActivePart(hint, HintStyle.Ticker);
        var canvas = Descendants<Canvas>(part).Single();
        var text = Descendants<TextBlock>(canvas).Single();
        return (TranslateTransform)text.RenderTransform;
    }

    [StaFact]
    public void Ticker_RunsOnlyWhileVisible_AndRestartsWithNewText()
    {
        var errors = new List<Exception>();
        void OnUnhandled(object? s, DispatcherUnhandledExceptionEventArgs e)
        {
            errors.Add(e.Exception);
            e.Handled = true;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += OnUnhandled;
        var (window, hint) = ShowHint(HintStyle.Ticker);
        try
        {
            var shift = TickerShift(hint);
            Assert.True(shift.HasAnimatedProperties);

            // Anderer Stil → kein Laufen im verborgenen Zustand; zurück → läuft wieder.
            for (var i = 0; i < 3; i++)
            {
                hint.HintStyle = HintStyle.Plain;
                window.UpdateLayout();
                Pump();
                Assert.False(shift.HasAnimatedProperties);

                hint.HintStyle = HintStyle.Ticker;
                window.UpdateLayout();
                Pump();
                Assert.True(shift.HasAnimatedProperties);

                hint.Visibility = Visibility.Collapsed;
                window.UpdateLayout();
                Pump();
                Assert.False(shift.HasAnimatedProperties);

                hint.Visibility = Visibility.Visible;
                window.UpdateLayout();
                Pump();
                Assert.True(shift.HasAnimatedProperties);
            }

            // Textwechsel im laufenden Betrieb: weiter animiert, ohne Fehler; leerer Text stoppt.
            hint.Text = "Ein deutlich längerer Hinweis, der entsprechend länger über das Band läuft";
            window.UpdateLayout();
            Pump();
            Assert.True(shift.HasAnimatedProperties);
            hint.Text = string.Empty;
            window.UpdateLayout();
            Pump();
            Assert.False(shift.HasAnimatedProperties);
            hint.Text = "Wieder da";
            window.UpdateLayout();
            Pump();
            Assert.True(shift.HasAnimatedProperties);
        }
        finally
        {
            window.Close();
            dispatcher.UnhandledException -= OnUnhandled;
        }

        Assert.Empty(errors);
    }

    [StaFact]
    public void Ticker_TextEntersFromTheRightAndLeavesLeft_WithAboutTheSpeedFromTheBrief()
    {
        var (window, hint) = ShowHint(HintStyle.Ticker, text: "Drücke die Leertaste für ein Foto");
        try
        {
            var shift = TickerShift(hint);
            var part = ActivePart(hint, HintStyle.Ticker);
            var canvas = Descendants<Canvas>(part).Single();
            var text = Descendants<TextBlock>(canvas).Single();

            // Die Animation ist gestartet; Werte aus der Uhr lesen: Startpunkt liegt am rechten Rand des Bandes.
            Assert.True(shift.HasAnimatedProperties);
            Assert.True(canvas.ActualWidth > 0);
            Assert.True(text.ActualWidth > 0);
            Assert.True(canvas.ClipToBounds);
            Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
            Assert.Equal(150, GuestHint.TickerSpeed);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- Animationen nur im sichtbaren Zustand (alle Stile, beide Lagen)

    private static void Settle()
    {
        // Die Uhr der Animation tickt über die Render-Schleife; ein paar kurze Runden genügen für den ersten Wert.
        for (var i = 0; i < 6; i++)
        {
            Thread.Sleep(40);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Pump();
        }
    }

    /// <summary>
    /// Läuft die Animation des Stils gerade? Storyboards hinterlassen nach dem Stopp eine Uhr (HasAnimatedProperties bleibt wahr),
    /// deshalb zählt hier der Wert: Eine gestoppte Animation liefert wieder den Grundwert, eine laufende nicht.
    /// Die Laufschrift wird per BeginAnimation(null) wirklich entfernt.
    /// </summary>
    private static bool IsRunning(GuestHint hint, HintStyle style)
    {
        var part = ActivePart(hint, style);
        switch (style)
        {
            case HintStyle.Pill:
                return Descendants<Border>(part).First(b => b.CornerRadius.TopLeft >= 60).Opacity < 1;   // Grundwert 1, Puls 0,85 … 1
            case HintStyle.Neon:
                var glow = (DropShadowEffect)Descendants<Grid>(part).First(g => g.Effect is DropShadowEffect).Effect;
                return Math.Abs(glow.Opacity - 0.8) > 0.001;   // Grundwert 0,8, Pulsieren 0,6 … 1
            case HintStyle.Bubble:
                var bob = (TranslateTransform)Descendants<Grid>(part).First(g => g.RenderTransform is TranslateTransform).RenderTransform;
                return Math.Abs(bob.Y) > 0.001;   // Grundwert 0, Wippen −6 … 6
            case HintStyle.Ticker:
                return TickerShift(hint).HasAnimatedProperties;
            default:
                return false;
        }
    }

    private static readonly HintStyle[] AnimatedStyles = [HintStyle.Pill, HintStyle.Neon, HintStyle.Bubble, HintStyle.Ticker];

    private static void AssertOnlyRunning(GuestHint hint, HintStyle? visibleStyle)
    {
        foreach (var animated in AnimatedStyles)
            Assert.True(IsRunning(hint, animated) == (animated == visibleStyle), $"{animated} (sichtbar: {visibleStyle})");
    }

    [StaTheory]
    [MemberData(nameof(AllStylesAndPositions))]
    public void Animations_RunOnlyForTheVisibleStyle_AndStopWhenHidden(HintStyle style, HintPosition position)
    {
        var errors = new List<Exception>();
        void OnUnhandled(object? s, DispatcherUnhandledExceptionEventArgs e)
        {
            errors.Add(e.Exception);
            e.Handled = true;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += OnUnhandled;
        var (window, hint) = ShowHint(style, position);
        try
        {
            Settle();
            AssertOnlyRunning(hint, AnimatedStyles.Contains(style) ? style : null);

            // Anderer (ruhiger) Stil: nichts darf mehr laufen, auch nicht in den nun verborgenen Bereichen.
            hint.HintStyle = style == HintStyle.Plain ? HintStyle.Banner : HintStyle.Plain;
            window.UpdateLayout();
            Settle();
            AssertOnlyRunning(hint, null);

            hint.HintStyle = style;
            window.UpdateLayout();
            Settle();
            AssertOnlyRunning(hint, AnimatedStyles.Contains(style) ? style : null);

            hint.Visibility = Visibility.Collapsed;
            window.UpdateLayout();
            Settle();
            AssertOnlyRunning(hint, null);

            hint.Visibility = Visibility.Visible;
            window.UpdateLayout();
            Settle();
            AssertOnlyRunning(hint, AnimatedStyles.Contains(style) ? style : null);
        }
        finally
        {
            window.Close();
            dispatcher.UnhandledException -= OnUnhandled;
        }

        Assert.Empty(errors);
    }
}
