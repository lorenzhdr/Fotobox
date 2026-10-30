using Fotobox.Core;
using Microsoft.Win32;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>Zugriff auf die Windows-Grafikeinstellung je Programm (abstrahiert für Tests).</summary>
public interface IGpuPreferenceStore
{
    /// <summary>Wert des Programms oder null, wenn keiner gesetzt ist.</summary>
    string? Read(string exePath);

    void Write(string exePath, string value);

    void Delete(string exePath);
}

/// <summary>Registry unter <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c> (Einstellung „Grafikleistung“ in Windows; Wertname = voller Exe-Pfad).</summary>
public sealed class RegistryGpuPreferenceStore : IGpuPreferenceStore
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public string? Read(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(exePath) as string;
    }

    public void Write(string exePath, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(exePath, value, RegistryValueKind.String);
    }

    public void Delete(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(exePath, throwOnMissingValue: false);
    }
}

/// <summary>Ergebnis von <see cref="GpuPreferenceRegistry.Apply"/>.</summary>
public enum GpuPreferenceResult
{
    /// <summary>„Hohe Leistung“ wurde neu gesetzt.</summary>
    Set,
    /// <summary>„Hohe Leistung“ war schon gesetzt.</summary>
    AlreadySet,
    /// <summary>Unser Eintrag wurde entfernt.</summary>
    Removed,
    /// <summary>Aus gewünscht und kein Eintrag vorhanden.</summary>
    NothingToRemove,
    /// <summary>Ein vom Benutzer gesetzter anderer Wert blieb unangetastet.</summary>
    ForeignValueKept,
    /// <summary>Kein Exe-Pfad oder die Registry war nicht erreichbar (nur Logwarnung).</summary>
    Failed,
}

/// <summary>
/// Setzt die Windows-Grafikeinstellung „Hohe Leistung“ für die Fotobox selbst (bei Hybridgrafik nutzt Windows dann die starke Karte).
/// Setzt nur den exakten Wert <c>GpuPreference=2;</c> und entfernt nur genau diesen; ein anderer, vom Benutzer gesetzter Wert bleibt immer
/// unangetastet. Wirkt erst beim nächsten Programmstart. Wirft nie, Fehler nur im Log. Der Exe-Pfad steht nie im Log.
/// </summary>
public sealed class GpuPreferenceRegistry(IGpuPreferenceStore store, string? exePath)
{
    /// <summary>Der Wert, den Windows für „Hohe Leistung“ schreibt.</summary>
    public const string HighPerformanceValue = "GpuPreference=2;";

    private readonly object _lock = new();

    public GpuPreferenceResult Apply(bool preferHighPerformance)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                AppLog.Warn("Grafikeinstellung: Der Programmpfad ist unbekannt, die Windows-Grafikeinstellung bleibt unverändert.");
                return GpuPreferenceResult.Failed;
            }

            lock (_lock)
            {
                var current = store.Read(exePath);
                if (preferHighPerformance)
                {
                    if (current is null)
                    {
                        store.Write(exePath, HighPerformanceValue);
                        AppLog.Info("Grafikeinstellung: Windows-Grafikleistung für die Fotobox auf „Hohe Leistung“ gesetzt (wirkt ab dem nächsten Start).");
                        return GpuPreferenceResult.Set;
                    }
                    if (string.Equals(current, HighPerformanceValue, StringComparison.Ordinal))
                        return GpuPreferenceResult.AlreadySet;
                    AppLog.Info("Grafikeinstellung: In Windows ist für die Fotobox schon ein anderer Wert gesetzt; er bleibt unverändert.");
                    return GpuPreferenceResult.ForeignValueKept;
                }

                if (current is null)
                    return GpuPreferenceResult.NothingToRemove;
                if (string.Equals(current, HighPerformanceValue, StringComparison.Ordinal))
                {
                    store.Delete(exePath);
                    AppLog.Info("Grafikeinstellung: Der Eintrag „Hohe Leistung“ für die Fotobox wurde entfernt (wirkt ab dem nächsten Start).");
                    return GpuPreferenceResult.Removed;
                }
                AppLog.Info("Grafikeinstellung: In Windows ist für die Fotobox ein anderer Wert gesetzt; er bleibt unverändert.");
                return GpuPreferenceResult.ForeignValueKept;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Grafikeinstellung: Die Windows-Grafikeinstellung konnte nicht gelesen oder geschrieben werden.", ex);
            return GpuPreferenceResult.Failed;
        }
    }
}
