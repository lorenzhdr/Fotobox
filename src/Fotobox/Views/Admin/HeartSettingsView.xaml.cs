using System.Windows.Controls;

namespace Fotobox.Views.Admin;

/// <summary>
/// Seite „Herz“ im Admin-Tab „Fun“. Die gesamte Logik liegt im <see cref="ViewModels.Admin.AdminViewModel"/>
/// (der DataContext wird vom Fenster geerbt).
/// </summary>
public partial class HeartSettingsView : UserControl
{
    public HeartSettingsView() => InitializeComponent();
}
