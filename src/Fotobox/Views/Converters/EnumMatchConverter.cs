using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Fotobox.Views.Converters;

/// <summary>
/// Wählkarten (RadioButton) für Enum-Eigenschaften: <c>Convert</c> prüft, ob der Wert dem Parameter entspricht;
/// <c>ConvertBack</c> liefert bei „gewählt“ den Parameter, bei „abgewählt“ <see cref="Binding.DoNothing"/> (die neue Wahl setzt den Wert).
/// Der Parameter kommt per <c>{x:Static}</c>, z. B. <c>ConverterParameter={x:Static core:HintStyle.Neon}</c>.
/// </summary>
[ValueConversion(typeof(Enum), typeof(bool))]
public class EnumMatchConverter : IValueConverter
{
    public virtual object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && value.Equals(parameter);

    public virtual object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}

/// <summary>Wie <see cref="EnumMatchConverter"/>, liefert aber <see cref="Visibility"/> (Visible bei Übereinstimmung, sonst Collapsed).</summary>
[ValueConversion(typeof(Enum), typeof(Visibility))]
public sealed class EnumMatchVisibilityConverter : EnumMatchConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (bool)base.Convert(value, targetType, parameter, culture) ? Visibility.Visible : Visibility.Collapsed;

    public override object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
