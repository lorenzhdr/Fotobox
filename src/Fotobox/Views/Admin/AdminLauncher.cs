// Stub – wird von Stream F implementiert
using System.Windows;
using Fotobox.Core;

namespace Fotobox.Views.Admin;

public sealed class AdminLauncher(ISettingsStore settings, IPinService pinService, ICameraService camera, IPrintService print) : IAdminLauncher
{
    public bool EnsurePinConfigured(Window? owner) => true;

    public AdminResult OpenAdmin(Window owner) => new(false, false, false);
}
