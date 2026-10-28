using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Fotobox.Core;

namespace Fotobox.Views.Booth;

/// <summary>Lage der Hinweiszeile im Bild.</summary>
public enum HintPosition { Bottom, Top }

/// <summary>
/// Hinweis der Gastansicht („Drücke die Leertaste für ein Foto 📸“) in einer von neun Darstellungen (<see cref="HintStyle"/>),
/// dazu die Zusatzzeile zur Layoutwahl (in der Darstellung <see cref="WheelHintStyle"/>). Wird in der Gastansicht und in der Admin-Vorschau verwendet, damit beide gleich aussehen.
/// Die Fläche ist auf 1920 × 1080 Pixel ausgelegt; das Control selbst nimmt keine Eingaben an.
/// </summary>
public sealed partial class GuestHint : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(GuestHint), new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty HintStyleProperty = DependencyProperty.Register(
        nameof(HintStyle), typeof(HintStyle), typeof(GuestHint), new PropertyMetadata(HintStyle.Pill));

    public static readonly DependencyProperty LayoutHintTextProperty = DependencyProperty.Register(
        nameof(LayoutHintText), typeof(string), typeof(GuestHint), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShowLayoutHintProperty = DependencyProperty.Register(
        nameof(ShowLayoutHint), typeof(bool), typeof(GuestHint), new PropertyMetadata(false));

    public static readonly DependencyProperty LayoutHintStyleProperty = DependencyProperty.Register(
        nameof(LayoutHintStyle), typeof(WheelHintStyle), typeof(GuestHint), new PropertyMetadata(WheelHintStyle.Matching));

    /// <summary>Lage in der Gastansicht: <see cref="HintPosition.Bottom"/> (Foto-Zeile) oder <see cref="HintPosition.Top"/> (Video-Zeile).</summary>
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(HintPosition), typeof(GuestHint), new PropertyMetadata(HintPosition.Bottom));

    /// <summary>Tempo der Laufschrift in Pixeln pro Sekunde (bezogen auf die Auslegung 1920 × 1080).</summary>
    internal const double TickerSpeed = 150;

    // Zuletzt gestartete Laufschrift-Animation (Anfang, Ende); gleiche Werte starten sie nicht neu.
    private double _tickerFrom = double.NaN, _tickerTo = double.NaN;

    public GuestHint()
    {
        InitializeComponent();
        TickerText.Text = Text;
        TickerCanvas.IsVisibleChanged += (_, _) => UpdateTicker();
        TickerCanvas.SizeChanged += (_, _) => UpdateTicker();
        TickerText.SizeChanged += (_, _) => UpdateTicker();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var hint = (GuestHint)d;
        // Vor InitializeComponent (Eigenschaften aus XAML werden erst danach gesetzt) gibt es TickerText noch nicht.
        if (hint.TickerText is null)
            return;
        hint.TickerText.Text = (string?)e.NewValue ?? string.Empty;
        hint.UpdateTicker();
    }

    /// <summary>
    /// Startet bzw. stoppt die Laufschrift: Sie läuft nur, solange sie sichtbar ist und Platz hat (Endlosanimation nur im sichtbaren
    /// Zustand, sonst kostet sie Render-Zeit über dem Livebild). Der Text fährt von rechts außerhalb des Bandes bis links hinaus.
    /// </summary>
    private void UpdateTicker()
    {
        var shift = (TranslateTransform)TickerText.RenderTransform;
        var width = TickerCanvas.ActualWidth;
        if (!TickerCanvas.IsVisible || width <= 0 || string.IsNullOrEmpty(TickerText.Text))
        {
            if (!double.IsNaN(_tickerFrom))
            {
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                _tickerFrom = _tickerTo = double.NaN;
            }
            return;
        }

        // DesiredSize statt ActualWidth: Nach einer Textänderung ist ActualWidth noch der alte Wert.
        TickerText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var from = width;
        var to = -Math.Ceiling(TickerText.DesiredSize.Width);
        if (from == _tickerFrom && to == _tickerTo)
            return;

        _tickerFrom = from;
        _tickerTo = to;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(Math.Max(1, (from - to) / TickerSpeed)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        shift.BeginAnimation(TranslateTransform.XProperty, animation);
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

    /// <summary>Darstellung der Zusatzzeile (Standard: passend zum Hinweis-Stil).</summary>
    public WheelHintStyle LayoutHintStyle
    {
        get => (WheelHintStyle)GetValue(LayoutHintStyleProperty);
        set => SetValue(LayoutHintStyleProperty, value);
    }

    public bool ShowLayoutHint
    {
        get => (bool)GetValue(ShowLayoutHintProperty);
        set => SetValue(ShowLayoutHintProperty, value);
    }
}
