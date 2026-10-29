using System.Diagnostics;
using System.IO;
using System.Text;
using Fotobox.Services.Audio;
using Xunit;

namespace Fotobox.Tests;

// Bewusst ohne [Collection(WpfCollection.Name)]: Der MusicPlayer nutzt nur MediaPlayer auf einem eigenen STA-Thread mit eigenem
// Dispatcher und lädt weder BAML noch Pack-URIs, kann also nicht mit den WPF-Tests im Hauptthread verklemmen.
public class MusicPlayerTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Aufräumen darf nie fehlschlagen.
            }
        }
    }

    private string TempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fotobox-music-{Guid.NewGuid():N}{extension}");
        _tempFiles.Add(path);
        return path;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    // Kurzer Sinuston als 16-bit-Mono-WAV.
    private static void WriteSineWav(string path, double seconds)
    {
        const int rate = 22050;
        var samples = (int)(rate * seconds);
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream, Encoding.ASCII);
        w.Write("RIFF"u8);
        w.Write(36 + samples * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000));
    }

    [Fact]
    public async Task MissingFile_DoesNotThrow_AndStaysSilent()
    {
        using var player = new MusicPlayer();
        player.Play(TempFile(".mp3"), 50, loop: true);

        await Task.Delay(300);
        Assert.False(player.IsPlaying);
        await Task.Delay(MusicPlayer.OpenTimeout + TimeSpan.FromMilliseconds(500));
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task GarbageFile_DoesNotThrow_AndGivesUpWithinTimeout()
    {
        var path = TempFile(".mp3");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("Das ist keine Musik, nur Text."));

        using var player = new MusicPlayer();
        player.Play(path, 50, loop: false);

        await Task.Delay(MusicPlayer.OpenTimeout + TimeSpan.FromSeconds(1));
        Assert.False(player.IsPlaying);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrEmptyPath_IsSilent(string? path)
    {
        using var player = new MusicPlayer();
        player.Play(path, 80, loop: false);

        await Task.Delay(200);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public void StopAndFadeOut_WithoutPlay_AndRepeated_DoNotThrow()
    {
        using var player = new MusicPlayer();
        player.Stop();
        player.FadeOut(TimeSpan.FromSeconds(1));
        player.FadeOut(TimeSpan.Zero);
        player.Stop();
        player.Stop();
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task StopAndFadeOut_AfterFailedPlay_AndRepeated_DoNotThrow()
    {
        using var player = new MusicPlayer();
        player.Play(TempFile(".wav"), 50, loop: false);
        player.FadeOut(TimeSpan.FromMilliseconds(200));
        player.FadeOut(TimeSpan.FromMilliseconds(200));
        player.Stop();
        player.Stop();
        player.Play("", 50, loop: false);

        await Task.Delay(300);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task Dispose_IsFast_AndLaterCallsAreNoOps()
    {
        var player = new MusicPlayer();
        player.Play(TempFile(".mp3"), 50, loop: true);
        await Task.Delay(100);

        var sw = Stopwatch.StartNew();
        player.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Dispose dauerte {sw.Elapsed}.");

        player.Play(TempFile(".wav"), 50, loop: true);
        player.FadeOut(TimeSpan.FromSeconds(1));
        player.Stop();
        player.Dispose();
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public void Dispose_WithoutAnyPlay_DoesNotThrow()
    {
        var player = new MusicPlayer();
        player.Dispose();
        player.Play("x.mp3", 50, loop: false);
        player.Stop();
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task ThrowingPlaybackChangedHandler_DoesNotBreakThePlayer()
    {
        using var player = new MusicPlayer();
        player.PlaybackChanged += (_, _) => throw new InvalidOperationException("Testfehler");
        player.Play(TempFile(".wav"), 50, loop: false);
        player.Stop();

        await Task.Delay(200);
        Assert.False(player.IsPlaying);
    }

    // Braucht ein Audiogerät und Media Foundation: nicht in der CI.
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task RealWav_PlaysAndFadesOut()
    {
        var path = TempFile(".wav");
        WriteSineWav(path, 3.0);

        using var player = new MusicPlayer();
        var changes = 0;
        player.PlaybackChanged += (_, _) => Interlocked.Increment(ref changes);

        player.Play(path, 30, loop: true);
        Assert.True(await WaitUntil(() => player.IsPlaying, TimeSpan.FromSeconds(5)), "Wiedergabe startete nicht.");

        // Mit Wiederholung läuft sie über das Dateiende hinaus weiter.
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.True(player.IsPlaying);

        player.FadeOut(TimeSpan.FromMilliseconds(300));
        Assert.True(await WaitUntil(() => !player.IsPlaying, TimeSpan.FromSeconds(3)), "Ausblendung endete nicht.");
        Assert.True(Volatile.Read(ref changes) >= 2);
    }
}
