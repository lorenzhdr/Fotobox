using Fotobox.Services.Detection.Acceleration;

namespace Fotobox;

/// <summary>
/// Einstiegspunkt. Statt des von WPF erzeugten <c>Main</c>: Der Messprozess der Beschleunigung (<c>--beschleunigung-messen</c>) wird
/// abgezweigt, bevor <see cref="App"/> entsteht – also ohne Fenster, Einstellungen, PIN, Kamera und Einzelinstanz-Sperre.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], MeasureProtocol.Switch, StringComparison.Ordinal))
            return MeasurementChild.Run(args);

        var app = new App();
        return app.Run();
    }
}
