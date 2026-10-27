using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Fotobox.Core;
using Fotobox.Services.Display;

namespace Fotobox.Views;

/// <summary>Monitor-Abfragen für Fenster (Win32), gemeinsam für Gast- und Admin-Ansicht.</summary>
internal static class WindowMonitor
{
    /// <summary>Monitor- und Arbeitsbereich-Rechteck (Gerätekoordinaten) des Monitors, auf dem das Fenster liegt.</summary>
    public static bool TryGetRects(IntPtr hwnd, out MonitorRect monitor, out MonitorRect work)
    {
        monitor = default;
        work = default;
        if (hwnd == IntPtr.Zero)
            return false;

        var handle = DisplayNative.MonitorFromWindow(hwnd, DisplayNative.MonitorDefaultToNearest);
        if (handle == IntPtr.Zero)
            return false;

        var info = DisplayNative.MonitorInfoEx.Create();
        if (!DisplayNative.GetMonitorInfo(handle, ref info))
            return false;

        monitor = ToRect(info.Monitor);
        work = ToRect(info.Work);
        return true;
    }

    /// <summary>
    /// Größe des Arbeitsbereichs (ohne Taskleiste) des Monitors, auf dem <paramref name="owner"/> liegt, in geräteunabhängigen Einheiten.
    /// Ohne Owner-Handle oder bei einem Fehler <see cref="SystemParameters.WorkArea"/> (Hauptbildschirm).
    /// </summary>
    public static Size GetWorkAreaSize(Window? owner)
    {
        var fallback = new Size(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        try
        {
            if (owner is null)
                return fallback;

            var hwnd = new WindowInteropHelper(owner).Handle;
            if (!TryGetRects(hwnd, out _, out var work) || work.Width <= 0 || work.Height <= 0)
                return fallback;

            var fromDevice = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var size = fromDevice.Transform(new Vector(work.Width, work.Height));
            return new Size(size.X, size.Y);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Arbeitsbereich des Owner-Monitors nicht lesbar, Hauptbildschirm wird verwendet: " + ex.Message);
            return fallback;
        }
    }

    private static MonitorRect ToRect(DisplayNative.Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
