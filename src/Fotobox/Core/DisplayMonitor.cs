namespace Fotobox.Core;

/// <summary>Rechteck eines Monitors in Bildschirmkoordinaten (wie <c>GetMonitorInfo.rcMonitor</c>, passend für <c>SetWindowPos</c>).</summary>
public readonly record struct MonitorRect(int Left, int Top, int Width, int Height);

/// <summary>
/// Ein angeschlossener Monitor. <paramref name="Id"/> ist stabil über Neustarts (Geräte-Pfad, Fallback GDI-Name),
/// <paramref name="Number"/> die Nummer aus dem GDI-Gerätenamen <c>\\.\DISPLAYn</c>
/// (nicht zwingend die Nummer in den Windows-Anzeigeeinstellungen), <paramref name="PixelWidth"/>/<paramref name="PixelHeight"/> die echte Auflösung.
/// </summary>
public sealed record DisplayMonitor(string Id, string Name, int Number, bool IsPrimary, int PixelWidth, int PixelHeight, MonitorRect Bounds)
{
    public override string ToString() => GuestMonitors.Label(this);
}

/// <summary>UI-freie Logik zur Monitorwahl der Gastansicht (<see cref="AppearanceSettings.GuestMonitorId"/>).</summary>
public static class GuestMonitors
{
    /// <summary>Monitor mit dieser Id, sonst der Hauptbildschirm, sonst der erste, sonst null (keine Monitore bekannt).</summary>
    public static DisplayMonitor? Resolve(IReadOnlyList<DisplayMonitor> monitors, string? id)
    {
        if (id is not null)
        {
            var match = monitors.FirstOrDefault(m => m.Id == id);
            if (match is not null) return match;
        }
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }

    /// <summary>„Bildschirm 2 – Name (1920 × 1080)“, beim Hauptbildschirm zusätzlich „ · Hauptbildschirm“.</summary>
    public static string Label(DisplayMonitor m) =>
        $"Bildschirm {m.Number} – {m.Name} ({m.PixelWidth} × {m.PixelHeight})" + (m.IsPrimary ? " · Hauptbildschirm" : "");
}
