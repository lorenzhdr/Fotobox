using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Views.Booth;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// Sichtprüfung per Bilddatei, kein Test im engeren Sinn: Läuft nur, wenn die Umgebungsvariable <c>FOTOBOX_RENDER_DIR</c> auf einen
/// Ordner zeigt (sonst sofort beendet, auch in der CI). Rendert die Zielboxen (jeder Stil in Rot und Cyan, eingerasteter Endzustand)
/// und den Fun-Text (HUD mittig/unten, Hinweis-Stil) auf einer 1920 × 1080 großen Attrappe der Gastansicht als PNG.
/// Aufruf: <c>FOTOBOX_RENDER_DIR=C:\pfad\render dotnet test --filter "FullyQualifiedName~FunVisualRenderTests"</c>.
/// </summary>
[Collection(WpfCollection.Name)]
public class FunVisualRenderTests
{
    private static string? RenderDir()
    {
        var dir = Environment.GetEnvironmentVariable("FOTOBOX_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SavePng(Visual visual, string path)
    {
        var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Angedeutete Person (Kopf und Schultern) als helle Flächen auf dem dunklen Bild.</summary>
    private static void AddPerson(Grid stage, double centerX, double headTop, double scale)
    {
        var fill = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
        stage.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 210 * scale, Height = 250 * scale, Fill = fill,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(centerX - 105 * scale, headTop, 0, 0),
        });
        stage.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 640 * scale, Height = 560 * scale, Fill = fill,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(centerX - 320 * scale, headTop + 270 * scale, 0, 0),
        });
    }

    private static Grid Stage(FunSnapshot snapshot, TargetBoxStyle style, TargetColor color, FunText? text)
    {
        var stage = new Grid
        {
            Width = 1920, Height = 1080, ClipToBounds = true,
            Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x3D, 0x45), Color.FromRgb(0x1C, 0x1E, 0x24), 90),
        };
        AddPerson(stage, 560, 230, 1.0);
        AddPerson(stage, 1380, 290, 0.85);
        stage.Children.Add(new TargetOverlay
        {
            ImageWidth = 1920, ImageHeight = 1080, BoxStyle = style, BoxColor = color, Snapshot = snapshot,
        });
        stage.Children.Add(new Border
        {
            Height = 320, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xB0, 0, 0, 0), 90),
        });
        if (text is not null)
            stage.Children.Add(text);
        return stage;
    }

    /// <summary>Zwei eingerastete Ziele (Oberkörper-Boxen um die beiden Personen), links "TARGET 1", rechts "TARGET 2".</summary>
    private static FunSnapshot TwoTargets(bool withText = false, string text = "")
    {
        FunTarget[] targets =
        [
            new(1, new NormRect(0.18, 0.15, 0.22, 0.5), "TARGET 1", false, default),
            new(2, new NormRect(0.6, 0.23, 0.2, 0.43), "TARGET 2", false, default),
        ];
        return new FunSnapshot(targets, withText ? FunPhase.ShowingText : FunPhase.Watching, withText, text, 2);
    }

    private static void Render(string path, Grid stage)
    {
        // In einem (außerhalb des Bildschirms) angezeigten Fenster rendern, wie bei den Hinweis-Bildern.
        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, Content = stage,
        };
        try
        {
            window.Show();
            stage.Measure(new Size(1920, 1080));
            stage.Arrange(new Rect(0, 0, 1920, 1080));
            stage.UpdateLayout();
            foreach (var text in stage.Children.OfType<FunText>())
            {
                text.FinishTyping();
                text.FinishFade();
            }
            stage.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            SavePng(stage, path);
        }
        finally
        {
            window.Close();
        }
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public void Render_TargetBoxes_AllStyles_RedAndCyan()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        foreach (var style in Enum.GetValues<TargetBoxStyle>())
        {
            foreach (var color in new[] { TargetColor.Red, TargetColor.Cyan })
                Render(Path.Combine(dir, $"fun_box_{style}_{color}.png"), Stage(TwoTargets(), style, color, text: null));
        }

        Assert.True(Directory.GetFiles(dir, "fun_box_*.png").Length >= 6);
    }

    [Trait("Category", "Visual")]
    [StaFact]
    public void Render_FunText_HudAndHint()
    {
        var dir = RenderDir();
        if (dir is null)
            return;

        var plural = FunTextFormatter.Resolve(new FunSettings(), 2);
        var singular = FunTextFormatter.Resolve(new FunSettings(), 1);

        foreach (var position in new[] { FunTextPosition.Center, FunTextPosition.Bottom })
        {
            var hud = new FunText { Text = plural, IsShown = true, Position = position, Color = TargetColor.Red };
            Render(Path.Combine(dir, $"fun_text_hud_{position}.png"), Stage(TwoTargets(true, plural), TargetBoxStyle.Corners, TargetColor.Red, hud));
        }

        var cyan = new FunText { Text = singular, IsShown = true, Position = FunTextPosition.Top, Color = TargetColor.Cyan };
        Render(Path.Combine(dir, "fun_text_hud_Top_Cyan.png"), Stage(TwoTargets(true, singular), TargetBoxStyle.CornersCrosshair, TargetColor.Cyan, cyan));

        foreach (var position in new[] { FunTextPosition.Center, FunTextPosition.Bottom })
        {
            var hint = new FunText
            {
                Text = plural, IsShown = true, Position = position, TextStyle = FunTextStyle.Hint, HintStyle = HintStyle.Pill,
            };
            Render(Path.Combine(dir, $"fun_text_hint_Pill_{position}.png"), Stage(TwoTargets(true, plural), TargetBoxStyle.Rectangle, TargetColor.Green, hint));
        }

        Assert.True(Directory.GetFiles(dir, "fun_text_*.png").Length >= 5);
    }
}
