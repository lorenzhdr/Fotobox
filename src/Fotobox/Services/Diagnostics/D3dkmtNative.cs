using System.Runtime.InteropServices;

namespace Fotobox.Services.Diagnostics;

/// <summary>
/// Abfragen der Grafikadapter über die Kernel-Thunks in <c>gdi32.dll</c> (<c>D3DKMT*</c>). Die Strukturen entsprechen
/// <c>d3dkmthk.h</c> (Windows SDK 10.0.26100) für 64-Bit-Prozesse (x64, ARM64); in 32-Bit-Prozessen wären sie anders ausgerichtet,
/// die App läuft nur 64-Bit. Alle Methoden liefern den NTSTATUS bzw. false statt zu werfen, außer wenn die DLL/der Einsprung fehlt
/// (<see cref="DllNotFoundException"/>/<see cref="EntryPointNotFoundException"/>) - das fängt der Aufrufer je Teilquelle ab.
/// </summary>
internal static class D3dkmtNative
{
    // KMTQUERYADAPTERINFOTYPE
    private const int QueryAdapterRegistryInfo = 8;
    private const int QueryAdapterType = 15;
    private const int QueryPhysicalAdapterDeviceIds = 31;
    private const int QueryGetSegmentGroupSize = 42;
    private const int QueryNodePerfData = 61;
    private const int QueryAdapterPerfData = 62;
    private const int QueryDriverDescription = 65;

    private const int DriverDescriptionChars = 4096; // D3DKMT_DRIVER_DESCRIPTION.DriverDescription[4096]
    private const int MaxPath = 260;
    private const int AdapterRegistryInfoChars = MaxPath * 4; // AdapterString, BiosString, DacType, ChipType

    // D3DKMT_ENUMADAPTERS_FILTER-Bits
    private const ulong FilterIncludeComputeOnly = 1UL << 0;
    private const ulong FilterIncludeDisplayOnly = 1UL << 1;

    // D3DKMT_ADAPTERTYPE-Bits
    private const uint TypeRenderSupported = 1u << 0;
    private const uint TypeDisplaySupported = 1u << 1;
    private const uint TypeSoftwareDevice = 1u << 2;
    private const uint TypeComputeOnly = 1u << 11;

    /// <summary>Ein aufgezählter Adapter: Handle (muss mit <see cref="CloseAdapter"/> geschlossen werden) und LUID.</summary>
    internal readonly record struct EnumeratedAdapter(uint Handle, ulong Luid);

    /// <summary>
    /// Zählt die Adapter auf, möglichst inklusive reiner Rechen-Adapter (NPU): zuerst <c>D3DKMTEnumAdapters3</c> mit Filter, sonst
    /// <c>D3DKMTEnumAdapters2</c>. Die Handles sind offen; der Aufrufer schließt sie. Leer bei Fehler.
    /// </summary>
    internal static List<EnumeratedAdapter> EnumerateAdapters()
    {
        try
        {
            var viaV3 = EnumerateAdapters(version3: true);
            if (viaV3.Count > 0)
                return viaV3;
        }
        catch (EntryPointNotFoundException)
        {
            // Windows vor Version 2004: nur D3DKMTEnumAdapters2.
        }
        return EnumerateAdapters(version3: false);
    }

    internal static List<EnumeratedAdapter> EnumerateAdapters(bool version3)
    {
        var result = new List<EnumeratedAdapter>();
        var request = new EnumAdapters3 { Filter = version3 ? FilterIncludeComputeOnly | FilterIncludeDisplayOnly : 0, NumAdapters = 0, Adapters = IntPtr.Zero };
        if (Call(version3, ref request) < 0 || request.NumAdapters == 0)
            return result;

        // Zwischen den Aufrufen kann ein Adapter dazukommen: etwas Reserve.
        var capacity = (int)Math.Min(request.NumAdapters + 4u, 64u);
        var itemSize = Marshal.SizeOf<AdapterInfoEntry>();
        var buffer = Marshal.AllocHGlobal(capacity * itemSize);
        try
        {
            request.NumAdapters = (uint)capacity;
            request.Adapters = buffer;
            if (Call(version3, ref request) < 0)
                return result;
            var count = (int)Math.Min(request.NumAdapters, (uint)capacity);
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<AdapterInfoEntry>(buffer + i * itemSize);
                if (entry.Handle != 0)
                    result.Add(new EnumeratedAdapter(entry.Handle, SystemMetricsLogic.MakeLuid((uint)entry.LuidHigh, entry.LuidLow)));
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Version 2 hat dieselben Felder ohne Filter; sie wird über die Version-3-Struktur ohne Filter aufgerufen (eigene Struktur).
    private static int Call(bool version3, ref EnumAdapters3 request)
    {
        if (version3)
            return D3DKMTEnumAdapters3(ref request);
        var v2 = new EnumAdapters2 { NumAdapters = request.NumAdapters, Adapters = request.Adapters };
        var status = D3DKMTEnumAdapters2(ref v2);
        request.NumAdapters = v2.NumAdapters;
        return status;
    }

    internal static void CloseAdapter(uint handle)
    {
        if (handle == 0)
            return;
        var close = new CloseAdapterArgs { Handle = handle };
        _ = D3DKMTCloseAdapter(ref close);
    }

    /// <summary>Öffnet einen Adapter über seine LUID (Handle mit <see cref="CloseAdapter"/> schließen); 0 bei Fehler.</summary>
    internal static uint OpenAdapterFromLuid(ulong luid)
    {
        var args = new OpenAdapterFromLuidArgs { LuidLow = (uint)(luid & 0xFFFFFFFF), LuidHigh = (int)(uint)(luid >> 32) };
        return D3DKMTOpenAdapterFromLuid(ref args) >= 0 ? args.Handle : 0;
    }

    /// <summary>Statische Eigenschaften eines geöffneten Adapters. Nicht ermittelbare Teile bleiben leer bzw. 0.</summary>
    internal static GraphicsAdapterInfo DescribeAdapter(uint handle, ulong luid)
    {
        var typeData = new AdapterTypeData();
        var haveType = TryQuery(handle, QueryAdapterType, ref typeData);
        var type = typeData.Value;

        uint vendor = 0, device = 0;
        var ids = new QueryDeviceIds { PhysicalAdapterIndex = 0 };
        if (TryQuery(handle, QueryPhysicalAdapterDeviceIds, ref ids))
        {
            vendor = ids.VendorId;
            device = ids.DeviceId;
        }

        var name = QueryString(handle, QueryDriverDescription, DriverDescriptionChars)
                   ?? QueryString(handle, QueryAdapterRegistryInfo, AdapterRegistryInfoChars);

        ulong dedicated = 0, local = 0, nonLocal = 0;
        var seg = new SegmentGroupSizeInfo { PhysicalAdapterIndex = 0 };
        if (TryQuery(handle, QueryGetSegmentGroupSize, ref seg))
        {
            dedicated = seg.LegacyDedicatedVideoMemorySize;
            local = seg.LocalMemory;
            nonLocal = seg.NonLocalMemory;
        }

        return new GraphicsAdapterInfo(
            luid, vendor, device, name,
            RenderSupported: haveType && (type & TypeRenderSupported) != 0,
            DisplaySupported: haveType && (type & TypeDisplaySupported) != 0,
            SoftwareDevice: haveType && (type & TypeSoftwareDevice) != 0,
            ComputeOnly: haveType && (type & TypeComputeOnly) != 0,
            DedicatedVideoMemoryBytes: dedicated,
            LocalMemoryBytes: local,
            NonLocalMemoryBytes: nonLocal);
    }

    /// <summary>Temperatur in °C (nur plausible Werte) eines geöffneten Adapters; null, wenn der Treiber nichts liefert.</summary>
    internal static double? QueryTemperatureC(uint handle)
    {
        var data = new AdapterPerfData { PhysicalAdapterIndex = 0 };
        // Temperature: deci-Celsius (1 = 0,1 °C).
        return TryQuery(handle, QueryAdapterPerfData, ref data) ? SystemMetricsLogic.PlausibleTemperatureC(data.Temperature / 10.0) : null;
    }

    /// <summary>Takt der Engine (Node) 0 in MHz eines geöffneten Adapters; null, wenn der Treiber nichts liefert.</summary>
    internal static double? QueryClockMhz(uint handle)
    {
        var data = new NodePerfData { NodeOrdinal = 0, PhysicalAdapterIndex = 0 };
        // Frequency: Hertz.
        return TryQuery(handle, QueryNodePerfData, ref data) ? SystemMetricsLogic.PlausibleClockMhzFromHz(data.Frequency) : null;
    }

    private static bool TryQuery<T>(uint handle, int type, ref T data) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(data, buffer, false);
            var query = new QueryAdapterInfoArgs { Handle = handle, Type = type, PrivateDriverData = buffer, PrivateDriverDataSize = (uint)size };
            if (D3DKMTQueryAdapterInfo(ref query) < 0)
                return false;
            data = Marshal.PtrToStructure<T>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Fragt einen Puffer mit WCHAR-Text am Anfang ab (Driver Description bzw. AdapterString der Registry-Info).
    private static string? QueryString(uint handle, int type, int chars)
    {
        var bytes = chars * sizeof(char);
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            for (var i = 0; i < bytes; i += sizeof(long))
                Marshal.WriteInt64(buffer, i, 0);
            var query = new QueryAdapterInfoArgs { Handle = handle, Type = type, PrivateDriverData = buffer, PrivateDriverDataSize = (uint)bytes };
            if (D3DKMTQueryAdapterInfo(ref query) < 0)
                return null;
            // Höchstens bis zum ersten Nullzeichen innerhalb der ersten MAX_PATH Zeichen (Registry-Info) bzw. des Puffers lesen.
            var limit = type == QueryAdapterRegistryInfo ? MaxPath : chars;
            var text = Marshal.PtrToStringUni(buffer, limit);
            var end = text.IndexOf('\0');
            if (end >= 0)
                text = text[..end];
            text = text.Trim();
            return text.Length == 0 ? null : text;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // D3DKMT_ADAPTERTYPE (Union aus Bitfeld und UINT Value)
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterTypeData
    {
        public uint Value;
    }

    // D3DKMT_ENUMADAPTERS3: Filter (D3DKMT_ENUMADAPTERS_FILTER, ULONGLONG-Bits), NumAdapters, pAdapters
    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters3
    {
        public ulong Filter;
        public uint NumAdapters;
        public IntPtr Adapters;
    }

    // D3DKMT_ENUMADAPTERS2
    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2
    {
        public uint NumAdapters;
        public IntPtr Adapters;
    }

    // D3DKMT_ADAPTERINFO: hAdapter, LUID (Low, High), NumOfSources, bPrecisePresentRegionsPreferred
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfoEntry
    {
        public uint Handle;
        public uint LuidLow;
        public int LuidHigh;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    // D3DKMT_CLOSEADAPTER
    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapterArgs
    {
        public uint Handle;
    }

    // D3DKMT_OPENADAPTERFROMLUID: LUID, hAdapter
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapterFromLuidArgs
    {
        public uint LuidLow;
        public int LuidHigh;
        public uint Handle;
    }

    // D3DKMT_QUERYADAPTERINFO: hAdapter, Type, pPrivateDriverData, PrivateDriverDataSize
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfoArgs
    {
        public uint Handle;
        public int Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    // D3DKMT_QUERY_DEVICE_IDS: PhysicalAdapterIndex (in), D3DKMT_DEVICE_IDS (out)
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryDeviceIds
    {
        public uint PhysicalAdapterIndex;
        public uint VendorId;
        public uint DeviceId;
        public uint SubVendorId;
        public uint SubSystemId;
        public uint RevisionId;
        public uint BusType;
    }

    // D3DKMT_SEGMENTGROUPSIZEINFO (D3DKMT_SEGMENTSIZEINFO LegacyInfo: 3 x ULONGLONG)
    [StructLayout(LayoutKind.Sequential)]
    private struct SegmentGroupSizeInfo
    {
        public uint PhysicalAdapterIndex;
        public ulong LegacyDedicatedVideoMemorySize;
        public ulong LegacyDedicatedSystemMemorySize;
        public ulong LegacySharedSystemMemorySize;
        public ulong LocalMemory;
        public ulong NonLocalMemory;
        public ulong NonBudgetMemory;
    }

    // D3DKMT_ADAPTER_PERFDATA
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PcieBandwidth;
        public uint FanRpm;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    // D3DKMT_NODE_PERFDATA
    [StructLayout(LayoutKind.Sequential)]
    private struct NodePerfData
    {
        public uint NodeOrdinal;
        public uint PhysicalAdapterIndex;
        public ulong Frequency;
        public ulong MaxFrequency;
        public ulong MaxFrequencyOC;
        public uint Voltage;
        public uint VoltageMax;
        public uint VoltageMaxOC;
        public ulong MaxTransitionLatency;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters3(ref EnumAdapters3 args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfoArgs args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref CloseAdapterArgs args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuidArgs args);
}
