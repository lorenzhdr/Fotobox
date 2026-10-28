using System.Windows.Controls;

namespace Fotobox.Views.Booth;

/// <summary>
/// Mausradzeile in einer der vier eigenen Darstellungen (kleine Pille, schlichter Text, Chip, Pfeile). Text, Darstellung und
/// Sichtbarkeit kommen vom <see cref="GuestHint"/>-Vorfahren; bei <c>WheelHintStyle.Matching</c> bleibt das Element leer
/// (dann zeigt <see cref="GuestHint"/> die zum Hinweis-Stil passende Textzeile).
/// </summary>
public sealed partial class WheelHintSlot : UserControl
{
    public WheelHintSlot()
    {
        InitializeComponent();
    }
}
