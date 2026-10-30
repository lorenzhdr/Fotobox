using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Fotobox.Views.Converters;

/// <summary>
/// Ladekreis: wandelt einen Fortschritt 0..1 in die Bogenlinie eines Rings um, im Uhrzeigersinn ab 12 Uhr. Der Bogen liegt mittig auf der
/// Linie eines Rings mit dem Durchmesser <see cref="Diameter"/> (Strichstärke <see cref="Thickness"/> der Linie, die ihn zeichnet), damit
/// ein <c>Path</c> der Größe <c>Diameter × Diameter</c> den Ring genau ausfüllt. Ohne Fortschritt (0, ungültig) ist die Geometrie leer,
/// ab 1 ein voller Kreis; Werte außerhalb werden geklemmt. Kein Fremdpaket: reine WPF-Geometrie.
/// </summary>
[ValueConversion(typeof(double), typeof(Geometry))]
public sealed class RingProgressConverter : IValueConverter
{
    /// <summary>Außendurchmesser des Rings in Pixeln (Breite und Höhe des <c>Path</c>).</summary>
    public double Diameter { get; set; } = 90;

    /// <summary>Strichstärke des Rings in Pixeln; die Bogenlinie liegt um die halbe Stärke nach innen versetzt.</summary>
    public double Thickness { get; set; } = 9;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Alles, was keine Zahl ist (auch ein ungebundener Wert), gilt als „noch nichts“ – ein Bindungsfehler darf nie werfen.
        var progress = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0,
        };
        return CreateGeometry(progress, Diameter, Thickness);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    /// <summary>Bogen für <paramref name="progress"/> (0..1, geklemmt) auf einem Ring mit dem gegebenen Durchmesser und der Strichstärke.</summary>
    public static Geometry CreateGeometry(double progress, double diameter, double thickness)
    {
        // NaN (und alles Ungültige) gilt als „noch nichts“.
        if (double.IsNaN(progress) || diameter <= 0)
            return Geometry.Empty;

        progress = Math.Clamp(progress, 0, 1);
        var radius = Math.Max(0.5, (diameter - Math.Max(0, thickness)) / 2);
        var center = diameter / 2;
        if (progress <= 0)
            return Geometry.Empty;

        if (progress >= 1)
        {
            // Ein Bogen von einem Punkt zu sich selbst wäre leer: der volle Kreis ist eine Ellipse.
            var full = new EllipseGeometry(new Point(center, center), radius, radius);
            full.Freeze();
            return full;
        }

        var angle = progress * 2 * Math.PI;
        var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center, center - radius), isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, isLargeArc: progress > 0.5, SweepDirection.Clockwise,
                isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }
}
