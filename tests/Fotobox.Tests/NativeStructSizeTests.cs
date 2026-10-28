using System.Runtime.InteropServices;
using Fotobox.Services.Display;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Größen der P/Invoke-Strukturen (Win32-Monitoraufzählung). Der Test läuft in der CI auf x64 und lokal auf ARM64
/// und sichert, dass die Strukturen auf beiden Architekturen der Windows-Definition entsprechen.
/// </summary>
public sealed class NativeStructSizeTests
{
    [Theory]
    [InlineData(typeof(DisplayNative.Rect), 16)]               // RECT: 4 × LONG
    [InlineData(typeof(DisplayNative.MonitorInfoEx), 104)]     // MONITORINFOEXW: 4 + 16 + 16 + 4 + 32 WCHAR
    [InlineData(typeof(Win32DisplayService.Native.Luid), 8)]   // LUID: DWORD + LONG
    [InlineData(typeof(Win32DisplayService.Native.PathInfo), 72)]            // DISPLAYCONFIG_PATH_INFO: 20 + 48 + 4
    [InlineData(typeof(Win32DisplayService.Native.ModeInfo), 64)]            // DISPLAYCONFIG_MODE_INFO
    [InlineData(typeof(Win32DisplayService.Native.Header), 20)]              // DISPLAYCONFIG_DEVICE_INFO_HEADER
    [InlineData(typeof(Win32DisplayService.Native.SourceDeviceName), 84)]    // Header 20 + 32 WCHAR
    [InlineData(typeof(Win32DisplayService.Native.TargetDeviceName), 420)]   // Header 20 + 16 + 64 WCHAR + 128 WCHAR
    [InlineData(typeof(Win32DisplayService.Native.DevMode), 220)]            // DEVMODEW
    public void Struct_HasWindowsSize(Type type, int expectedSize)
    {
        Assert.Equal(expectedSize, Marshal.SizeOf(type));
    }
}
