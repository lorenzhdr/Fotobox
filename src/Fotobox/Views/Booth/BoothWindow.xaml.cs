using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Fotobox.Core;
using Fotobox.ViewModels.Booth;

namespace Fotobox.Views.Booth;

/// <summary>Vollbild-Gastansicht: Leertaste löst ein Foto aus, Strg+Alt+A öffnet die Admin-Ansicht.</summary>
public sealed partial class BoothWindow : Window
{
    private static readonly TimeSpan FocusReclaimDelay = TimeSpan.FromMilliseconds(400);

    private readonly BoothViewModel _viewModel;
    private readonly IAdminLauncher _adminLauncher;
    // Schließen ist nur über „Fotobox beenden“ in der Admin-Ansicht (oder Abmeldung/App-Shutdown) erlaubt.
    private bool _allowClose;
    private bool _focusReclaimPending;

    public BoothWindow(BoothViewModel viewModel, ICameraService camera, IAdminLauncher adminLauncher, ISettingsStore settings)
    {
        _viewModel = viewModel;
        _adminLauncher = adminLauncher;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.CountdownTicked += (_, _) => PlayCountdownTick();
        viewModel.FlashRequested += (_, _) => PlayFlash();

        Loaded += OnLoaded;
        Closing += OnClosing;
        Activated += (_, _) => Focus();
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;

        // Windows-Abmeldung/Herunterfahren darf nie blockiert werden.
        if (Application.Current is { } app)
            app.SessionEnding += (_, _) => _allowClose = true;
    }

    private bool IsShuttingDown => _allowClose || Dispatcher.HasShutdownStarted;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Initialisierung der Gastansicht fehlgeschlagen.", ex);
        }
    }

    /// <summary>Alt+F4, Systemmenü oder Taskleiste beenden die Fotobox nicht; nur ein erlaubtes Schließen fährt herunter.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!IsShuttingDown)
        {
            e.Cancel = true;
            return;
        }

        _viewModel.Shutdown();
    }

    /// <summary>
    /// Holt den Fokus zurück, wenn ihn ein anderes Programm (Druckdialog, Statusmonitor, Benachrichtigung) übernommen hat –
    /// sonst reagiert die Leertaste nicht mehr. Verzögert und nur, wenn kein eigener Dialog offen ist.
    /// </summary>
    private async void OnDeactivated(object? sender, EventArgs e)
    {
        if (_focusReclaimPending || !CanReclaimFocus)
            return;

        _focusReclaimPending = true;
        try
        {
            await Task.Delay(FocusReclaimDelay);
            if (!CanReclaimFocus)
                return;

            Activate();
            Focus();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Fokus konnte nicht zurückgeholt werden.", ex);
        }
        finally
        {
            _focusReclaimPending = false;
        }
    }

    private bool CanReclaimFocus =>
        IsLoaded && !IsActive && !IsShuttingDown && !_viewModel.IsAdminOpen && !HasOtherVisibleWindow();

    // Eigene Dialoge (PIN, Admin, Erststart) sind Fenster dieser Anwendung und dürfen nicht verdrängt werden.
    private bool HasOtherVisibleWindow()
    {
        if (Application.Current is not { } app)
            return false;

        foreach (Window window in app.Windows)
        {
            if (!ReferenceEquals(window, this) && window.IsVisible)
                return true;
        }
        return false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        // Alt+F4 und Alt+Leertaste (Systemmenü) wirkungslos machen.
        if (e.Key == Key.System && (key == Key.F4 || key == Key.Space))
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Space)
        {
            e.Handled = true;
            if (!e.IsRepeat && !alt)
                _viewModel.TryTrigger();
            return;
        }

        if (key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            e.Handled = true;
            if (!e.IsRepeat && _viewModel.CanOpenAdmin)
                Dispatcher.BeginInvoke(OpenAdmin);
        }
    }

    private async void OpenAdmin()
    {
        if (!_viewModel.CanOpenAdmin) return;
        _viewModel.IsAdminOpen = true;

        var result = new AdminResult(false, false, false);
        Cursor = Cursors.Arrow;
        try
        {
            result = _adminLauncher.OpenAdmin(this);
        }
        catch (Exception ex)
        {
            AppLog.Error("Admin-Ansicht konnte nicht geöffnet werden.", ex);
        }
        finally
        {
            Cursor = Cursors.None;
            _viewModel.IsAdminOpen = false;
            Activate();
            Focus();
        }

        if (result.ExitRequested)
        {
            _allowClose = true;
            Application.Current.Shutdown();
            return;
        }

        if (result.SettingsChanged)
        {
            try { await _viewModel.RestartCameraAsync(); }
            catch (Exception ex) { AppLog.Error("Kamera-Neustart nach Einstellungsänderung fehlgeschlagen.", ex); }
        }
    }

    /// <summary>Jede neue Zahl: aus größerer Skalierung hereinzoomen und gegen Ende ausblenden.</summary>
    private void PlayCountdownTick()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();

        var scaleX = new DoubleAnimation(1.7, 1.0, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease };
        Storyboard.SetTarget(scaleX, CountdownBlock);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        var scaleY = scaleX.Clone();
        Storyboard.SetTarget(scaleY, CountdownBlock);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(720))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(980))));
        Storyboard.SetTarget(fade, CountdownBlock);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));

        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    /// <summary>Weißer Blitz: Opacity 1 → 0 in ca. 400 ms.</summary>
    private void PlayFlash()
    {
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        FlashOverlay.BeginAnimation(OpacityProperty, animation);
    }
}
