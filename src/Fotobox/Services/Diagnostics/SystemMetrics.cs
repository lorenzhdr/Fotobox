namespace Fotobox.Services.Diagnostics;

/// <summary>
/// Messwerte des Systems zu einem Zeitpunkt. <c>null</c> = nicht verfügbar (Quelle fehlt, Treiber liefert nichts, erster Messwert
/// einer Rate noch nicht da). Prozentwerte liegen in 0..100, Temperaturen in °C, Takte in MHz, Speicher in Bytes.
/// </summary>
public sealed record SystemMetricsSnapshot(
    double? CpuPercent = null,
    double? CpuClockMhz = null,
    double? CpuTemperatureC = null,
    double? GpuPercent = null,
    double? GpuClockMhz = null,
    double? GpuTemperatureC = null,
    double? GpuMemoryUsedBytes = null,
    double? GpuMemoryTotalBytes = null,
    string? GpuName = null,
    double? NpuPercent = null,
    string? NpuName = null,
    double? RamUsedBytes = null,
    double? RamTotalBytes = null)
{
    /// <summary>Nichts verfügbar.</summary>
    public static SystemMetricsSnapshot Empty { get; } = new();
}

/// <summary>
/// Die von der KI-Erkennung genutzte Grafikkarte (Hersteller-/Geräte-Id und Name), gefüllt vom Aufrufer; <c>null</c> statt
/// einer Auswahl = keine bekannt (dann nimmt die Quelle die erste echte Grafikkarte).
/// </summary>
public sealed record GpuSelection(uint VendorId, uint DeviceId, string? Name);

/// <summary>
/// Quelle für Systemwerte. <see cref="Sample"/> darf blockieren: Der Aufrufer ruft es auf dem Threadpool mit Frist auf und
/// nie zweimal gleichzeitig. Wirft nie; nicht ermittelbare Werte sind <c>null</c>. <see cref="IDisposable.Dispose"/> gibt native
/// Ressourcen nie während eines laufenden <see cref="Sample"/> frei (die Freigabe folgt dann an dessen Ende).
/// </summary>
public interface ISystemMetricsSource : IDisposable
{
    /// <summary>Misst jetzt. <paramref name="gpu"/> = die von der KI genutzte Grafikkarte (null = keine bekannt).</summary>
    SystemMetricsSnapshot Sample(GpuSelection? gpu);
}
