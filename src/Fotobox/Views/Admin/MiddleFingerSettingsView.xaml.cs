using System.Windows.Controls;

namespace Fotobox.Views.Admin;

/// <summary>
/// Seite „Mittelfinger“ im Admin-Tab „Fun“. Die gesamte Logik liegt im <see cref="ViewModels.Admin.AdminViewModel"/>
/// (der DataContext wird vom Fenster geerbt); die Vorschau teilt sich die Erkennung mit der Seite „Peaceerkennung“.
/// </summary>
public partial class MiddleFingerSettingsView : UserControl
{
    public MiddleFingerSettingsView() => InitializeComponent();
}
