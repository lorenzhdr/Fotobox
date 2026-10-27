using System.Runtime.InteropServices;

namespace Fotobox.Services.Display;

/// <summary>Gemeinsame Win32-Aufrufe für Monitor-Aufzählung und Fensterplatzierung (ohne WinForms).</summary>
internal static class DisplayNative
{
    public const uint MonitorDefaultToNearest = 2;
    public const uint MonitorInfoFPrimary = 1;

    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    public const int WmDisplayChange = 0x007E;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public static MonitorInfoEx Create() => new() { Size = Marshal.SizeOf<MonitorInfoEx>(), DeviceName = "" };
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
