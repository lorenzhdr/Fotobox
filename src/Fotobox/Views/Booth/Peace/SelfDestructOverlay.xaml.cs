using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Fotobox.Views.Booth.Peace;

/// <summary>
/// Alarm-Darstellung der Selbstzerstörung (rote Warnschrift, Vignette, Blinkrahmen) und Explosion. Nur Darstellung, nimmt keine Eingaben
/// an. Vertrag: Animationen laufen nur, solange das Element sichtbar ist (<see cref="UIElement.IsVisible"/>); beim Ausblenden werden alle
/// angehalten. In den letzten drei Sekunden pulsiert und blinkt alles schneller und stärker.
/// </summary>
public partial class SelfDestructOverlay : UserControl
{
    /// <summary>Verbleibende Sekunden des Alarm-Countdowns.</summary>
    public static readonly DependencyProperty SecondsRemainingProperty = DependencyProperty.Register(
        nameof(SecondsRemaining), typeof(int), typeof(SelfDestructOverlay),
        new PropertyMetadata(0, (d, _) => ((SelfDestructOverlay)d).OnSecondsChanged()));

    /// <summary>Ab so vielen verbleibenden Sekunden läuft der Alarm in der schnellen, intensiven Stufe.</summary>
    private const int FastFromSeconds = 3;

    private bool _running;
    private bool _fast;
    private bool _exploding;

    public SelfDestructOverlay()
    {
        InitializeComponent();
        UpdateTexts();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                StartAlarm();
            else
                StopAll();
        };
        Loaded += (_, _) =>
        {
            if (IsVisible)
                StartAlarm();
        };
    }

    public int SecondsRemaining
    {
        get => (int)GetValue(SecondsRemainingProperty);
        set => SetValue(SecondsRemainingProperty, value);
    }

    /// <summary>Laufen gerade Alarm- oder Explosionsanimationen (für Tests).</summary>
    internal bool AreAnimationsRunning => _running || _exploding;

    // ---- Texte ----

    private void OnSecondsChanged()
    {
        UpdateTexts();
        if (!IsVisible)
            return;

        PulseDigits();
        var fast = SecondsRemaining <= FastFromSeconds;
        if (fast != _fast)
            StartAlarm();
    }

    private void UpdateTexts()
    {
        var seconds = Math.Max(0, SecondsRemaining);
        DigitsText.Text = seconds >= 60 ? $"{seconds / 60:00}:{seconds % 60:00}" : $"00:{seconds:00}";
        SubText.Text = seconds switch
        {
            <= FastFromSeconds => "Alle Systeme überlastet",
            <= 7 => "Kernschmelze steht bevor",
            _ => "Bitte Ruhe bewahren",
        };
    }

    // ---- Alarm ----

    private void StartAlarm()
    {
        if (!IsVisible)
            return;

        _running = true;
        _fast = SecondsRemaining <= FastFromSeconds;

        // Ein Takt: 1 s langsam, 0,5 s schnell. Vignette zwischen 0,25 und 0,6 (schnell: 0,35 bis 0,85).
        var half = TimeSpan.FromMilliseconds(_fast ? 250 : 500);
        Pulsate(Vignette, UIElement.OpacityProperty, _fast ? 0.35 : 0.25, _fast ? 0.85 : 0.6, half);

        // Rahmen blinkt hart zwischen hell und dunkel.
        Blink(Frame, _fast ? 120 : 400, _fast ? 0.1 : 0.15);

        // Warnstreifen und Ziffern flackern leicht.
        Pulsate(StripeTop, UIElement.OpacityProperty, _fast ? 0.35 : 0.65, 1.0, half);
        Pulsate(StripeBottom, UIElement.OpacityProperty, _fast ? 0.35 : 0.65, 1.0, half);
        Pulsate(DigitsText, UIElement.OpacityProperty, _fast ? 0.7 : 0.88, 1.0, half);
        if (_fast)
            Blink(SubText, 160, 0.35);
        else
            SubText.BeginAnimation(OpacityProperty, null);
    }

    private static void Pulsate(UIElement target, DependencyProperty property, double low, double high, TimeSpan half)
    {
        target.BeginAnimation(property, new DoubleAnimation(low, high, new Duration(half))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private static void Blink(UIElement target, double halfMilliseconds, double low)
    {
        var frames = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(low, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(halfMilliseconds))));
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(halfMilliseconds * 2))));
        target.BeginAnimation(UIElement.OpacityProperty, frames);
    }

    private void PulseDigits()
    {
        var animation = new DoubleAnimation(1.10, 1.0, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        DigitsScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        DigitsScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void StopAlarm()
    {
        _running = false;
        Vignette.BeginAnimation(OpacityProperty, null);
        Frame.BeginAnimation(OpacityProperty, null);
        StripeTop.BeginAnimation(OpacityProperty, null);
        StripeBottom.BeginAnimation(OpacityProperty, null);
        DigitsText.BeginAnimation(OpacityProperty, null);
        SubText.BeginAnimation(OpacityProperty, null);
        DigitsScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DigitsScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void StopAll()
    {
        StopAlarm();
        StopExplosion();
    }

    // ---- Explosion ----

    /// <summary>Spielt die Explosion (Blitz, Puls, Glitch, Wackeln, etwa 900 ms).</summary>
    public void PlayExplosion()
    {
        StopExplosion();
        if (!IsVisible)
            return;

        _exploding = true;
        var rng = Random.Shared;

        // Weißer Blitz: sofort voll, dann ausblenden.
        Flash.Visibility = Visibility.Visible;
        var flash = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        flash.Completed += (_, _) => FinishExplosion();
        Flash.BeginAnimation(OpacityProperty, flash);

        // Oranger Puls, der sich über den Bildschirm ausdehnt.
        Pulse.Visibility = Visibility.Visible;
        var grow = new DoubleAnimation(0.15, 2.4, new Duration(TimeSpan.FromMilliseconds(800)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(850))));
        Pulse.BeginAnimation(OpacityProperty, fade);

        // Wackeln: harte Sprünge, die nach und nach kleiner werden.
        AnimateShake(ShakeTransform, TranslateTransform.XProperty, rng, 26);
        AnimateShake(ShakeTransform, TranslateTransform.YProperty, rng, 18);

        // Glitch-Balken für etwa 300 ms.
        AddGlitchBars(rng);
    }

    private static void AnimateShake(Transform transform, DependencyProperty property, Random rng, double amplitude)
    {
        var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        const int steps = 15;
        for (var i = 0; i < steps; i++)
        {
            var decay = 1.0 - (double)i / steps;
            var value = (rng.NextDouble() * 2 - 1) * amplitude * decay;
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 60))));
        }
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(steps * 60))));
        transform.BeginAnimation(property, frames);
    }

    private void AddGlitchBars(Random rng)
    {
        GlitchLayer.Children.Clear();
        var colors = new[]
        {
            Color.FromArgb(0xE0, 0xFF, 0x20, 0x20), Color.FromArgb(0xD0, 0x20, 0xF0, 0xFF), Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0xE0, 0x00, 0x00, 0x00), Color.FromArgb(0xD8, 0xFF, 0xB0, 0x00),
        };
        var height = ActualHeight > 0 ? ActualHeight : 1080;
        var count = rng.Next(6, 9);
        for (var i = 0; i < count; i++)
        {
            var brush = new SolidColorBrush(colors[rng.Next(colors.Length)]);
            brush.Freeze();
            var shift = new TranslateTransform();
            var bar = new Rectangle
            {
                Fill = brush,
                Height = rng.Next(10, 56),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, rng.NextDouble() * Math.Max(1, height - 60), 0, 0),
                RenderTransform = shift,
                Opacity = 0,
            };
            GlitchLayer.Children.Add(bar);

            // Springt alle 50 ms seitlich; sichtbar nur in den ersten 300 ms.
            var offsets = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            var visible = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            for (var step = 0; step < 6; step++)
            {
                var time = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(step * 50));
                offsets.KeyFrames.Add(new DiscreteDoubleKeyFrame((rng.NextDouble() * 2 - 1) * 160, time));
                visible.KeyFrames.Add(new DiscreteDoubleKeyFrame(rng.NextDouble() < 0.8 ? 1.0 : 0.0, time));
            }
            var end = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300));
            offsets.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, end));
            visible.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, end));
            shift.BeginAnimation(TranslateTransform.XProperty, offsets);
            bar.BeginAnimation(OpacityProperty, visible);
        }
    }

    private void FinishExplosion()
    {
        StopExplosion();
    }

    private void StopExplosion()
    {
        _exploding = false;
        Flash.BeginAnimation(OpacityProperty, null);
        Flash.Visibility = Visibility.Collapsed;
        Pulse.BeginAnimation(OpacityProperty, null);
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Pulse.Visibility = Visibility.Collapsed;
        ShakeTransform.BeginAnimation(TranslateTransform.XProperty, null);
        ShakeTransform.BeginAnimation(TranslateTransform.YProperty, null);
        foreach (var bar in GlitchLayer.Children.OfType<UIElement>())
        {
            bar.BeginAnimation(OpacityProperty, null);
            (bar.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.XProperty, null);
        }
        GlitchLayer.Children.Clear();
    }
}
