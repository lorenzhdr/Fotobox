using System.Globalization;
using Fotobox.Core;
using Fotobox.Services.Detection.Acceleration;
using Fotobox.Services.Diagnostics;

namespace Fotobox.ViewModels.Booth;

/// <summary>Warnstufe eines Messwerts der Leistungsanzeige (gilt nur für Auslastung und Speicher).</summary>
public enum PerformanceWarnLevel { Normal, Warn, Critical }

/// <summary>
/// Eine Zeile der Leistungsanzeige. <paramref name="BarFraction"/> (0..1) nur für Werte mit Balken (Auslastung, Speicher; 0 solange kein
/// Wert da ist, damit die Zeile ihre Höhe behält), sonst <c>null</c> (kein Balken).
/// </summary>
public sealed record PerformanceOverlayRow(string Label, string Text, double? BarFraction = null, PerformanceWarnLevel Level = PerformanceWarnLevel.Normal);

/// <summary>
/// Alles, was die Zeilen der Leistungsanzeige brauchen (reine Daten, damit der Aufbau ohne Uhr, Timer und Hardware testbar ist).
/// <c>null</c> bei einem App-Wert = noch nicht messbar oder Funktion inaktiv (Anzeige „–“); <see cref="System"/> = <c>null</c>: keine
/// aktuelle Messung der Systemwerte (noch keine, hängender oder fehlgeschlagener Lauf), alle Systemwerte „–“.
/// <see cref="SystemSettled"/>: Es gab schon eine zweite erfolgreiche Messung (die erste kennt keine Raten); bis dahin zeigen fehlende
/// Systemwerte „–“ statt „nicht verfügbar“.
/// </summary>
internal sealed record PerformanceOverlayData(
    SystemMetricsSnapshot? System,
    bool SystemSettled,
    double? CameraFps,
    double? DisplayFps,
    double? TargetingRate,
    double? TargetingCycleMs,
    double? GestureRate,
    double? GestureCycleMs,
    IReadOnlyList<ModelPlacement> Plan,
    bool TargetingOnWindowsFaceDetector);

/// <summary>
/// Reiner Aufbau und Formatierung der Leistungsanzeige: Zeilen für die eingeschalteten Werte, deutsche Formate, Warnstufen.
/// „–“ = (noch) kein Wert oder Funktion inaktiv; „nicht verfügbar“ = die Messung lief, das Gerät liefert den Wert aber nicht.
/// </summary>
internal static class PerformanceOverlayRows
{
    internal const string Dash = "–";
    internal const string Unavailable = "nicht verfügbar";

    /// <summary>Ab dieser Auslastung (Prozent) wird ein Wert gelb.</summary>
    internal const double WarnPercent = 70;

    /// <summary>Ab dieser Auslastung (Prozent) wird ein Wert rot.</summary>
    internal const double CriticalPercent = 90;

    private const double BytesPerGb = 1024.0 * 1024.0 * 1024.0;

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Warnstufe einer Auslastung bzw. Speicherbelegung in Prozent: unter 70 normal, ab 70 Warnung, ab 90 kritisch.</summary>
    internal static PerformanceWarnLevel WarnLevel(double percent) =>
        percent >= CriticalPercent ? PerformanceWarnLevel.Critical
        : percent >= WarnPercent ? PerformanceWarnLevel.Warn
        : PerformanceWarnLevel.Normal;

    internal static string FormatPercent(double percent) => string.Create(German, $"{Math.Round(percent):0} %");

    internal static string FormatGhz(double mhz) => string.Create(German, $"{mhz / 1000.0:0.0} GHz");

    internal static string FormatCelsius(double celsius) => string.Create(German, $"{Math.Round(celsius):0} °C");

    /// <summary>Speicher in GB (1 GB = 1024³ Bytes, wie in Windows): „7,4/16,0 GB“, ohne Gesamtgröße „7,4 GB“.</summary>
    internal static string FormatMemory(double usedBytes, double? totalBytes) =>
        totalBytes is > 0
            ? string.Create(German, $"{usedBytes / BytesPerGb:0.0}/{totalBytes.Value / BytesPerGb:0.0} GB")
            : string.Create(German, $"{usedBytes / BytesPerGb:0.0} GB");

    internal static string FormatFps(double fps) => string.Create(German, $"{Math.Round(fps):0} FPS");

    internal static string FormatRate(double perSecond) => string.Create(German, $"{perSecond:0.0} Analysen/s");

    /// <summary>Rechenzeit: ab 10 ms ganze Millisekunden („12 ms“), darunter eine Nachkommastelle („4,6 ms“).</summary>
    internal static string FormatMs(double ms) =>
        ms >= 10 ? string.Create(German, $"{Math.Round(ms):0} ms") : string.Create(German, $"{ms:0.0} ms");

    /// <summary>Gerät eines Modells laut Plan: „CPU“, „CPU (Rückfall)“ oder der Name der Grafikkarte/KI-Einheit; ohne Plan „–“.</summary>
    internal static string DeviceText(ModelPlacement? placement)
    {
        if (placement is null)
            return Dash;
        if (placement.Kind == AcceleratorKind.Cpu)
            return placement.FellBack ? "CPU (Rückfall)" : "CPU";
        if (!string.IsNullOrWhiteSpace(placement.DeviceName))
            return placement.DeviceName.Trim();
        return placement.Kind == AcceleratorKind.Gpu ? "GPU" : "NPU";
    }

    /// <summary>Die Zeilen für alle eingeschalteten Werte, in fester Reihenfolge.</summary>
    internal static IReadOnlyList<PerformanceOverlayRow> Build(PerformanceOverlaySettings s, PerformanceOverlayData d)
    {
        var rows = new List<PerformanceOverlayRow>(24);
        var sys = d.System;

        if (s.ShowCpu)
            rows.Add(PercentRow("CPU", sys?.CpuPercent, d));
        if (s.ShowCpuClock)
            rows.Add(Value("CPU-Takt", sys?.CpuClockMhz, d, FormatGhz));
        if (s.ShowCpuTemperature)
            rows.Add(Value("CPU-Temperatur", sys?.CpuTemperatureC, d, FormatCelsius));
        if (s.ShowGpu)
            rows.Add(PercentRow("GPU", sys?.GpuPercent, d));
        if (s.ShowGpuClock)
            rows.Add(Value("GPU-Takt", sys?.GpuClockMhz, d, FormatGhz));
        if (s.ShowGpuTemperature)
            rows.Add(Value("GPU-Temperatur", sys?.GpuTemperatureC, d, FormatCelsius));
        if (s.ShowGpuMemory)
            rows.Add(MemoryRow("GPU-Speicher", sys?.GpuMemoryUsedBytes, sys?.GpuMemoryTotalBytes, d));
        if (s.ShowNpu)
            rows.Add(PercentRow("NPU", sys?.NpuPercent, d));
        if (s.ShowRam)
            rows.Add(MemoryRow("RAM", sys?.RamUsedBytes, sys?.RamTotalBytes, d));
        if (s.ShowCameraFps)
            rows.Add(new PerformanceOverlayRow("Kamera", d.CameraFps is { } cam ? FormatFps(cam) : Dash));
        if (s.ShowDisplayFps)
            rows.Add(new PerformanceOverlayRow("Anzeige", d.DisplayFps is { } disp ? FormatFps(disp) : Dash));
        if (s.ShowAnalysisRate)
        {
            rows.Add(new PerformanceOverlayRow("Zielerfassung", d.TargetingRate is { } t ? FormatRate(t) : Dash));
            rows.Add(new PerformanceOverlayRow("Gesten", d.GestureRate is { } g ? FormatRate(g) : Dash));
        }
        if (s.ShowCycleTime)
        {
            rows.Add(new PerformanceOverlayRow("Zielerfassung Zeit", d.TargetingCycleMs is { } t ? FormatMs(t) : Dash));
            rows.Add(new PerformanceOverlayRow("Gesten Zeit", d.GestureCycleMs is { } g ? FormatMs(g) : Dash));
        }
        if (s.ShowModelDevices)
        {
            // Läuft die Zielerfassung auf dem Windows-Gesichtsdetektor, läuft das Personenmodell (Yolox) nicht: Der Detektor steht an seiner Stelle.
            rows.Add(d.TargetingOnWindowsFaceDetector
                ? new PerformanceOverlayRow("Zielerfassung Gerät", "Windows-Gesichtserkennung")
                : new PerformanceOverlayRow("Personen", DeviceText(Find(d.Plan, ModelId.Yolox))));
            rows.Add(new PerformanceOverlayRow("Gesichter", DeviceText(Find(d.Plan, ModelId.YuNet))));
            rows.Add(new PerformanceOverlayRow("Hände", DeviceText(Find(d.Plan, ModelId.Palm))));
            rows.Add(new PerformanceOverlayRow("Handpunkte", DeviceText(Find(d.Plan, ModelId.Landmark))));
        }
        return rows;
    }

    private static ModelPlacement? Find(IReadOnlyList<ModelPlacement> plan, ModelId model) => plan.FirstOrDefault(p => p.Model == model);

    // Fehlender Wert: ohne aktuelle Messung oder in der allerersten (noch ohne Raten) „–“, sonst „nicht verfügbar“.
    private static string Missing(PerformanceOverlayData d) => d.System is null || !d.SystemSettled ? Dash : Unavailable;

    private static PerformanceOverlayRow Value(string label, double? value, PerformanceOverlayData d, Func<double, string> format) =>
        new(label, value is { } v && d.System is not null ? format(v) : Missing(d));

    private static PerformanceOverlayRow PercentRow(string label, double? percent, PerformanceOverlayData d)
    {
        if (percent is not { } p || d.System is null)
            return new PerformanceOverlayRow(label, Missing(d), 0);
        return new PerformanceOverlayRow(label, FormatPercent(p), Math.Clamp(p / 100.0, 0, 1), WarnLevel(p));
    }

    private static PerformanceOverlayRow MemoryRow(string label, double? used, double? total, PerformanceOverlayData d)
    {
        if (used is not { } u || d.System is null)
            return new PerformanceOverlayRow(label, Missing(d), 0);
        if (total is not > 0)
            return new PerformanceOverlayRow(label, FormatMemory(u, null), 0);
        var percent = u / total.Value * 100.0;
        return new PerformanceOverlayRow(label, FormatMemory(u, total), Math.Clamp(percent / 100.0, 0, 1), WarnLevel(percent));
    }
}
