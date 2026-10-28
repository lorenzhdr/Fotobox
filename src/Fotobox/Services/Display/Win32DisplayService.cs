using System.Globalization;
using System.Runtime.InteropServices;
using Fotobox.Core;

namespace Fotobox.Services.Display;

/// <summary>
/// Monitorliste über Win32 (<c>EnumDisplayMonitors</c>, <c>EnumDisplaySettings</c>, <c>QueryDisplayConfig</c>), ohne WinForms.
/// Die Id ist der Geräte-Pfad des Monitors (stabil über Neustarts und unterscheidet baugleiche Modelle), Fallback der GDI-Name.
/// Wirft nie: Fehler werden geloggt und ergeben eine leere bzw. unvollständige Liste.
/// </summary>
public sealed class Win32DisplayService : IDisplayService
{
    private const string UnknownMonitorName = "Monitor";
    private const string InternalMonitorName = "Integrierter Bildschirm";

    private readonly Func<List<GdiMonitor>> _enumerateGdi;
    private readonly Func<Dictionary<string, TargetInfo>> _queryTargets;

    private readonly object _logGate = new();
    private string? _lastSignature;

    public Win32DisplayService()
        : this(EnumerateGdiMonitors, QueryTargets)
    {
    }

    /// <summary>Für Tests: Quellen der GDI-Monitore und der DisplayConfig-Zuordnung austauschbar.</summary>
    internal Win32DisplayService(Func<List<GdiMonitor>> enumerateGdi, Func<Dictionary<string, TargetInfo>> queryTargets)
    {
        _enumerateGdi = enumerateGdi;
        _queryTargets = queryTargets;
    }

    // Letzte erfolgreiche Zuordnung GDI-Name -> (Id, Name); GetMonitors kann aus mehreren Threads aufgerufen werden.
    private readonly object _knownGate = new();
    private Dictionary<string, KnownMonitor> _known = new(StringComparer.OrdinalIgnoreCase);

    private sealed record KnownMonitor(string Id, string Name);

    private Dictionary<string, KnownMonitor> SnapshotKnown()
    {
        lock (_knownGate)
            return new Dictionary<string, KnownMonitor>(_known, StringComparer.OrdinalIgnoreCase);
    }

    private void RememberKnown(Dictionary<string, KnownMonitor> fresh)
    {
        lock (_knownGate)
            _known = fresh;
    }

    public IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        try
        {
            var gdiMonitors = _enumerateGdi();

            Dictionary<string, TargetInfo> targets;
            try
            {
                targets = _queryTargets();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Monitornamen konnten nicht ermittelt werden (DisplayConfig); es werden die zuletzt bekannten bzw. Ersatznamen verwendet.", ex);
                targets = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
            }

            // Scheitert DisplayConfig ganz oder nur für einzelne Pfade, würden die Ids auf GDI-Namen zurückfallen; die gespeicherte
            // Id passte dann nicht mehr und die Gastansicht spränge auf den Hauptbildschirm. Daher je GDI-Name ohne Geräte-Pfad
            // die letzte erfolgreiche Zuordnung GDI-Name -> (Id, Name) verwenden; nur unbekannte GDI-Namen bekommen den GDI-Namen als Id.
            var known = SnapshotKnown();

            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<DisplayMonitor>(gdiMonitors.Count);
            var fresh = new Dictionary<string, KnownMonitor>(StringComparer.OrdinalIgnoreCase);
            var running = 0;
            foreach (var g in gdiMonitors)
            {
                running++;
                targets.TryGetValue(g.GdiName, out var target);

                var hasDevicePath = !string.IsNullOrWhiteSpace(target?.DevicePath)
                    && target.DevicePath.Length <= SettingsLimits.MonitorIdMaxLength;
                known.TryGetValue(g.GdiName, out var cached);

                string id;
                string name;
                if (!hasDevicePath && cached is not null)
                {
                    id = cached.Id;
                    name = !string.IsNullOrWhiteSpace(target?.FriendlyName) ? target.FriendlyName.Trim() : cached.Name;
                }
                else
                {
                    id = hasDevicePath ? target!.DevicePath! : g.GdiName;
                    name = !string.IsNullOrWhiteSpace(target?.FriendlyName)
                        ? target.FriendlyName.Trim()
                        : target is not null && IsInternalTechnology(target.OutputTechnology) ? InternalMonitorName : UnknownMonitorName;
                }

                var reliableId = hasDevicePath || cached is not null;
                if (!usedIds.Add(id))
                {
                    id = g.GdiName;
                    usedIds.Add(id);
                    reliableId = false;
                }
                // Nur echte Geräte-Pfade merken; ein GDI-Name als Ersatz-Id überschreibt keine gute Zuordnung.
                if (reliableId)
                    fresh[g.GdiName] = new KnownMonitor(id, name);

                var (pixelWidth, pixelHeight) = GetCurrentResolution(g.GdiName, g.Bounds);
                result.Add(new DisplayMonitor(id, name, ParseNumber(g.GdiName, running), g.IsPrimary, pixelWidth, pixelHeight, g.Bounds));
            }

            // Eine leere Aufzählung (vorübergehender Fehler) löscht die bekannte Zuordnung nicht.
            if (gdiMonitors.Count > 0)
                RememberKnown(fresh);

            result.Sort((a, b) => a.Number.CompareTo(b.Number));
            LogIfChanged(result);
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Monitore konnten nicht ermittelt werden.", ex);
            return [];
        }
    }

    private void LogIfChanged(List<DisplayMonitor> monitors)
    {
        try
        {
            var signature = string.Join("|", monitors.Select(m => m.Id));
            lock (_logGate)
            {
                if (signature == _lastSignature)
                    return;
                _lastSignature = signature;
            }
            AppLog.Info(string.Create(CultureInfo.InvariantCulture, $"Monitore erkannt: {monitors.Count}."));
        }
        catch
        {
            // Logging darf nie stören.
        }
    }

    internal sealed record GdiMonitor(string GdiName, MonitorRect Bounds, bool IsPrimary);

    internal sealed record TargetInfo(string? FriendlyName, string? DevicePath, uint OutputTechnology);

    private static List<GdiMonitor> EnumerateGdiMonitors()
    {
        var list = new List<GdiMonitor>();
        Native.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            try
            {
                var info = DisplayNative.MonitorInfoEx.Create();
                if (DisplayNative.GetMonitorInfo(hMonitor, ref info) && !string.IsNullOrWhiteSpace(info.DeviceName))
                {
                    var r = info.Monitor;
                    list.Add(new GdiMonitor(
                        info.DeviceName.Trim(),
                        new MonitorRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                        (info.Flags & DisplayNative.MonitorInfoFPrimary) != 0));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ein Monitor konnte nicht gelesen werden.", ex);
            }
            return true; // weiter aufzählen
        };

        if (!Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            AppLog.Warn("EnumDisplayMonitors ist fehlgeschlagen.");
        GC.KeepAlive(callback);
        return list;
    }

    /// <summary>Echte Auflösung des Monitors (<c>ENUM_CURRENT_SETTINGS</c>), Fallback die Größe des Monitorrechtecks.</summary>
    private static (int Width, int Height) GetCurrentResolution(string gdiName, MonitorRect bounds)
    {
        try
        {
            var mode = Native.DevMode.Create();
            if (Native.EnumDisplaySettings(gdiName, Native.EnumCurrentSettings, ref mode)
                && mode.PelsWidth > 0 && mode.PelsHeight > 0
                && mode.PelsWidth <= int.MaxValue && mode.PelsHeight <= int.MaxValue)
            {
                return ((int)mode.PelsWidth, (int)mode.PelsHeight);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Die Auflösung eines Monitors konnte nicht gelesen werden.", ex);
        }
        return (bounds.Width, bounds.Height);
    }

    /// <summary>Ordnet je aktivem Pfad den GDI-Namen (Source) dem Monitor (Target) zu; bei Klonmodus gewinnt das erste Target.</summary>
    private static Dictionary<string, TargetInfo> QueryTargets()
    {
        var map = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);

        Native.PathInfo[] paths = [];
        var numPaths = 0u;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var rc = Native.GetDisplayConfigBufferSizes(Native.QdcOnlyActivePaths, out numPaths, out var numModes);
            if (rc != 0)
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes: Fehler {rc}.");

            paths = new Native.PathInfo[numPaths];
            var modes = new Native.ModeInfo[numModes];
            rc = Native.QueryDisplayConfig(Native.QdcOnlyActivePaths, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (rc == 0)
                break;
            if (rc != Native.ErrorInsufficientBuffer || attempt == 4)
                throw new InvalidOperationException($"QueryDisplayConfig: Fehler {rc}.");
        }

        for (var i = 0; i < numPaths && i < paths.Length; i++)
        {
            try
            {
                var path = paths[i];

                var source = new Native.SourceDeviceName
                {
                    Header = Native.Header.For(Native.DeviceInfoGetSourceName, Marshal.SizeOf<Native.SourceDeviceName>(),
                        path.SourceAdapterId, path.SourceId),
                    ViewGdiDeviceName = "",
                };
                if (Native.DisplayConfigGetDeviceInfo(ref source) != 0 || string.IsNullOrWhiteSpace(source.ViewGdiDeviceName))
                    continue;
                var gdiName = source.ViewGdiDeviceName.Trim();
                if (map.ContainsKey(gdiName))
                    continue; // Klonmodus: erstes Target gewinnt

                var target = new Native.TargetDeviceName
                {
                    Header = Native.Header.For(Native.DeviceInfoGetTargetName, Marshal.SizeOf<Native.TargetDeviceName>(),
                        path.TargetAdapterId, path.TargetId),
                    MonitorFriendlyDeviceName = "",
                    MonitorDevicePath = "",
                };
                if (Native.DisplayConfigGetDeviceInfo(ref target) != 0)
                {
                    map[gdiName] = new TargetInfo(null, null, path.TargetOutputTechnology);
                    continue;
                }

                map[gdiName] = new TargetInfo(target.MonitorFriendlyDeviceName, target.MonitorDevicePath, target.OutputTechnology);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ein Monitorpfad konnte nicht gelesen werden.", ex);
            }
        }
        return map;
    }

    private static bool IsInternalTechnology(uint technology) =>
        technology is Native.OutputTechnologyInternal
            or Native.OutputTechnologyLvds
            or Native.OutputTechnologyDisplayPortEmbedded
            or Native.OutputTechnologyUdiEmbedded;

    /// <summary>Zahl-Suffix aus <c>\\.\DISPLAYn</c>; sonst die laufende Nummer.</summary>
    private static int ParseNumber(string gdiName, int fallback)
    {
        const string marker = "DISPLAY";
        var idx = gdiName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0
            && int.TryParse(gdiName.AsSpan(idx + marker.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n > 0)
        {
            return n;
        }
        return fallback;
    }

    /// <summary>Win32-Strukturen und -Aufrufe, die nur dieser Dienst braucht (Layouts nach wingdi.h; alle Zeigerfelder sind <see cref="IntPtr"/> bzw. fehlen).</summary>
    internal static class Native
    {
        public const uint QdcOnlyActivePaths = 0x00000002;
        public const int ErrorInsufficientBuffer = 122;
        public const int EnumCurrentSettings = -1;

        public const uint DeviceInfoGetSourceName = 1;
        public const uint DeviceInfoGetTargetName = 2;

        public const uint OutputTechnologyLvds = 6;
        public const uint OutputTechnologyDisplayPortEmbedded = 11;
        public const uint OutputTechnologyUdiEmbedded = 13;
        public const uint OutputTechnologyInternal = 0x80000000;

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        /// <summary>DISPLAYCONFIG_PATH_INFO (72 Byte): Source (20) + Target (48) + flags (4). Unions als einfache Felder.</summary>
        [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct PathInfo
        {
            // DISPLAYCONFIG_PATH_SOURCE_INFO
            public Luid SourceAdapterId;
            public uint SourceId;
            public uint SourceModeInfoIdx;
            public uint SourceStatusFlags;

            // DISPLAYCONFIG_PATH_TARGET_INFO
            public Luid TargetAdapterId;
            public uint TargetId;
            public uint TargetModeInfoIdx;
            public uint TargetOutputTechnology;
            public uint TargetRotation;
            public uint TargetScaling;
            public uint RefreshRateNumerator;
            public uint RefreshRateDenominator;
            public uint TargetScanLineOrdering;
            public int TargetAvailable;
            public uint TargetStatusFlags;

            public uint Flags;
        }

        /// <summary>DISPLAYCONFIG_MODE_INFO (64 Byte, die Union wird nicht gelesen und nur als Platz reserviert).</summary>
        [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 64)]
        public struct ModeInfo
        {
            public uint InfoType;
            public uint Id;
            public Luid AdapterId;
        }

        /// <summary>DISPLAYCONFIG_DEVICE_INFO_HEADER (20 Byte).</summary>
        [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct Header
        {
            public uint Type;
            public uint Size;
            public Luid AdapterId;
            public uint Id;

            public static Header For(uint type, int size, Luid adapterId, uint id) =>
                new() { Type = type, Size = (uint)size, AdapterId = adapterId, Id = id };
        }

        /// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME (84 Byte).</summary>
        [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SourceDeviceName
        {
            public Header Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string ViewGdiDeviceName;
        }

        /// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME (420 Byte).</summary>
        [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TargetDeviceName
        {
            public Header Header;
            public uint Flags;
            public uint OutputTechnology;
            public ushort EdidManufactureId;
            public ushort EdidProductCodeId;
            public uint ConnectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string MonitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string MonitorDevicePath;
        }

        /// <summary>DEVMODEW (220 Byte); die Union aus Drucker-/Anzeigefeldern ist als vier <see cref="int"/> abgebildet.</summary>
        [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public ushort SpecVersion;
            public ushort DriverVersion;
            public ushort Size;
            public ushort DriverExtra;
            public uint Fields;
            public int UnionA, UnionB, UnionC, UnionD;
            public short Color, Duplex, YResolution, TtOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel;
            public uint PelsWidth;
            public uint PelsHeight;
            public uint DisplayFlags;
            public uint DisplayFrequency;
            public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;

            public static DevMode Create() => new()
            {
                DeviceName = "",
                FormName = "",
                Size = (ushort)Marshal.SizeOf<DevMode>(),
            };
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode mode);

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] PathInfo[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] ModeInfo[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName info);
    }
}
