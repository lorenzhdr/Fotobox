using System.Windows;
using System.Windows.Controls;

namespace Fotobox.Views.Admin;

/// <summary>Ziffernblock 0–9 mit Löschen und OK für die PIN-Dialoge.</summary>
public partial class PinPad : UserControl
{
    public PinPad()
    {
        InitializeComponent();
    }

    public event Action<char>? DigitPressed;
    public event Action? BackspacePressed;
    public event Action? SubmitPressed;

    /// <summary>Beschriftung der Bestätigungstaste (Standard „OK“).</summary>
    public string SubmitText
    {
        get => (string)OkButton.Content;
        set => OkButton.Content = value;
    }

    private void OnDigit(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string { Length: 1 } text })
            DigitPressed?.Invoke(text[0]);
    }

    private void OnBackspace(object sender, RoutedEventArgs e) => BackspacePressed?.Invoke();

    private void OnSubmit(object sender, RoutedEventArgs e) => SubmitPressed?.Invoke();
}
