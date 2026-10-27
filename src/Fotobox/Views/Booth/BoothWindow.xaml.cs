using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Fotobox.Core;
using Fotobox.Services.Display;
using Fotobox.ViewModels.Booth;

namespace Fotobox.Views.Booth;

/// <summary>Vollbild-Gastansicht: Die eingestellten Tasten lösen ein Foto aus, brechen Countdown oder Druck ab bzw. öffnen die Admin-Ansicht.</summary>
public sealed partial class BoothWindow : Window
{
    private static readonly TimeSpan FocusReclaimDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DisplayChangeDebounce = TimeSpan.FromSeconds(1);
    /// <summary>Länger darf die Monitorabfrage nie dauern (sie ist eigentlich sofort fertig); danach bleibt das Fenster, wo es ist.</summary>
    private static readonly TimeSpan MonitorQueryTimeout = TimeSpan.FromSeconds(3);

    private readonly BoothViewModel _viewModel;
    private readonly IAdminLauncher _adminLauncher;
    private readonly ISettingsStore _settings;
    private readonly IDisplayService _displays;
    // Fasst mehrere WM_DISPLAYCHANGE zusammen (beim Ein-/Ausstecken kommen oft mehrere in kurzer Folge).
    private DispatcherTimer? _displayChangeTimer;
    // Jede Platzierungsanfrage erhöht den Zähler; eine überholte Antwort wird verworfen.
    private int _placementGeneration;
    // Zuletzt ermittelter Zielmonitor (OnLoaded prüft damit nur noch, ohne erneut aufzuzählen).
    private DisplayMonitor? _target;
    // Schließen ist nur über „Fotobox beenden“ in der Admin-Ansicht (oder Abmeldung/App-Shutdown) erlaubt.
    private bool _allowClose;
    private bool _focusReclaimPending;
    // Noch nicht verbrauchte Mausrad-Deltas (kleiner als eine Stufe).
    private int _wheelRemainder;

    public BoothWindow(BoothViewModel viewModel, IAdminLauncher adminLauncher, ISettingsStore settings,
        IDisplayService displays)
    {
        _viewModel = viewModel;
        _adminLauncher = adminLauncher;
        _settings = settings;
        _displays = displays;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.CountdownTicked += (_, _) => PlayCountdownTick();
        viewModel.FlashRequested += (_, _) => PlayFlash();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Activated += (_, _) => Focus();
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewMouseWheel += OnPreviewMouseWheel;

        // Windows-Abmeldung/Herunterfahren darf nie blockiert werden.
        if (Application.Current is { } app)
            app.SessionEnding += (_, _) => _allowClose = true;
    }

    private bool IsShuttingDown => _allowClose || Dispatcher.HasShutdownStarted;

    /// <summary>
    /// Das Fenster hat jetzt ein Handle, ist aber noch nicht sichtbar: direkt auf den gewählten Monitor setzen
    /// (kein Aufblitzen auf dem Hauptbildschirm) und auf Monitoränderungen (Ein-/Ausstecken, Auflösung) lauschen.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Monitoränderungen können nicht überwacht werden.", ex);
        }

        // Das Fenster ist noch unsichtbar: begrenzt synchron warten, damit es nicht erst auf dem falschen Monitor erscheint.
        // Bei Zeitüberschreitung oder Fehler bleibt es maximiert auf dem Standardmonitor (OnLoaded versucht es noch einmal).
        _placementGeneration++;
        PlaceOnTargetMonitor(QueryMonitorsBlocking());
    }

    private IReadOnlyList<DisplayMonitor> QueryMonitorsBlocking()
    {
        try
        {
            var task = Task.Run(() => _displays.GetMonitors());
            if (task.Wait(MonitorQueryTimeout))
                return task.Result;

            AppLog.Warn("Die Monitorabfrage beim Start dauert zu lange; die Gastansicht bleibt auf dem Standardbildschirm.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Die Monitorabfrage beim Start ist fehlgeschlagen; die Gastansicht bleibt auf dem Standardbildschirm.", ex);
        }
        return [];
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == DisplayNative.WmDisplayChange)
            ScheduleReplace();
        return IntPtr.Zero;
    }

    private void ScheduleReplace()
    {
        if (_displayChangeTimer is null)
        {
            _displayChangeTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = DisplayChangeDebounce };
            _displayChangeTimer.Tick += (timer, _) =>
            {
                ((DispatcherTimer)timer!).Stop();
                // Bei offenem Admin nicht verschieben; OpenAdmin platziert nach dem Schließen ohnehin neu.
                if (IsShuttingDown || _viewModel.IsAdminOpen)
                    return;

                // Mitten im Durchgang (Countdown bis Druck) nicht verschieben: später erneut prüfen.
                if (!_viewModel.CanMoveWindow)
                {
                    ((DispatcherTimer)timer).Start();
                    return;
                }

                RefreshPlacement();
            };
        }

        _displayChangeTimer.Stop();
        _displayChangeTimer.Start();
    }

    /// <summary>Fire-and-forget für Ereignishandler: <see cref="RefreshPlacementAsync"/> wirft nie.</summary>
    private async void RefreshPlacement()
    {
        try
        {
            await RefreshPlacementAsync(requireMovable: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Gastansicht konnte nicht neu platziert werden.", ex);
        }
    }

    /// <summary>
    /// Fragt die Monitore im Hintergrund ab (mit Zeitlimit) und platziert die Gastansicht danach im UI-Thread.
    /// Eine überholte Anfrage (inzwischen eine neuere gestartet), ein geschlossenes Fenster, eine offene Admin-Ansicht
    /// oder (mit <paramref name="requireMovable"/>) ein laufender Durchgang verwerfen das Ergebnis. Bei Fehler oder
    /// Zeitüberschreitung bleibt das Fenster, wo es ist. Wirft nie.
    /// </summary>
    public async Task RefreshPlacementAsync(bool requireMovable = false)
    {
        try
        {
            var generation = ++_placementGeneration;
            IReadOnlyList<DisplayMonitor> monitors;
            try
            {
                monitors = await Task.Run(() => _displays.GetMonitors()).WaitAsync(MonitorQueryTimeout);
            }
            catch (TimeoutException)
            {
                AppLog.Warn("Die Monitorabfrage dauert zu lange; die Gastansicht wird nicht verschoben.");
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Die Monitorabfrage ist fehlgeschlagen; die Gastansicht wird nicht verschoben.", ex);
                return;
            }

            if (generation != _placementGeneration || IsShuttingDown || _viewModel.IsAdminOpen)
                return;

            if (requireMovable && !_viewModel.CanMoveWindow)
            {
                // Währenddessen hat ein Durchgang begonnen: später erneut versuchen.
                ScheduleReplace();
                return;
            }

            PlaceOnTargetMonitor(monitors);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Gastansicht konnte nicht neu platziert werden.", ex);
        }
    }

    /// <summary>
    /// Zeigt die Gastansicht als Vollbild auf dem gewählten Monitor der übergebenen Liste (Hauptbildschirm, falls dieser fehlt;
    /// bei leerer Liste bleibt das Fenster, wo es ist). Liegt sie bereits maximiert dort, geschieht nichts.
    /// Läuft im UI-Thread, wirft nie; am Ende ist das Fenster immer maximiert.
    /// </summary>
    public void PlaceOnTargetMonitor(IReadOnlyList<DisplayMonitor> monitors)
    {
        DisplayMonitor? target = null;
        try
        {
            target = GuestMonitors.Resolve(monitors, _settings.Current.Appearance.GuestMonitorId);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Zielbildschirm der Gastansicht konnte nicht bestimmt werden.", ex);
        }

        _target = target;
        ApplyPlacement(target);
    }

    /// <summary>Setzt das Fenster auf <paramref name="target"/> (null = nichts verschieben) und maximiert es. Wirft nie.</summary>
    private void ApplyPlacement(DisplayMonitor? target)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (target is null || hwnd == IntPtr.Zero)
                return;

            if (WindowState == WindowState.Maximized
                && WindowMonitor.TryGetRects(hwnd, out var current, out _)
                && current == target.Bounds)
                return;

            // Erst aus dem Maximiert-Zustand lösen, ins Monitor-Rechteck setzen, dann wieder maximieren:
            // Windows maximiert auf dem Monitor, auf dem das (normale) Fenster liegt.
            WindowState = WindowState.Normal;
            var b = target.Bounds;
            if (!DisplayNative.SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height,
                    DisplayNative.SwpNoZOrder | DisplayNative.SwpNoActivate))
                AppLog.Warn($"Gastansicht konnte nicht auf den gewählten Bildschirm gesetzt werden (Win32-Fehler {Marshal.GetLastPInvokeError()}).");
            else
                AppLog.Info($"Gastansicht auf Bildschirm {target.Number} gesetzt.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Gastansicht konnte nicht auf den gewählten Bildschirm gesetzt werden.", ex);
        }
        finally
        {
            try
            {
                if (WindowState != WindowState.Maximized)
                    WindowState = WindowState.Maximized;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Gastansicht konnte nicht maximiert werden.", ex);
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Falls das Platzieren vor dem Anzeigen nicht gegriffen hat (macht nichts, wenn alles stimmt) – ohne erneute Abfrage,
        // mit dem beim Start ermittelten Ziel. Fehlt es (Abfrage fehlgeschlagen), jetzt im Hintergrund noch einmal versuchen.
        if (_target is null)
            RefreshPlacement();
        else
            ApplyPlacement(_target);
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

        _displayChangeTimer?.Stop();
        _viewModel.Shutdown();
    }

    /// <summary>
    /// Holt den Fokus zurück, wenn ihn ein anderes Programm (Druckdialog, Statusmonitor, Benachrichtigung) übernommen hat –
    /// sonst reagieren die Tasten nicht mehr. Verzögert und nur, wenn kein eigener Dialog offen ist.
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

        // Alt+F4 und Alt+Leertaste (Systemmenü) wirkungslos machen.
        if (e.Key == Key.System && (key == Key.F4 || key == Key.Space))
        {
            e.Handled = true;
            return;
        }

        // Belegung (Auslösen, Abbrechen, Admin) kommt aus den Einstellungen.
        var chord = KeyChord.FromKeyEvent(e.Key, e.SystemKey, Keyboard.Modifiers);
        var action = _viewModel.HandleKey(chord);
        if (action == BoothKeyAction.None)
            return;

        e.Handled = true;
        if (e.IsRepeat)
            return;

        switch (action)
        {
            case BoothKeyAction.OpenAdmin:
                if (_viewModel.CanOpenAdmin)
                    Dispatcher.BeginInvoke(OpenAdmin);
                break;
            case BoothKeyAction.Cancel:
                _viewModel.TryCancel();
                break;
            case BoothKeyAction.Trigger:
                _viewModel.TryTrigger();
                break;
        }
    }

    /// <summary>Linke/rechte Maustaste löst (je nach Einstellung) ein Foto aus; Doppelklicks zählen nicht doppelt.</summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right))
            return;

        // Der Klick gehört der Gastansicht, nie einem darunterliegenden Element.
        e.Handled = true;
        if (e.ClickCount > 1)
            return;

        if (_viewModel.HandleMouseButton(e.ChangedButton) == BoothKeyAction.Trigger)
            _viewModel.TryTrigger();
    }

    /// <summary>
    /// Mausrad wechselt das Layout: je volle Raster-Stufe (120) ein Schritt, nach oben = vorheriges Layout
    /// (umkehrbar, siehe <see cref="BoothViewModel.ScrollLayout"/>).
    /// Feine Wheel-Deltas (z. B. Touchpads) werden gesammelt; ein Richtungswechsel verwirft den Rest.
    /// </summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (e.Delta == 0)
            return;

        if (Math.Sign(_wheelRemainder) == -Math.Sign(e.Delta))
            _wheelRemainder = 0;
        _wheelRemainder += e.Delta;

        const int step = 120;   // Mouse.MouseWheelDeltaForOneLine
        while (Math.Abs(_wheelRemainder) >= step)
        {
            var up = _wheelRemainder > 0;
            _viewModel.ScrollLayout(up);
            _wheelRemainder += up ? -step : step;
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
        }

        if (result.ExitRequested)
        {
            _allowClose = true;
            Application.Current.Shutdown();
            return;
        }

        // Immer neu platzieren: deckt einen geänderten Monitor und ein Ein-/Ausstecken während der Admin-Ansicht ab.
        await RefreshPlacementAsync();
        Activate();
        Focus();

        // Kamera nur neu starten, wenn sich ihre Einstellungen geändert haben oder sie nicht läuft –
        // ein unnötiger Neustart war früher die Ursache für Abstürze nach dem Speichern.
        if (result.SettingsChanged && (result.CameraChanged || _viewModel.Camera.Status != CameraStatus.Running))
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
