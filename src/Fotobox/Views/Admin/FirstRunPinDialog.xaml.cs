using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fotobox.ViewModels.Admin;

namespace Fotobox.Views.Admin;

/// <summary>Erststart: PIN festlegen (4–12 Ziffern, mit Wiederholung).</summary>
public partial class FirstRunPinDialog : Window
{
    private PasswordBox _activeBox;

    public FirstRunPinDialog(Window? owner)
    {
        InitializeComponent();
        _activeBox = PinBox;

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
        PinBoxHelper.Configure(RepeatBox);
        PinBox.GotKeyboardFocus += (_, _) => _activeBox = PinBox;
        RepeatBox.GotKeyboardFocus += (_, _) => _activeBox = RepeatBox;

        Pad.DigitPressed += d => PinBoxHelper.Append(_activeBox, d);
        Pad.BackspacePressed += () => PinBoxHelper.Backspace(_activeBox);
        Pad.SubmitPressed += Submit;

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            PinBox.Focus();
            Keyboard.Focus(PinBox);
        };
    }

    /// <summary>Die bestätigte PIN (gültig nach <c>DialogResult == true</c>).</summary>
    public string Pin { get; private set; } = string.Empty;

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
        // Enter im ersten Feld springt zur Wiederholung, solange diese noch leer ist.
        if (_activeBox == PinBox && RepeatBox.Password.Length == 0 && PinRules.IsValid(PinBox.Password))
        {
            MessageText.Text = string.Empty;
            RepeatBox.Focus();
            return;
        }

        var error = PinRules.GetError(PinBox.Password, RepeatBox.Password);
        if (error is not null)
        {
            MessageText.Text = error;
            // Bei fehlender Übereinstimmung die Wiederholung neu eingeben lassen.
            if (PinRules.IsValid(PinBox.Password))
            {
                RepeatBox.Clear();
                RepeatBox.Focus();
            }
            else
            {
                PinBox.Focus();
            }
            return;
        }

        Pin = PinBox.Password;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
