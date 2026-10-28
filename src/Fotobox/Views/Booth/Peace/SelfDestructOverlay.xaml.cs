using System.Windows;
using System.Windows.Controls;

namespace Fotobox.Views.Booth.Peace;

/// <summary>
/// Alarm-Darstellung der Selbstzerstörung (rote Warnschrift, Vignette, Blinkrahmen) und Explosion. Nur Darstellung, nimmt keine Eingaben
/// an. Vertrag: Animationen laufen nur, solange das Element sichtbar ist (<see cref="UIElement.IsVisible"/>). Platzhalter: noch leer.
/// </summary>
public partial class SelfDestructOverlay : UserControl
{
    /// <summary>Verbleibende Sekunden des Alarm-Countdowns.</summary>
    public static readonly DependencyProperty SecondsRemainingProperty = DependencyProperty.Register(
        nameof(SecondsRemaining), typeof(int), typeof(SelfDestructOverlay), new PropertyMetadata(0));

    public SelfDestructOverlay()
    {
        InitializeComponent();
    }

    public int SecondsRemaining
    {
        get => (int)GetValue(SecondsRemainingProperty);
        set => SetValue(SecondsRemainingProperty, value);
    }

    /// <summary>Spielt die Explosion (Blitz, Puls, Glitch, Wackeln, etwa 900 ms).</summary>
    public void PlayExplosion()
    {
        // Platzhalter: Animation folgt.
    }
}
