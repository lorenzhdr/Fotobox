using System.IO;
using System.Windows.Threading;
using Fotobox.ViewModels.Admin;
using Fotobox.Views.Admin;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Schließen des Admin-Fensters: X/Alt+F4 fragt bei ungespeicherten Änderungen nach, die Buttons (Schließen,
/// Speichern &amp; schließen, Beenden) dürfen danach nicht ein zweites Mal fragen. Das Fenster läuft modal (wie im Betrieb),
/// weil <c>DialogResult</c> nur dort das Schließen auslöst.
/// </summary>
[Collection(WpfCollection.Name)]
public class AdminWindowClosingTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-admin-closing-" + Guid.NewGuid().ToString("N"));

        public Harness()
        {
            Directory.CreateDirectory(_dir);
            Store = new AdminViewModelTests.FakeStore(_dir);
            Ui = new AdminViewModelTests.FakeUi();
            Vm = new AdminViewModel(Store, new AdminViewModelTests.FakePinService(Store),
                new AdminViewModelTests.FakeCamera(), new AdminViewModelTests.FakePrint(), Ui);
            Window = new AdminWindow(Vm) { ShowInTaskbar = false };
        }

        public AdminViewModelTests.FakeStore Store { get; }
        public AdminViewModelTests.FakeUi Ui { get; }
        public AdminViewModel Vm { get; }
        public AdminWindow Window { get; }
        public bool TimedOut { get; private set; }

        /// <summary>Zeigt das Fenster modal und führt <paramref name="script"/> auf dem UI-Thread aus; endet das Fenster dabei nicht, wird es nach 10 s zwangsweise geschlossen.</summary>
        public bool? Run(Action script)
        {
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            guard.Tick += (_, _) =>
            {
                TimedOut = true;
                guard.Stop();
                Ui.ConfirmResult = true;
                Window.Close();
            };
            Window.Loaded += (_, _) => Window.Dispatcher.BeginInvoke(script, DispatcherPriority.Background);
            guard.Start();
            try
            {
                return Window.ShowDialog();
            }
            finally
            {
                guard.Stop();
            }
        }

        public void Dispose()
        {
            Vm.Dispose();
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }
    }

    [StaFact]
    public void CloseWindow_WithChanges_AskedNo_StaysOpen_ThenYesCloses()
    {
        using var h = new Harness();
        var visibleAfterNo = false;

        var result = h.Run(() =>
        {
            h.Vm.CountdownSeconds = 9;
            h.Ui.ConfirmResult = false;
            h.Window.Close();
            visibleAfterNo = h.Window.IsVisible;

            h.Ui.ConfirmResult = true;
            h.Window.Close();
        });

        Assert.False(h.TimedOut);
        Assert.True(visibleAfterNo);
        Assert.Equal(2, h.Ui.ConfirmCalls);
        Assert.Equal("Ungespeicherte Änderungen", h.Ui.LastConfirmTitle);
        Assert.NotEqual(true, result);
        Assert.Equal(0, h.Store.SaveCount);
    }

    [StaFact]
    public void CloseWindow_WithChanges_AskedYes_ClosesAfterOneQuestion()
    {
        using var h = new Harness();

        h.Run(() =>
        {
            h.Vm.CountdownSeconds = 9;
            h.Ui.ConfirmResult = true;
            h.Window.Close();
        });

        Assert.False(h.TimedOut);
        Assert.Equal(1, h.Ui.ConfirmCalls);
        Assert.Equal(0, h.Store.SaveCount);
    }

    [StaFact]
    public void CloseWindow_WithoutChanges_ClosesWithoutQuestion()
    {
        using var h = new Harness();

        h.Run(() => h.Window.Close());

        Assert.False(h.TimedOut);
        Assert.Equal(0, h.Ui.ConfirmCalls);
    }

    [StaFact]
    public void CloseWindow_AfterApply_WithoutFurtherChanges_ClosesWithoutQuestion()
    {
        using var h = new Harness();

        h.Run(() =>
        {
            h.Vm.CountdownSeconds = 9;
            h.Vm.ApplyCommand.Execute(null);
            h.Window.Close();
        });

        Assert.False(h.TimedOut);
        Assert.Equal(0, h.Ui.ConfirmCalls);
        Assert.Equal(1, h.Store.SaveCount);
    }

    [StaFact]
    public void SaveAndClose_ClosesWindowWithoutAnyQuestion()
    {
        using var h = new Harness();

        var result = h.Run(() =>
        {
            h.Vm.CountdownSeconds = 9;
            h.Vm.SaveCommand.Execute(null);
        });

        Assert.False(h.TimedOut);
        Assert.True(result);
        Assert.Equal(0, h.Ui.ConfirmCalls);
        Assert.Equal(1, h.Store.SaveCount);
        Assert.Equal(9, h.Store.Current.Flow.CountdownSeconds);
    }

    [StaFact]
    public void CloseButton_WithChanges_AsksOnlyOnce()
    {
        using var h = new Harness();

        var result = h.Run(() =>
        {
            h.Vm.CountdownSeconds = 9;
            h.Ui.ConfirmResult = true;
            h.Vm.CloseCommand.Execute(null);
        });

        Assert.False(h.TimedOut);
        Assert.NotEqual(true, result);
        Assert.Equal(1, h.Ui.ConfirmCalls);
        Assert.Equal(0, h.Store.SaveCount);
    }

    [StaFact]
    public void ExitButton_WithChanges_AsksOnlyTheExitQuestion()
    {
        using var h = new Harness();

        h.Run(() =>
        {
            h.Vm.CountdownSeconds = 9;
            h.Ui.ConfirmResult = true;
            h.Vm.ExitCommand.Execute(null);
        });

        Assert.False(h.TimedOut);
        Assert.True(h.Vm.ExitRequested);
        Assert.Equal(1, h.Ui.ConfirmCalls);
        Assert.Equal("Fotobox+ beenden", h.Ui.LastConfirmTitle);
    }
}
