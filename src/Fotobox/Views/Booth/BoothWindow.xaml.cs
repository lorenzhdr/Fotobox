using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Fotobox.Core;
using Fotobox.ViewModels.Booth;

namespace Fotobox.Views.Booth;

/// <summary>Vollbild-Gastansicht: Leertaste löst ein Foto aus, Strg+Alt+A öffnet die Admin-Ansicht.</summary>
public sealed partial class BoothWindow : Window
{
    private readonly BoothViewModel _viewModel;
    private readonly IAdminLauncher _adminLauncher;
    private bool _adminOpen;

    public BoothWindow(BoothViewModel viewModel, ICameraService camera, IAdminLauncher adminLauncher, ISettingsStore settings)
    {
        _viewModel = viewModel;
        _adminLauncher = adminLauncher;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.CountdownTicked += (_, _) => PlayCountdownTick();
        viewModel.FlashRequested += (_, _) => PlayFlash();

        Loaded += OnLoaded;
        Closing += (_, _) => _viewModel.Shutdown();
        PreviewKeyDown += OnPreviewKeyDown;
    }

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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Space)
        {
            e.Handled = true;
            if (!e.IsRepeat && !_adminOpen)
                _viewModel.Controller.TryTrigger();
            return;
        }

        if (key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            e.Handled = true;
            if (!e.IsRepeat && !_adminOpen && _viewModel.CanOpenAdmin)
                Dispatcher.BeginInvoke(OpenAdmin);
        }
    }

    private async void OpenAdmin()
    {
        if (_adminOpen || !_viewModel.CanOpenAdmin) return;
        _adminOpen = true;

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
            _adminOpen = false;
            Activate();
            Focus();
        }

        if (result.ExitRequested)
        {
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
