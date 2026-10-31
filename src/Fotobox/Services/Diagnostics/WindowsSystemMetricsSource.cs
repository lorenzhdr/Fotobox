using System.Runtime.InteropServices;
using AppLog = Fotobox.Core.AppLog;
using Fotobox.Services.Detection;

namespace Fotobox.Services.Diagnostics;

/// <summary>
/// Systemwerte unter Windows: CPU/GPU/NPU-Auslastung, CPU-Takt und -Temperatur, GPU-Speicher über Leistungsindikatoren (PDH, englische
/// Zählernamen), GPU-Name, -Takt und -Temperatur über D3DKMT (<c>gdi32.dll</c>), Arbeitsspeicher über <c>GlobalMemoryStatusEx</c>.
/// Im Konstruktor geschieht nichts Natives; alles wird beim ersten <see cref="Sample"/> angelegt. Jede Teilquelle ist für sich
/// abgesichert: Ein Fehler macht nur ihre Werte <c>null</c> und wird höchstens einmal je Quelle geloggt (ohne Messwerte). Die
/// Raten (CPU, GPU, NPU, Takt) brauchen zwei Messungen: Der erste <see cref="Sample"/> liefert dafür noch <c>null</c>.
/// </summary>
public sealed class WindowsSystemMetricsSource : ISystemMetricsSource
{
    /// <summary>Wie lange die Adapterliste (D3DKMT) gilt, bevor sie neu aufgebaut wird.</summary>
    internal static readonly TimeSpan AdapterListLifetime = TimeSpan.FromSeconds(30);

    private const string CpuUtilityPath = @"\Processor Information(_Total)\% Processor Utility";
    private const string CpuPerformancePath = @"\Processor Information(_Total)\% Processor Performance";
    private const string CpuFrequencyPath = @"\Processor Information(_Total)\Processor Frequency";
    private const string ThermalPath = @"\Thermal Zone Information(*)\Temperature";
    private const string GpuEnginePath = @"\GPU Engine(*)\Utilization Percentage";
    private const string GpuDedicatedPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string GpuSharedPath = @"\GPU Adapter Memory(*)\Shared Usage";

    private readonly DeferredReleaseGate _gate;
    private readonly object _sampleLock = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    // Nur unter _sampleLock und innerhalb von _gate.TryEnter/Exit benutzt.
    private PdhQuery? _query;
    private bool _initialized;
    private Counter _cpuUtility = Counter.Unavailable("CPU-Auslastung");
    private Counter _cpuPerformance = Counter.Unavailable("CPU-Takt");
    private Counter _cpuFrequency = Counter.Unavailable("CPU-Basistakt");
    private Counter _thermal = Counter.Unavailable("CPU-Temperatur");
    private Counter _gpuEngine = Counter.Unavailable("GPU/NPU-Auslastung");
    private Counter _gpuDedicated = Counter.Unavailable("GPU-Speicher (dediziert)");
    private Counter _gpuShared = Counter.Unavailable("GPU-Speicher (gemeinsam)");
    private IReadOnlyList<GraphicsAdapterInfo> _adapters = [];
    private long _adaptersBuiltAt;
    private bool _adaptersBuilt;

    public WindowsSystemMetricsSource()
    {
        _gate = new DeferredReleaseGate(ReleaseNative);
    }

    public SystemMetricsSnapshot Sample(GpuSelection? gpu)
    {
        if (!_gate.TryEnter())
            return SystemMetricsSnapshot.Empty;
        try
        {
            lock (_sampleLock)
                return SampleCore(gpu);
        }
        catch (Exception ex)
        {
            WarnOnce("Messung", "Systemwerte konnten nicht gemessen werden.", ex);
            return SystemMetricsSnapshot.Empty;
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>Gibt die native Abfrage frei - sofort, oder am Ende eines gerade laufenden <see cref="Sample"/>. Wirft nie.</summary>
    public void Dispose() => _ = _gate.RequestReleaseAsync(TimeSpan.Zero);

    // Läuft genau einmal, nie parallel zu einem Sample (DeferredReleaseGate).
    private void ReleaseNative()
    {
        lock (_sampleLock)
        {
            try
            {
                _query?.Dispose();
            }
            catch
            {
                // wirft nie
            }
            _query = null;
        }
    }

    private SystemMetricsSnapshot SampleCore(GpuSelection? gpuSelection)
    {
        var ram = Guard("RAM", ReadRam);

        // Beim ersten Sample wird nur die Grundlinie der Ratenzähler gelegt; lesbar sind sie erst ab dem zweiten.
        var pdh = new PdhReadings();
        if (!_initialized)
        {
            _initialized = true;
            Guard("Leistungsindikatoren", InitializePdh);
        }
        else
        {
            pdh = ReadPdh();
        }

        RefreshAdapters();
        var gpuAdapter = Guard("GPU-Auswahl", () => SystemMetricsLogic.SelectGpu(_adapters, gpuSelection));
        var npuAdapter = Guard("NPU-Auswahl", () => SystemMetricsLogic.SelectNpu(_adapters));

        double? gpuPercent = null, gpuClock = null, gpuTemp = null, gpuUsed = null, gpuTotal = null, npuPercent = null;
        string? gpuName = null, npuName = null;

        if (gpuAdapter is not null)
        {
            gpuName = gpuAdapter.Name;
            gpuPercent = pdh.EngineUtilization is { } e && e.TryGetValue(gpuAdapter.Luid, out var gp) ? gp : null;

            double? dedicated = pdh.Dedicated is { } d && d.TryGetValue(gpuAdapter.Luid, out var dv) ? dv : null;
            double? shared = pdh.Shared is { } s && s.TryGetValue(gpuAdapter.Luid, out var sv) ? sv : null;
            (gpuUsed, gpuTotal) = SystemMetricsLogic.ComputeGpuMemory(dedicated, shared, gpuAdapter.DedicatedVideoMemoryBytes, gpuAdapter.LocalMemoryBytes, gpuAdapter.NonLocalMemoryBytes);

            (gpuClock, gpuTemp) = Guard("GPU-Takt und -Temperatur", () => ReadAdapterDynamics(gpuAdapter.Luid));
        }
        gpuName ??= gpuSelection?.Name;

        if (npuAdapter is not null)
        {
            npuName = npuAdapter.Name;
            npuPercent = pdh.EngineUtilization is { } e && e.TryGetValue(npuAdapter.Luid, out var np) ? np : null;
        }

        return new SystemMetricsSnapshot(
            CpuPercent: pdh.CpuPercent,
            CpuClockMhz: pdh.CpuClockMhz,
            CpuTemperatureC: pdh.CpuTemperatureC,
            GpuPercent: gpuPercent,
            GpuClockMhz: gpuClock,
            GpuTemperatureC: gpuTemp,
            GpuMemoryUsedBytes: gpuUsed,
            GpuMemoryTotalBytes: gpuTotal,
            GpuName: gpuName,
            NpuPercent: npuPercent,
            NpuName: npuName,
            RamUsedBytes: ram?.Used,
            RamTotalBytes: ram?.Total);
    }

    // ---- PDH --------------------------------------------------------------------------------------------------------------

    private void InitializePdh()
    {
        _query = PdhQuery.Open(out var status);
        if (_query is null)
        {
            WarnOnce("PDH", $"Leistungsindikatoren nicht verfügbar (PDH 0x{status:X8}).", null);
            return;
        }

        _cpuUtility = AddCounter(_cpuUtility, CpuUtilityPath);
        _cpuPerformance = AddCounter(_cpuPerformance, CpuPerformancePath);
        _cpuFrequency = AddCounter(_cpuFrequency, CpuFrequencyPath);
        _thermal = AddCounter(_thermal, ThermalPath);
        _gpuEngine = AddCounter(_gpuEngine, GpuEnginePath);
        _gpuDedicated = AddCounter(_gpuDedicated, GpuDedicatedPath);
        _gpuShared = AddCounter(_gpuShared, GpuSharedPath);

        // Erste Messung der Ratenzähler (Grundlinie).
        CollectOnce();
    }

    private Counter AddCounter(Counter counter, string path)
    {
        try
        {
            var status = _query!.TryAddCounter(path, out var handle);
            if (status == PdhQuery.Success && handle != IntPtr.Zero)
                return counter with { Handle = handle };
            WarnOnce(counter.Label, $"Zähler „{counter.Label}“ nicht verfügbar (PDH 0x{status:X8}).", null);
        }
        catch (Exception ex)
        {
            WarnOnce(counter.Label, $"Zähler „{counter.Label}“ nicht verfügbar.", ex);
        }
        return counter;
    }

    private void CollectOnce()
    {
        var status = _query!.Collect();
        // PDH_NO_DATA: z. B. fehlt ein Zähler gerade; die übrigen sind trotzdem lesbar.
        if (status != PdhQuery.Success && status != PdhQuery.NoData)
            WarnOnce("PDH-Messung", $"Leistungsindikatoren konnten nicht gelesen werden (PDH 0x{status:X8}).", null);
    }

    private sealed record PdhReadings(
        double? CpuPercent = null,
        double? CpuClockMhz = null,
        double? CpuTemperatureC = null,
        Dictionary<ulong, double>? EngineUtilization = null,
        Dictionary<ulong, double>? Dedicated = null,
        Dictionary<ulong, double>? Shared = null);

    private PdhReadings ReadPdh()
    {
        if (_query is null)
            return new PdhReadings();

        if (!Guard("PDH-Messung", () => { CollectOnce(); return true; }))
            return new PdhReadings();

        var cpu = Guard("CPU-Auslastung", () => ReadDouble(_cpuUtility) is { } v ? SystemMetricsLogic.ClampPercent(v) : null);

        var clock = Guard("CPU-Takt", () =>
        {
            if (ReadDouble(_cpuFrequency) is { } baseMhz && ReadDouble(_cpuPerformance) is { } perf)
                return SystemMetricsLogic.ComputeCpuClockMhz(baseMhz, perf);
            return null;
        });

        var temp = Guard("CPU-Temperatur", () => ReadArray(_thermal) is { } zones ? SystemMetricsLogic.HighestZoneCelsius(zones.Select(z => z.Value)) : null);

        var engines = Guard("GPU/NPU-Auslastung", () => ReadArray(_gpuEngine) is { } items ? SystemMetricsLogic.AggregateEngineUtilization(items) : null);
        var dedicated = Guard("GPU-Speicher (dediziert)", () => ReadArray(_gpuDedicated) is { } items ? SystemMetricsLogic.AggregateAdapterMemory(items) : null);
        var shared = Guard("GPU-Speicher (gemeinsam)", () => ReadArray(_gpuShared) is { } items ? SystemMetricsLogic.AggregateAdapterMemory(items) : null);

        return new PdhReadings(cpu, clock, temp, engines, dedicated, shared);
    }

    private double? ReadDouble(Counter counter)
    {
        if (counter.Handle == IntPtr.Zero)
            return null;
        var status = PdhQuery.TryGetDouble(counter.Handle, out var value);
        if (status == PdhQuery.Success)
            return value;
        WarnOncePdh(counter, status);
        return null;
    }

    private List<(string Name, double Value)>? ReadArray(Counter counter)
    {
        if (counter.Handle == IntPtr.Zero)
            return null;
        var items = new List<(string Name, double Value)>();
        var status = PdhQuery.TryGetArray(counter.Handle, items);
        if (status == PdhQuery.Success)
            return items;
        // „Keine Daten“ ist kein Fehler (z. B. gerade keine Instanz).
        if (status != PdhQuery.NoData)
            WarnOncePdh(counter, status);
        return null;
    }

    private void WarnOncePdh(Counter counter, uint status) =>
        WarnOnce(counter.Label + "/lesen", $"Zähler „{counter.Label}“ lieferte keinen Wert (PDH 0x{status:X8}).", null);

    private sealed record Counter(string Label, IntPtr Handle)
    {
        public static Counter Unavailable(string label) => new(label, IntPtr.Zero);
    }

    // ---- D3DKMT ----------------------------------------------------------------------------------------------------------

    private void RefreshAdapters()
    {
        var now = Environment.TickCount64;
        if (_adaptersBuilt && now - _adaptersBuiltAt < (long)AdapterListLifetime.TotalMilliseconds)
            return;

        // Auch bei Fehlschlag erst nach der Frist erneut versuchen (kein Aufbau je Tick); die alte Liste bleibt dann erhalten.
        _adaptersBuilt = true;
        _adaptersBuiltAt = now;
        Guard("Grafikadapter", () =>
        {
            var handles = D3dkmtNative.EnumerateAdapters();
            var list = new List<GraphicsAdapterInfo>(handles.Count);
            try
            {
                foreach (var h in handles)
                {
                    try
                    {
                        list.Add(D3dkmtNative.DescribeAdapter(h.Handle, h.Luid));
                    }
                    catch (Exception ex) when (ex is not DllNotFoundException and not EntryPointNotFoundException)
                    {
                        WarnOnce("Grafikadapter/Einzeln", "Ein Grafikadapter konnte nicht abgefragt werden.", ex);
                    }
                }
            }
            finally
            {
                foreach (var h in handles)
                    D3dkmtNative.CloseAdapter(h.Handle);
            }
            if (list.Count > 0 || _adapters.Count == 0)
                _adapters = list;
            return true;
        });
    }

    private (double? ClockMhz, double? TemperatureC) ReadAdapterDynamics(ulong luid)
    {
        var handle = D3dkmtNative.OpenAdapterFromLuid(luid);
        if (handle == 0)
        {
            WarnOnce("GPU-Takt und -Temperatur/Öffnen", "Der Grafikadapter ließ sich für Takt und Temperatur nicht öffnen.", null);
            return (null, null);
        }
        try
        {
            var clock = Guard("GPU-Takt", () => D3dkmtNative.QueryClockMhz(handle));
            var temp = Guard("GPU-Temperatur", () => D3dkmtNative.QueryTemperatureC(handle));
            return (clock, temp);
        }
        finally
        {
            D3dkmtNative.CloseAdapter(handle);
        }
    }

    // ---- Arbeitsspeicher --------------------------------------------------------------------------------------------------

    private static (double Used, double Total)? ReadRam()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
            return null;
        var total = (double)status.TotalPhys;
        var used = Math.Clamp(total - status.AvailPhys, 0, total);
        return (used, total);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    // ---- Fehlerbehandlung -------------------------------------------------------------------------------------------------

    private void Guard(string source, Action action) =>
        Guard<bool>(source, () =>
        {
            action();
            return true;
        });

    /// <summary>Führt eine Teilquelle aus; bei einer Ausnahme (auch fehlende DLL) ergibt sie default und wird einmal geloggt.</summary>
    private T? Guard<T>(string source, Func<T?> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            WarnOnce(source, $"Systemwert „{source}“ nicht verfügbar.", ex);
            return default;
        }
    }

    private void WarnOnce(string key, string message, Exception? ex)
    {
        lock (_warned)
        {
            if (!_warned.Add(key))
                return;
        }
        AppLog.Warn(message, ex);
    }
}
