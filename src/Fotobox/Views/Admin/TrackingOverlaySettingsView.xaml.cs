using System.Windows.Controls;

namespace Fotobox.Views.Admin;

/// <summary>
/// Seite „Tracking-Overlay“ im Admin-Tab „Fun“. Die gesamte Logik liegt im <see cref="ViewModels.Admin.AdminViewModel"/>
/// (der DataContext wird vom Fenster geerbt); die Seite hat keine Vorschau und startet keine Erkennung.
/// </summary>
public partial class TrackingOverlaySettingsView : UserControl
{
    public TrackingOverlaySettingsView() => InitializeComponent();
}
