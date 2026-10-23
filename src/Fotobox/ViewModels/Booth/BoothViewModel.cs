// Stub – wird von Stream E implementiert
using CommunityToolkit.Mvvm.ComponentModel;
using Fotobox.Core;

namespace Fotobox.ViewModels.Booth;

public sealed partial class BoothViewModel(BoothController controller, ICameraService camera, ISettingsStore settings) : ObservableObject
{
    public BoothController Controller { get; } = controller;
    public ICameraService Camera { get; } = camera;
    public ISettingsStore Settings { get; } = settings;
}
