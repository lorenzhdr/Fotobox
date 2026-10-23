// Stub – wird von Stream E implementiert
using System.Windows;
using Fotobox.Core;
using Fotobox.ViewModels.Booth;

namespace Fotobox.Views.Booth;

public partial class BoothWindow : Window
{
    private readonly ICameraService _camera;
    private readonly IAdminLauncher _adminLauncher;
    private readonly ISettingsStore _settings;

    public BoothWindow(BoothViewModel viewModel, ICameraService camera, IAdminLauncher adminLauncher, ISettingsStore settings)
    {
        _camera = camera;
        _adminLauncher = adminLauncher;
        _settings = settings;
        DataContext = viewModel;
        InitializeComponent();
    }
}
