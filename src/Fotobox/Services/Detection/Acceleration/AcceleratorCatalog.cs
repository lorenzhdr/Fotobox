using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Fotobox.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>Findet die Rechengeräte (über Windows ML) und installiert Hersteller-Ausführungsanbieter. Wirft nach außen nie.</summary>
public interface IAcceleratorCatalog
{
    /// <summary>Zuletzt gefundene Geräte (CPU immer zuerst); vor <see cref="DiscoverAsync"/> nur die CPU.</summary>
    IReadOnlyList<AcceleratorDevice> Devices { get; }

    /// <summary>
    /// Registriert die bereits vorhandenen zertifizierten Ausführungsanbieter (ohne Download) und liest die Geräteliste. Mit Frist
    /// (<see cref="AccelerationTimeouts.Discovery"/>); jeder Fehler ergibt nur die CPU plus Logwarnung.
    /// </summary>
    Task<IReadOnlyList<AcceleratorDevice>> DiscoverAsync(CancellationToken ct = default);

    /// <summary>Liest die Geräteliste erneut (ohne Registrierung), z. B. nach einer Installation.</summary>
    IReadOnlyList<AcceleratorDevice> RefreshDevices();

    /// <summary>Zustand der Ausführungsanbieter des Windows-ML-Katalogs (leer, wenn der Katalog nicht verfügbar ist).</summary>
    IReadOnlyList<ProviderState> GetProviderStates();

    /// <summary>
    /// Lädt und installiert die zertifizierten Hersteller-Ausführungsanbieter (braucht Internet, kann Minuten dauern; Frist
    /// <see cref="AccelerationTimeouts.Install"/>) und registriert sie. <paramref name="progress"/> meldet 0 bis 1 (von einem beliebigen Thread).
    /// Wirft nie; das Ergebnis trägt Erfolg bzw. Fehlertext. Neue Geräte nutzt die Erkennung erst nach einem Neustart.
    /// </summary>
    Task<ProviderInstallResult> InstallVendorProvidersAsync(IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <inheritdoc cref="IAcceleratorCatalog" />
public sealed class AcceleratorCatalog : IAcceleratorCatalog
{
    private volatile IReadOnlyList<AcceleratorDevice> _devices = [AcceleratorDevice.CpuDevice];

    public IReadOnlyList<AcceleratorDevice> Devices => _devices;

    public async Task<IReadOnlyList<AcceleratorDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        try
        {
            // Die WinRT-Aufrufe laufen auf dem Threadpool (MTA); die äußere Frist liegt knapp über der inneren.
            var devices = await Task.Run(() => DiscoverCore(ct), CancellationToken.None)
                .WaitAsync(AccelerationTimeouts.Discovery + TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            _devices = devices;
            return devices;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die Geräteliste war nicht abrufbar, es bleibt bei der CPU.", ex);
            return _devices;
        }
    }

    public IReadOnlyList<AcceleratorDevice> RefreshDevices()
    {
        try
        {
            var devices = ReadDevices();
            _devices = devices;
            return devices;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die Geräteliste konnte nicht aktualisiert werden.", ex);
            return _devices;
        }
    }

    private static IReadOnlyList<AcceleratorDevice> DiscoverCore(CancellationToken ct)
    {
        try
        {
            RegisterPresentProviders(ct);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die vorhandenen Ausführungsanbieter konnten nicht registriert werden.", ex);
        }
        return ReadDevices();
    }

    private static void RegisterPresentProviders(CancellationToken ct)
    {
        var catalog = ExecutionProviderCatalog.GetDefault();
        if (catalog is null)
        {
            AppLog.Info("Beschleunigung: Der Windows-ML-Katalog ist nicht verfügbar (nur eingebaute Anbieter).");
            return;
        }

        // Ohne Download: nur die schon installierten zertifizierten Anbieter, normalerweise einige Millisekunden.
        var operation = catalog.RegisterCertifiedAsync();
        try
        {
            if (!operation.AsTask(ct).Wait(AccelerationTimeouts.Discovery, ct))
            {
                AppLog.Warn($"Beschleunigung: Die Registrierung der Ausführungsanbieter dauerte länger als {AccelerationTimeouts.Discovery.TotalSeconds:0} s und wird abgebrochen.");
                operation.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            throw;
        }
    }

    /// <summary>Liest die Geräte aus ONNX Runtime (Grafikkarten und KI-Einheiten); die CPU steht immer vorn.</summary>
    private static IReadOnlyList<AcceleratorDevice> ReadDevices()
    {
        var result = new List<AcceleratorDevice> { AcceleratorDevice.CpuDevice };
        var seen = new HashSet<string>(StringComparer.Ordinal) { AcceleratorDevice.CpuKey };
        IReadOnlyList<OrtEpDevice> epDevices;
        try
        {
            epDevices = OrtEnv.Instance().GetEpDevices();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die Geräteliste von ONNX Runtime war nicht lesbar.", ex);
            return result;
        }

        // Feste Reihenfolge (Adapternummer, dann Schlüssel), damit Duplikate gleicher Karten immer dieselbe Nummer bekommen.
        var mapped = new List<(AcceleratorDevice Device, int Adapter)>();
        foreach (var ep in epDevices)
        {
            try
            {
                var device = FromEpDevice(ep, out var adapter);
                if (device is not null)
                    mapped.Add((device, adapter));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Beschleunigung: Ein Gerät von ONNX Runtime wurde übersprungen.", ex);
            }
        }

        foreach (var (device, _) in mapped.OrderBy(m => m.Adapter).ThenBy(m => m.Device.Key, StringComparer.Ordinal))
        {
            var unique = device;
            for (var n = 2; !seen.Add(unique.Key); n++)
                unique = device with { Key = device.Key + "#" + n.ToString(CultureInfo.InvariantCulture) };
            result.Add(unique);
        }
        return result;
    }

    /// <summary>Wandelt ein ONNX-Runtime-Gerät um; null für CPU-Geräte (die CPU ist immer <see cref="AcceleratorDevice.CpuDevice"/>).</summary>
    internal static AcceleratorDevice? FromEpDevice(OrtEpDevice ep, out int adapterNumber)
    {
        adapterNumber = int.MaxValue;
        var hardware = ep.HardwareDevice;
        var kind = hardware.Type switch
        {
            OrtHardwareDeviceType.GPU => AcceleratorKind.Gpu,
            OrtHardwareDeviceType.NPU => AcceleratorKind.Npu,
            _ => (AcceleratorKind?)null,
        };
        if (kind is null)
            return null;

        var hw = ToDictionary(hardware.Metadata.Entries);
        var epMeta = ToDictionary(ep.EpMetadata.Entries);
        hw.TryGetValue("Description", out var description);
        var vendor = string.IsNullOrWhiteSpace(hardware.Vendor) ? ep.EpVendor : hardware.Vendor;
        var name = !string.IsNullOrWhiteSpace(description)
            ? description.Trim()
            : $"{vendor} {(kind == AcceleratorKind.Gpu ? "GPU" : "NPU")}".Trim();

        if (hw.TryGetValue("DxgiAdapterNumber", out var adapterText) && int.TryParse(adapterText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var adapter))
            adapterNumber = adapter;

        var highPerformanceIndex = int.MaxValue;
        if (hw.TryGetValue("DxgiHighPerformanceIndex", out var indexText) && int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex))
            highPerformanceIndex = parsedIndex;
        var videoMemory = hw.TryGetValue("DxgiVideoMemory", out var memoryText) ? ParseMemoryMegabytes(memoryText) : 0;

        return new AcceleratorDevice(
            AcceleratorSelector.BuildKey(ep.EpName, hardware.VendorId, hardware.DeviceId, description),
            name,
            kind.Value,
            ep.EpName,
            IsDiscrete: hw.TryGetValue("Discrete", out var discrete) && ParseBool(discrete),
            HighPerformanceIndex: highPerformanceIndex,
            VideoMemory: videoMemory,
            Version: FindVersion(epMeta) ?? FindVersion(hw),
            VendorId: hardware.VendorId,
            DeviceId: hardware.DeviceId)
        {
            Handle = ep,
        };
    }

    /// <summary>
    /// Videospeicher in Megabyte aus dem Metadatenwert. ONNX Runtime liefert Text mit Einheit (z. B. „128 MB“, „8 GB“); eine reine
    /// Zahl gilt als Byte-Angabe. Unlesbar → 0. Wird nur zum Vergleichen (Rangfolge) und zur Anzeige genutzt.
    /// </summary>
    internal static long ParseMemoryMegabytes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        var trimmed = text.Trim();
        var split = 0;
        while (split < trimmed.Length && (char.IsDigit(trimmed[split]) || trimmed[split] is '.' or ','))
            split++;
        if (split == 0 || !double.TryParse(trimmed[..split].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0)
            return 0;
        var factor = trimmed[split..].Trim().ToUpperInvariant() switch
        {
            "KB" or "KIB" => 1.0 / 1024,
            "MB" or "MIB" => 1.0,
            "GB" or "GIB" => 1024.0,
            "TB" or "TIB" => 1024.0 * 1024,
            _ => 1.0 / (1024 * 1024), // ohne Einheit: Bytes
        };
        var megabytes = number * factor;
        return megabytes >= long.MaxValue ? long.MaxValue : (long)megabytes;
    }

    private static Dictionary<string, string> ToDictionary(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            result[entry.Key] = entry.Value;
        return result;
    }

    // "1", "true" (Groß-/Kleinschreibung egal) oder "yes".
    private static bool ParseBool(string text) =>
        bool.TryParse(text, out var value) ? value : text.Trim() is "1" or "yes" or "Yes";

    private static string? FindVersion(Dictionary<string, string> meta)
    {
        foreach (var entry in meta)
        {
            if (entry.Key.Contains("version", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.Value))
                return entry.Value.Trim();
        }
        return null;
    }

    public IReadOnlyList<ProviderState> GetProviderStates()
    {
        try
        {
            var catalog = ExecutionProviderCatalog.GetDefault();
            if (catalog is null)
                return [];
            var result = new List<ProviderState>();
            foreach (var provider in catalog.FindAllProviders())
            {
                try
                {
                    // PackageId kann bei nicht installierten Anbietern fehlen (null).
                    result.Add(new ProviderState(provider.Name, provider.PackageId?.FullName ?? "", provider.ReadyState.ToString(),
                        provider.ReadyState == ExecutionProviderReadyState.Ready));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Beschleunigung: Ein Ausführungsanbieter des Katalogs war nicht lesbar.", ex);
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Der Zustand der Ausführungsanbieter war nicht abrufbar.", ex);
            return [];
        }
    }

    public async Task<ProviderInstallResult> InstallVendorProvidersAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var catalog = ExecutionProviderCatalog.GetDefault();
            if (catalog is null)
                return new ProviderInstallResult(false, "Windows ML ist auf diesem Computer nicht verfügbar.", []);

            AppLog.Info("Beschleunigung: Hersteller-Ausführungsanbieter werden geladen und installiert.");
            // Die WinRT-Aufrufe laufen auf dem Threadpool (MTA); Abbruch und Frist rufen Cancel() der Operation auf.
            var installed = await Task.Run(async () =>
            {
                var operation = catalog.EnsureAndRegisterCertifiedAsync();
                try
                {
                    return await operation.AsTask(ct, progress ?? new Progress<double>())
                        .WaitAsync(AccelerationTimeouts.Install, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                {
                    operation.Cancel();
                    throw;
                }
            }, CancellationToken.None).ConfigureAwait(false);

            var names = installed?.Select(p => p.Name).ToList() ?? [];
            AppLog.Info($"Beschleunigung: Ausführungsanbieter registriert: {(names.Count == 0 ? "keine neuen" : string.Join(", ", names))}.");
            return new ProviderInstallResult(true, names.Count == 0 ? "Es gibt keine weiteren Ausführungsanbieter für diesen Computer." : "Die Ausführungsanbieter wurden installiert.", names);
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Beschleunigung: Die Installation der Ausführungsanbieter wurde abgebrochen.");
            return new ProviderInstallResult(false, "Die Installation wurde abgebrochen.", [], Cancelled: true);
        }
        catch (TimeoutException)
        {
            AppLog.Warn("Beschleunigung: Die Installation der Ausführungsanbieter dauerte zu lange und wurde abgebrochen.");
            return new ProviderInstallResult(false, "Die Installation dauerte zu lange und wurde abgebrochen. Prüfen Sie die Internetverbindung.", []);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die Installation der Ausführungsanbieter ist fehlgeschlagen.", ex);
            return new ProviderInstallResult(false, "Die Installation ist fehlgeschlagen: " + ex.Message, []);
        }
    }
}
