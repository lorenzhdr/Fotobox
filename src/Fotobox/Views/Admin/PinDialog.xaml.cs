using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Fotobox.ViewModels.Admin;

namespace Fotobox.Views.Admin;

/// <summary>PIN-Abfrage vor der Admin-Ansicht. Nach zu vielen Fehlversuchen ist die Eingabe zeitweise gesperrt.</summary>
public partial class PinDialog : Window
{
    private readonly Func<string, bool> _verify;
    private readonly PinAttemptLimiter _limiter;
    private readonly DispatcherTimer _lockTimer;

    public PinDialog(Func<string, bool> verify, PinAttemptLimiter limiter, Window? owner)
    {
        _verify = verify;
        _limiter = limiter;
        InitializeComponent();

        if (owner is { IsLoaded: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        PinBoxHelper.Configure(PinBox);
        Pad.DigitPressed += d => PinBoxHelper.Append(PinBox, d);
        Pad.BackspacePressed += () => PinBoxHelper.Backspace(PinBox);
        Pad.SubmitPressed += Submit;

        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _lockTimer.Tick += (_, _) => UpdateLockState();

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            UpdateLockState();
            PinBox.Focus();
            Keyboard.Focus(PinBox);
        };
        Closed += (_, _) => _lockTimer.Stop();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter or Key.Return:
                e.Handled = true;
                Submit();
                break;
            case Key.Escape:
                e.Handled = true;
                DialogResult = false;
                break;
        }
    }

    private void Submit()
    {
        if (_limiter.IsLockedOut)
        {
            UpdateLockState();
            return;
        }

        var pin = PinBox.Password;
        if (pin.Length == 0)
            return;

        if (_verify(pin))
        {
            _limiter.RegisterSuccess();
            DialogResult = true;
            return;
        }

        _limiter.RegisterFailure();
        PinBox.Clear();
        if (_limiter.IsLockedOut)
        {
            UpdateLockState();
            return;
        }

        var remaining = _limiter.RemainingAttempts;
        MessageText.Text = remaining == 1
            ? "Falsche PIN. Noch 1 Versuch."
            : $"Falsche PIN. Noch {remaining} Versuche.";
        PinBox.Focus();
    }

    /// <summary>Sperrzustand anzeigen: Eingabe deaktivieren und Restzeit herunterzählen.</summary>
    private void UpdateLockState()
    {
        var remaining = _limiter.RemainingLockout;
        var locked = remaining > TimeSpan.Zero;

        PinBox.IsEnabled = !locked;
        Pad.IsEnabled = !locked;

        if (locked)
        {
            var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
            MessageText.Text = string.Format(CultureInfo.CurrentCulture,
                "Zu viele Fehlversuche. Bitte in {0} s erneut versuchen.", seconds);
            if (!_lockTimer.IsEnabled)
                _lockTimer.Start();
        }
        else if (_lockTimer.IsEnabled)
        {
            _lockTimer.Stop();
            MessageText.Text = string.Empty;
            PinBox.Focus();
            Keyboard.Focus(PinBox);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
