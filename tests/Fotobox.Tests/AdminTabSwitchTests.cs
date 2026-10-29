using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Regression: Nach dem Tab „Gastansicht“ (Live-Vorschau mit zwei <see cref="GuestHint"/>) zeigte der Inhaltsbereich beim
/// Wechsel auf „Tasten“/„Kamera“ weiter die Gastansicht. Die Fenster sind hier wirklich sichtbar (sonst feuern die
/// <c>IsVisible</c>-Trigger der Hinweis-Animation nie).
/// </summary>
[Collection(WpfCollection.Name)]
public class AdminTabSwitchTests
{
    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [StaFact]
    public void AdminWindow_SwitchingTabsAfterGuestScreen_ShowsMatchingContent_WithoutExceptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-tabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var errors = new List<Exception>();
        void OnUnhandled(object? s, DispatcherUnhandledExceptionEventArgs e)
        {
            errors.Add(e.Exception);
            e.Handled = true;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += OnUnhandled;
        try
        {
            var store = new AdminViewModelTests.FakeStore(dir);
            var vm = new AdminViewModel(store, new AdminViewModelTests.FakePinService(store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), new AdminViewModelTests.FakeUi());
            var window = new AdminWindow(vm) { ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                var tabs = FindTabControl(window)!;
                var items = tabs.Items.OfType<TabItem>().ToList();

                foreach (var header in new[]
                         {
                             "Gastansicht", "Tasten", "Kamera", "Video", "Gastansicht", "Layout", "Gastansicht", "Kamera",
                             "Darstellung", "Gastansicht", "Speicher", "Gastansicht", "Sicherheit & Daten",
                         })
                {
                    var item = items.Single(t => t.Header as string == header);
                    var index = items.IndexOf(item);

                    // Beides ausprobieren: Auswahl per Index und per Element.
                    if (index % 2 == 0)
                        tabs.SelectedIndex = index;
                    else
                        tabs.SelectedItem = item;
                    window.UpdateLayout();
                    Pump();

                    Assert.Same(item, tabs.SelectedItem);
                    Assert.True(item.IsSelected);
                    Assert.Equal(1, items.Count(t => t.IsSelected));
                    Assert.Same(item.Content, tabs.SelectedContent);
                    var presenter = FindContentHost(tabs);
                    Assert.NotNull(presenter);
                    Assert.Same(item.Content, presenter!.Content);
                    // Der sichtbare Inhalt gehört wirklich zum Tab: Die Gast-Vorschau gibt es nur in „Gastansicht“.
                    var hints = FindDescendants<GuestHint>(presenter).ToList();
                    Assert.Equal(header == "Gastansicht" ? 2 : 0, hints.Count);
                    Assert.Empty(errors);
                }
            }
            finally
            {
                window.Close();
                vm.Dispose();
            }
        }
        finally
        {
            dispatcher.UnhandledException -= OnUnhandled;
            Directory.Delete(dir, recursive: true);
        }

        Assert.Empty(errors);
    }

    [StaTheory]
    [InlineData(HintPosition.Bottom)]
    [InlineData(HintPosition.Top)]
    public void GuestHint_ShowingAndHiding_DoesNotThrow_AndPillPulses(HintPosition position)
    {
        var errors = new List<Exception>();
        void OnUnhandled(object? s, DispatcherUnhandledExceptionEventArgs e)
        {
            errors.Add(e.Exception);
            e.Handled = true;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += OnUnhandled;
        var hint = new GuestHint { Text = "Drücke die Leertaste", HintStyle = HintStyle.Pill, Position = position };
        var window = new Window
        {
            Width = 960, Height = 540, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = hint,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            Pump();

            var pill = FindDescendants<Border>(hint).First(b => b.CornerRadius.TopLeft >= 60);
            Assert.True(pill.IsVisible);
            // Pulsieren im Idle: Die Animation hält RenderTransform und Deckkraft in Bewegung.
            Assert.True(pill.HasAnimatedProperties);

            // Ausblenden über die steuernde Property (Darstellung wechseln) und über Visibility, jeweils mehrfach.
            for (var i = 0; i < 3; i++)
            {
                hint.HintStyle = HintStyle.Plain;
                window.UpdateLayout();
                Pump();
                Assert.False(pill.IsVisible);

                hint.HintStyle = HintStyle.Pill;
                window.UpdateLayout();
                Pump();
                Assert.True(pill.IsVisible);
                Assert.True(pill.HasAnimatedProperties);

                hint.Visibility = Visibility.Collapsed;
                window.UpdateLayout();
                Pump();
                Assert.False(pill.IsVisible);

                hint.Visibility = Visibility.Visible;
                window.UpdateLayout();
                Pump();
                Assert.True(pill.IsVisible);
            }

            // Auch die zuletzt angezeigte Pille muss sich beim Schließen sauber abbauen.
            window.Content = null;
            Pump();
        }
        finally
        {
            window.Close();
            dispatcher.UnhandledException -= OnUnhandled;
        }

        Assert.Empty(errors);
    }

    private static ContentPresenter? FindContentHost(TabControl tabs) =>
        FindDescendants<ContentPresenter>(tabs).FirstOrDefault(p => p.Name == "PART_SelectedContentHost");

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindDescendants<T>(child))
                yield return nested;
        }
    }

    private static TabControl? FindTabControl(DependencyObject parent)
    {
        if (parent is TabControl tabs)
            return tabs;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (FindTabControl(child) is { } found)
                return found;
        }
        return null;
    }
}
