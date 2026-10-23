using System.Windows;
using System.Windows.Controls;

namespace Fotobox.Views.Admin;

/// <summary>Ganzzahl-Auswahl mit Plus-/Minus-Tasten, begrenzt auf <see cref="Minimum"/>..<see cref="Maximum"/>.</summary>
public partial class NumberStepper : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberStepper),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumberStepper), new PropertyMetadata(0, OnChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumberStepper), new PropertyMetadata(100, OnChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(int), typeof(NumberStepper), new PropertyMetadata(1));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(NumberStepper), new PropertyMetadata(string.Empty, OnChanged));

    public NumberStepper()
    {
        InitializeComponent();
        Refresh();
    }

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>Einheit hinter dem Wert, z. B. „ s“.</summary>
    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((NumberStepper)d).Refresh();

    private void OnDown(object sender, RoutedEventArgs e) => Value = Math.Max(Minimum, Value - Step);

    private void OnUp(object sender, RoutedEventArgs e) => Value = Math.Min(Maximum, Value + Step);

    private void Refresh()
    {
        if (ValueText is null)
            return;
        ValueText.Text = $"{Value}{Suffix}";
        DownButton.IsEnabled = Value > Minimum;
        UpButton.IsEnabled = Value < Maximum;
    }
}
