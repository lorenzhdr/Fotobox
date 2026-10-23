namespace Fotobox.Core;

/// <summary>Abbildung zwischen der Gast-Layoutwahl (<see cref="GuestLayout"/>) und den Layout-Einstellungen.</summary>
public static class GuestLayouts
{
    /// <summary>Die zu den Layout-Einstellungen passende Variante.</summary>
    public static GuestLayout From(LayoutSettings l) => l.Kind switch
    {
        LayoutKind.Overlay => GuestLayout.Overlay,
        LayoutKind.Strip => l.StripStyle == StripStyle.Single ? GuestLayout.StripSingle : GuestLayout.StripDouble,
        _ => GuestLayout.Single,
    };

    /// <summary>Setzt <see cref="LayoutSettings.Kind"/> und bei Streifen <see cref="LayoutSettings.StripStyle"/>; alles andere bleibt unverändert.</summary>
    public static void ApplyTo(LayoutSettings l, GuestLayout g)
    {
        switch (g)
        {
            case GuestLayout.StripDouble:
                l.Kind = LayoutKind.Strip;
                l.StripStyle = StripStyle.DoubleCut;
                break;
            case GuestLayout.StripSingle:
                l.Kind = LayoutKind.Strip;
                l.StripStyle = StripStyle.Single;
                break;
            case GuestLayout.Overlay:
                l.Kind = LayoutKind.Overlay;
                break;
            default:
                l.Kind = LayoutKind.SingleBorderless;
                break;
        }
    }

    /// <summary>Anzeigename für Gast und Admin; <paramref name="stripPhotoCount"/> ist die Fotozahl der Streifen.</summary>
    public static string DisplayName(GuestLayout g, int stripPhotoCount) => g switch
    {
        GuestLayout.StripDouble => $"{stripPhotoCount} Fotos – 2 Streifen",
        GuestLayout.StripSingle => $"{stripPhotoCount} Fotos – 1 Streifen",
        GuestLayout.Overlay => "Foto mit Rahmen",
        _ => "Ein Foto",
    };
}
