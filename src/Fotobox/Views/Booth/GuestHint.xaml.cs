using System.Windows;
using System.Windows.Controls;
using Fotobox.Core;

namespace Fotobox.Views.Booth;

/// <summary>Lage der Hinweiszeile im Bild.</summary>
public enum HintPosition { Bottom, Top }

/// <summary>
/// Hinweis der Gastansicht („Drücke die Leertaste für ein Foto 📸“) in einer von drei Darstellungen (<see cref="HintStyle"/>),
/// dazu die Zusatzzeile zur Layoutwahl. Wird in der Gastansicht und in der Admin-Vorschau verwendet, damit beide gleich aussehen.
/// Die Fläche ist auf 1920 × 1080 Pixel ausgelegt; das Control selbst nimmt keine Eingaben an.
/// </summary>
public sealed partial class GuestHint : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(GuestHint), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HintStyleProperty = DependencyProperty.Register(
        nameof(HintStyle), typeof(HintStyle), typeof(GuestHint), new PropertyMetadata(HintStyle.Pill));

    public static readonly DependencyProperty LayoutHintTextProperty = DependencyProperty.Register(
        nameof(LayoutHintText), typeof(string), typeof(GuestHint), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShowLayoutHintProperty = DependencyProperty.Register(
        nameof(ShowLayoutHint), typeof(bool), typeof(GuestHint), new PropertyMetadata(false));

    /// <summary>Lage in der Gastansicht: <see cref="HintPosition.Bottom"/> (Foto-Zeile) oder <see cref="HintPosition.Top"/> (Video-Zeile).</summary>
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(HintPosition), typeof(GuestHint), new PropertyMetadata(HintPosition.Bottom));

    public GuestHint()
    {
        InitializeComponent();
    }

    public HintPosition Position
    {
        get => (HintPosition)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>Der Hinweistext (bereits mit eingesetzten Platzhaltern).</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public HintStyle HintStyle
    {
        get => (HintStyle)GetValue(HintStyleProperty);
        set => SetValue(HintStyleProperty, value);
    }

    /// <summary>Text der Zusatzzeile („Mausrad: Layout wechseln · …“).</summary>
    public string LayoutHintText
    {
        get => (string)GetValue(LayoutHintTextProperty);
        set => SetValue(LayoutHintTextProperty, value);
    }

    public bool ShowLayoutHint
    {
        get => (bool)GetValue(ShowLayoutHintProperty);
        set => SetValue(ShowLayoutHintProperty, value);
    }
}
