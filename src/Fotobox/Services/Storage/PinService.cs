// Stub – wird von Stream D implementiert
using Fotobox.Core;

namespace Fotobox.Services.Storage;

public sealed class PinService(ISettingsStore settings) : IPinService
{
    public bool HasPin => !string.IsNullOrEmpty(settings.Current.Security.PinHash);

    public void SetPin(string pin) =>
        throw new NotImplementedException("Stub – wird von Stream D implementiert.");

    public bool Verify(string pin) => false;
}
