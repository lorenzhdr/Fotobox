using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Fotobox.Core;
using Microsoft.Win32;

namespace Fotobox.Views.Admin;

/// <summary>
/// Hell/Dunkel für Admin-Fenster und PIN-Dialoge: tauscht die Farbpalette (AdminColors.Light/Dark.xaml) in den
/// Ressourcen des Fensters aus und passt die Windows-Titelleiste an. Alle Admin-Stile beziehen Farben per DynamicResource.
/// </summary>
public static class AdminThemeManager
{
    private const string LightSource = "/Fotobox;component/Views/Admin/AdminColors.Light.xaml";
    private const string DarkSource = "/Fotobox;component/Views/Admin/AdminColors.Dark.xaml";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmUseImmersiveDarkMode = 20;

    /// <summary>Ob das Schema dunkel ist; <see cref="AdminThemeMode.System"/> folgt dem Windows-App-Modus.</summary>
    public static bool IsDark(AdminThemeMode mode) => mode switch
    {
        AdminThemeMode.Dark => true,
        AdminThemeMode.Light => false,
        _ => SystemUsesDarkApps(),
    };

    /// <summary>Windows-Einstellung „App-Modus“: dunkel, wenn AppsUseLightTheme = 0. Fehlt der Wert oder ist er nicht lesbar: hell.</summary>
    public static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Windows-App-Modus nicht lesbar, helles Schema: " + ex.Message);
            return false;
        }
    }

    /// <summary>Wendet das Schema auf das Fenster an (auch mehrfach, z. B. als Live-Vorschau im Admin).</summary>
    public static void Apply(Window window, AdminThemeMode mode)
    {
        var dark = IsDark(mode);
        ApplyPalette(window.Resources, dark);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            SetDarkTitleBar(handle, dark);
        }
        else
        {
            void OnSourceInitialized(object? sender, EventArgs e)
            {
                window.SourceInitialized -= OnSourceInitialized;
                SetDarkTitleBar(new WindowInteropHelper(window).Handle, IsDarkPalette(window.Resources));
            }
            window.SourceInitialized += OnSourceInitialized;
        }
    }

    /// <summary>Ersetzt die gemergte Palette (oder fügt sie vorn ein, falls keine vorhanden ist).</summary>
    private static void ApplyPalette(ResourceDictionary resources, bool dark)
    {
        var merged = resources.MergedDictionaries;
        var index = -1;
        for (var i = 0; i < merged.Count; i++)
        {
            if (IsPalette(merged[i]))
            {
                if (IsDarkSource(merged[i]) == dark)
                    return;
                index = i;
                break;
            }
        }

        var palette = new ResourceDictionary { Source = new Uri(dark ? DarkSource : LightSource, UriKind.Relative) };
        if (index >= 0)
            merged[index] = palette;
        else
            merged.Insert(0, palette);
    }

    /// <summary>Ob in den Ressourcen die dunkle Palette gemergt ist.</summary>
    public static bool IsDarkPalette(ResourceDictionary resources)
    {
        foreach (var dictionary in resources.MergedDictionaries)
        {
            if (IsPalette(dictionary))
                return IsDarkSource(dictionary);
        }
        return false;
    }

    private static bool IsPalette(ResourceDictionary d) =>
        d.Source?.OriginalString.Contains("AdminColors.", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsDarkSource(ResourceDictionary d) =>
        d.Source?.OriginalString.EndsWith("AdminColors.Dark.xaml", StringComparison.OrdinalIgnoreCase) == true;

    private static void SetDarkTitleBar(IntPtr handle, bool dark)
    {
        if (handle == IntPtr.Zero)
            return;
        try
        {
            var value = dark ? 1 : 0;
            // Ab Windows 10 20H1 bzw. Windows 11; ältere Versionen melden einen Fehler, der hier keine Rolle spielt.
            _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Titelleiste nicht umstellbar: " + ex.Message);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
