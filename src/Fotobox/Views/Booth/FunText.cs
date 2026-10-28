using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Fotobox.Core;

namespace Fotobox.Views.Booth;

/// <summary>
/// Der Text der Zielerfassung („Hey du …“ / „Hey ihr …“) in der Gastansicht, in zwei Darstellungen: HUD (Monospace in der Boxfarbe auf
/// dunklem Feld mit Eckklammern, Buchstabe für Buchstabe getippt mit blinkendem Cursor) oder als gewohnter Hinweis
/// (<see cref="GuestHint"/> in dessen Stil). Getippt wird nur beim Einblenden (<see cref="IsShown"/> false → true); ändert sich der
/// Text bei sichtbarem, fertig getipptem Feld (du ↔ ihr, andere Anzahl), wird er ohne neuen Tipp-Effekt ausgetauscht. Ein- und
/// Ausblenden sind kurz weich; der Text bleibt dabei stehen, auch wenn <see cref="Text"/> schon leer ist. Die Fläche ist wie bei
/// den Hinweisen auf 1920 × 1080 ausgelegt; das Control nimmt keine Eingaben an.
/// </summary>
public sealed class FunText : UserControl
{
    private static readonly TimeSpan TypeInterval = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(260);
    // Der Cursor wechselt alle 10 Takte (350 ms).
    private const int CursorToggleTicks = 10;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FunText),
        new PropertyMetadata(string.Empty, (d, e) => ((FunText)d).OnTextChanged((string?)e.NewValue ?? "")));

    public static readonly DependencyProperty IsShownProperty = DependencyProperty.Register(
        nameof(IsShown), typeof(bool), typeof(FunText),
        new PropertyMetadata(false, (d, e) => ((FunText)d).OnShownChanged((bool)e.NewValue)));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(FunTextPosition), typeof(FunText),
        new PropertyMetadata(FunTextPosition.Center, (d, _) => ((FunText)d).ApplyPlacement()));

    public static readonly DependencyProperty TextStyleProperty = DependencyProperty.Register(
        nameof(TextStyle), typeof(FunTextStyle), typeof(FunText),
        new PropertyMetadata(FunTextStyle.Hud, (d, _) => ((FunText)d).ApplyStyle()));

    public static readonly DependencyProperty HintStyleProperty = DependencyProperty.Register(
        nameof(HintStyle), typeof(HintStyle), typeof(FunText),
        new PropertyMetadata(HintStyle.Pill, (d, e) => ((FunText)d)._hint.HintStyle = (HintStyle)e.NewValue));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(TargetColor), typeof(FunText),
        new PropertyMetadata(TargetColor.Red, (d, _) => ((FunText)d).ApplyColor()));

    private readonly GuestHint _hint = new() { Visibility = Visibility.Collapsed };
    private readonly Grid _hud = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _ghost = NewTextBlock();
    private readonly TextBlock _typedBlock = NewTextBlock();
    private readonly Run _typedRun = new();
    private readonly Run _cursorRun = new("▌");
    private readonly List<Path> _brackets = [];
    private readonly DispatcherTimer _timer;

    private Brush _colorBrush = Brushes.Red;
    private string _fullText = "";
    private int _typed;
    private bool _typing;
    private int _ticks;
    private bool _cursorOn = true;
    private int _fadeVersion;

    public FunText()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Visibility = Visibility.Collapsed;
        Opacity = 0;

        _typedBlock.Inlines.Add(_typedRun);
        _typedBlock.Inlines.Add(_cursorRun);

        var background = new Border
        {
            Background = new SolidColorBrush(Color_(0xB8, 0x07, 0x0B, 0x10)),
            CornerRadius = new CornerRadius(2),
        };
        _hud.Children.Add(background);
        // Der unsichtbare Volltext legt die Feldgröße von Anfang an fest; der getippte Text liegt deckungsgleich darüber.
        var textHost = new Grid { Margin = new Thickness(48, 28, 48, 28) };
        textHost.Children.Add(_ghost);
        textHost.Children.Add(_typedBlock);
        _hud.Children.Add(textHost);
        AddBracket(HorizontalAlignment.Left, VerticalAlignment.Top, 1, 1);
        AddBracket(HorizontalAlignment.Right, VerticalAlignment.Top, -1, 1);
        AddBracket(HorizontalAlignment.Left, VerticalAlignment.Bottom, 1, -1);
        AddBracket(HorizontalAlignment.Right, VerticalAlignment.Bottom, -1, -1);

        var root = new Grid { ClipToBounds = false };
        root.Children.Add(_hud);
        root.Children.Add(_hint);
        Content = root;

        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TypeInterval };
        _timer.Tick += (_, _) => OnTimerTick();

        ApplyColor();
        ApplyStyle();
        ApplyPlacement();
        IsVisibleChanged += (_, e) =>
        {
            // Nicht sichtbar (Fenster oder Eltern eingeklappt): Timer aus.
            if (!(bool)e.NewValue)
                _timer.Stop();
            else if (IsShown && TextStyle == FunTextStyle.Hud)
                _timer.Start();
        };
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsShown { get => (bool)GetValue(IsShownProperty); set => SetValue(IsShownProperty, value); }
    public FunTextPosition Position { get => (FunTextPosition)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public FunTextStyle TextStyle { get => (FunTextStyle)GetValue(TextStyleProperty); set => SetValue(TextStyleProperty, value); }
    public HintStyle HintStyle { get => (HintStyle)GetValue(HintStyleProperty); set => SetValue(HintStyleProperty, value); }
    public TargetColor Color { get => (TargetColor)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    /// <summary>Bisher getippter Teil des Texts (für Tests).</summary>
    internal string DisplayedText => _fullText[..Math.Min(_typed, _fullText.Length)];

    /// <summary>Der Tipp-Effekt läuft noch (für Tests).</summary>
    internal bool IsTyping => _typing;

    /// <summary>Der gesamte Text ist zu sehen; beendet den Tipp-Effekt sofort (für Tests und Standbilder).</summary>
    internal void FinishTyping()
    {
        _typed = _fullText.Length;
        _typing = false;
        UpdateTyped();
    }

    /// <summary>Beendet das Ein-/Ausblenden sofort (für Tests und Standbilder).</summary>
    internal void FinishFade()
    {
        _fadeVersion++;
        BeginAnimation(OpacityProperty, null);
        if (IsShown)
        {
            Visibility = Visibility.Visible;
            Opacity = 1;
        }
        else
        {
            Opacity = 0;
            Visibility = Visibility.Collapsed;
        }
    }

    internal bool HudVisible => _hud.Visibility == Visibility.Visible;
    internal bool HintVisible => _hint.Visibility == Visibility.Visible;
    internal GuestHint Hint => _hint;

    // ---- Zustand

    private void OnShownChanged(bool shown)
    {
        var version = ++_fadeVersion;
        if (shown)
        {
            _fullText = Text ?? "";
            _typed = 0;
            _typing = true;
            _ticks = 0;
            _cursorOn = true;
            SyncText();
            Visibility = Visibility.Visible;
            BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, FadeIn) { FillBehavior = FillBehavior.HoldEnd });
            if (TextStyle == FunTextStyle.Hud && IsVisible)
                _timer.Start();
            return;
        }

        // Ausblenden: der Text bleibt bis zum Ende stehen, getippt wird nicht weiter.
        _timer.Stop();
        _typed = _fullText.Length;
        _typing = false;
        UpdateTyped();
        var fade = new DoubleAnimation(Opacity, 0, FadeOut) { FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) =>
        {
            if (version == _fadeVersion && !IsShown)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                Visibility = Visibility.Collapsed;
            }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void OnTextChanged(string text)
    {
        // Leer = die Zielerfassung blendet gerade aus: den alten Text stehen lassen.
        if (text.Length == 0)
            return;

        var finished = !_typing;
        _fullText = text;
        if (!IsShown)
            return;

        if (finished)
            _typed = text.Length; // Austausch bei sichtbarem, fertig getipptem Feld: ohne neuen Tipp-Effekt
        else
            _typed = Math.Min(_typed, text.Length);
        SyncText();
    }

    private void OnTimerTick()
    {
        _ticks++;
        if (_ticks % CursorToggleTicks == 0)
            _cursorOn = !_cursorOn;

        if (_typing && _fullText.Length > 0)
        {
            _typed = Math.Min(_typed + 1, _fullText.Length);
            if (_typed >= _fullText.Length)
                _typing = false;
        }
        UpdateTyped();
    }

    private void SyncText()
    {
        _ghost.Text = _fullText;
        _hint.Text = _fullText;
        UpdateTyped();
    }

    private void UpdateTyped()
    {
        _typedRun.Text = DisplayedText;
        _cursorRun.Foreground = _cursorOn ? _colorBrush : Brushes.Transparent;
    }

    // ---- Darstellung

    private void ApplyColor()
    {
        var brush = new SolidColorBrush(TargetVisual.ResolveColor(Color));
        brush.Freeze();
        _colorBrush = brush;
        _typedBlock.Foreground = brush;
        foreach (var bracket in _brackets)
            bracket.Stroke = brush;
        UpdateTyped();
    }

    private void ApplyStyle()
    {
        var hud = TextStyle == FunTextStyle.Hud;
        _hud.Visibility = hud ? Visibility.Visible : Visibility.Collapsed;
        _hint.Visibility = hud ? Visibility.Collapsed : Visibility.Visible;
        if (!hud)
            _timer.Stop();
        else if (IsShown && IsVisible)
            _timer.Start();
    }

    private void ApplyPlacement()
    {
        switch (Position)
        {
            case FunTextPosition.Bottom:
                _hud.VerticalAlignment = VerticalAlignment.Bottom;
                _hud.Margin = new Thickness(0, 0, 0, 90);
                _hint.Position = HintPosition.Bottom;
                _hint.VerticalAlignment = VerticalAlignment.Stretch;
                _hint.Margin = new Thickness(0);
                break;
            case FunTextPosition.Top:
                _hud.VerticalAlignment = VerticalAlignment.Top;
                _hud.Margin = new Thickness(0, 60, 0, 0);
                _hint.Position = HintPosition.Top;
                _hint.VerticalAlignment = VerticalAlignment.Stretch;
                _hint.Margin = new Thickness(0);
                break;
            default:
                _hud.VerticalAlignment = VerticalAlignment.Center;
                _hud.Margin = new Thickness(0);
                // Der Hinweis lässt unten 80 Pixel Rand; der obere Rand gleicht das aus, damit der Hinweis mittig sitzt.
                _hint.Position = HintPosition.Bottom;
                _hint.VerticalAlignment = VerticalAlignment.Center;
                _hint.Margin = new Thickness(0, 80, 0, 0);
                break;
        }
    }

    private void AddBracket(HorizontalAlignment h, VerticalAlignment v, int dx, int dy)
    {
        const double len = 30;
        // L-Form: von der Kante über die Ecke zur anderen Kante; (dx, dy) zeigt von der Ecke ins Feld.
        // Die Linie liegt eine halbe Strichstärke innerhalb der Fläche, damit nichts abgeschnitten wird.
        const double inset = 2, arm = len - 2 * inset;
        var corner = new Point(dx > 0 ? inset : len - inset, dy > 0 ? inset : len - inset);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(corner.X + dx * arm, corner.Y), false, false);
            ctx.LineTo(corner, true, false);
            ctx.LineTo(new Point(corner.X, corner.Y + dy * arm), true, false);
        }
        geometry.Freeze();
        var path = new Path
        {
            Data = geometry,
            Width = len,
            Height = len,
            StrokeThickness = 4,
            StrokeLineJoin = PenLineJoin.Miter,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Margin = new Thickness(-4),
            IsHitTestVisible = false,
        };
        _brackets.Add(path);
        _hud.Children.Add(path);
    }

    private static TextBlock NewTextBlock() => new()
    {
        FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
        FontSize = 44,
        FontWeight = FontWeights.Bold,
        LineHeight = 60,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        MaxHeight = 120,
        MaxWidth = 1100,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextAlignment = TextAlignment.Left,
        Foreground = Brushes.Transparent,
    };

    private static System.Windows.Media.Color Color_(byte a, byte r, byte g, byte b) =>
        System.Windows.Media.Color.FromArgb(a, r, g, b);
}
