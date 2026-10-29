using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

[Collection(WpfCollection.Name)]
public class ProportionalWheelScrollTests
{
    private static ScrollViewer CreateViewer(double contentHeight, double viewportHeight, out FrameworkElement content)
    {
        content = new Border { Height = contentHeight, Background = Brushes.Gray };
        var viewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
        ProportionalWheelScroll.SetIsEnabled(viewer, true);
        Layout(viewer, viewportHeight);
        return viewer;
    }

    private static void Layout(FrameworkElement root, double height)
    {
        root.Measure(new Size(300, height));
        root.Arrange(new Rect(0, 0, 300, height));
        root.UpdateLayout();
    }

    private static MouseWheelEventArgs Wheel(UIElement target, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = target,
        };
        target.RaiseEvent(args);
        // Der Versatz des ScrollViewers wird erst im Layoutdurchlauf übernommen
        target.UpdateLayout();
        return args;
    }

    /// <summary>Erwartete Strecke für ein Delta (unabhängig von <see cref="Fotobox.Core.WheelScrollMath"/> gerechnet).</summary>
    private static double Expected(int delta, double viewport)
    {
        int lines = SystemParameters.WheelScrollLines;
        return lines > 0 ? delta / 120.0 * lines * 16 : delta / 120.0 * viewport;
    }

    [StaFact]
    public void SmallDelta_ScrollsProportionally()
    {
        var viewer = CreateViewer(1000, 200, out var content);

        var args = Wheel(content, -30);
        viewer.UpdateLayout();

        Assert.True(args.Handled);
        Assert.Equal(-Expected(-30, viewer.ViewportHeight), viewer.VerticalOffset, 3);
    }

    [StaFact]
    public void FullNotch_ScrollsFourTimesAsFarAsQuarterNotch()
    {
        var viewer = CreateViewer(2000, 200, out var content);

        Wheel(content, -30);
        viewer.UpdateLayout();
        double small = viewer.VerticalOffset;
        viewer.ScrollToVerticalOffset(0);
        viewer.UpdateLayout();
        Wheel(content, -120);

        Assert.True(small > 0);
        Assert.Equal(small * 4, viewer.VerticalOffset, 3);
    }

    [StaFact]
    public void WheelUp_ScrollsBack()
    {
        var viewer = CreateViewer(1000, 200, out var content);
        viewer.ScrollToVerticalOffset(300);
        viewer.UpdateLayout();

        var args = Wheel(content, 30);

        Assert.True(args.Handled);
        Assert.Equal(300 - Expected(30, viewer.ViewportHeight), viewer.VerticalOffset, 3);
    }

    [StaFact]
    public void AtTop_WheelUp_IsNotHandled()
    {
        var viewer = CreateViewer(1000, 200, out var content);

        var args = Wheel(content, 120);

        Assert.False(args.Handled);
        Assert.Equal(0, viewer.VerticalOffset);
    }

    [StaFact]
    public void AtBottom_WheelDown_IsNotHandled()
    {
        var viewer = CreateViewer(1000, 200, out var content);
        viewer.ScrollToBottom();
        viewer.UpdateLayout();

        var args = Wheel(content, -120);

        Assert.False(args.Handled);
    }

    [StaFact]
    public void NoScrollableContent_IsNotHandled()
    {
        CreateViewer(100, 200, out var content);

        var args = Wheel(content, -120);

        Assert.False(args.Handled);
    }

    [StaFact]
    public void Disabled_DoesNotHandle()
    {
        var viewer = CreateViewer(1000, 200, out var content);
        ProportionalWheelScroll.SetIsEnabled(viewer, false);

        // Ohne Fenster/Eingabequelle behandelt auch das WPF-Standardverhalten das Ereignis nicht über den Preview-Pfad:
        // der Versatz bleibt unverändert, solange der Helfer abgemeldet ist.
        Wheel(content, -120);

        Assert.Equal(0, viewer.VerticalOffset);
    }

    private static (ScrollViewer outer, ScrollViewer inner, FrameworkElement innerContent) CreateNested()
    {
        var innerContent = new Border { Height = 600, Background = Brushes.Gray };
        var inner = new ScrollViewer
        {
            Height = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = innerContent,
        };
        var panel = new StackPanel();
        panel.Children.Add(inner);
        panel.Children.Add(new Border { Height = 1500, Background = Brushes.DarkGray });
        var outer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        ProportionalWheelScroll.SetIsEnabled(outer, true);
        ProportionalWheelScroll.SetIsEnabled(inner, true);
        Layout(outer, 300);
        return (outer, inner, innerContent);
    }

    [StaFact]
    public void Nested_InnerScrollsFirst_OuterStays()
    {
        var (outer, inner, innerContent) = CreateNested();

        var args = Wheel(innerContent, -120);

        Assert.True(args.Handled);
        Assert.True(inner.VerticalOffset > 0);
        Assert.Equal(0, outer.VerticalOffset);
    }

    [StaFact]
    public void Nested_InnerAtLimit_OuterContinues()
    {
        var (outer, inner, innerContent) = CreateNested();
        inner.ScrollToBottom();
        inner.UpdateLayout();
        double innerOffset = inner.VerticalOffset;

        var args = Wheel(innerContent, -120);

        Assert.True(args.Handled);
        Assert.Equal(innerOffset, inner.VerticalOffset);
        Assert.True(outer.VerticalOffset > 0);
    }

    [StaFact]
    public void Nested_WheelUp_InnerAtBottomScrollsBack()
    {
        var (outer, inner, innerContent) = CreateNested();
        inner.ScrollToBottom();
        inner.UpdateLayout();
        double innerOffset = inner.VerticalOffset;

        Wheel(innerContent, 120);

        Assert.True(inner.VerticalOffset < innerOffset);
        Assert.Equal(0, outer.VerticalOffset);
    }

    [StaFact]
    public void ItemScrolling_AccumulatesToWholeNotches()
    {
        int lines = SystemParameters.WheelScrollLines;
        if (lines <= 0)
            return; // „Seite pro Rastung“ ist hier nicht sinnvoll prüfbar

        // VirtualizingStackPanel scrollt bei CanContentScroll in logischen Einheiten (Einträgen)
        var panel = new VirtualizingStackPanel();
        for (int i = 0; i < 200; i++)
            panel.Children.Add(new TextBlock { Text = "Eintrag " + i, Height = 20 });
        var viewer = new ScrollViewer { CanContentScroll = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        ProportionalWheelScroll.SetIsEnabled(viewer, true);
        Layout(viewer, 200);
        Assert.True(viewer.ScrollableHeight > 0);
        var item = panel.Children[0];

        for (int i = 0; i < 3; i++)
        {
            var partial = Wheel(item, -30);
            Assert.True(partial.Handled);
        }
        Assert.Equal(0, viewer.VerticalOffset);

        Wheel(item, -30);

        Assert.Equal(lines, viewer.VerticalOffset);
    }

    [StaFact]
    public void AdminTheme_EnablesProportionalScrollingForScrollViewers()
    {
        var theme = new ResourceDictionary { Source = new Uri("/Fotobox;component/Views/Admin/AdminTheme.xaml", UriKind.Relative) };
        var style = Assert.IsType<Style>(theme[typeof(ScrollViewer)]);
        var viewer = new ScrollViewer { Style = style };

        Assert.True(ProportionalWheelScroll.GetIsEnabled(viewer));
    }
}
