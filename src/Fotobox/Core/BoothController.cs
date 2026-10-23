// Stub – wird von Stream E implementiert
namespace Fotobox.Core;

public enum BoothState { Starting, Idle, Countdown, Capturing, Preview, Printing, Error }

public sealed class BoothController(
    ICameraService camera,
    ILayoutComposer composer,
    IPrintService print,
    IPhotoStorage storage,
    ISettingsStore settings,
    TimeProvider time)
{
    private BoothState _state = BoothState.Starting;

    public BoothState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? StateChanged;

    public bool TryTrigger() => false;
}
