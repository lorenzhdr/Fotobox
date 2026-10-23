using System.Windows;
using System.Windows.Controls;
using Fotobox.ViewModels.Admin;

namespace Fotobox.Views.Admin;

/// <summary>Modale Admin-Ansicht (Einstellungen). Die Logik liegt im <see cref="AdminViewModel"/>.</summary>
public partial class AdminWindow : Window
{
    private readonly AdminViewModel _viewModel;

    public AdminWindow(AdminViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // Auf kleinen Bildschirmen nicht höher als der Arbeitsbereich.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 20);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 20);

        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PinFieldsCleared += () =>
        {
            NewPinBox.Clear();
            RepeatPinBox.Clear();
        };
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void OnCloseRequested(bool save)
    {
        // DialogResult schließt das modale Fenster; true = gespeichert.
        DialogResult = save;
    }

    private void OnNewPinChanged(object sender, RoutedEventArgs e) =>
        _viewModel.NewPin = ((PasswordBox)sender).Password;

    private void OnRepeatPinChanged(object sender, RoutedEventArgs e) =>
        _viewModel.RepeatPin = ((PasswordBox)sender).Password;
}
