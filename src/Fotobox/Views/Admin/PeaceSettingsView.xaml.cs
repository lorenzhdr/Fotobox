using System.Windows;
using System.Windows.Controls;
using Fotobox.ViewModels.Admin;

namespace Fotobox.Views.Admin;

/// <summary>
/// Seite „Peaceerkennung“ im Admin-Tab „Fun“. Die Logik liegt im <see cref="AdminViewModel"/> (DataContext wird vom Fenster geerbt);
/// hier wird nur die Explosion des Test-Countdowns an das <c>SelfDestructOverlay</c> weitergereicht.
/// </summary>
public partial class PeaceSettingsView : UserControl
{
    private AdminViewModel? _viewModel;

    public PeaceSettingsView()
    {
        InitializeComponent();
        // Bewusst nicht bei Unloaded abmelden: Ein Tab-Wechsel entlädt den Inhalt, der Test-Countdown soll danach weiterwirken.
        // Das Ansichtsmodell lebt genauso lange wie das Fenster.
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (e.NewValue is AdminViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.PeaceTestExplosionRequested += OnExplosionRequested;
        }
    }

    private void Detach()
    {
        if (_viewModel is not null)
            _viewModel.PeaceTestExplosionRequested -= OnExplosionRequested;
        _viewModel = null;
    }

    private void OnExplosionRequested(object? sender, EventArgs e) => PeaceSelfDestruct.PlayExplosion();
}
