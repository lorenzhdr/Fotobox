using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fotobox.Core;

namespace Fotobox.Views.Admin;

/// <summary>Erststart: PIN festlegen (4–12 Ziffern, mit Wiederholung).</summary>
public partial class FirstRunPinDialog : Window
{
    private PasswordBox _activeBox;

    /// <param name="owner">Besitzerfenster (null = Bildschirmmitte).</param>
    /// <param name="theme">Farbschema.</param>
    /// <param name="adminKeys">Tasten der Admin-Ansicht aus den Einstellungen (für den Hinweistext); null = Standardtaste.</param>
    public FirstRunPinDialog(Window? owner, AdminThemeMode theme = AdminThemeMode.System, IEnumerable<string>? adminKeys = null)
    {
        InitializeComponent();
        AdminKeyHint.Text = $"Hinweis: Mit {FormatAdminKeys(adminKeys)} öffnen Sie später die Einstellungen.";
        AdminThemeManager.Apply(this, theme);
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

    /// <summary>
    /// Anzeigetext der Admin-Tasten wie in der Admin-Ansicht (z. B. „Strg+Alt+A“, mehrere mit „ oder “). Ungültige Einträge
    /// werden übersprungen; bleibt keine Taste übrig, gilt die Standardtaste (wie in der Gastansicht).
    /// </summary>
    internal static string FormatAdminKeys(IEnumerable<string>? adminKeys)
    {
        var chords = new List<KeyChord>();
        foreach (var entry in adminKeys ?? [])
        {
            if (KeyChord.TryParse(entry, out var chord) && !chords.Contains(chord))
                chords.Add(chord);
        }

        if (chords.Count == 0 && KeyChord.TryParse(InputSettings.DefaultAdminKey, out var fallback))
            chords.Add(fallback);
        return string.Join(" oder ", chords.Select(c => c.DisplayName));
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
