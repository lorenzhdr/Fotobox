using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Fotobox.Core;

namespace Fotobox.Views.Admin;

/// <summary>
/// Proportionales Mausrad-Scrollen für <see cref="ScrollViewer"/> im Admin. WPF wertet bei <c>OnMouseWheel</c> nur das Vorzeichen
/// des Deltas aus, sodass jedes Ereignis eines Präzisions-Trackpads (kleines Delta, viele Ereignisse) wie eine volle Rastung
/// wirkt. Dieser Helfer skaliert nach dem Betrag des Deltas (<see cref="WheelScrollMath"/>).
/// Zuständig ist immer der innerste ScrollViewer unter dem Mauszeiger, der in die Richtung scrollen kann; ist er am Anschlag,
/// scrollt der nächste äußere weiter.
/// </summary>
public static class ProportionalWheelScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ProportionalWheelScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    // Rest der Deltas je ScrollViewer für zeilenweises Scrollen
    private static readonly DependencyProperty AccumulatorProperty = DependencyProperty.RegisterAttached(
        "Accumulator", typeof(WheelDeltaAccumulator), typeof(ProportionalWheelScroll));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
            return;
        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true)
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not ScrollViewer viewer)
            return;
        // Umschalt = horizontales Scrollen, bleibt dem Standardverhalten überlassen
        if ((ModifierState.Current & ModifierKeys.Shift) != 0)
            return;
        // Nur der innerste ScrollViewer, der in diese Richtung scrollen kann, handelt; äußere lassen das ungehandelt durch.
        if (!ReferenceEquals(FindResponsible(e.OriginalSource as DependencyObject, e.Delta), viewer))
            return;

        if (viewer.CanContentScroll)
            ScrollByItems(viewer, e.Delta);
        else
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset
                - WheelScrollMath.PixelsFor(e.Delta, SystemParameters.WheelScrollLines, viewportHeight: viewer.ViewportHeight));
        e.Handled = true;
    }

    private static void ScrollByItems(ScrollViewer viewer, int delta)
    {
        var accumulator = (WheelDeltaAccumulator?)viewer.GetValue(AccumulatorProperty);
        if (accumulator == null)
        {
            accumulator = new WheelDeltaAccumulator();
            viewer.SetValue(AccumulatorProperty, accumulator);
        }

        int notches = accumulator.Add(delta);
        int lines = SystemParameters.WheelScrollLines;
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            if (lines <= 0)
            {
                if (notches > 0) viewer.PageUp(); else viewer.PageDown();
                continue;
            }
            for (int j = 0; j < lines; j++)
            {
                if (notches > 0) viewer.LineUp(); else viewer.LineDown();
            }
        }
    }

    /// <summary>Innerster aktivierter ScrollViewer ab <paramref name="source"/> aufwärts, der in Richtung <paramref name="delta"/> scrollen kann.</summary>
    internal static ScrollViewer? FindResponsible(DependencyObject? source, int delta)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is ScrollViewer candidate && GetIsEnabled(candidate) && CanScroll(candidate, delta))
                return candidate;
        }
        return null;
    }

    private static bool CanScroll(ScrollViewer viewer, int delta)
    {
        if (viewer.ScrollableHeight <= 0)
            return false;
        return delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight;
    }

    // Visueller Baum; ein Popup (ComboBox-Liste) hat einen eigenen Baum, dessen ScrollViewer ist dort allein zuständig.
    private static DependencyObject? GetParent(DependencyObject element) =>
        element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
}
